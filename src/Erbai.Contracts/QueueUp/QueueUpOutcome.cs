namespace Erbai.Contracts.QueueUp;

/// <summary>队列操作结果（排队/取消/完成等命令的语义返回；属于契约面，UI 与插件共用）。</summary>
public sealed record QueueUpOutcome(bool Accepted, QueueUpEntry? Entry = null, string? Reason = null);