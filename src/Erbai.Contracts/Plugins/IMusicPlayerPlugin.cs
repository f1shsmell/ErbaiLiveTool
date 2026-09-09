using Erbai.Contracts.Configuration;
using Erbai.Contracts.Players;

namespace Erbai.Contracts.Plugins;

/// <summary>
/// 音乐播放器插件（语义直接平移旧 PlayerPort v2）。
/// 五平台（lxmusic/netease/kugou/qqmusic/folia）同构并列。
/// </summary>
public interface IMusicPlayerPlugin : IAsyncDisposable
{
    /// <summary>播放器键："lxmusic" | "netease" | "kugou" | "qqmusic" | "folia"。</summary>
    string Key { get; }

    string DisplayName { get; }

    PlayerCapabilities Capabilities { get; }

    /// <summary>探测并建立连接（连接器启动 / 状态客户端拉起），返回首帧快照。</summary>
    Task<PlayerSnapshot> ActivateAsync(AppConfig config, CancellationToken ct);

    /// <summary>释放连接（停止订阅/子进程）；幂等。</summary>
    Task DeactivateAsync();

    /// <summary>获取当前快照（轮询兜底路径）。</summary>
    Task<PlayerSnapshot> ProbeAsync(CancellationToken ct);

    /// <summary>播放器原生搜索；不支持原生搜索的平台返回空列表。</summary>
    Task<IReadOnlyList<PlayerTrack>> SearchAsync(string query, CancellationToken ct);

    /// <summary>执行播放器命令，返回显式结果（成功/拒绝/不支持）。</summary>
    Task<PlayerOperationResult> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct);

    /// <summary>
    /// 快照事件流（可选能力：Capabilities 无 SnapshotEvents 时应返回 null，
    /// 宿主回退 Probe 轮询）。
    /// </summary>
    IAsyncEnumerable<PlayerSnapshot>? WatchSnapshotsAsync(CancellationToken ct);
}
