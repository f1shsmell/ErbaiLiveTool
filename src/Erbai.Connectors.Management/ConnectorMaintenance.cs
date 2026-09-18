using Erbai.Connectors.Management.Catalog;
using Erbai.Connectors.Management.Versioning;

namespace Erbai.Connectors.Management;

/// <summary>单次维护对某个平台做了什么。</summary>
public enum ConnectorMaintenanceOutcome
{
    /// <summary>已是最新，或无事可做。</summary>
    UpToDate,

    /// <summary>首次安装成功。</summary>
    Installed,

    /// <summary>更新成功。</summary>
    Updated,

    /// <summary>有换播放器分支 / 主版本的更新，但不自动应用，等用户确认。</summary>
    ManualUpdateRequired,

    /// <summary>有更新但性质不允许自动应用，也不是"需手动确认"那一类（如远端分支更旧）。</summary>
    NotAutoApplicable,

    /// <summary>该平台此刻已有更新在执行。</summary>
    Skipped,

    /// <summary>执行失败，当前安装未被破坏。</summary>
    Failed,
}

/// <summary>一次维护对某个平台的结果。</summary>
public sealed record ConnectorMaintenanceAction(
    string PlayerKey,
    ConnectorMaintenanceOutcome Outcome,
    string Message);

/// <summary>一轮维护的汇总。</summary>
public sealed record ConnectorMaintenanceRun(
    DateTimeOffset StartedAt,
    bool SkippedBecauseAlreadyRunning,
    IReadOnlyList<ConnectorUpdateStatus> Statuses,
    IReadOnlyList<ConnectorMaintenanceAction> Actions);

/// <summary>
/// 连接器后台维护：采集版本状态、按决策 D5 的边界自动应用更新、并以固定周期轮询。
/// </summary>
/// <remarks>
/// <para>
/// <b>自动 vs 手动边界（决策 D5）</b>：只有"首次安装"和"同播放器分支内的补丁"会自动应用。
/// 换播放器分支或主版本推进一律只记日志 + 交 UI 提示——因为那意味着连接器是给另一个播放器
/// 版本适配的，自动换上去可能让用户原本能用的播放器直接失效。
/// </para>
/// <para>
/// <b>失败不破坏现状</b>：更新走 <see cref="ConnectorInstaller"/>，任一步失败都会回滚到旧版本
/// 并抛 <see cref="ConnectorManagementException"/>。本类把异常收敛成
/// <see cref="ConnectorMaintenanceOutcome.Failed"/> 动作，绝不让单个平台的失败中断其余平台。
/// </para>
/// <para>
/// <b>开发 / 隔离环境不得自动安装</b>：参考实现用 <c>shouldRunAutomaticConnectorMaintenance</c>
/// 明确禁止在隔离的 user-data 目录下自动装卸连接器。本实现把这个决定权交给调用方——
/// 只有应用在生产路径下才应调用 <see cref="Start"/>（P5 接线处负责判断）。
/// </para>
/// </remarks>
public sealed class ConnectorMaintenance : IDisposable
{
    /// <summary>默认轮询周期（与参考实现一致）。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    private readonly ConnectorCatalogClient _catalog;
    private readonly ConnectorInstaller _installer;
    private readonly IConnectorStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly Action<string>? _log;

    private readonly HashSet<string> _updating = new(StringComparer.Ordinal);
    private readonly object _updatingGate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private ITimer? _timer;
    private bool _disposed;

    public ConnectorMaintenance(
        ConnectorCatalogClient catalog,
        ConnectorInstaller installer,
        IConnectorStore store,
        TimeSpan? interval = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _interval = interval ?? DefaultInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>轮询周期。</summary>
    public TimeSpan Interval => _interval;

    /// <summary>是否已启动周期轮询。</summary>
    public bool IsStarted => _timer is not null;

    /// <summary>某平台此刻是否有更新在执行。</summary>
    public bool IsUpdating(string playerKey)
    {
        lock (_updatingGate)
        {
            return _updating.Contains(playerKey);
        }
    }

    /// <summary>
    /// 采集四个平台的版本状态。清单不可达时抛出 <see cref="ConnectorManagementException"/>。
    /// </summary>
    /// <param name="forceRefresh">是否绕过清单的 5 分钟 TTL 缓存。</param>
    public async Task<IReadOnlyList<ConnectorUpdateStatus>> GetStatusesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ConnectorCatalogSnapshot snapshot =
            await _catalog.GetSnapshotAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

        DateTimeOffset checkedAt = _timeProvider.GetUtcNow();
        List<ConnectorUpdateStatus> statuses = [];

        foreach (string playerKey in ConnectorPlayers.PluginPlayerKeys)
        {
            statuses.Add(BuildStatus(playerKey, snapshot, checkedAt));
        }

        return statuses;
    }

    /// <summary>
    /// 跑一轮维护：采集状态，然后按自动 / 手动边界对每个平台动作。
    /// </summary>
    /// <remarks>
    /// 同一时刻只允许一轮在跑。周期定时器可能在上一轮还没结束时触发，此时直接返回
    /// <see cref="ConnectorMaintenanceRun.SkippedBecauseAlreadyRunning"/> 而不是排队——
    /// 排队会让"网络慢"变成"越积越多轮"，而每轮的结果其实一样。
    /// </remarks>
    public async Task<ConnectorMaintenanceRun> RunOnceAsync(
        bool forceRefresh = true,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Log("[连接器维护] 上一轮尚未结束，跳过本轮。");
            return new ConnectorMaintenanceRun(startedAt, SkippedBecauseAlreadyRunning: true, [], []);
        }

        try
        {
            IReadOnlyList<ConnectorUpdateStatus> statuses =
                await GetStatusesAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

            List<ConnectorMaintenanceAction> actions = [];

            foreach (ConnectorUpdateStatus status in statuses)
            {
                cancellationToken.ThrowIfCancellationRequested();

                actions.Add(await ApplyStatusAsync(status, cancellationToken).ConfigureAwait(false));
            }

            return new ConnectorMaintenanceRun(startedAt, SkippedBecauseAlreadyRunning: false, statuses, actions);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// 对单个平台执行安装 / 更新。供 UI 的"安装""更新"按钮直接调用。
    /// </summary>
    /// <param name="force">
    /// <see langword="false"/> 时遵守自动 / 手动边界（后台维护用）；
    /// <see langword="true"/> 表示用户已明确确认，允许跨播放器分支 / 主版本更新。
    /// </param>
    public async Task<ConnectorMaintenanceAction> UpdateAsync(
        string playerKey,
        bool force = false,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerKey);

        if (!ConnectorPlayers.IsPluginPlayerKey(playerKey))
        {
            return new ConnectorMaintenanceAction(
                playerKey,
                ConnectorMaintenanceOutcome.Failed,
                $"不是本应用支持的插件平台：{playerKey}。");
        }

        ConnectorCatalogSnapshot snapshot =
            await _catalog.GetSnapshotAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

        ConnectorUpdateStatus status = BuildStatus(playerKey, snapshot, _timeProvider.GetUtcNow());

        if (status.LatestVersion is null)
        {
            string reason = status.RejectionReason ?? "清单中没有该平台的条目。";
            return new ConnectorMaintenanceAction(playerKey, ConnectorMaintenanceOutcome.Failed, reason);
        }

        return await ExecuteAsync(status, force, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>启动周期轮询（幂等）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_timer is not null)
        {
            return;
        }

        Log($"[连接器维护] 启动周期检查，每 {_interval.TotalMinutes:0.#} 分钟一次。");

        _timer = _timeProvider.CreateTimer(
            _ => _ = RunOnceSafeAsync(),
            state: null,
            dueTime: _interval,
            period: _interval);
    }

    /// <summary>停止周期轮询（幂等）。</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _runGate.Dispose();
    }

    /// <summary>
    /// 定时器回调里的入口：<b>绝不能</b>让异常逃出去。
    /// </summary>
    /// <remarks>
    /// <see cref="TimeProvider.CreateTimer(Action{object?}, object?, TimeSpan, TimeSpan)"/> 的回调
    /// 抛异常时没有调用方能接住（我们丢掉了返回的 Task），进程级未观察异常就是这么来的。
    /// </remarks>
    private async Task RunOnceSafeAsync()
    {
        try
        {
            await RunOnceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"[连接器维护] 本轮检查失败：{ex.Message}");
        }
    }

    private ConnectorUpdateStatus BuildStatus(
        string playerKey,
        ConnectorCatalogSnapshot snapshot,
        DateTimeOffset checkedAt)
    {
        ActiveConnector? active = _store.ReadActive(playerKey);
        ConnectorCatalogEntry? entry = snapshot.Find(playerKey);
        string? rejection = snapshot.FindRejection(playerKey);

        ConnectorUpdateKind kind = entry is null
            ? ConnectorUpdateKind.None
            : ConnectorVersionPolicy.Classify(active?.Version, entry.Version, playerKey);

        bool updateAvailable = entry is not null && kind != ConnectorUpdateKind.None;

        return new ConnectorUpdateStatus
        {
            PlayerKey = playerKey,
            DisplayName = entry?.Name,
            Installed = active is not null,
            CurrentVersion = active?.Version,
            LatestVersion = entry?.Version,
            MinimumCoreVersion = entry?.MinimumCoreVersion,
            UpdateAvailable = updateAvailable,
            UpdateKind = kind,
            AutoUpdateAvailable = updateAvailable
                && ConnectorVersionPolicy.CanAutoUpdate(active?.Version, entry?.Version, playerKey),
            ManualUpdateAvailable = updateAvailable
                && ConnectorVersionPolicy.RequiresManualUpdate(active?.Version, entry?.Version, playerKey),
            TestedPlayerVersion = entry?.TestedPlayerVersion ?? entry?.PlayerVersionPolicy,
            PlayerVersionPolicy = entry?.PlayerVersionPolicy,
            Updating = IsUpdating(playerKey),
            RejectionReason = rejection,
            CheckedAt = checkedAt,
        };
    }

    /// <summary>按自动 / 手动边界决定本轮该不该动这个平台。</summary>
    private Task<ConnectorMaintenanceAction> ApplyStatusAsync(
        ConnectorUpdateStatus status,
        CancellationToken cancellationToken)
    {
        if (status.RejectionReason is not null)
        {
            Log($"[连接器维护] {status.PlayerKey} 的清单条目被拒绝：{status.RejectionReason}");
            return Task.FromResult(new ConnectorMaintenanceAction(
                status.PlayerKey,
                ConnectorMaintenanceOutcome.Failed,
                status.RejectionReason));
        }

        if (status.Updating)
        {
            return Task.FromResult(new ConnectorMaintenanceAction(
                status.PlayerKey,
                ConnectorMaintenanceOutcome.Skipped,
                "该平台已有更新在执行。"));
        }

        if (!status.UpdateAvailable)
        {
            return Task.FromResult(new ConnectorMaintenanceAction(
                status.PlayerKey,
                ConnectorMaintenanceOutcome.UpToDate,
                status.Installed ? "已是最新版本。" : "清单中没有可安装的版本。"));
        }

        if (status.Installed && !status.AutoUpdateAvailable)
        {
            return Task.FromResult(ManualOrNotApplicable(status));
        }

        return ExecuteAsync(status, force: false, cancellationToken);
    }

    private ConnectorMaintenanceAction ManualOrNotApplicable(ConnectorUpdateStatus status)
    {
        if (status.ManualUpdateAvailable)
        {
            string message =
                $"{status.PlayerKey} 有新的播放器版本分支 {status.LatestVersion}"
                + $"（支持播放器版本 {status.TestedPlayerVersion ?? "未注明"}）；"
                + "不会自动更新，请在播放器设置中手动确认。";

            Log($"[连接器维护] {message}");
            return new ConnectorMaintenanceAction(
                status.PlayerKey,
                ConnectorMaintenanceOutcome.ManualUpdateRequired,
                message);
        }

        string kept = $"{status.PlayerKey} 有不可自动应用的更新 {status.LatestVersion}，已保留当前版本。";
        Log($"[连接器维护] {kept}");

        return new ConnectorMaintenanceAction(
            status.PlayerKey,
            ConnectorMaintenanceOutcome.NotAutoApplicable,
            kept);
    }

    /// <summary>
    /// 真正执行安装 / 更新。这是全类唯一会改磁盘的地方。
    /// </summary>
    private async Task<ConnectorMaintenanceAction> ExecuteAsync(
        ConnectorUpdateStatus status,
        bool force,
        CancellationToken cancellationToken)
    {
        // 先判"有没有更新"。少了这一步，对一个已是最新的连接器点"更新"会走到下面的
        // 手动分支，报出"有不可自动应用的更新 <当前版本>"——既分类错，消息也是假的。
        if (!status.UpdateAvailable)
        {
            return new ConnectorMaintenanceAction(
                status.PlayerKey,
                ConnectorMaintenanceOutcome.UpToDate,
                status.Installed ? "已是最新版本。" : "清单中没有可安装的版本。");
        }

        if (!force && status.Installed && !status.AutoUpdateAvailable)
        {
            return ManualOrNotApplicable(status);
        }

        if (!TryBeginUpdate(status.PlayerKey))
        {
            return new ConnectorMaintenanceAction(
                status.PlayerKey,
                ConnectorMaintenanceOutcome.Skipped,
                "该平台已有更新在执行。");
        }

        try
        {
            ConnectorCatalogEntry? entry =
                await _catalog.FindAsync(status.PlayerKey, forceRefresh: false, cancellationToken)
                    .ConfigureAwait(false);

            if (entry is null)
            {
                return new ConnectorMaintenanceAction(
                    status.PlayerKey,
                    ConnectorMaintenanceOutcome.Failed,
                    "清单中没有该平台的条目。");
            }

            // forceReinstall 一律 false：即便用户手动确认跨分支更新，也不需要重新下载同版本。
            ConnectorInstallResult result = await _installer
                .InstallAsync(status.PlayerKey, entry, forceReinstall: false, runtimeEnvironment: null, progress: null, cancellationToken)
                .ConfigureAwait(false);

            bool wasInstalled = status.Installed;
            string message = wasInstalled
                ? $"{status.PlayerKey} 已更新到 {result.Version}。"
                : $"{status.PlayerKey} 已安装 {result.Version}。";

            Log($"[连接器维护] {message}");

            return new ConnectorMaintenanceAction(
                status.PlayerKey,
                wasInstalled ? ConnectorMaintenanceOutcome.Updated : ConnectorMaintenanceOutcome.Installed,
                message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 安装器已保证失败时回滚到旧版本，这里只负责不让异常外溢到其余平台。
            string message = $"{status.PlayerKey} 更新失败：{ex.Message}";
            Log($"[连接器维护] {message}");

            return new ConnectorMaintenanceAction(
                status.PlayerKey,
                ConnectorMaintenanceOutcome.Failed,
                message);
        }
        finally
        {
            EndUpdate(status.PlayerKey);
        }
    }

    private bool TryBeginUpdate(string playerKey)
    {
        lock (_updatingGate)
        {
            return _updating.Add(playerKey);
        }
    }

    private void EndUpdate(string playerKey)
    {
        lock (_updatingGate)
        {
            _updating.Remove(playerKey);
        }
    }

    private void Log(string message) => _log?.Invoke(message);
}
