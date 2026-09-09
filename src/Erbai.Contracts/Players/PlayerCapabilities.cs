namespace Erbai.Contracts.Players;

/// <summary>
/// 播放器插件能力（加性能力协商：宿主对未知位直接忽略，按已知位选择实现路径）。
/// </summary>
[Flags]
public enum PlayerCapabilities
{
    None = 0,

    /// <summary>支持原生搜索（<c>SearchAsync</c> 返回非空结果；否则恒返回 []）。</summary>
    Search = 1 << 0,

    /// <summary>支持快照事件流（<c>WatchSnapshotsAsync</c>）；缺失时宿主回退 Probe 轮询。</summary>
    SnapshotEvents = 1 << 1,

    /// <summary>队列可编程（InsertNext / ArmNextGuard / InterruptSelected）。</summary>
    QueueProgrammable = 1 << 2,

    /// <summary>支持暂停/恢复（Pause / Resume）。</summary>
    PauseResume = 1 << 3,
}
