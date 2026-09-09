using System.Text.Json;
using Erbai.Contracts.Live;

namespace Erbai.Live.Bilibili.Protocol;

/// <summary>
/// B站弹幕 WS 业务消息解析 → 规范化 <see cref="LiveEvent"/>（docs/04 §2.2 消息解析面；
/// 保留结构化用户名偏好）。
/// 解析失败/不识别的 cmd 返回 null（不影响循环）。
/// </summary>
public static class BilibiliMessageParser
{
    public const string Platform = "bilibili";

    /// <summary>
    /// 解析一条业务消息。解析异常记录在 <paramref name="onError"/>（不抛出、不中断循环）。
    /// </summary>
    public static LiveEvent? Parse(JsonElement root, long roomOwnerUid, string roomId, Action<string>? onError = null)
    {
        string? cmd = null;
        try
        {
            cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;
            if (cmd is null)
            {
                return null;
            }

            return cmd switch
            {
                _ when cmd.StartsWith("DANMU_MSG", StringComparison.Ordinal) => ParseDanmaku(root, roomOwnerUid, roomId),
                "SEND_GIFT" => ParseGift(root, roomOwnerUid, roomId),
                "GUARD_BUY" => ParseGuardBuy(root, roomOwnerUid, roomId),
                "USER_TOAST_MSG_V2" or "USER_TOAST_MSG" => ParseGuardToast(root, roomOwnerUid, roomId),
                "SUPER_CHAT_MESSAGE" => ParseSuperChat(root, roomOwnerUid, roomId),
                "SUPER_CHAT_MESSAGE_DELETE" => null, // 删除醒目留言：无事件面，跳过
                "INTERACT_WORD" => ParseInteractWord(root, roomOwnerUid, roomId),
                "LIVE" => ParseLiveState(root, roomId, "开播"),
                "PREPARING" => ParseLiveState(root, roomId, "下播"),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            onError?.Invoke($"解析 cmd={cmd} 失败（已跳过）: {ex.Message}");
            return null;
        }
    }

    /// <summary>DANMU_MSG：info[1] 文本；info[2] 用户槽；info[3] 勋章；info[0][15] 结构化用户名。</summary>
    private static LiveEvent ParseDanmaku(JsonElement root, long roomOwnerUid, string roomId)
    {
        var info = root.GetProperty("info");
        var text = Arr(info, 1) is { } t ? Str(t) : "";
        var user = Arr(info, 2) ?? default;

        long uid = 0;
        string uname = "";
        bool admin = false;
        if (user.ValueKind == JsonValueKind.Array)
        {
            uid = user.GetArrayLength() > 0 ? Int64(user[0]) : 0;
            uname = user.GetArrayLength() > 1 ? Str(user[1]) : "";
            admin = user.GetArrayLength() > 2 && user[2].ValueKind == JsonValueKind.Number && user[2].GetInt32() == 1;
        }

        // blivedm 本地修改：结构化用户名偏好（mode_info.user.base.name/uname 更长时采用）
        var modeInfo = Arr(info, 0) is { } info0 && Arr(info0, 15) is { } mi ? mi : default;
        if (modeInfo.ValueKind == JsonValueKind.Object && modeInfo.TryGetProperty("user", out var userObj))
        {
            var structured = userObj.ValueKind == JsonValueKind.Object && userObj.TryGetProperty("base", out var baseInfo)
                ? GetString(baseInfo, "name") ?? GetString(baseInfo, "uname") ?? ""
                : "";
            if (structured.Length > uname.Length)
            {
                uname = structured;
            }
        }

        int? medalLevel = null;
        if (Arr(info, 3) is { } medal && medal.ValueKind == JsonValueKind.Array && medal.GetArrayLength() > 0)
        {
            var level = Int64(medal[0]);
            if (level > 0)
            {
                medalLevel = (int)level;
            }
        }

        var timestamp = Arr(info, 0) is { } info0b && Arr(info0b, 4) is { } ts ? FromTimestamp(Int64(ts)) : DateTimeOffset.UtcNow;

        return new LiveEvent
        {
            Platform = Platform,
            RoomId = roomId,
            Kind = LiveEventKind.Danmaku,
            UserId = uid,
            Nickname = uname,
            Text = text,
            IsAdmin = admin,
            IsAnchor = uid != 0 && uid == roomOwnerUid,
            MedalLevel = medalLevel,
            Timestamp = timestamp,
            RawJson = root.GetRawText(),
        };
    }

    /// <summary>SEND_GIFT：data.giftName/num/price/coin_type/total_coin/guard_level + medal_info。</summary>
    private static LiveEvent ParseGift(JsonElement root, long roomOwnerUid, string roomId)
    {
        var data = root.GetProperty("data");
        return new LiveEvent
        {
            Platform = Platform,
            RoomId = roomId,
            Kind = LiveEventKind.Gift,
            UserId = Int64(data, "uid"),
            Nickname = GetString(data, "uname") ?? "",
            GiftName = GetString(data, "giftName"),
            GiftCount = (int)Int64(data, "num"),
            TotalCoin = Int64(data, "total_coin"),
            CoinType = GetString(data, "coin_type"),
            IsAdmin = null,
            IsAnchor = Int64(data, "uid") != 0 && Int64(data, "uid") == roomOwnerUid,
            MedalLevel = MedalLevel(data),
            Timestamp = DateTimeOffset.UtcNow,
            RawJson = root.GetRawText(),
        };
    }

    /// <summary>GUARD_BUY：data.gift_name/price/num/guard_level。</summary>
    private static LiveEvent ParseGuardBuy(JsonElement root, long roomOwnerUid, string roomId)
    {
        var data = root.GetProperty("data");
        return GuardBuyEvent(root, data, roomOwnerUid, roomId, giftName: GetString(data, "gift_name") ?? "");
    }

    /// <summary>USER_TOAST_MSG_V2：上舰 toast（role_name 为舰队名），归一为 GuardBuy。</summary>
    private static LiveEvent ParseGuardToast(JsonElement root, long roomOwnerUid, string roomId)
    {
        var data = root.GetProperty("data");
        return GuardBuyEvent(root, data, roomOwnerUid, roomId, giftName: GetString(data, "role_name") ?? "");
    }

    private static LiveEvent GuardBuyEvent(JsonElement root, JsonElement data, long roomOwnerUid, string roomId, string giftName)
    {
        var uid = Int64(data, "uid");
        var price = Int64(data, "price");
        var num = Int64(data, "num");
        return new LiveEvent
        {
            Platform = Platform,
            RoomId = roomId,
            Kind = LiveEventKind.GuardBuy,
            UserId = uid,
            Nickname = GetString(data, "username") ?? GetString(data, "uname") ?? "",
            GiftName = giftName,
            GiftCount = (int)num,
            TotalCoin = price * num,
            CoinType = "gold",
            IsAdmin = null,
            IsAnchor = uid != 0 && uid == roomOwnerUid,
            Timestamp = DateTimeOffset.UtcNow,
            RawJson = root.GetRawText(),
        };
    }

    /// <summary>SUPER_CHAT_MESSAGE：醒目留言，文本归一到 Danmaku（可点歌/命令）。</summary>
    private static LiveEvent ParseSuperChat(JsonElement root, long roomOwnerUid, string roomId)
    {
        var data = root.GetProperty("data");
        var uid = Int64(data, "uid");
        var user = data.TryGetProperty("user", out var u) ? u : default;
        return new LiveEvent
        {
            Platform = Platform,
            RoomId = roomId,
            Kind = LiveEventKind.Danmaku,
            UserId = uid,
            Nickname = GetString(user, "uname") ?? "",
            Text = GetString(data, "message"),
            IsAdmin = null,
            IsAnchor = uid != 0 && uid == roomOwnerUid,
            MedalLevel = MedalLevel(data),
            Timestamp = DateTimeOffset.UtcNow,
            RawJson = root.GetRawText(),
        };
    }

    /// <summary>INTERACT_WORD：msg_type 1进入/2关注/3分享/6点赞（user 与 uinfo 双形态）。</summary>
    private static LiveEvent ParseInteractWord(JsonElement root, long roomOwnerUid, string roomId)
    {
        var data = root.GetProperty("data");
        long uid = 0;
        string uname = "";
        if (data.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            uid = Int64(user, "uid");
            uname = GetString(user, "uname") ?? "";
        }
        else if (data.TryGetProperty("uinfo", out var uinfo) && uinfo.ValueKind == JsonValueKind.Object)
        {
            uid = Int64(uinfo, "uid");
            uname = uinfo.TryGetProperty("base", out var baseInfo) ? GetString(baseInfo, "name") ?? "" : "";
        }

        var kind = (int)Int64(data, "msg_type") switch
        {
            2 or 4 or 5 => LiveEventKind.Follow,
            3 => LiveEventKind.Share,
            6 => LiveEventKind.Like,
            _ => LiveEventKind.Enter,
        };

        return new LiveEvent
        {
            Platform = Platform,
            RoomId = roomId,
            Kind = kind,
            UserId = uid,
            Nickname = uname,
            IsAdmin = null,
            IsAnchor = uid != 0 && uid == roomOwnerUid,
            Timestamp = DateTimeOffset.UtcNow,
            RawJson = root.GetRawText(),
        };
    }

    private static LiveEvent ParseLiveState(JsonElement root, string roomId, string text)
    {
        return new LiveEvent
        {
            Platform = Platform,
            RoomId = roomId,
            Kind = LiveEventKind.LiveState,
            UserId = 0,
            Nickname = "直播间",
            Text = text,
            IsAnchor = null,
            Timestamp = DateTimeOffset.UtcNow,
            RawJson = root.GetRawText(),
        };
    }

    // ── 字段辅助（多形态容忍：缺字段/类型不符 → 默认值，不抛异常） ────────────

    private static JsonElement? Arr(JsonElement parent, int index) =>
        parent.ValueKind == JsonValueKind.Array && parent.GetArrayLength() > index
            ? parent[index]
            : null;

    private static string Str(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();

    private static long Int64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) ? Int64(e) : 0;

    private static long Int64(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : 0,
        JsonValueKind.String => long.TryParse(e.GetString(), out var s) ? s : 0,
        _ => 0,
    };

    private static string? GetString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static int? MedalLevel(JsonElement data)
    {
        if (!data.TryGetProperty("medal_info", out var medal) || medal.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var level = Int64(medal, "medal_level");
        return level > 0 ? (int)level : null;
    }

    /// <summary>
    /// info[0][4] 时间戳：老格式秒（10 位），2026 起实测为毫秒（13 位）——
    /// 按数值量级自动识别，避免 FromUnixTimeSeconds 超范围抛异常。
    /// </summary>
    private static DateTimeOffset FromTimestamp(long value)
    {
        if (value <= 0)
        {
            return DateTimeOffset.UtcNow;
        }

        return value >= 100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }
}
