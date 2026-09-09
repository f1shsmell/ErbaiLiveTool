namespace Erbai.Contracts.Storage;

/// <summary>用户黑名单行（对应 banned_users 表；ban/unban 幂等 UPSERT）。</summary>
public sealed record BannedUser
{
    public long Id { get; init; }

    public required string Platform { get; init; }

    public string RoomId { get; init; } = "";

    public required string UserId { get; init; }

    public string Nickname { get; init; } = "";

    public string Reason { get; init; } = "";

    public string BannedBy { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }
}
