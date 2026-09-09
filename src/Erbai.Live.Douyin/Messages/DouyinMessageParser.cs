using System.Text.Json;
using Erbai.Contracts.Live;

namespace Erbai.Live.Douyin.Messages;

/// <summary>
/// 抖音 Grabber WS 报文解析 → 规范化 <see cref="LiveEvent"/>（docs/04 §3.1 字段解析面）。
/// 信封 <c>{Type, Data}</c>：Type=1 弹幕 / 2 点赞 / 3 进入 / 4 关注 / 5 礼物 / 6 统计（忽略）/
/// 7 粉丝团 / 8 分享 / 9 下播；Data 是需二次解析的 JSON 字符串（Type=9 无 Data）。
/// 字段多形态归一：文本 Content/Msg/Message、身份键 ADMIN_IDENTITY_KEYS/ANCHOR_IDENTITY_KEYS
/// （下划线无关、大小写无关、role 语义、Owner 块匹配主播）、粉丝团等级 FansClub.Level 多形态。
/// 解析失败/不识别的报文返回 null（不影响循环）。
/// </summary>
public static class DouyinMessageParser
{
    public const string Platform = "douyin";

    public const int DanmakuType = 1;
    public const int FanClubType = 7;
    public const int OfflineType = 9;

    /// <summary>Grabber schema 中携带主播（房主）身份的键（anchor-only，不隐式升 admin）。</summary>
    private static readonly IReadOnlySet<string> AnchorIdentityKeys = new HashSet<string>(
        new[] { "isanchor", "anchor", "isowner", "owner", "role" }, StringComparer.Ordinal);

    /// <summary>Grabber schema 中携带管理员/主播身份的键。</summary>
    private static readonly IReadOnlySet<string> AdminIdentityKeys = new HashSet<string>(
        new[]
        {
            "isadmin", "isanchor", "ismoderator", "isroomadmin", "roomadmin",
            "admin", "anchor", "moderator", "role",
        },
        StringComparer.Ordinal);

    /// <summary>
    /// 解析一条 <c>{Type, Data}</c> 报文。解析异常记录在 <paramref name="onError"/>（不抛出）。
    /// <paramref name="allowedRoomIds"/> 非空时按 WebRoomId/RoomId 白名单过滤。
    /// </summary>
    public static LiveEvent? Parse(
        string envelopeJson,
        IReadOnlySet<string>? allowedRoomIds = null,
        Action<string>? onError = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(envelopeJson);
            var dataToken = doc.RootElement.TryGetProperty("Data", out var d) ? d : default;
            if (dataToken.ValueKind == JsonValueKind.String)
            {
                // Data 是需二次解析的 JSON 字符串：两层 JsonDocument 的生命周期都要
                // 覆盖到解析完成（内层 JsonElement 引用其父 doc，提前 dispose 会抛
                // ObjectDisposedException）
                using var inner = JsonDocument.Parse(dataToken.GetString() ?? "");
                return ParseCore(doc.RootElement, inner.RootElement, allowedRoomIds, onError);
            }

            return ParseCore(doc.RootElement, dataToken, allowedRoomIds, onError);
        }
        catch (JsonException ex)
        {
            onError?.Invoke($"抖音报文 JSON 解析失败（已跳过）: {ex.Message}");
            return null;
        }
    }

    /// <summary>解析核心（信封 + 已解析 Data；两者父 JsonDocument 必须由调用方保持存活）。</summary>
    private static LiveEvent? ParseCore(
        JsonElement envelope,
        JsonElement data,
        IReadOnlySet<string>? allowedRoomIds,
        Action<string>? onError)
    {
        try
        {
            var type = GetInt(envelope, "Type") ?? 0;
            if (type is not (DanmakuType or 2 or 3 or 4 or 5 or 7 or 8 or OfflineType))
            {
                return null; // 统计/未知类型忽略
            }

            if (type == OfflineType)
            {
                return ParseOffline(envelope);
            }

            if (data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var user = data.TryGetProperty("User", out var u) && u.ValueKind == JsonValueKind.Object
                ? u
                : default;

            var content = JoinWords(FirstString(data, "Content", "Msg", "Message"));
            if (type == DanmakuType && content.Length == 0)
            {
                return null;
            }

            var nickname = JoinWords(FirstString(user, "Nickname")) is { Length: > 0 } n ? n : "抖音观众";
            var roomId = FirstString(data, "WebRoomId", "RoomId") ?? "";
            var userId = ExtractUserId(data, user);
            var isAnchor = ExtractIsAnchor(data, user);
            // 部分 Grabber 变体保留了 User 块（含 SecUid）但丢了数字 Id 与 IsAnchor
            // 标记：Owner 匹配已识别为主播时，回退到 Owner 块 Id（身份持久化不丢）。
            if (userId.Length == 0 && isAnchor)
            {
                var owner = data.TryGetProperty("Owner", out var o) && o.ValueKind == JsonValueKind.Object
                    ? o
                    : default;
                userId = FirstString(owner, "UserId", "UserID") ?? "";
            }

            if (allowedRoomIds is { Count: > 0 } && roomId.Length > 0 && !allowedRoomIds.Contains(roomId))
            {
                return null;
            }

            var fanLevel = type == FanClubType
                ? ExtractFanLevel(data, user) ?? (GetInt(data, "Level"))
                : ExtractFanLevel(data, user);

            var evt = new LiveEvent
            {
                Platform = Platform,
                RoomId = roomId,
                Kind = LiveEventKind.Enter, // 占位：下方 switch 逐分支覆盖
                Nickname = nickname,
                UserId = ParseUserId(userId),
                IsAdmin = ExtractIsAdmin(data, user),
                IsAnchor = isAnchor,
                FanLevel = fanLevel,
                Timestamp = DateTimeOffset.UtcNow,
                RawJson = envelope.GetRawText(),
            };

            return type switch
            {
                DanmakuType => evt with { Kind = LiveEventKind.Danmaku, Text = content },
                2 => evt with { Kind = LiveEventKind.Like },
                3 => evt with { Kind = LiveEventKind.Enter },
                4 => evt with { Kind = LiveEventKind.Follow },
                5 => evt with
                {
                    Kind = LiveEventKind.Gift,
                    GiftName = FirstString(data, "GiftName"),
                    GiftCount = GetInt(data, "GiftCount") ?? 1,
                    TotalCoin = GetLong(data, "DiamondCount") ?? 0,
                    CoinType = "gold", // 抖音电池映射 gold（01 §2 LiveEvent 注释）
                },
                7 => evt with { Kind = LiveEventKind.Subscribe },
                8 => evt with { Kind = LiveEventKind.Share },
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            onError?.Invoke($"抖音报文解析失败（已跳过）: {ex.Message}");
            return null;
        }
    }

    /// <summary>Type=9 下播（无 Data；docs/04 §3.1）。</summary>
    private static LiveEvent ParseOffline(JsonElement envelope)
    {
        return new LiveEvent
        {
            Platform = Platform,
            RoomId = FirstString(envelope, "RoomId") ?? "",
            Kind = LiveEventKind.LiveState,
            UserId = 0,
            Nickname = "直播间",
            Text = "下播",
            Timestamp = DateTimeOffset.UtcNow,
            RawJson = envelope.GetRawText(),
        };    }

    /// <summary>
    /// 判定 `key` 在 grabber 用户载荷中是否携带给定特权（admin/anchor）的显式真值。
    /// 仅键存在不够：schema 常带否定值身份键（IsModerator: "0"），把"键存在"当显式
    /// 会在每条普通弹幕上降级已持久化特权。
    /// </summary>
    public static bool FlagIsExplicit(JsonElement user, string key, bool anchor)
    {
        var normalized = NormalizeKey(key);
        var keys = anchor ? AnchorIdentityKeys : AdminIdentityKeys;
        if (!keys.Contains(normalized))
        {
            return false;
        }

        if (!user.TryGetProperty(key, out var value))
        {
            return false;
        }

        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            // 结构键（如 Data.Owner）不是身份标志：非空 dict 不得读作显式
            return false;
        }

        if (normalized == "role")
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var tokens = anchor
                ? new[] { "anchor", "owner", "主播" }
                : new[] { "admin", "moderator", "anchor", "room" };
            var text = value.GetString() ?? "";
            return tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        return AsBool(value);
    }

    /// <summary>管理员/主播 flag 归一（多 key 名、下划线无关、大小写无关、role 语义）。</summary>
    private static bool ExtractIsAdmin(JsonElement data, JsonElement user) =>
        ScanIdentityFlags(data, user, anchor: false);

    private static bool ExtractIsAnchor(JsonElement data, JsonElement user)
    {
        if (ScanIdentityFlags(data, user, anchor: true))
        {
            return true;
        }

        // 无标志时回退 Owner 块匹配：发送者自己的 Id/SecUid == 房主 Id/SecUid 即主播
        // （覆盖上游 chat 消息不带 IsAnchor 标记的 Grabber 版本）
        var owner = data.TryGetProperty("Owner", out var o) && o.ValueKind == JsonValueKind.Object
            ? o
            : default;
        if (owner.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var ownerUserId = FirstString(owner, "UserId", "UserID") ?? "";
        var ownerSecUid = FirstString(owner, "SecUid") ?? "";
        var senderUserId = FirstString(user, "Id", "UserId", "UserID") ?? "";
        var senderSecUid = FirstString(user, "SecUid") ?? "";
        return ownerUserId.Length > 0 && senderUserId.Length > 0 && ownerUserId == senderUserId
            || ownerSecUid.Length > 0 && senderSecUid.Length > 0 && ownerSecUid == senderSecUid;
    }

    private static bool ScanIdentityFlags(JsonElement data, JsonElement user, bool anchor)
    {
        var keys = anchor ? AnchorIdentityKeys : AdminIdentityKeys;
        foreach (var source in new[] { user, data })
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var prop in source.EnumerateObject())
            {
                var normalized = NormalizeKey(prop.Name);
                if (!keys.Contains(normalized))
                {
                    continue;
                }

                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    continue;
                }

                if (normalized == "role")
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var tokens = anchor
                            ? new[] { "anchor", "owner", "主播" }
                            : new[] { "admin", "moderator", "anchor", "room" };
                        var text = prop.Value.GetString() ?? "";
                        if (tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                }
                else if (AsBool(prop.Value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeKey(string key) =>
        key.Replace("_", "").ToLowerInvariant();

    private static bool AsBool(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                var text = (value.GetString() ?? "").Trim().ToLowerInvariant();
                return text is "1" or "true" or "yes" or "y" or "是" or "管理员" or "房管" or "主播";
            case JsonValueKind.Number:
                return value.TryGetInt32(out var n) && n != 0;
            default:
                return false;
        }
    }

    private static string ExtractUserId(JsonElement data, JsonElement user)
    {
        foreach (var source in new[] { user, data })
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var key in new[] { "UserId", "UserID", "Uid", "UID", "Id", "ID" })
            {
                if (source.TryGetProperty(key, out var value) &&
                    value.ValueKind != JsonValueKind.Null &&
                    value.ValueKind != JsonValueKind.Undefined)
                {
                    var text = value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? ""
                        : value.ToString();
                    if (text.Trim().Length > 0)
                    {
                        return text.Trim();
                    }
                }
            }
        }

        return "";
    }

    /// <summary>只返回粉丝团等级，绝不碰无关的用户 Level 字段（旧 _extract_fan_level 平移）。</summary>
    private static int? ExtractFanLevel(JsonElement data, JsonElement user)
    {
        // 直接键（部分 Grabber 变体平铺该值）
        var directKeys = new HashSet<string>(
            new[]
            {
                "FanLevel", "FansLevel", "FansClubLevel", "FanClubLevel", "FansclubLevel",
                "fan_level", "fans_club_level",
            }.Select(NormalizeKey), StringComparer.Ordinal);

        foreach (var source in new[] { data, user })
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var prop in source.EnumerateObject())
            {
                if (directKeys.Contains(NormalizeKey(prop.Name)))
                {
                    var level = AsLevel(prop.Value);
                    if (level is not null)
                    {
                        return level;
                    }
                }
            }
        }

        // 文档形态：{"User": {"FansClub": {"Level": 4}}}；历史变体 FansClubInfo/FanClub/
        // FansLevel、对象序列化为单元素列表。只遍历粉丝团键下的值，避免误用 User.Level。
        foreach (var source in new[] { user, data })
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var prop in source.EnumerateObject())
            {
                if (IsClubToken(prop.Name) || IsClubToken(NormalizeKey(prop.Name)))
                {
                    var level = WalkClub(prop.Value);
                    if (level is not null)
                    {
                        return level;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsClubToken(string name)
    {
        var normalized = NormalizeKey(name);
        return normalized.Contains("fansclub") || normalized.Contains("fanclub") || normalized.Contains("粉丝团");
    }

    private static int? WalkClub(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in value.EnumerateObject())
            {
                var normalized = NormalizeKey(prop.Name);
                if (normalized is "level" or "fanlevel" or "fanslevel" or "fansclublevel" or "fanclublevel")
                {
                    var level = AsLevel(prop.Value);
                    if (level is not null)
                    {
                        return level;
                    }
                }

                var nested = WalkClub(prop.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = WalkClub(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static int? AsLevel(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
        {
            return Math.Max(n, 0);
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out var parsed))
        {
            return Math.Max(parsed, 0);
        }

        return null;
    }

    private static long ParseUserId(string userId) =>
        long.TryParse(userId, out var parsed) ? parsed : 0;

    /// <summary>逗号分隔列表（旧 " ".join(split()) 语义：空白折叠 + 剔除）。</summary>
    private static string JoinWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? FirstString(JsonElement parent, params string[] keys)
    {
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (parent.TryGetProperty(key, out var value) &&
                value.ValueKind != JsonValueKind.Null &&
                value.ValueKind != JsonValueKind.Undefined)
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.ToString(),
                    _ => null,
                };
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement parent, string key)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
        {
            return n;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static long? GetLong(JsonElement parent, string key)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n))
        {
            return n;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
