namespace Erbai.Contracts.Requests;

/// <summary>
/// 点歌请求领域模型（对应旧 song_requests 表；字段面见 docs/03 §3.1）。
/// </summary>
public sealed record SongRequest
{
    /// <summary>数据库自增主键；未入库时为 null。</summary>
    public long? RequestId { get; set; }

    /// <summary>不可变历史号；入库时在 BEGIN IMMEDIATE 事务内以 MAX(sequence)+1 原子分配。</summary>
    public long? Sequence { get; set; }

    public required string Platform { get; init; }

    public string RoomId { get; init; } = "";

    public required string UserId { get; init; }

    public string Nickname { get; init; } = "";

    public bool IsAdmin { get; init; }

    public bool IsAnchor { get; init; }

    public int? FanLevel { get; init; }

    public int? MedalLevel { get; init; }

    public required string SongName { get; init; }

    public string Singer { get; init; } = "";

    public string CanonicalSongKey { get; init; } = "";

    public required RequestStatus Status { get; init; }

    public string FailureReason { get; init; } = "";

    /// <summary>搜索候选结果（不透明 JSON 字符串，阶段 2 起由点歌模块写入/消费）。</summary>
    public string? SearchResultJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
