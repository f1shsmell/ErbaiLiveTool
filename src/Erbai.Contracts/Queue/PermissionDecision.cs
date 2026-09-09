namespace Erbai.Contracts.Queue;

/// <summary>
/// 权限决策。
/// reason_code 取值：allowed / user_banned / duplicate_song / queue_full /
/// user_limit_reached / fan_level_too_low / medal_level_too_low / level_unknown /
/// song_blacklisted 等；拒绝时持久化进 rejected 记录的 failure_reason。
/// </summary>
public sealed record PermissionDecision
{
    public bool Allowed { get; init; }

    public string ReasonCode { get; init; } = "allowed";

    public string Message { get; init; } = "";

    public static PermissionDecision Allow() => new() { Allowed = true };

    public static PermissionDecision Deny(string reasonCode, string message) =>
        new() { Allowed = false, ReasonCode = reasonCode, Message = message };
}
