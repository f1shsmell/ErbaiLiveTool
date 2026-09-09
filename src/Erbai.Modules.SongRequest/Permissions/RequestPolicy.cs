using Erbai.Contracts.Configuration;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Contracts.Storage;

namespace Erbai.Modules.SongRequest.Permissions
{
    using SongRequest = Erbai.Contracts.Requests.SongRequest;

/// <summary>
/// 集中式点歌权限策略。
/// 检查顺序由调用方（提交流水线）保证：黑名单 → 重复点歌 → 队满 → 单用户上限。
/// </summary>
public sealed class RequestPolicy
{
    public int MaxQueueSize { get; private set; }

    public int DisplayLimit { get; private set; }

    public bool UserLimitEnabled { get; private set; }

    public int MaxPerUser { get; private set; }

    public bool DedupeEnabled { get; private set; }

    public string DisplayOrder { get; private set; } = "asc";

    public string LevelUnknownPolicy { get; private set; } = "deny";

    public bool AdminBypass { get; private set; }

    public int DouyinMinFanLevel { get; private set; }

    public int BilibiliMinMedalLevel { get; private set; }

    public RequestPolicy(AppConfig config) => Reload(config);

    /// <summary>运行时热更新策略（设置页保存后调用；旧版构造快照导致改配置不生效）。</summary>
    public void Reload(AppConfig config)
    {
        var queue = config.Queue;
        var permissions = config.Permissions;
        MaxQueueSize = Math.Max(queue.MaxSize, 1);
        DisplayLimit = Math.Max(queue.DisplayLimit, 1);
        UserLimitEnabled = queue.UserLimitEnabled;
        MaxPerUser = Math.Max(queue.MaxPerUser, 1);
        DedupeEnabled = queue.DedupeEnabled;
        DisplayOrder = string.IsNullOrWhiteSpace(queue.DisplayOrder) ? "asc" : queue.DisplayOrder.Trim().ToLowerInvariant();
        LevelUnknownPolicy = string.IsNullOrWhiteSpace(permissions.LevelUnknownPolicy)
            ? "deny"
            : permissions.LevelUnknownPolicy.Trim().ToLowerInvariant();
        AdminBypass = permissions.AdminBypass;
        DouyinMinFanLevel = Math.Max(permissions.DouyinMinFanLevel, 0);
        BilibiliMinMedalLevel = Math.Max(permissions.BilibiliMinMedalLevel, 0);
    }

    /// <summary>用户门槛检查：admin/anchor 直通；平台等级门槛；等级未知策略。</summary>
    public PermissionDecision CheckUser(User user)
    {
        if (AdminBypass && (user.IsAdmin || user.IsAnchor))
        {
            return PermissionDecision.Allow();
        }

        if (user.Platform == "douyin")
        {
            return CheckLevel(user.FanLevel, DouyinMinFanLevel, "fan_level_too_low",
                "抖音粉丝团等级达到 {0} 级后才可以点歌");
        }

        if (user.Platform == "bilibili")
        {
            return CheckLevel(user.MedalLevel, BilibiliMinMedalLevel, "medal_level_too_low",
                "B站勋章等级达到 {0} 级后才可以点歌");
        }

        return PermissionDecision.Allow();
    }

    /// <summary>队列容量与单用户上限（user_id 为空时按昵称计数）。</summary>
    public PermissionDecision CheckQueue(SongRequest request, IReadOnlyList<SongRequest> activeRequests)
    {
        var active = activeRequests.Where(item => RequestStatuses.IsActive(item.Status)).ToList();
        if (active.Count >= MaxQueueSize)
        {
            return PermissionDecision.Deny("queue_full", "点歌队列已满，请稍后再试");
        }

        if (!UserLimitEnabled)
        {
            return PermissionDecision.Allow();
        }

        var stableUser = !string.IsNullOrEmpty(request.UserId) ? request.UserId : request.Nickname;
        var userCount = active.Count(item =>
            item.Platform == request.Platform &&
            item.RoomId == request.RoomId &&
            (!string.IsNullOrEmpty(item.UserId) ? item.UserId : item.Nickname) == stableUser);
        if (userCount >= MaxPerUser)
        {
            return PermissionDecision.Deny("user_limit_reached", "每位观众同时最多保留一首未完成歌曲");
        }

        return PermissionDecision.Allow();
    }

    private PermissionDecision CheckLevel(int? level, int minimum, string reasonCode, string messageTemplate)
    {
        if (minimum <= 0)
        {
            return PermissionDecision.Allow();
        }

        if (level is null)
        {
            return UnknownLevelDecision(minimum);
        }

        if (level.Value < minimum)
        {
            return PermissionDecision.Deny(reasonCode, string.Format(messageTemplate, minimum));
        }

        return PermissionDecision.Allow();
    }

    private PermissionDecision UnknownLevelDecision(int minimum)
    {
        if (LevelUnknownPolicy == "allow")
        {
            return PermissionDecision.Allow();
        }

        return PermissionDecision.Deny("level_unknown",
            $"暂时无法确认等级，需要达到 {minimum} 级后才可以点歌");
    }
}

}
