namespace Erbai.Contracts.QueueUp;

/// <summary>排队条目来源（弹幕命令 / 礼物插队规则）。</summary>
public enum QueueUpSource
{
    /// <summary>弹幕「排队」命令。</summary>
    Danmaku,

    /// <summary>礼物插队规则自动入队。</summary>
    Gift,
}

/// <summary>排队条目状态（最小版：queued 活动，completed/cancelled 终态保留历史）。</summary>
public enum QueueUpStatus
{
    /// <summary>在队。</summary>
    Queued,

    /// <summary>已出队（管理员「完成」）。</summary>
    Completed,

    /// <summary>已取消（用户「取消排队」）。</summary>
    Cancelled,
}

/// <summary>
/// 排队条目（docs/01 §3.7：{id, userId, nickname, content, source, createdAt, status}）。
/// 持久化到 queueup_entries 表；活动队列按 created_at 升序 FIFO。
/// </summary>
public sealed record QueueUpEntry
{
    public long Id { get; init; }

    /// <summary>平台用户标识（Platform:RoomId:UserId 语义由调用方约定，与点歌模块一致）。</summary>
    public required string UserId { get; init; }

    public required string Nickname { get; init; }

    /// <summary>排队内容（弹幕「排队 [内容]」的 [内容]；礼物插队为空串）。</summary>
    public string Content { get; init; } = "";

    public QueueUpSource Source { get; init; } = QueueUpSource.Danmaku;

    public QueueUpStatus Status { get; init; } = QueueUpStatus.Queued;

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// queueup.* 事件信封（沿用 WS 事件信封 {event, version, timestamp, data}，
/// 对齐 queue.* 的 QueueEventEnvelope 模式；Overlay 排队看板频道消费）。
/// </summary>
public sealed record QueueUpEventEnvelope
{
    /// <summary>事件名："queueup.added" / "queueup.updated" / "queueup.completed" /
    /// "queueup.cancelled" / "queueup.rejected"。</summary>
    public required string Event { get; init; }

    public int Version { get; init; } = 1;

    public required DateTimeOffset Timestamp { get; init; }

    public required QueueUpEventData Data { get; init; }
}

/// <summary>queueup.* 事件载荷；每次变化都附完整快照（推送驱动，前端免轮询）。</summary>
public sealed record QueueUpEventData
{
    /// <summary>涉及的条目（rejected 时是尝试入队的条目或 null）。</summary>
    public QueueUpEntry? Entry { get; init; }

    /// <summary>拒绝原因（queue_full / not_eligible / banned）。</summary>
    public string? Reason { get; init; }

    /// <summary>完整队列快照（快照失败时退化为空快照，不丢事件）。</summary>
    public QueueUpSnapshot? Snapshot { get; init; }
}

/// <summary>排队队列快照（position 动态重编号，与点歌 QueueSnapshot 风格一致）。</summary>
public sealed record QueueUpSnapshot
{
    public IReadOnlyList<QueueUpItem> Items { get; init; } = [];

    public int Total { get; init; }

    /// <summary>队列容量（queueup.max_entries）。</summary>
    public int MaxEntries { get; init; }
}

/// <summary>快照中的单条队列项（条目 + 动态位置，1 起）。</summary>
public sealed record QueueUpItem
{
    public required QueueUpEntry Entry { get; init; }

    public int Position { get; init; }
}
