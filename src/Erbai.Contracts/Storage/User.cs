namespace Erbai.Contracts.Storage;

/// <summary>
/// 持久化用户（对应 users 表，字段面见 docs/03 §2–§3）。
/// UPSERT 语义：昵称覆盖、等级 COALESCE 不降级、特权按调用方显式值覆盖、
/// request_count 仅 increment 时 +1。
/// </summary>
public sealed record User
{
    public required string Platform { get; init; }

    public string RoomId { get; init; } = "";

    public required string UserId { get; init; }

    public string Nickname { get; init; } = "";

    public bool IsAdmin { get; init; }

    public bool IsAnchor { get; init; }

    public int? FanLevel { get; init; }

    public int? MedalLevel { get; init; }

    public int RequestCount { get; init; }

    public DateTimeOffset? LastRequestAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
