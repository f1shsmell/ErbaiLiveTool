using System.Text.Json;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Core.Hosting;
using Erbai.Live.Douyin.Messages;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Live.Douyin.Services;

/// <summary>
/// 抖音用户持久化同步（docs/04 §3.2，与 LiveEventBridge 平级）：
/// - 粉丝团等级 LRU 缓存 5000（普通弹幕缺等级时补查，防长播无限增长）；
/// - 信号量 50 限流并发持久化（粉丝团事件洪泛不撑爆 DB 线程）；
/// - 显式标志精细语义：管理员/主播 flag 只有携带真值才覆盖持久化特权
///   （普通消息绝不降级已持久化 admin/anchor；UserService.MergeDouyinUserFlags 兜底）；
/// - 粉丝团（Subscribe）事件只更新等级，不产生点歌（旧 handle_danmaku 语义）。
/// 消费 EventBus 上的 LiveEvent（Platform == "douyin"），插件保持纯净。
/// </summary>
public static class DouyinUserSync
{
    private const int MaxFanLevelEntries = 5000;
    private const int PersistConcurrency = 50;

    /// <summary>启动同步循环（组合根调用；ct 取消即停）。</summary>
    public static Task RunAsync(IEventBus bus, UserService users, ILogBus logs, CancellationToken ct) =>
        ModuleLoops.RunConsumerLoopAsync(
            bus.Subscribe<LiveEvent>(capacity: 256),
            (evt, loopCt) =>
            {
                if (evt.Platform != DouyinMessageParser.Platform)
                {
                    return Task.CompletedTask;
                }

                return HandleAsync(users, evt, loopCt);
            },
            logs,
            "douyin.users",
            ct);

    /// <summary>处理单条抖音事件（LRU + 持久化；测试可直调）。</summary>
    public static async Task HandleAsync(UserService users, LiveEvent evt, CancellationToken ct = default)
    {
        var roomId = evt.RoomId ?? "";
        var userId = evt.UserId.ToString();

        if (evt.Kind == LiveEventKind.Subscribe)
        {
            // 粉丝团入团/升级：只更新等级缓存 + 持久化，不产生点歌
            if (userId != "0" && evt.FanLevel is not null)
            {
                RememberFanLevel(roomId, userId, evt.FanLevel.Value);
                await SaveWithSemaphoreAsync(users, roomId, userId, evt.Nickname,
                    evt.FanLevel, evt.IsAdmin ?? false, evt.IsAnchor ?? false, ct);
            }

            return;
        }

        if (evt.Kind != LiveEventKind.Danmaku && evt.Kind != LiveEventKind.Gift)
        {
            return;
        }

        var fanLevel = evt.FanLevel;
        if (fanLevel is null && userId != "0")
        {
            fanLevel = _fanLevels.GetValueOrDefault((roomId, userId));
        }

        var (hasExplicitAdmin, hasExplicitAnchor) = ExtractExplicitFlags(evt.RawJson);
        await SaveWithSemaphoreAsync(users, roomId, userId, evt.Nickname,
            fanLevel, evt.IsAdmin ?? false, evt.IsAnchor ?? false,
            ct: ct, hasExplicitAdminFlag: hasExplicitAdmin, hasExplicitAnchorFlag: hasExplicitAnchor);
    }

    // ── 粉丝团等级 LRU（精确 LRU：LinkedList 序 + Dictionary 定位） ────────

    private static readonly Dictionary<(string RoomId, string UserId), int> _fanLevels = [];
    private static readonly LinkedList<(string RoomId, string UserId)> _lruOrder = [];

    private static void RememberFanLevel(string roomId, string userId, int level)
    {
        var key = (roomId, userId);
        lock (_fanLevels)
        {
            if (_fanLevels.TryGetValue(key, out _))
            {
                _fanLevels[key] = level;
                _lruOrder.Remove(key);
                _lruOrder.AddFirst(key);
                return;
            }

            _fanLevels[key] = level;
            _lruOrder.AddFirst(key);
            while (_fanLevels.Count > MaxFanLevelEntries)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
                _fanLevels.Remove(oldest);
            }
        }
    }

    // ── 持久化限流（信号量 50） ────────────────────────────────────────────

    private static readonly SemaphoreSlim _persistGate = new(PersistConcurrency, PersistConcurrency);

    private static async Task SaveWithSemaphoreAsync(
        UserService users,
        string roomId,
        string userId,
        string nickname,
        int? fanLevel,
        bool isAdmin,
        bool isAnchor,
        CancellationToken ct,
        bool hasExplicitAdminFlag = false,
        bool hasExplicitAnchorFlag = false)
    {
        if (userId == "0")
        {
            return; // 未识别用户（无 Id 只有 SecUid）：无持久化键，跳过
        }

        await _persistGate.WaitAsync(ct);
        try
        {
            await users.SaveDouyinUserAsync(
                roomId, userId, nickname, fanLevel, isAdmin, isAnchor,
                hasExplicitAdminFlag, hasExplicitAnchorFlag, ct);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    /// <summary>从原始报文重算"显式标志"（FlagIsExplicit 语义，旧 handle_danmaku 平移）。
    /// 扫描面必须与 DouyinMessageParser 一致（Data 顶层 + Data.User 两层）：
    /// 只扫 User 时顶层身份键（如 Data.IsModerator="0" 的显式否定）不会触发降级语义
    /// （docs/00 修复记录 #9）。</summary>
    private static (bool HasExplicitAdmin, bool HasExplicitAnchor) ExtractExplicitFlags(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return (false, false);
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("Data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return (false, false);
            }

            var hasAdmin = false;
            var hasAnchor = false;
            Scan(data);
            if (data.TryGetProperty("User", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                Scan(user);
            }

            return (hasAdmin, hasAnchor);

            void Scan(JsonElement source)
            {
                foreach (var prop in source.EnumerateObject())
                {
                    hasAdmin |= DouyinMessageParser.FlagIsExplicit(source, prop.Name, anchor: false);
                    hasAnchor |= DouyinMessageParser.FlagIsExplicit(source, prop.Name, anchor: true);
                }
            }
        }
        catch (JsonException)
        {
            return (false, false);
        }
    }
}
