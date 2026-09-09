using Erbai.Contracts.Requests;

namespace Erbai.Contracts.Queue;

/// <summary>queue.* 事件信封（平移旧 WS 事件信封 {event, version, timestamp, data}）。</summary>
public sealed record QueueEventEnvelope
{
    /// <summary>事件名："queue.added" / "queue.rejected" / "queue.searching" 等。</summary>
    public required string Event { get; init; }

    public int Version { get; init; } = 1;

    public required DateTimeOffset Timestamp { get; init; }

    public required QueueEventData Data { get; init; }
}

/// <summary>queue.* 事件载荷；每个事件都附带完整队列快照（推送驱动，前端不轮询）。</summary>
public sealed record QueueEventData
{
    public SongRequest? Request { get; init; }

    /// <summary>失败事件的原因（song_not_found / recovered_after_restart 等）。</summary>
    public string? Reason { get; init; }

    /// <summary>queue.started 的恢复入队数。</summary>
    public int? Recovered { get; init; }

    public PermissionDecision? Decision { get; init; }

    /// <summary>搜索结果（dispatch 时附带；不透明 JSON 字符串）。</summary>
    public string? ResultJson { get; init; }

    public QueueSnapshot? QueueSnapshot { get; init; }
}

/// <summary>
/// 队列快照投影：position 每次读取动态
/// 重编号；sequence 是不可变历史号；display_order 只影响展示，派发固定 FIFO。
/// </summary>
public sealed record QueueSnapshot
{
    public IReadOnlyList<QueueItem> Items { get; init; } = [];

    public int QueueTotal { get; init; }

    /// <summary>null = 快照生成失败时的退化空快照（前端以 '-' 渲染，不显示 0/0）。</summary>
    public int? QueueLimit { get; init; }

    public int? DisplayLimit { get; init; }

    public long? CurrentRequestId { get; init; }

    public QueuePlayerStatus Player { get; init; } = new();
}

/// <summary>快照中的单条队列项（活动请求 + 动态 position + is_current）。</summary>
public sealed record QueueItem
{
    public required SongRequest Request { get; init; }

    public int Position { get; init; }

    public bool IsCurrent { get; init; }
}

/// <summary>
/// 快照热路径的 player 字段（只读缓存，绝不发起网络请求；未接入播放器时
/// Connected=false，前端据此呈现灰色而不是把"未探测"伪装成"已连接"）。
/// </summary>
public sealed record QueuePlayerStatus
{
    public string Key { get; init; } = "lxmusic";

    public string Label { get; init; } = "";

    public bool Connected { get; init; }

    public string? Status { get; init; }

    public string? SongName { get; init; }

    public string? Singer { get; init; }

    public string? Version { get; init; }

    public string? NextObservation { get; init; }
}
