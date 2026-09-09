namespace Erbai.Contracts.Live;

/// <summary>规范化直播事件类型（平台无关；点歌/排队/特效/日志都消费它）。</summary>
public enum LiveEventKind
{
    /// <summary>弹幕。</summary>
    Danmaku,

    /// <summary>礼物。</summary>
    Gift,

    /// <summary>上舰（大航海/守护）。</summary>
    GuardBuy,

    /// <summary>进入直播间。</summary>
    Enter,

    /// <summary>关注。</summary>
    Follow,

    /// <summary>订阅（B站关注 / 抖音粉丝团入团等）。</summary>
    Subscribe,

    /// <summary>点赞。</summary>
    Like,

    /// <summary>分享。</summary>
    Share,

    /// <summary>开播/下播状态变化。</summary>
    LiveState,
}

/// <summary>规范化直播事件（平台无关；点歌/排队/特效/日志都消费它）。</summary>
public sealed record LiveEvent
{
    public required string Platform { get; init; }
    public required string RoomId { get; init; }
    public required LiveEventKind Kind { get; init; }
    public required long UserId { get; init; }
    public required string Nickname { get; init; }

    /// <summary>弹幕文本（仅 Kind == Danmaku 时非空）。</summary>
    public string? Text { get; init; }

    // 礼物字段（仅 Kind == Gift 时有意义）
    public string? GiftName { get; init; }
    public int GiftCount { get; init; }
    public long TotalCoin { get; init; }

    /// <summary>金币类型：gold / silver（抖音电池映射 gold）。</summary>
    public string? CoinType { get; init; }

    public bool? IsAdmin { get; init; }
    public bool? IsAnchor { get; init; }

    /// <summary>抖音粉丝团等级 / B站勋章等级（可能未知）。</summary>
    public int? FanLevel { get; init; }
    public int? MedalLevel { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>原始报文（供日志/调试）。</summary>
    public string? RawJson { get; init; }
}
