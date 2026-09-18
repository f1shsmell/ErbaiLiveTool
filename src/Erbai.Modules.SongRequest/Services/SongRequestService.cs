using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Players;
using Erbai.Contracts.Requests;

namespace Erbai.Modules.SongRequest.Services
{
    using SongRequest = Erbai.Contracts.Requests.SongRequest;

/// <summary>单条弹幕的上下文（平台适配器填充后调用 HandleMessageAsync）。</summary>
public sealed record DanmakuContext
{
    public required string Text { get; init; }

    public string Nickname { get; init; } = "直播观众";

    public string Platform { get; init; } = "douyin";

    public string RoomId { get; init; } = "";

    public string UserId { get; init; } = "";

    public bool IsAdmin { get; init; }

    public bool IsAnchor { get; init; }

    public int? FanLevel { get; init; }

    public int? MedalLevel { get; init; }
}

/// <summary>
/// 弹幕命令处理：
/// 管理命令（设置/取消管理员@、拉黑/取消拉黑@）在点歌解析之前匹配；
/// 切歌命令（下一首/切歌/跳过）执行 skip_first + 播放器切歌；
/// 点歌命令经歌曲黑名单拦截 → 提交流水线。
/// 昵称模糊匹配语义：精确优先于模糊；命中多于 1 人拒绝操作；0 命中静默。
/// </summary>
public sealed class SongRequestService
{
    private readonly SongQueueService _queue;
    private readonly IStorageEngine _store;
    private readonly ILogBus _logs;
    private readonly SongBlacklist _blacklist;

    public SongRequestService(SongQueueService queue, IStorageEngine store, ILogBus logs, SongBlacklist blacklist)
    {
        _queue = queue;
        _store = store;
        _logs = logs;
        _blacklist = blacklist;
    }

    /// <summary>返回 true 表示弹幕已被本服务消费（点歌/命令/黑名单拦截），false 表示无关弹幕。</summary>
    public async Task<bool> HandleMessageAsync(DanmakuContext ctx, CancellationToken ct = default)
    {
        var normalized = SongRequestParser.Normalize(ctx.Text);
        if (normalized.Length == 0)
        {
            return false;
        }

        // 管理命令必须走在点歌解析之前（权限命令优先）
        var setMatch = SongRequestParser.AdminSetRegex().Match(normalized);
        if (setMatch.Success)
        {
            return await HandleAdminCommandAsync(ctx, setMatch.Groups[1].Value.Trim(), admin: true, ct);
        }

        var clearMatch = SongRequestParser.AdminClearRegex().Match(normalized);
        if (clearMatch.Success)
        {
            return await HandleAdminCommandAsync(ctx, clearMatch.Groups[1].Value.Trim(), admin: false, ct);
        }

        // 歌曲黑名单命令必须在用户拉黑命令之前匹配：否则「拉黑歌曲 晴天」会被
        // 用户拉黑正则「拉黑\s*@?\s*(.+)」误当成「拉黑用户 @歌曲 晴天」
        var banSongSetMatch = SongRequestParser.BanSongSetRegex().Match(normalized);
        if (banSongSetMatch.Success)
        {
            return await HandleSongBanCommandAsync(ctx, banSongSetMatch.Groups[1].Value.Trim(), banned: true, ct);
        }

        var banSongClearMatch = SongRequestParser.BanSongClearRegex().Match(normalized);
        if (banSongClearMatch.Success)
        {
            return await HandleSongBanCommandAsync(ctx, banSongClearMatch.Groups[1].Value.Trim(), banned: false, ct);
        }

        var banSetMatch = SongRequestParser.BanSetRegex().Match(normalized);
        if (banSetMatch.Success)
        {
            return await HandleBanCommandAsync(ctx, banSetMatch.Groups[1].Value.Trim(), banned: true, ct);
        }

        var banClearMatch = SongRequestParser.BanClearRegex().Match(normalized);
        if (banClearMatch.Success)
        {
            return await HandleBanCommandAsync(ctx, banClearMatch.Groups[1].Value.Trim(), banned: false, ct);
        }

        var command = SongRequestParser.Parse(normalized);
        if (command is null)
        {
            return false;
        }

        if (command.Action == "skip")
        {
            if (!await HasOperatorPrivilegeAsync(ctx, ct))
            {
                Log(ctx, $"「{ctx.Nickname}」尝试切歌，但没有主播/管理员权限");
                return true;
            }

            Log(ctx, $"「{ctx.Nickname}」执行切歌");
            await SkipQueuedCurrentAsync(ctx, ct);
            return true;
        }

        var request = BuildRequest(ctx, command.SongName, command.Singer);
        if (_blacklist.IsBlacklisted(command.SongName))
        {
            Log(ctx, $"已拦截黑名单歌曲：{command.SongName}（点歌人：{ctx.Nickname}）");
            await _queue.RecordRejectedAsync(request, "blacklisted", "歌曲在黑名单中", ct);
            return true;
        }

        Log(ctx, $"收到请求：{command.SongName}{(command.Singer.Length > 0 ? $" - {command.Singer}" : "")}（点歌人：{ctx.Nickname}）");
        var (queued, decision) = await _queue.SubmitAsync(request, ct);
        if (queued is null)
        {
            Log(ctx, $"点歌被拒绝：{decision.ReasonCode}（{decision.Message}）");
        }

        return true;
    }

    /// <summary>
    /// 切歌：终态跳过队首。**不再向播放器发原生 Next**——播放器 Next 切的是它自己
    /// 播放列表的"别的歌"，会先放别的歌才轮到队列派发的下一首（用户实测 2026-08-27，
    /// 替代旧「skip_first + 播放器切歌」契约，docs/03 §1 已同步修订）；队列下一首由
    /// Worker 经 PlaySelected 直接切歌。队列本就无活动请求时显式触发空闲挂起，
    /// 否则播放器会继续放（实测「没歌时切歌还放歌」）。
    /// </summary>
    public async Task SkipQueuedCurrentAsync(DanmakuContext ctx, CancellationToken ct = default)
    {
        var skipped = await _queue.SkipFirstAsync(ct);
        if (skipped is not null)
        {
            return; // 有被跳过曲目：SetTerminalAsync 已结算并唤醒 Worker 派发下一首
        }

        // 队列无活动请求（SkipFirstAsync 未走终态结算，Pause 挂在 SetTerminalAsync 内）：
        // 显式触发空闲挂起（幂等；有活动/在途/空闲歌可播时按既定逻辑不动）
        await _queue.PausePlayerWhenIdleAsync(ct);
    }

    /// <summary>
    /// 操作员特权判定（切歌 / 管理命令共用）：弹幕事件的实时标志优先（命中即返回，
    /// 不查库）；未命中时回落到 users 表里持久化的 is_admin / is_anchor。
    ///
    /// 为什么必须查库（2026-09-18 用户实测「主播与设置的管理员均无法切歌」）：
    /// 特权有两个来源——平台事件标志（房管/主播标记）与持久化记录。「设置管理员@XX」
    /// 写的就是 users.is_admin，平台事件永远不会为应用内设置的管理员打标；抖音主播
    /// 的弹幕也常常不带 anchor 标志（Grabber 报文体差异）。只看事件标志会让这两类
    /// 人一律被拒。存储侧「特权合并不降级」语义见 docs/03 §2——此前该语义只在
    /// 写入侧与 bilibili 提交流水线生效，读取侧（本方法）是缺口。
    ///
    /// 查询必须<b>跨房间</b>（<see cref="IStorageEngine.GetUserPrivilegeAsync"/>），
    /// 不能按 ctx.RoomId 精确匹配：实测库中同一用户既有 room_id='54380982833'
    /// （is_anchor=1）又有 room_id=''（弹幕事件缺 RoomId 时落库）两条记录，切歌
    /// 弹幕恰好走空 RoomId 那条 → 精确匹配会查到无特权的那行，主播照样被拒。
    /// </summary>
    private async Task<bool> HasOperatorPrivilegeAsync(DanmakuContext ctx, CancellationToken ct)
    {
        if (ctx.IsAdmin || ctx.IsAnchor)
        {
            return true;
        }

        if (string.IsNullOrEmpty(ctx.UserId))
        {
            return false;
        }

        try
        {
            var (isAdmin, isAnchor) = await _store.GetUserPrivilegeAsync(ctx.Platform, ctx.UserId, ct);
            return isAdmin || isAnchor;
        }
        catch (Exception ex)
        {
            // 存储抖动按无特权处理：命令路径不得因查询失败而崩溃
            _logs.Log(LogLevel.Warning, $"[{ctx.Platform}点歌] 特权查询失败（按无特权处理）：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> HandleAdminCommandAsync(DanmakuContext ctx, string target, bool admin, CancellationToken ct)
    {
        if (!await HasOperatorPrivilegeAsync(ctx, ct))
        {
            Log(ctx, $"「{ctx.Nickname}」尝试管理管理员，但没有主播/房管权限");
            return true;
        }

        if (target.Length == 0)
        {
            return true;
        }

        var user = await ResolveUserByNicknameAsync(ctx, target, ct);
        if (user is null)
        {
            return true; // 0 命中静默返回
        }

        var updated = await _store.SetUserAdminAsync(user.Platform, user.RoomId, user.UserId, admin, ct);
        if (updated is null)
        {
            Log(ctx, $"用户不存在：{user.Nickname}");
            return true;
        }

        Log(ctx, $"已{(admin ? "设置" : "取消")}管理员：{updated.Nickname}（{updated.UserId}）");
        return true;
    }

    private async Task<bool> HandleBanCommandAsync(DanmakuContext ctx, string target, bool banned, CancellationToken ct)
    {
        if (!await HasOperatorPrivilegeAsync(ctx, ct))
        {
            Log(ctx, $"「{ctx.Nickname}」尝试拉黑用户，但没有主播/房管权限");
            return true;
        }

        if (target.Length == 0)
        {
            return true;
        }

        var user = await ResolveUserByNicknameAsync(ctx, target, ct);
        if (user is null)
        {
            return true; // 0 命中静默返回
        }

        if (banned)
        {
            await _store.BanUserAsync(user.Platform, user.RoomId, user.UserId, user.Nickname,
                reason: "主播通过弹幕拉黑", bannedBy: ctx.Nickname, ct: ct);
            Log(ctx, $"已拉黑用户：{user.Nickname}（{user.UserId}）");
        }
        else
        {
            var removed = await _store.UnbanUserAsync(user.Platform, user.RoomId, user.UserId, ct);
            Log(ctx, $"{(removed ? "已解除拉黑" : "该用户不在黑名单中")}：{user.Nickname}（{user.UserId}）");
        }

        return true;
    }

    private async Task<bool> HandleSongBanCommandAsync(DanmakuContext ctx, string songName, bool banned, CancellationToken ct)
    {
        if (!await HasOperatorPrivilegeAsync(ctx, ct))
        {
            Log(ctx, $"「{ctx.Nickname}」尝试拉黑歌曲，但没有主播/房管权限");
            return true;
        }

        if (songName.Length == 0)
        {
            return true;
        }

        if (banned)
        {
            // 整段作为规则：*关键词 走子串规则，其余走精确歌名（与 UI 语义一致）
            var changed = await _blacklist.AddRuleAsync(songName, ct);
            Log(ctx, changed ? $"已拉黑歌曲：{songName}" : $"歌曲已在黑名单中：{songName}");
        }
        else
        {
            var removed = await _blacklist.RemoveRuleAsync(songName, ct);
            Log(ctx, removed ? $"已解除歌曲拉黑：{songName}" : $"该歌曲不在黑名单中：{songName}");
        }

        return true;
    }

    /// <summary>
    /// 昵称模糊匹配解析：精确命中优先于模糊；命中多于 1 人拒绝操作（防误伤
    /// 同名）；0 命中返回 null（调用方静默）。
    /// </summary>
    private async Task<Erbai.Contracts.Storage.User?> ResolveUserByNicknameAsync(
        DanmakuContext ctx, string nickname, CancellationToken ct)
    {
        var users = await _store.ListUsersAsync(platform: ctx.Platform, roomId: ctx.RoomId,
            nickname: nickname, limit: 50, ct: ct);
        if (users.Count == 0)
        {
            Log(ctx, $"未找到昵称含「{nickname}」的用户");
            return null;
        }

        var exact = users.Where(u => u.Nickname == nickname).ToList();
        var targets = exact.Count > 0 ? exact : users.ToList();
        if (targets.Count > 1)
        {
            var names = string.Join("、", targets.Take(5).Select(u => u.Nickname));
            Log(ctx, $"昵称「{nickname}」匹配到多个用户（{names}…），请使用精确昵称");
            return null;
        }

        return targets[0];
    }

    private static SongRequest BuildRequest(DanmakuContext ctx, string songName, string singer) => new()
    {
        Platform = ctx.Platform,
        RoomId = ctx.RoomId,
        UserId = ctx.UserId,
        Nickname = ctx.Nickname,
        IsAdmin = ctx.IsAdmin,
        IsAnchor = ctx.IsAnchor,
        FanLevel = ctx.FanLevel,
        MedalLevel = ctx.MedalLevel,
        SongName = songName,
        Singer = singer,
        Status = RequestStatus.Received,
    };

    private void Log(DanmakuContext ctx, string message) =>
        _logs.Log(LogLevel.Information, $"[{ctx.Platform}点歌] {message}");
}

}
