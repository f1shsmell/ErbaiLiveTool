using System.Net;
using System.Security.Cryptography;
using System.Text;
using Erbai.Connectors.Management.Catalog;
using Erbai.Connectors.Management.Versioning;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// <see cref="ConnectorMaintenance"/> 的状态采集、自动 / 手动边界与周期轮询。
/// </summary>
/// <remarks>
/// <para>
/// 清单与归档都是合成的，用假的 CDN 驱动；健康检查是替身。要钉死的是<b>决策语义</b>：
/// 什么情况下自动动、什么情况下连下载都不许发、失败会不会牵连别的平台、定时器会不会重叠。
/// </para>
/// <para>
/// 合成包不可能通过 Ed25519 校验（私钥在上游手里，这是设计使然），所以"自动更新成功"
/// 这条路径没法在这里跑通——本文件里它表现为"走到了签名校验这一步"，这已经能证明
/// 自动分支确实执行了完整的校验管线。真正的成功路径由
/// <see cref="ConnectorMaintenanceTests.RealCatalog_InstallsAndUpdatesEndToEnd"/> 覆盖。
/// </para>
/// </remarks>
public class ConnectorMaintenanceTests : IDisposable
{
    private const string NetEase = "netease";
    private const string Folia = "folia";

    /// <summary>上游真实版本形态，用来让版本分类走到真实分支。</summary>
    private const string NetEaseInstalled = "3.1.38.205386.1";
    private const string NetEasePatch = "3.1.38.205386.2";
    private const string NetEaseNextBranch = "3.1.38.205387.1";
    private const string FoliaInstalled = "1.1.3";
    private const string FoliaNextBranch = "1.2.0";

    private readonly TempDirectory _temp = new();
    private readonly ConnectorInstallLayout _layout;
    private readonly ConnectorStore _store;
    private readonly StubHealthChecker _health = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _log = [];

    public ConnectorMaintenanceTests()
    {
        _layout = new ConnectorInstallLayout(_temp.Combine("player-connectors"));
        _store = new ConnectorStore(_layout);
    }

    public void Dispose() => _temp.Dispose();

    // ---------------------------------------------------------------------------------
    // 状态采集
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task GetStatuses_ReportsInstallForUninstalledPlugins()
    {
        using HttpClient http = CreateHttp(CreateHandler(DefaultFixtures()));
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        IReadOnlyList<ConnectorUpdateStatus> statuses = await maintenance.GetStatusesAsync();

        Assert.Equal(4, statuses.Count);

        foreach (ConnectorUpdateStatus status in statuses)
        {
            Assert.False(status.Installed);
            Assert.Null(status.CurrentVersion);
            Assert.Equal(ConnectorUpdateKind.Install, status.UpdateKind);
            Assert.True(status.UpdateAvailable);
            Assert.True(status.AutoUpdateAvailable);
            Assert.False(status.ManualUpdateAvailable);
            Assert.False(status.Updating);
            Assert.Null(status.RejectionReason);
        }

        Assert.Equal(["netease", "kugou", "qqmusic", "folia"], statuses.Select(s => s.PlayerKey));
    }

    [Fact]
    public async Task GetStatuses_ClassifiesSameBranchBumpAsAutoApplicablePatch()
    {
        SeedInstalled(NetEase, NetEaseInstalled);

        using HttpClient http = CreateHttp(CreateHandler(DefaultFixtures()));
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorUpdateStatus status = await FindStatusAsync(maintenance, NetEase);

        Assert.True(status.Installed);
        Assert.Equal(NetEaseInstalled, status.CurrentVersion);
        Assert.Equal(NetEasePatch, status.LatestVersion);
        Assert.Equal(ConnectorUpdateKind.Patch, status.UpdateKind);
        Assert.True(status.AutoUpdateAvailable);
        Assert.False(status.ManualUpdateAvailable);
    }

    [Fact]
    public async Task GetStatuses_ClassifiesBranchChangeAsManual()
    {
        SeedInstalled(NetEase, NetEaseInstalled);

        using HttpClient http = CreateHttp(CreateHandler(DefaultFixtures(netEaseVersion: NetEaseNextBranch)));
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorUpdateStatus status = await FindStatusAsync(maintenance, NetEase);

        Assert.Equal(ConnectorUpdateKind.Player, status.UpdateKind);
        Assert.False(status.AutoUpdateAvailable);
        Assert.True(status.ManualUpdateAvailable);
    }

    [Fact]
    public async Task GetStatuses_ReportsUpToDateWhenVersionsMatch()
    {
        SeedInstalled(NetEase, NetEaseInstalled);

        using HttpClient http = CreateHttp(CreateHandler(DefaultFixtures(netEaseVersion: NetEaseInstalled)));
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorUpdateStatus status = await FindStatusAsync(maintenance, NetEase);

        Assert.False(status.UpdateAvailable);
        Assert.False(status.AutoUpdateAvailable);
        Assert.False(status.ManualUpdateAvailable);
        Assert.Equal(ConnectorUpdateKind.None, status.UpdateKind);
    }

    /// <summary>被清单拒绝的平台不能"凭空消失"，要带上原因出现在状态里。</summary>
    [Fact]
    public async Task GetStatuses_SurfacesRejectedEntryReason()
    {
        string catalog = MutateEntry(
            BuildCatalog(DefaultFixtures()),
            Folia,
            "\"protocolVersion\":1",
            "\"protocolVersion\": 2");

        using HttpClient http = CreateHttp(CreateHandler(DefaultFixtures(), catalogOverride: catalog));
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        IReadOnlyList<ConnectorUpdateStatus> statuses = await maintenance.GetStatusesAsync();
        ConnectorUpdateStatus status = statuses.Single(s => s.PlayerKey == Folia);

        Assert.Contains("协议版本不兼容", status.RejectionReason, StringComparison.Ordinal);
        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);

        // 其余三个平台不受影响。
        Assert.Equal(3, statuses.Count(s => s.RejectionReason is null));
    }

    // ---------------------------------------------------------------------------------
    // 自动 / 手动边界（决策 D5）
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// 这是决策 D5 的核心保证：只有"换播放器分支"的更新可用时，<b>连一个下载请求都不许发</b>。
    /// 只断言"结果不是 Updated"是不够的——那无法区分"没做"和"做了但失败了"。
    /// </summary>
    [Fact]
    public async Task RunOnce_DoesNotTouchTheNetworkWhenOnlyAManualUpdateIsAvailable()
    {
        // 四个平台都已装好，只有 netease 有"换分支"的更新可用——这样"没有下载请求"
        // 才是干净的断言，不会被其余平台的首次安装混进来。
        SeedAllUpToDate();

        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures(netEaseVersion: NetEaseNextBranch));
        using HttpClient http = CreateHttp(handler);
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorMaintenanceRun run = await maintenance.RunOnceAsync();

        ConnectorMaintenanceAction action = run.Actions.Single(a => a.PlayerKey == NetEase);
        Assert.Equal(ConnectorMaintenanceOutcome.ManualUpdateRequired, action.Outcome);
        Assert.Contains("不会自动更新", action.Message, StringComparison.Ordinal);

        // 除清单外，一个连接器归档请求都不该发出。
        Assert.Equal(1, handler.CountRequests(url => url.Contains("catalog.json", StringComparison.Ordinal)));
        Assert.Equal(0, handler.CountRequests(url => url.Contains("/download/", StringComparison.Ordinal)));

        // 已装版本必须原封不动。
        Assert.Equal(NetEaseInstalled, _store.ReadActive(NetEase)!.Version);
    }

    [Fact]
    public async Task RunOnce_ReportsManualUpdateForBranchChangeOnEveryPlatform()
    {
        SeedAllUpToDate();

        using HttpClient http = CreateHttp(CreateHandler(DefaultFixtures(
            netEaseVersion: NetEaseNextBranch,
            foliaVersion: FoliaNextBranch)));

        ConnectorMaintenance maintenance = CreateMaintenance(http);
        ConnectorMaintenanceRun run = await maintenance.RunOnceAsync();

        Assert.Equal(
            ConnectorMaintenanceOutcome.ManualUpdateRequired,
            run.Actions.Single(a => a.PlayerKey == NetEase).Outcome);
        Assert.Equal(
            ConnectorMaintenanceOutcome.ManualUpdateRequired,
            run.Actions.Single(a => a.PlayerKey == Folia).Outcome);
    }

    /// <summary>
    /// 补丁可自动应用：必须真的走完整条校验管线。合成包过不了 Ed25519，所以断言落在
    /// "下载发生了" + "失败原因是签名" + "旧版本没被破坏"这三点上。
    /// </summary>
    [Fact]
    public async Task RunOnce_AppliesPatchThroughTheFullVerificationPipeline()
    {
        SeedInstalled(NetEase, NetEaseInstalled);

        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures());
        using HttpClient http = CreateHttp(handler);
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorMaintenanceRun run = await maintenance.RunOnceAsync();

        ConnectorMaintenanceAction action = run.Actions.Single(a => a.PlayerKey == NetEase);

        // 走到了签名校验，说明 size 与 SHA-256 都已通过——自动分支确实执行了完整管线。
        Assert.Equal(ConnectorMaintenanceOutcome.Failed, action.Outcome);
        Assert.Contains("签名", action.Message, StringComparison.Ordinal);

        Assert.True(handler.CountRequests(url => url.Contains("/download/netease/", StringComparison.Ordinal)) > 0);

        // 失败必须回滚：旧版本还在，且没有被写坏。
        ActiveConnector active = Assert.IsType<ActiveConnector>(_store.ReadActive(NetEase));
        Assert.Equal(NetEaseInstalled, active.Version);
        Assert.True(File.Exists(active.Executable));
        Assert.False(Directory.Exists(_layout.GetVersionDirectory(NetEase, NetEasePatch)));
    }

    /// <summary>某个平台的失败不能中断其余平台——四个平台是独立资产。</summary>
    [Fact]
    public async Task RunOnce_KeepsProcessingOtherPlayersAfterOneFailure()
    {
        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures());
        using HttpClient http = CreateHttp(handler);
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorMaintenanceRun run = await maintenance.RunOnceAsync();

        Assert.Equal(4, run.Actions.Count);
        Assert.All(run.Actions, action => Assert.Equal(ConnectorMaintenanceOutcome.Failed, action.Outcome));
        Assert.Equal(
            ["netease", "kugou", "qqmusic", "folia"],
            run.Actions.Select(a => a.PlayerKey));
    }

    [Fact]
    public async Task RunOnce_IsUpToDateWhenNothingChanged()
    {
        SeedAllUpToDate();

        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures(netEaseVersion: NetEaseInstalled));
        using HttpClient http = CreateHttp(handler);
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorMaintenanceRun run = await maintenance.RunOnceAsync();

        Assert.All(run.Actions, action => Assert.Equal(ConnectorMaintenanceOutcome.UpToDate, action.Outcome));
        Assert.Equal(0, handler.CountRequests(url => url.Contains("/download/", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------------------------
    // UpdateAsync（UI 手动确认入口）
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_RefusesManualUpdateUnlessForced()
    {
        SeedInstalled(NetEase, NetEaseInstalled);

        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures(netEaseVersion: NetEaseNextBranch));
        using HttpClient http = CreateHttp(handler);
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorMaintenanceAction refused = await maintenance.UpdateAsync(NetEase);

        Assert.Equal(ConnectorMaintenanceOutcome.ManualUpdateRequired, refused.Outcome);
        Assert.Equal(0, handler.CountRequests(url => url.Contains("/download/", StringComparison.Ordinal)));

        // 用户明确确认后才允许跨分支——这里同样会走到签名校验，说明强制路径确实放行了。
        ConnectorMaintenanceAction forced = await maintenance.UpdateAsync(NetEase, force: true);

        Assert.Equal(ConnectorMaintenanceOutcome.Failed, forced.Outcome);
        Assert.Contains("签名", forced.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAsync_RejectsUnknownPlayerKey()
    {
        using HttpClient http = CreateHttp(CreateHandler(DefaultFixtures()));
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorMaintenanceAction action = await maintenance.UpdateAsync("lxmusic");

        Assert.Equal(ConnectorMaintenanceOutcome.Failed, action.Outcome);
        Assert.Contains("不是本应用支持的插件平台", action.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 回归：对"已是最新"的连接器点更新，必须报 <see cref="ConnectorMaintenanceOutcome.UpToDate"/>。
    /// 曾经会掉进手动分支，报出"有不可自动应用的更新 &lt;当前版本&gt;"——分类错、消息也假。
    /// 这是真实端到端测试发现的。
    /// </summary>
    [Fact]
    public async Task UpdateAsync_OnUpToDateConnectorReportsUpToDate()
    {
        SeedInstalled(NetEase, NetEaseInstalled);

        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures(netEaseVersion: NetEaseInstalled));
        using HttpClient http = CreateHttp(handler);
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorMaintenanceAction action = await maintenance.UpdateAsync(NetEase);

        Assert.Equal(ConnectorMaintenanceOutcome.UpToDate, action.Outcome);
        Assert.DoesNotContain("不可自动应用", action.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.CountRequests(url => url.Contains("/download/", StringComparison.Ordinal)));
    }

    /// <summary>强制更新一个已是最新的连接器，同样不该去下载。</summary>
    [Fact]
    public async Task UpdateAsync_ForceOnUpToDateConnectorStillReportsUpToDate()
    {
        SeedInstalled(NetEase, NetEaseInstalled);

        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures(netEaseVersion: NetEaseInstalled));
        using HttpClient http = CreateHttp(handler);
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorMaintenanceAction action = await maintenance.UpdateAsync(NetEase, force: true);

        Assert.Equal(ConnectorMaintenanceOutcome.UpToDate, action.Outcome);
        Assert.Equal(0, handler.CountRequests(url => url.Contains("/download/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpdateAsync_ReportsRejectedEntryAsFailure()
    {
        string catalog = MutateEntry(
            BuildCatalog(DefaultFixtures()),
            Folia,
            "\"protocolVersion\":1",
            "\"protocolVersion\": 2");

        using HttpClient http = CreateHttp(CreateHandler(DefaultFixtures(), catalogOverride: catalog));
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        ConnectorMaintenanceAction action = await maintenance.UpdateAsync(Folia);

        Assert.Equal(ConnectorMaintenanceOutcome.Failed, action.Outcome);
        Assert.Contains("协议版本不兼容", action.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------
    // 重入与并发
    // ---------------------------------------------------------------------------------

    /// <summary>上一轮没结束时，新的一轮直接跳过而不是排队——排队会把"网络慢"放大成积压。</summary>
    [Fact]
    public async Task RunOnce_SkipsWhenPreviousRunIsStillInFlight()
    {
        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures());

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Gate = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        using HttpClient http = CreateHttp(handler);
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        Task<ConnectorMaintenanceRun> first = maintenance.RunOnceAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        ConnectorMaintenanceRun second = await maintenance.RunOnceAsync();

        Assert.True(second.SkippedBecauseAlreadyRunning);
        Assert.Empty(second.Statuses);

        release.SetResult();
        ConnectorMaintenanceRun completed = await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(completed.SkippedBecauseAlreadyRunning);
        Assert.Equal(4, completed.Statuses.Count);
    }

    /// <summary>同一平台不会有两个安装同时跑。</summary>
    [Fact]
    public async Task UpdateAsync_SkipsWhenSamePlatformIsAlreadyUpdating()
    {
        SeedInstalled(NetEase, NetEaseInstalled);

        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures());
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 只拦下载请求，让清单请求先过去。
        handler.Gate = request =>
        {
            if (!request.RequestUri!.AbsoluteUri.Contains("/download/", StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            entered.TrySetResult();
            return release.Task;
        };

        using HttpClient http = CreateHttp(handler);
        ConnectorMaintenance maintenance = CreateMaintenance(http);

        Task<ConnectorMaintenanceAction> first = maintenance.UpdateAsync(NetEase);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(maintenance.IsUpdating(NetEase));

        ConnectorMaintenanceAction second = await maintenance.UpdateAsync(NetEase);

        Assert.Equal(ConnectorMaintenanceOutcome.Skipped, second.Outcome);
        Assert.Contains("已有更新在执行", second.Message, StringComparison.Ordinal);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(maintenance.IsUpdating(NetEase));
    }

    // ---------------------------------------------------------------------------------
    // 周期轮询
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Start_IsIdempotent()
    {
        using HttpClient http = CreateHttp(CreateHandler(DefaultFixtures()));
        using ConnectorMaintenance maintenance = CreateMaintenance(http);

        maintenance.Start();
        maintenance.Start();
        maintenance.Start();

        Assert.True(maintenance.IsStarted);
        Assert.Equal(1, _log.Count(line => line.Contains("启动周期检查", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PeriodicCheck_RunsOnlyAfterTheIntervalElapses()
    {
        // 全部平台都已是最新，每轮只发清单请求——否则安装请求会混进计数，
        // 而且上一轮的安装还没结束会让下一轮被重入保护跳过，断言就变成竞态。
        SeedAllUpToDate();

        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures(netEaseVersion: NetEaseInstalled));
        using HttpClient http = CreateHttp(handler);
        using ConnectorMaintenance maintenance = CreateMaintenance(http);

        maintenance.Start();

        // 差一点点到点：不该有任何请求。
        _time.Advance(maintenance.Interval - TimeSpan.FromMinutes(1));
        Assert.Equal(0, handler.CountRequests(url => url.Contains("catalog.json", StringComparison.Ordinal)));

        _time.Advance(TimeSpan.FromMinutes(2));
        await TestWait.UntilAsync(
            () => handler.CountRequests(url => url.Contains("catalog.json", StringComparison.Ordinal)) == 1,
            "越过周期后应触发一次清单检查");

        // 下一轮：TTL 是 5 分钟、周期是 30 分钟，所以必定重新拉取。
        _time.Advance(maintenance.Interval);
        await TestWait.UntilAsync(
            () => handler.CountRequests(url => url.Contains("catalog.json", StringComparison.Ordinal)) == 2,
            "第二个周期应再触发一次清单检查");
    }

    [Fact]
    public async Task PeriodicCheck_StopsAfterStop()
    {
        SeedAllUpToDate();

        StubHttpMessageHandler handler = CreateHandler(DefaultFixtures(netEaseVersion: NetEaseInstalled));
        using HttpClient http = CreateHttp(handler);
        using ConnectorMaintenance maintenance = CreateMaintenance(http);

        maintenance.Start();
        _time.Advance(maintenance.Interval);

        await TestWait.UntilAsync(
            () => handler.CountRequests(url => url.Contains("catalog.json", StringComparison.Ordinal)) == 1,
            "第一轮应触发检查");

        // Start 只应建一个定时器（幂等），Stop 后必须彻底释放。
        Assert.Equal(1, _time.ActiveTimerCount);

        maintenance.Stop();
        Assert.False(maintenance.IsStarted);
        Assert.Equal(0, _time.ActiveTimerCount);

        _time.Advance(TimeSpan.FromHours(3));
        await Task.Delay(100);

        Assert.Equal(1, handler.CountRequests(url => url.Contains("catalog.json", StringComparison.Ordinal)));
    }

    /// <summary>
    /// 定时器回调抛出的异常没有任何调用方能接住，会变成进程级未观察异常。
    /// 一次失败不能把整个周期检查弄停。
    /// </summary>
    [Fact]
    public async Task PeriodicCheck_FailureDoesNotKillTheSchedule()
    {
        SeedAllUpToDate();

        bool failing = true;
        StubHttpMessageHandler handler = CreateHandler(
            DefaultFixtures(netEaseVersion: NetEaseInstalled),
            catalogFails: () => failing);

        using HttpClient http = CreateHttp(handler);
        using ConnectorMaintenance maintenance = CreateMaintenance(http);

        maintenance.Start();
        _time.Advance(maintenance.Interval);

        await TestWait.UntilAsync(
            () => _log.Any(line => line.Contains("本轮检查失败", StringComparison.Ordinal)),
            "清单 500 时应记录本轮失败");

        // 调度必须还活着。
        failing = false;
        _time.Advance(maintenance.Interval);

        await TestWait.UntilAsync(
            () => handler.CountRequests(url => url.Contains("catalog.json", StringComparison.Ordinal)) >= 2,
            "失败之后仍应继续下一轮");

        Assert.True(maintenance.IsStarted);
    }

    // ---------------------------------------------------------------------------------
    // 真实清单 / 真实包（默认跳过）
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// 用真实上游清单与真实签名包跑通"安装 → 更新"全链路。默认跳过，避免拖慢常规测试；
    /// 需要时置 <c>ERBAI_TEST_REAL_CONNECTOR=1</c>。
    /// </summary>
    /// <remarks>
    /// 合成包永远过不了 Ed25519（私钥在上游），所以"自动更新成功"这条路径只有真实包能覆盖。
    /// 只跑 netease：它是唯一 win-x64 且 self-contained 无关的包，不需要 x86 私有运行时。
    /// </remarks>
    [Fact]
    public async Task RealCatalog_InstallsAndUpdatesEndToEnd()
    {
        if (Environment.GetEnvironmentVariable("ERBAI_TEST_REAL_CONNECTOR") != "1")
        {
            return;
        }

        using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
        ConnectorDownloader downloader = new(http, retryDelay: TimeSpan.FromMilliseconds(500));
        ConnectorInstaller installer = new(_layout, _store, downloader, _health, _log.Add, runtimeProvider: null);
        ConnectorCatalogClient catalog = new(http);
        ConnectorMaintenance maintenance = new(catalog, installer, _store, log: _log.Add);

        ConnectorMaintenanceAction installed = await maintenance.UpdateAsync(NetEase);

        Assert.Equal(ConnectorMaintenanceOutcome.Installed, installed.Outcome);

        ActiveConnector active = Assert.IsType<ActiveConnector>(_store.ReadActive(NetEase));
        Assert.True(active.Verified);
        Assert.True(File.Exists(active.Executable));

        // 再跑一轮：已经是最新，不该有任何动作。
        ConnectorMaintenanceAction again = await maintenance.UpdateAsync(NetEase);

        Assert.Equal(ConnectorMaintenanceOutcome.UpToDate, again.Outcome);
    }

    // ---------------------------------------------------------------------------------

    private ConnectorMaintenance CreateMaintenance(HttpClient http) =>
        new(
            new ConnectorCatalogClient(http, timeProvider: _time),
            new ConnectorInstaller(
                _layout,
                _store,
                new ConnectorDownloader(http, retryDelay: TimeSpan.Zero, maxAttempts: 1, timeout: TimeSpan.FromSeconds(10)),
                _health,
                _log.Add),
            _store,
            interval: TimeSpan.FromMinutes(30),
            timeProvider: _time,
            log: _log.Add);

    private static HttpClient CreateHttp(StubHttpMessageHandler handler) => new(handler) { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<ConnectorUpdateStatus> FindStatusAsync(
        ConnectorMaintenance maintenance,
        string playerKey)
    {
        IReadOnlyList<ConnectorUpdateStatus> statuses = await maintenance.GetStatusesAsync();
        return statuses.Single(status => status.PlayerKey == playerKey);
    }

    /// <summary>造一个"已装好"的状态：版本目录 + 可执行文件 + active.json，三者齐全才算装好。</summary>
    private void SeedInstalled(string playerKey, string version)
    {
        string executableName = ConnectorPlayers.GetExecutableNames(playerKey)[0];
        string versionDirectory = _layout.GetVersionDirectory(playerKey, version);
        Directory.CreateDirectory(versionDirectory);

        string executablePath = Path.Combine(versionDirectory, executableName);
        File.WriteAllText(executablePath, "seed");

        _store.WriteActive(playerKey, new ActiveConnector
        {
            Id = playerKey,
            Version = version,
            Executable = executablePath,
            Deployment = "self-contained",
            Verified = true,
            ActivatedAt = _time.GetUtcNow().ToString("O"),
        });
    }

    /// <summary>四个平台全部已装且与清单一致——让每轮维护只发清单请求。</summary>
    private void SeedAllUpToDate()
    {
        SeedInstalled(NetEase, NetEaseInstalled);
        SeedInstalled("kugou", "20.1.41.1");
        SeedInstalled("qqmusic", "22.61.2");
        SeedInstalled(Folia, FoliaInstalled);
    }

    private static List<PluginFixture> DefaultFixtures(
        string netEaseVersion = NetEasePatch,
        string foliaVersion = FoliaInstalled) =>
    [
        new(NetEase, netEaseVersion, "win-x64", "self-contained", "netease-payload"),
        new("kugou", "20.1.41.1", "win-x86", "self-contained", "kugou-payload"),
        new("qqmusic", "22.61.2", "win-x86", "self-contained", "qqmusic-payload"),
        new(Folia, foliaVersion, "win-x86", "self-contained", "folia-payload"),
    ];

    private static string BuildCatalog(IEnumerable<PluginFixture> fixtures)
    {
        StringBuilder builder = new();
        builder.Append("""{"schemaVersion":2,"publicKeyId":"bilincm-connectors-2026-01","connectors":{""");

        bool first = true;
        foreach (PluginFixture fixture in fixtures)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append($$"""
                "{{fixture.Key}}":{
                  "id":"{{fixture.Key}}",
                  "name":"{{fixture.Key}}-display",
                  "channel":"stable",
                  "version":"{{fixture.Version}}",
                  "protocolVersion":1,
                  "minimumCoreVersion":"1.0.0",
                  "testedPlayerVersion":"{{fixture.Version}}",
                  "package":{
                    "deployment":"{{fixture.Deployment}}",
                    "runtime":"{{fixture.Rid}}",
                    "asset":"{{fixture.Asset}}",
                    "size":{{fixture.Archive.LongLength}},
                    "sha256":"{{fixture.Sha256}}",
                    "signature":"{{fixture.Signature}}",
                    "downloadUrl":"{{fixture.Url}}"
                  }
                }
                """);
        }

        builder.Append("}}");
        return builder.ToString();
    }

    /// <param name="catalogFails">
    /// 动态开关：返回 <see langword="true"/> 时清单请求返回 500。
    /// 用来验证"一轮失败不会把周期检查弄停"。
    /// </param>
    private static StubHttpMessageHandler CreateHandler(
        List<PluginFixture> fixtures,
        string? catalogOverride = null,
        Func<bool>? catalogFails = null)
    {
        string catalog = catalogOverride ?? BuildCatalog(fixtures);

        Dictionary<string, byte[]> archives = fixtures.ToDictionary(
            fixture => fixture.Url,
            fixture => fixture.Archive,
            StringComparer.Ordinal);

        return new StubHttpMessageHandler((request, _) =>
        {
            string url = request.RequestUri!.AbsoluteUri;

            if (url.Contains("catalog.json", StringComparison.Ordinal))
            {
                if (catalogFails?.Invoke() == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(catalog, Encoding.UTF8, "application/json"),
                };
            }

            return archives.TryGetValue(url, out byte[]? archive)
                ? StubHttpMessageHandler.WholeContent(archive)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static string MutateEntry(string catalog, string playerKey, string from, string to)
    {
        int start = catalog.IndexOf($"\"id\":\"{playerKey}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"夹具里找不到 {playerKey} 条目。");

        int next = catalog.IndexOf("\"id\":\"", start + 1, StringComparison.Ordinal);
        int end = next < 0 ? catalog.Length : next;

        string replaced = catalog[start..end].Replace(from, to, StringComparison.Ordinal);
        return string.Concat(catalog.AsSpan(0, start), replaced, catalog.AsSpan(end));
    }

    /// <summary>合成的连接器发布包及其清单描述。</summary>
    private sealed record PluginFixture(
        string Key,
        string Version,
        string Rid,
        string Deployment,
        string Marker)
    {
        internal byte[] Archive { get; } = ArchiveBuilder.CreateConnectorPackage(
            ConnectorPlayers.GetExecutableNames(Key)[0],
            Marker);

        internal string Asset => Deployment == "framework-dependent"
            ? $"awoo-connector-{Key}-{Version}-{Rid}-framework-dependent.zip"
            : $"awoo-connector-{Key}-{Version}-{Rid}.zip";

        internal string Url =>
            $"https://app.enkianss.us/connectors/v2/download/{Key}/{Version}/{Asset}";

        internal string Sha256 =>
            Convert.ToHexString(SHA256.HashData(Archive)).ToLowerInvariant();

        /// <summary>格式合法（64 字节 base64）但内容必然验签失败的签名。</summary>
        internal string Signature => Convert.ToBase64String(new byte[64]);
    }
}
