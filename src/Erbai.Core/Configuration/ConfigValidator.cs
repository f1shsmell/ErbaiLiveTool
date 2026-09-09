using System.Text.Json;
using Erbai.Contracts.Configuration;

namespace Erbai.Core.Configuration;

/// <summary>
/// 配置校验器：类型/枚举/范围规则（docs/03 §4）。校验失败抛 <see cref="ConfigException"/>；成功返回规范化
/// 副本（归一化枚举/去重/补默认值）。已删：queue.duplicate_scope（quirk #2）、
/// api.admin_token（决策 #6）。
/// </summary>
public static class ConfigValidator
{
    public static readonly IReadOnlySet<string> SupportedProviders = new HashSet<string>
    {
        "kugou", "netease", "qqmusic",
    };

    public static readonly IReadOnlySet<string> SupportedPlayers = new HashSet<string>
    {
        "lxmusic", "netease", "kugou", "qqmusic", "folia",
    };

    public static readonly IReadOnlySet<string> LoopbackHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "127.0.0.1", "::1", "localhost",
    };

    public static AppConfig Validate(AppConfig settings)
    {
        if (settings is null)
        {
            throw new ConfigException("settings must not be null");
        }

        // ---- queue ----
        var queue = settings.Queue;
        CheckIntRange(queue.MaxSize, 1, 999, "queue.max_size");
        CheckIntRange(queue.DisplayLimit, 1, 100, "queue.display_limit");
        CheckIntRange(queue.MaxPerUser, 1, 100, "queue.max_per_user");
        CheckPositive(queue.PlaybackStartTimeout, "queue.playback_start_timeout");
        CheckPositive(queue.PlaybackTimeout, "queue.playback_timeout");
        var displayOrder = queue.DisplayOrder.Trim().ToLowerInvariant();
        if (displayOrder is not ("asc" or "desc"))
        {
            throw new ConfigException("queue.display_order must be one of asc, desc");
        }

        var normalizedQueue = queue with { DisplayOrder = displayOrder };

        // ---- player ----
        var player = settings.Player;
        var playerKey = player.Key.Trim().ToLowerInvariant();
        if (!SupportedPlayers.Contains(playerKey))
        {
            throw new ConfigException($"player.key must be one of {string.Join(", ", SupportedPlayers.Order())}");
        }

        // ---- providers ----
        var providers = settings.Providers;
        if (providers.Enabled.Count == 0)
        {
            throw new ConfigException("providers.enabled must contain at least one provider");
        }

        var normalized = new List<string>();
        foreach (var item in providers.Enabled)
        {
            var value = item.Trim().ToLowerInvariant();
            if (!SupportedProviders.Contains(value))
            {
                throw new ConfigException($"unsupported providers: {value}");
            }

            if (!normalized.Contains(value))
            {
                normalized.Add(value);
            }
        }

        // ---- permissions ----
        if (settings.Permissions.DouyinMinFanLevel < 0)
        {
            throw new ConfigException("permissions.douyin_min_fan_level must be >= 0");
        }

        if (settings.Permissions.BilibiliMinMedalLevel < 0)
        {
            throw new ConfigException("permissions.bilibili_min_medal_level must be >= 0");
        }

        var levelPolicy = settings.Permissions.LevelUnknownPolicy.Trim().ToLowerInvariant();
        if (levelPolicy is not ("deny" or "allow"))
        {
            throw new ConfigException("permissions.level_unknown_policy must be 'deny' or 'allow'");
        }

        // ---- ui（host 强制回环，归一化 127.0.0.1；端口 1-65535）----
        CheckIntRange(settings.Ui.Port, 1, 65535, "ui.port");
        var host = settings.Ui.Host.Trim().ToLowerInvariant();
        if (!LoopbackHosts.Contains(host))
        {
            throw new ConfigException(
                "ui.host 必须是本机回环地址（127.0.0.1 / ::1 / localhost），本地 HTTP 只允许本机访问");
        }

        // ---- overlay ----
        var theme = settings.Overlay.Theme.Trim().ToLowerInvariant();
        if (theme is not ("clash" or "light"))
        {
            throw new ConfigException("overlay.theme must be 'clash' or 'light'");
        }

        // ---- idle_playlist ----
        CheckPositive(settings.IdlePlaylist.PlayInterval, "idle_playlist.play_interval");

        // ---- queueup（阶段 5：容量 / 资格门槛 / 规则表）----
        CheckIntRange(settings.QueueUp.MaxEntries, 1, 500, "queueup.max_entries");
        var queueUpRules = ValidateQueueUpRules(settings.QueueUp.Rules);

        // ---- giftfx（阶段 5：开关 / 时长）----
        CheckIntRange(settings.GiftFx.DurationSeconds, 1, 60, "giftfx.duration_seconds");

        // ---- 顶层 ----
        var roomIds = settings.DouyinRoomIds
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (settings.DouyinReconnectDelay < 0)
        {
            throw new ConfigException("douyin_reconnect_delay must be >= 0");
        }

        // ---- douyin_grabber.app_settings（白名单 + 类型/范围；未知键报错）----
        var appSettings = ValidateGrabberAppSettings(settings.DouyinGrabber.AppSettings);

        return settings with
        {
            Platform = settings.Platform.Trim(),
            RoomId = settings.RoomId.Trim(),
            DouyinWsUrl = settings.DouyinWsUrl.Trim(),
            DouyinRoomIds = roomIds,
            Queue = normalizedQueue,
            Player = player with { Key = playerKey },
            Providers = providers with { Enabled = normalized },
            Permissions = settings.Permissions with { LevelUnknownPolicy = levelPolicy },
            Ui = settings.Ui with { Host = host is "::1" or "localhost" ? "127.0.0.1" : host },
            Overlay = settings.Overlay with { Theme = theme },
            DouyinGrabber = settings.DouyinGrabber with { AppSettings = appSettings },
            QueueUp = settings.QueueUp with { Rules = queueUpRules },
        };
    }

    /// <summary>排队插队规则表校验：match_kind / action 枚举、match_value 非空、
    /// coin_threshold 须为非负整数、position 1-500（规范化 trim）。</summary>
    private static IReadOnlyList<QueueUpRuleConfig> ValidateQueueUpRules(IReadOnlyList<QueueUpRuleConfig> rules)
    {
        var result = new List<QueueUpRuleConfig>(rules.Count);
        foreach (var rule in rules)
        {
            var matchKind = rule.MatchKind.Trim().ToLowerInvariant();
            if (matchKind is not ("gift_name" or "coin_threshold"))
            {
                throw new ConfigException("queueup.rules[].match_kind must be 'gift_name' or 'coin_threshold'");
            }

            var action = rule.Action.Trim().ToLowerInvariant();
            if (action is not ("insert_at" or "grant_eligibility"))
            {
                throw new ConfigException("queueup.rules[].action must be 'insert_at' or 'grant_eligibility'");
            }

            var matchValue = (rule.MatchValue ?? "").Trim();
            if (matchValue.Length == 0)
            {
                throw new ConfigException("queueup.rules[].match_value must not be empty");
            }

            if (matchKind == "coin_threshold" &&
                (!long.TryParse(matchValue, out var threshold) || threshold < 0))
            {
                throw new ConfigException("queueup.rules[].match_value must be a non-negative integer for coin_threshold");
            }

            CheckIntRange(rule.Position, 1, 500, "queueup.rules[].position");
            result.Add(rule with { MatchKind = matchKind, Action = action, MatchValue = matchValue });
        }

        return result;
    }

    private static IReadOnlyDictionary<string, object> ValidateGrabberAppSettings(
        IReadOnlyDictionary<string, object> appSettings)
    {
        var unknown = appSettings.Keys
            .Where(k => !GrabberAppSettings.AllKeys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            throw new ConfigException($"unsupported douyin_grabber.app_settings keys: {string.Join(", ", unknown)}");
        }

        var result = new Dictionary<string, object>(GrabberAppSettings.CreateDefault(), StringComparer.Ordinal);
        foreach (var (key, rawValue) in appSettings)
        {
            var value = UnwrapJsonValue(key, rawValue);
            if (GrabberAppSettings.BoolKeys.Contains(key))
            {
                if (value is not bool)
                {
                    throw new ConfigException($"douyin_grabber.app_settings.{key} must be a boolean");
                }
            }
            else if (GrabberAppSettings.IntKeys.Contains(key))
            {
                if (value is bool)
                {
                    throw new ConfigException($"douyin_grabber.app_settings.{key} must be an integer");
                }

                long number = value switch
                {
                    int i => i,
                    long l => l,
                    double d when d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue => (long)d,
                    _ => throw new ConfigException($"douyin_grabber.app_settings.{key} must be an integer"),
                };
                var lower = GrabberAppSettings.PortKeys.Contains(key) ? 1 : 1000;
                var upper = GrabberAppSettings.PortKeys.Contains(key) ? 65535 : 60000;
                if (number < lower || number > upper)
                {
                    throw new ConfigException(
                        $"douyin_grabber.app_settings.{key} must be between {lower} and {upper}");
                }

                value = (int)number;
            }
            else
            {
                value = value?.ToString() ?? "";
            }

            result[key] = value;
        }

        return result;
    }

    private static object? UnwrapJsonValue(string key, object rawValue)
    {
        // 反序列化 IReadOnlyDictionary<string, object> 时值可能是 JsonElement
        if (rawValue is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.GetInt64(),
                JsonValueKind.String => element.GetString(),
                _ => throw new ConfigException($"douyin_grabber.app_settings.{key} must be a scalar value"),
            };
        }

        return rawValue;
    }

    private static void CheckIntRange(int value, int lower, int upper, string key)
    {
        if (value < lower || value > upper)
        {
            throw new ConfigException($"{key} must be between {lower} and {upper}");
        }
    }

    private static void CheckPositive(int value, string key)
    {
        if (value <= 0)
        {
            throw new ConfigException($"{key} must be positive");
        }
    }
}
