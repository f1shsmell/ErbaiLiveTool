using Erbai.Connectors.Management.Versioning;

namespace Erbai.Connectors.Management;

/// <summary>
/// 单个平台连接器的版本状态，供设置页展示与后台维护判定。
/// </summary>
/// <remarks>
/// <see cref="MinimumCoreVersion"/> 刻意只做展示：它是上游自家应用（AwooMusicBot）的版本口径，
/// 与本应用版本号<b>不可比</b>（决策 D4）。参考实现用它做兼容门控，本实现不能照搬——
/// 我们的版本号与它不同源，比出来的结果没有意义。
/// 真正的协议兼容门控在清单校验层（<c>protocolVersion</c> 必须等于
/// <c>ConnectorProtocol.ProtocolVersion</c>），不兼容的条目根本不会出现在状态列表里，
/// 而是出现在 <see cref="ConnectorUpdateStatus.Error"/> 或 <see cref="RejectionReason"/> 中。
/// </remarks>
public sealed record ConnectorUpdateStatus
{
    /// <summary>平台 key。</summary>
    public required string PlayerKey { get; init; }

    /// <summary>清单里的展示名；清单不可达时为 <see langword="null"/>。</summary>
    public string? DisplayName { get; init; }

    /// <summary>本地是否已安装且记录可用。</summary>
    public bool Installed { get; init; }

    /// <summary>本地已安装版本。</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>清单里的最新版本。</summary>
    public string? LatestVersion { get; init; }

    /// <summary>上游声明的宿主版本要求。仅展示，不参与任何判定。</summary>
    public string? MinimumCoreVersion { get; init; }

    /// <summary>是否有可应用的更新（含首次安装）。</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>本次推进的性质。</summary>
    public ConnectorUpdateKind UpdateKind { get; init; }

    /// <summary>可自动应用（安装 / 同分支补丁）。</summary>
    public bool AutoUpdateAvailable { get; init; }

    /// <summary>需用户手动确认（换播放器分支 / 主版本推进）。</summary>
    public bool ManualUpdateAvailable { get; init; }

    /// <summary>该连接器针对的播放器版本，供 UI 提示"更新后需要 XX 版播放器"。</summary>
    public string? TestedPlayerVersion { get; init; }

    /// <summary>上游的播放器版本策略说明。</summary>
    public string? PlayerVersionPolicy { get; init; }

    /// <summary>该平台此刻有更新正在执行。</summary>
    public bool Updating { get; init; }

    /// <summary>清单里该条目被逐条拒绝的原因；未被拒绝为 <see langword="null"/>。</summary>
    public string? RejectionReason { get; init; }

    /// <summary>本次状态采集的时间。</summary>
    public DateTimeOffset CheckedAt { get; init; }
}
