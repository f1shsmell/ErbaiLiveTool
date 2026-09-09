namespace Erbai.Contracts.QueueUp;

/// <summary>
/// 排队队列服务的宿主协作面（内置 QueueUp 插件迁移为目录式插件后，
/// 主程序 UI 经此接口操作队列——跨 ALC 共享契约强转成立的前提是两端引用同一 Erbai.Contracts）。
/// </summary>
public interface IQueueUpService
{
    /// <summary>当前队列快照（成组状态，供 UI 渲染）。</summary>
    QueueUpSnapshot Snapshot();

    /// <summary>完成队首（仅 admin/anchor 语义由模块内 Danmaku 命令处理；此处为 UI 操作入口）。</summary>
    Task<QueueUpOutcome> CompleteNextAsync(CancellationToken ct = default);

    /// <summary>取消指定条目（UI 选中项操作）。</summary>
    Task<QueueUpOutcome> CancelEntryAsync(long entryId, CancellationToken ct = default);
}