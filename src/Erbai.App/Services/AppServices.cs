using Erbai.Connectors.Management;
using Erbai.Connectors.Management.Catalog;
using Erbai.Connectors.Management.Runtime;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Players;
using Erbai.Contracts.Plugins;
using Erbai.Core.Configuration;
using Erbai.Core.Events;
using Erbai.Core.Hosting;
using Erbai.Core.Logging;
using Erbai.Core.Plugins;
using Erbai.Core.Storage;
using Erbai.Live.Bilibili;
using Erbai.Live.Bilibili.Login;
using Erbai.Live.Bilibili.Protocol;
using Erbai.Live.Douyin;
using Erbai.Live.Douyin.Hosting;
using Erbai.Live.Douyin.Services;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;
using Erbai.Player.Connectors;
using Erbai.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Erbai.App.Services;

/// <summary>
/// 组合根：DI 容器（Microsoft.Extensions.DependencyInjection）装配存储/配置/事件总线/点歌模块/
/// 搜索三源/连接器播放器/Web overlay/B站平台/抖音平台 + 平台监督者。
/// 装配声明与启动序列见 <see cref="CreateAsync"/>；<see cref="DisposeAsync"/> 按依赖逆序显式释放
/// （不依赖容器释放，避免双重释放）。播放器（热切换）与 QueueUp（插件可禁用置空）属可变状态，
/// 不进容器，由本类持有。
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly CancellationTokenSource _bridgeCts;
    private readonly LiveEventLogWriter? _liveLog;
    private ModuleContext? _moduleContext; // 插件热启用复用的运行时上下文（CreateAsync 启动序列填充）
    private ConnectorPlayerPlugin? _player;
    private IQueueUpModule? _queueUp;

    private AppServices(ServiceProvider provider, ConnectorPlayerPlugin? player,
        IQueueUpModule? queueUp, CancellationTokenSource bridgeCts, LiveEventLogWriter? liveLog,
        PlayerConnectorResolver connectorPlayerResolver, ConnectorMaintenance connectorMaintenance,
        ConnectorInstallLayout connectorLayout, ConnectorInstaller connectorInstaller,
        ConnectorCatalogClient connectorCatalog)
    {
        _provider = provider;
        _player = player;
        _queueUp = queueUp;
        _bridgeCts = bridgeCts;
        _liveLog = liveLog;
        ConnectorPlayerResolver = connectorPlayerResolver;
        ConnectorMaintenance = connectorMaintenance;
        ConnectorLayout = connectorLayout;
        ConnectorInstaller = connectorInstaller;
        ConnectorCatalog = connectorCatalog;
    }

    public EventBus Bus => _provider.GetRequiredService<EventBus>();

    public LogBus Logs => _provider.GetRequiredService<LogBus>();

    public SqliteStorageEngine Storage => _provider.GetRequiredService<SqliteStorageEngine>();

    public ConfigStore Config => _provider.GetRequiredService<ConfigStore>();

    public SongQueueService Queue => _provider.GetRequiredService<SongQueueService>();

    public SongRequestService Commands => _provider.GetRequiredService<SongRequestService>();

    public SongBlacklist Blacklist => _provider.GetRequiredService<SongBlacklist>();

    public SearchCoordinator Search => _provider.GetRequiredService<SearchCoordinator>();

    /// <summary>连接器播放器（连接器 exe 缺失时为 null，队列走 fallback 时长；设置页切换播放器时热替换）。</summary>
    public ConnectorPlayerPlugin? Player { get => _player; private set => _player = value; }

    /// <summary>player key → 连接器启动参数（内置 lxmusic / 已安装插件）。</summary>
    public PlayerConnectorResolver ConnectorPlayerResolver { get; }

    /// <summary>
    /// 连接器插件维护：状态采集 + 按 D5 边界自动更新 + 30 分钟周期轮询。
    /// 设置页/插件页的安装与更新按钮都走它。
    /// </summary>
    public ConnectorMaintenance ConnectorMaintenance { get; }

    /// <summary>
    /// 连接器安装目录布局（插件页展示安装根路径、"打开连接器目录"用）。
    /// </summary>
    public ConnectorInstallLayout ConnectorLayout { get; }

    /// <summary>
    /// 连接器安装器。插件页的"从本地 ZIP 安装"（决策 D6）直接走它——与在线安装同一条管线
    /// （校验 → 解压 → 健康检查 → 原子替换），失败回滚语义也一致。
    /// </summary>
    public ConnectorInstaller ConnectorInstaller { get; }

    /// <summary>
    /// 连接器清单客户端。本地 ZIP 安装时用它按资产名反查清单条目，
    /// 从而走完整的 size + SHA-256 + Ed25519 校验（而不是退化成"未校验安装"）。
    /// </summary>
    public ConnectorCatalogClient ConnectorCatalog { get; }

    public OverlayServer? Overlay => _provider.GetService<OverlayServer>();

    /// <summary>悬浮窗治理器（决策 #17）：三类悬浮窗（点歌/排队/弹幕）各自开关/置顶/穿透。</summary>
    public OverlayWindowManager? OverlayWindows => _provider.GetService<OverlayWindowManager>();

    /// <summary>B站 HTTP 客户端（登录服务与插件共享，保证 cookie/buvid3 一致）。</summary>
    public BilibiliApiClient? BiliApi => _provider.GetRequiredService<BilibiliApiClient>();

    /// <summary>B站弹幕插件（阶段 3；配置 Platform=bilibili 时启用）。</summary>
    public BilibiliLivePlugin? Bilibili => _provider.GetRequiredService<BilibiliLivePlugin>();

    /// <summary>B站登录服务（扫码/凭据刷新/登出）。</summary>
    public BilibiliLoginService? Login => _provider.GetRequiredService<BilibiliLoginService>();

    /// <summary>抖音 Grabber 子进程宿主（阶段 4；Grabber exe 缺失时 StartAsync 返回明确错误）。</summary>
    public DouyinGrabberHost GrabberHost => _provider.GetRequiredService<DouyinGrabberHost>();

    /// <summary>抖音弹幕插件（阶段 4）。</summary>
    public DouyinLivePlugin? Douyin => _provider.GetRequiredService<DouyinLivePlugin>();

    /// <summary>平台监督者（单平台重启隔离；B站/抖音统一管理）。</summary>
    public PlatformSupervisor Supervisor => _provider.GetRequiredService<PlatformSupervisor>();

    /// <summary>功能模块宿主（阶段 5：QueueUp/GiftFx，启停互不影响）。</summary>
    public ModuleHost Modules => _provider.GetRequiredService<ModuleHost>();

    /// <summary>目录式插件（Stage A）：全部插件目录视图（加载/禁用/失败 + 原因，供插件管理页展示）。</summary>
    public IReadOnlyList<PluginDirectoryInfo> PluginDirectories =>
        _provider.GetService<PluginLoader>()?.Directories ?? [];

    /// <summary>插件根目录（UI 提示用户放置插件的位置；随宿主 exe 目录定位，见 AppPaths）。</summary>
    public string PluginRoot { get; } = Path.Combine(AppPaths.HostDir, "Plugins");

    /// <summary>插件启停（写 config.ini [General] Enabled；禁用即卸载，启用热加载立即生效）。返回提示消息。</summary>
    public async Task<string> SetPluginEnabledAsync(string directoryKey, bool enabled)
    {
        var loader = _provider.GetService<PluginLoader>();
        var message = loader is null
            ? "插件加载器未启用。"
            : await loader.SetEnabledAsync(directoryKey, enabled, Modules, enabled ? _moduleContext : null);

        // 卸载内置 queueup 后清掉悬空实例引用（审计遗留项落地）：否则
        // AppServices.QueueUp 强引用已卸载插件 ALC 中的实例——ALC 永不回收,
        // 且「排队不可用」时 QueueUp 仍非 null 的语义错误。
        if (string.Equals(directoryKey, "queueup", StringComparison.Ordinal))
        {
            // 启用 = 热加载后从新 ALC 重取实例；禁用 = 清空（热启用路径同样覆盖）
            QueueUp = enabled
                ? loader?.GetPluginInstance<IQueueUpModule>("queueup")
                : null;
        }

        return message;
    }

    /// <summary>
    /// 排队队列模块（Stage A 遗留②迁移为目录式插件后经 Contracts 接口访问；
    /// 插件被禁用/缺失/加载失败时为 null，UI 必须判空）。
    /// </summary>
    public IQueueUpModule? QueueUp { get => _queueUp; private set => _queueUp = value; }

    /// <summary>B站平台当前运行状态（概览页显示）。</summary>
    public string BilibiliStatus { get; private set; } = "未启动";

    public bool BilibiliRunning => Supervisor.IsRunning("bilibili");

    /// <summary>抖音平台当前运行状态（概览页显示）。</summary>
    public string DouyinStatus { get; private set; } = "未启动";

    public bool DouyinRunning => Supervisor.IsRunning("douyin");

    /// <summary>overlay 地址（OBS 浏览器源 / 悬浮窗）。</summary>
    public string OverlayUrl => Overlay is null
        ? "(overlay 未启动)"
        : $"http://127.0.0.1:{Overlay.BoundPort}/overlay";

    public static async Task<AppServices> CreateAsync()
    {
        // 单文件发布下 AppContext.BaseDirectory 指向 %TEMP%\.net\<App>\<hash>\ 解压缓存，
        // 随附文件（config/logs/数据库/DouyinBarrageGrab/Erbai.Connector.exe 等）必须按
        // 宿主 exe 目录定位，否则全被错误定位到 Temp（docs/00 修复记录 #13）
        var baseDir = AppPaths.HostDir;

        // ── 底座（需异步初始化/失败不拖垮主程序的部分先建，再注册进容器）──
        var config = new ConfigStore(Path.Combine(baseDir, "config.json"));
        var settings = await config.LoadAsync();

        // 文件日志（logs/erbai-YYYYMMDD.log 按天滚动）：弹幕日志页只显示打开期间
        // 的事件，连接器激活失败等关键错误必须落盘才能事后排查
        var logs = LoggingSetup.CreateLogBus(Path.Combine(baseDir, "logs"));
        // 直播事件落盘（logs/erbai-live-YYYYMMDD.log 按天滚动）：平台事件桥接处写入，
        // 日志页打开时回看（runtime.live_event_log 开关，默认开）
        LiveEventLogWriter? liveLog = settings.Runtime.LiveEventLog
            ? new LiveEventLogWriter(Path.Combine(baseDir, "logs"))
            : null;
        var bus = new EventBus();
        var storage = new SqliteStorageEngine(Path.Combine(baseDir, settings.Storage.DatabasePath));
        await storage.OpenAsync();

        // Web overlay（端口上探；失败不拖垮主程序）
        OverlayServer? overlay = null;
        try
        {
            overlay = new OverlayServer(settings, bus);
            await overlay.StartAsync();
            overlay.StartSnapshotCache();
        }
        catch (Exception ex)
        {
            logs.Log(Erbai.Contracts.Logging.LogLevel.Warning, $"overlay 启动失败（继续运行）：{ex.Message}");
        }

        // ── 容器注册（单例装配；接口映射供插件 ServiceResolver 与内部解析使用）──
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<ConfigStore>());
        services.AddSingleton(bus);
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());
        services.AddSingleton(logs);
        services.AddSingleton<ILogBus>(sp => sp.GetRequiredService<LogBus>());
        services.AddSingleton(storage);
        services.AddSingleton<IStorageEngine>(sp => sp.GetRequiredService<SqliteStorageEngine>());
        if (overlay is not null)
        {
            services.AddSingleton(overlay);
        }

        // 插件构造参数解析用的函数服务（原 PluginLoader ServiceResolver 手写映射）
        services.AddSingleton<Func<AppConfig>>(sp => () => sp.GetRequiredService<ConfigStore>().Settings);

        // 点歌链路（SongQueueService 的 Processor/Player 依赖环由启动序列 setter 注入）
        services.AddSingleton(sp =>
        {
            var queue = new SongQueueService(
                sp.GetRequiredService<IStorageEngine>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<ConfigStore>().Settings);
            queue.Logs = sp.GetRequiredService<ILogBus>(); // 队列链路诊断落盘（docs/00 修复记录 #15）
            return queue;
        });
        services.AddSingleton<SongBlacklist>();
        services.AddSingleton(sp => new SearchCoordinator(
            new SearchHttpClient(),
            sp.GetRequiredService<ConfigStore>().Settings.Providers.Enabled,
            sp.GetRequiredService<ConfigStore>().Settings.Search.PreferHot));
        services.AddSingleton<SongRequestService>();
        services.AddSingleton<UserService>();

        // B站平台（阶段 3）：共享 api client + DPAPI 凭据 + 登录服务 + 弹幕插件
        services.AddSingleton<BilibiliApiClient>();
        services.AddSingleton(sp => new BilibiliCredentials(Path.Combine(baseDir, BilibiliCredentials.FileName)));
        services.AddSingleton<BilibiliLoginService>();
        services.AddSingleton<BilibiliLivePlugin>();

        // 抖音平台（阶段 4）：Grabber 子进程宿主 + 弹幕插件
        services.AddSingleton(sp =>
        {
            var current = sp.GetRequiredService<ConfigStore>().Settings;
            return new DouyinGrabberHost(new DouyinGrabberHostOptions
            {
                ExecutablePath = string.IsNullOrWhiteSpace(current.DouyinGrabber.ExecutablePath)
                    ? Path.Combine(baseDir, "DouyinBarrageGrab", "WssBarrageServer.exe")
                    : current.DouyinGrabber.ExecutablePath,
                AppSettings = current.DouyinGrabber.AppSettings,
                WsPort = ParseWsPort(current.DouyinWsUrl),
                ProxyPort = current.DouyinGrabber.AppSettings.GetValueOrDefault("proxyPort") is { } p &&
                            int.TryParse(p.ToString(), out var proxyPort)
                    ? proxyPort
                    : 8827,
                Logs = sp.GetRequiredService<ILogBus>(),
            });
        });
        services.AddSingleton(sp => new DouyinLivePlugin(
            sp.GetRequiredService<ILogBus>(),
            allowedRoomIds: sp.GetRequiredService<ConfigStore>().Settings.DouyinRoomIds.ToHashSet(StringComparer.Ordinal)));

        // 功能模块宿主 + 目录式插件（依赖经 ALC 宿主优先共享解析；ServiceResolver 直通容器）
        services.AddSingleton<ModuleHost>();
        services.AddSingleton(sp => new PluginLoader(new PluginLoaderOptions
        {
            PluginsRoot = Path.Combine(baseDir, "Plugins"),
            Logs = sp.GetRequiredService<ILogBus>(),
            ServiceResolver = type => sp.GetService(type),
        }));

        // 连接器插件管理（插件化改造）：lxmusic 仍由随包发布的 Erbai.Connector.exe 提供；
        // netease/kugou/qqmusic/folia 改由第三方连接器插件提供，由本套组件负责
        // 清单校验 → 下载 → 签名校验 → 解压 → 私有运行时 → 健康检查 → 激活。
        services.AddSingleton(new ConnectorInstallLayout());
        services.AddSingleton(new PrivateDotnetRuntimeLayout());
        // 必须走 ConnectorHttp.Create()：它统一了 5 分钟超时（连接器包 ~7MB、
        // 私有运行时 33–48MB，HttpClient 默认的 100 秒不够稳），并保持与
        // BilibiliApiClient / SearchHttpClient 一致的 UA 约定。
        services.AddSingleton(_ => ConnectorHttp.Create());
        services.AddSingleton(sp => new PrivateDotnetRuntimeManager(
            sp.GetRequiredService<PrivateDotnetRuntimeLayout>(),
            sp.GetRequiredService<HttpClient>(),
            message => sp.GetRequiredService<ILogBus>().Log(LogLevel.Information, message)));
        services.AddSingleton<IPrivateRuntimeProvider>(sp => sp.GetRequiredService<PrivateDotnetRuntimeManager>());

        // 运行时根必须落在该 rid 的私有目录内——P2 预留的注入校验在这里闭合，
        // 让"active.json 被手工改指向别处"无法把连接器引到任意 dotnet.exe 上。
        services.AddSingleton<IConnectorStore>(sp => new ConnectorStore(
            sp.GetRequiredService<ConnectorInstallLayout>(),
            sp.GetRequiredService<PrivateDotnetRuntimeManager>().IsAcceptableRuntimeRoot));

        services.AddSingleton(sp => new ConnectorDownloader(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IConnectorHealthChecker>(_ => new ConnectorHealthChecker());
        services.AddSingleton(sp => new ConnectorInstaller(
            sp.GetRequiredService<ConnectorInstallLayout>(),
            sp.GetRequiredService<IConnectorStore>(),
            sp.GetRequiredService<ConnectorDownloader>(),
            sp.GetRequiredService<IConnectorHealthChecker>(),
            message => sp.GetRequiredService<ILogBus>().Log(LogLevel.Information, message),
            sp.GetRequiredService<IPrivateRuntimeProvider>()));
        services.AddSingleton(sp => new ConnectorCatalogClient(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new ConnectorMaintenance(
            sp.GetRequiredService<ConnectorCatalogClient>(),
            sp.GetRequiredService<ConnectorInstaller>(),
            sp.GetRequiredService<IConnectorStore>(),
            log: message => sp.GetRequiredService<ILogBus>().Log(LogLevel.Information, message)));
        services.AddSingleton(sp => new PlayerConnectorResolver(
            sp.GetRequiredService<ConnectorInstallLayout>(),
            sp.GetRequiredService<IConnectorStore>(),
            baseDir,
            message => sp.GetRequiredService<ILogBus>().Log(LogLevel.Warning, message)));

        services.AddSingleton(sp => new PlatformSupervisor(sp.GetRequiredService<ILogBus>()));
        services.AddSingleton(sp =>
            new OverlayWindowManager(
                sp.GetService<OverlayServer>(),
                sp.GetRequiredService<ILogBus>(),
                sp.GetService<IConfigStore>())); // 配置存储：退出时写回悬浮窗位置（2026-09）

        var provider = services.BuildServiceProvider();

        // ── 启动序列（异步初始化 + 订阅编排，顺序语义与旧组合根一致）──

        var blacklist = provider.GetRequiredService<SongBlacklist>();
        await blacklist.LoadAsync();
        var queue = provider.GetRequiredService<SongQueueService>();
        queue.Processor = provider.GetRequiredService<SearchCoordinator>().ToProcessor();
        var commands = provider.GetRequiredService<SongRequestService>();
        var userService = provider.GetRequiredService<UserService>();

        // 连接器播放器。插件化改造后连接器有两个来源：lxmusic 由随包发布的
        // Erbai.Connector.exe 提供，其余四平台由已安装的第三方插件提供——
        // 具体路径与环境变量由 PlayerConnectorResolver 统一解析（只读盘、不联网）。
        // 未安装时静默降级（队列走估算时长），并在设置页/插件页给出下载指引。
        var connectorResolver = provider.GetRequiredService<PlayerConnectorResolver>();
        var connectorMaintenance = provider.GetRequiredService<ConnectorMaintenance>();

        ConnectorPlayerPlugin? player = null;
        Task<PlayerSnapshot>? playerActivation = null;
        PlayerConnectorResolution resolution = connectorResolver.Resolve(
            settings.Player.Key,
            BuildConnectorEnvironment(settings.Player),
            settings.Player.FoliaToken);

        if (resolution.IsUsable)
        {
            player = new ConnectorPlayerPlugin(
                new ConnectorClient(resolution.ExecutablePath!, resolution.PlayerKey, resolution.Environment),
                resolution.DisplayName);
            queue.Player = player;
            // 连接器激活（启动连接器子进程并握手，可能耗时不短）：
            // 不在此处 await，而是与下方插件加载/模块启动并行（见 WhenAll 汇合点），
            // 首窗更快出现。
            try
            {
                playerActivation = player.ActivateAsync(settings, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logs.Log(Erbai.Contracts.Logging.LogLevel.Warning,
                    $"{resolution.PlayerKey} 连接器激活失败（继续运行）：{ex.Message}");
            }
        }
        else
        {
            logs.Log(Erbai.Contracts.Logging.LogLevel.Warning,
                $"{resolution.PlayerKey} 连接器不可用，点歌将按估算时长播放：{resolution.Detail}");
        }

        // 事件桥接（B站/抖音通用）+ 抖音用户持久化同步
        var bridgeCts = new CancellationTokenSource();
        _ = LiveEventBridge.RunAsync(bus, commands, logs, bridgeCts.Token);
        _ = DouyinUserSync.RunAsync(bus, userService, logs, bridgeCts.Token);

        // 阶段 5 功能模块（排队 / 礼物特效）——目录式插件：
        // 由 PluginLoader 从 Plugins\queueup|giftfx\ 加载注册进 ModuleHost
        // （依赖注入经 ServiceResolver 按构造参数类型解析宿主服务——Erbai.Core 等宿主
        // 程序集走插件 ALC 的宿主优先共享解析，无需拷贝依赖）。启动顺序仍由 ModuleHost
        // KindOrder 保证（功能模块 → 播放器 → 直播平台）。
        var moduleHost = provider.GetRequiredService<ModuleHost>();
        var pluginLoader = provider.GetRequiredService<PluginLoader>();
        var pluginResult = pluginLoader.LoadAll(moduleHost);
        var queueUp = pluginLoader.GetPluginInstance<IQueueUpModule>("queueup");
        if (queueUp is null)
        {
            logs.Log(Erbai.Contracts.Logging.LogLevel.Warning,
                "内置插件 queueup 未加载（Plugins\\queueup 缺失/被禁用/加载失败）——排队队列不可用");
        }

        foreach (var pluginError in pluginResult.Errors)
        {
            logs.Log(Erbai.Contracts.Logging.LogLevel.Warning,
                $"插件加载失败 [{pluginError.DirectoryKey}]: {pluginError.Reason}");
        }

        var moduleContext = new ModuleContext
        {
            EventBus = bus,
            Config = config,
            Storage = storage,
            Logs = logs,
            Overlay = overlay, // IOverlayHub 实现；overlay 启动失败时为 null（模块容忍）
        };
        // 并行汇合：功能模块启动 与 连接器激活 并行推进；连接器激活失败仅记
        // 日志、不拖垮启动（原语义：激活失败降级继续）。局部函数封装异步异常捕获。
        async Task AwaitPlayerActivationAsync()
        {
            if (playerActivation is null)
            {
                return;
            }

            try
            {
                await playerActivation;
            }
            catch (Exception ex)
            {
                logs.Log(Erbai.Contracts.Logging.LogLevel.Warning,
                    $"{settings.Player.Key} 连接器激活失败（继续运行）：{ex.Message}");
            }
        }

        var moduleStartTask = moduleHost.StartAllAsync(moduleContext);
        var playerStartTask = AwaitPlayerActivationAsync();
        await Task.WhenAll(moduleStartTask, playerStartTask);

        // 平台监督者（阶段 4）：统一管理平台监听循环，单平台重启隔离。
        // runner 每次启动读 config.Settings 现值（不捕获 CreateAsync 时的快照）——
        // 概览页先 PersistAsync 再启动，若 runner 用旧快照，改房间号/白名单后
        // 点启动仍按旧值连（docs/00 阶段 4 修复记录 #2）。
        var bilibili = provider.GetRequiredService<BilibiliLivePlugin>();
        var grabberHost = provider.GetRequiredService<DouyinGrabberHost>();
        var douyin = provider.GetRequiredService<DouyinLivePlugin>();
        var supervisor = provider.GetRequiredService<PlatformSupervisor>();
        supervisor.RegisterRunner("bilibili", async ct =>
        {
            await bilibili.StartAsync(config.Settings, ct);
            // 平台事件流 → EventBus 桥接（LiveEventBridge / 弹幕日志页都订阅 EventBus）。
            // 必须在 StartAsync 之后读取 Events——StartAsync 会重建事件通道
            var forward = ModuleLoops.RunConsumerLoopAsync(
                bilibili.Events,
                (evt, _) =>
                {
                    bus.Publish(evt);
                    liveLog?.Write(evt); // 直播事件落盘（日志页回看）
                    return Task.CompletedTask;
                },
                logs,
                "bilibili.events",
                ct);
            try
            {
                await forward;
            }
            finally
            {
                await bilibili.StopAsync();
            }
        });
        supervisor.RegisterRunner("douyin", async ct =>
        {
            // 按最新配置刷新抓包器（端口/白名单键）与插件房间白名单
            var current = config.Settings;
            grabberHost.UpdateConfig(
                current.DouyinGrabber.AppSettings,
                ParseWsPort(current.DouyinWsUrl),
                current.DouyinGrabber.AppSettings.GetValueOrDefault("proxyPort") is { } p &&
                int.TryParse(p.ToString(), out var proxyPort)
                    ? proxyPort
                    : 8827);
            douyin.UpdateRoomFilter(current.DouyinRoomIds);

            var grabberStatus = await grabberHost.StartAsync(ct);
            if (grabberStatus.Error.Length > 0)
            {
                throw new InvalidOperationException($"抖音抓包器启动失败：{grabberStatus.Error}");
            }

            await douyin.StartAsync(current, ct);
            var forward = ModuleLoops.RunConsumerLoopAsync(
                douyin.Events,
                (evt, _) =>
                {
                    bus.Publish(evt);
                    liveLog?.Write(evt); // 直播事件落盘（日志页回看）
                    return Task.CompletedTask;
                },
                logs,
                "douyin.events",
                ct);
            try
            {
                await forward;
            }
            finally
            {
                await douyin.StopAsync();
                await grabberHost.StopAsync();
            }
        });

        // 启动后台恢复登录态：读取 DPAPI 凭据文件 → nav 校验 → 需刷新自动刷新。
        // interactive:false = 静默（无凭据/失效不弹扫码，保持匿名），失败不拖垮启动。
        var login = provider.GetRequiredService<BilibiliLoginService>();
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await login.EnsureLoginAsync(interactive: false, ct: CancellationToken.None);
                if (result is not null)
                {
                    logs.Log(Erbai.Contracts.Logging.LogLevel.Information,
                        $"[登录] 启动恢复登录态：{result.AccountName}（直播间 {result.LiveRoomId ?? "无"}）");
                }
            }
            catch (Exception ex)
            {
                logs.Log(Erbai.Contracts.Logging.LogLevel.Warning, $"[登录] 启动恢复登录态失败（保持匿名）：{ex.Message}");
            }
        });

        await queue.StartAsync();

        // 悬浮窗治理（决策 #17）：按配置启停三类悬浮窗；overlay 未启动时 manager 空转不建窗
        var overlayWindows = provider.GetRequiredService<OverlayWindowManager>();
        try
        {
            overlayWindows.ApplyConfig(config.Settings);
        }
        catch (Exception ex)
        {
            logs.Log(Erbai.Contracts.Logging.LogLevel.Warning, $"悬浮窗初始应用失败（继续运行）：{ex.Message}");
        }

        var app = new AppServices(
            provider, player, queueUp, bridgeCts, liveLog,
            connectorResolver, connectorMaintenance,
            provider.GetRequiredService<ConnectorInstallLayout>(),
            provider.GetRequiredService<ConnectorInstaller>(),
            provider.GetRequiredService<ConnectorCatalogClient>());

        // 连接器插件后台维护（30 分钟一轮）。刻意放在启动序列末尾、且首轮 dueTime = 周期：
        // 启动瞬间不联网、不与首窗抢磁盘与网络；此后每轮只在有"可自动应用的更新"时才下载。
        // 关停由 AppServices.DisposeAsync 负责。
        connectorMaintenance.Start();
        app._moduleContext = moduleContext; // 插件热启用（SetEnabledAsync）复用
        logs.Log(Erbai.Contracts.Logging.LogLevel.Information,
            $"组合根装配完成：overlay={app.OverlayUrl} 播放器={player?.Key ?? "(无)"} " +
            $"B站登录={login.CurrentCredentials?.HasLogin == true} 抖音Grabber={Path.GetFileName(grabberHost.ExecutablePath)} " +
            $"功能模块={string.Join("+", moduleHost.Modules.Select(m => m.Key))} " +
            $"插件={pluginResult.Loaded.Count} 个(禁用 {pluginResult.Disabled.Count}/失败 {pluginResult.Errors.Count})");
        return app;
    }

    /// <summary>
    /// 播放器配置 → <b>内置连接器（lxmusic）</b>的子进程环境变量（连接器侧经
    /// <c>*Options.FromEnvironment()</c> 读取；不落盘）。
    /// </summary>
    /// <remarks>
    /// 只负责 lxmusic 的那几个 <c>LX_*</c> 开关。Folia token 不在这里——
    /// 它属于 folia 插件，由 <see cref="PlayerConnectorResolver"/> 在解析插件时注入，
    /// 免得把凭据塞进一个"给所有连接器都用"的环境字典里。
    /// </remarks>
    private static IReadOnlyDictionary<string, string> BuildConnectorEnvironment(PlayerConfig player)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LX_HTTP_URL"] = player.Lxmusic.HttpUrl,
            ["LX_HTTP_ENABLED"] = player.Lxmusic.HttpEnabled ? "true" : "false",
            ["LX_SSE_ENABLED"] = player.Lxmusic.SseEnabled ? "true" : "false",
            ["LX_USE_HTTP_CONTROL"] = player.Lxmusic.UseHttpControl ? "true" : "false",
        };
    }

    private static int ParseWsPort(string wsUrl)
    {
        try
        {
            var uri = new Uri(wsUrl);
            // Uri.Port 对未显式带端口的 ws:// URL 返回 scheme 默认值 80——
            // 必须按"是否显式端口"判断，否则兜底 8888 失效（docs/00 修复记录 #10）
            if (!uri.IsDefaultPort)
            {
                return uri.Port;
            }

            return 8888;
        }
        catch (Exception)
        {
            return 8888;
        }
    }

    // ── 播放器热切换（设置页保存后调用；旧连接器停用释放，队列立即改用新连接器）──

    /// <summary>
    /// 切换音乐播放器连接器：新连接器先激活成功再接管（失败保持原状不中断点歌）。
    /// 正在播放的歌曲随旧连接器释放而按失败跳过，下一首起走新播放器。
    /// </summary>
    /// <remarks>
    /// 目标连接器未安装时<b>不切换</b>，而是返回带下载指引的说明（决策 D-D）——
    /// 静默切成一个拉不起来的播放器比留在旧播放器上糟糕得多。
    /// </remarks>
    public async Task<string> SwitchPlayerAsync(string playerKey)
    {
        var settings = Config.Settings;
        PlayerConnectorResolution resolution = ConnectorPlayerResolver.Resolve(
            playerKey,
            BuildConnectorEnvironment(settings.Player),
            settings.Player.FoliaToken);

        if (!resolution.IsUsable)
        {
            Logs.Log(Erbai.Contracts.Logging.LogLevel.Warning,
                $"播放器 {playerKey} 不可用，未切换：{resolution.Detail}");

            return $"{resolution.DisplayName} 连接器不可用，播放器未切换：{resolution.Detail}";
        }

        var old = Player;
        ConnectorPlayerPlugin? next = null;
        try
        {
            next = new ConnectorPlayerPlugin(
                new ConnectorClient(resolution.ExecutablePath!, resolution.PlayerKey, resolution.Environment),
                resolution.DisplayName);
            await next.ActivateAsync(settings, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logs.Log(Erbai.Contracts.Logging.LogLevel.Warning,
                $"{playerKey} 连接器激活失败（保持原播放器 {old?.Key ?? "无"}）：{ex.Message}");
            if (next is not null)
            {
                await next.DisposeAsync();
            }

            return $"切换失败：{ex.Message}（继续使用 {old?.DisplayName ?? "无连接器"}）";
        }

        Player = next;
        Queue.Player = next;
        if (old is not null)
        {
            try
            {
                await old.DeactivateAsync();
            }
            catch (Exception ex)
            {
                Logs.Log(Erbai.Contracts.Logging.LogLevel.Warning, $"旧播放器 {old.Key} 停用异常（忽略）：{ex.Message}");
            }

            await old.DisposeAsync();
        }

        Logs.Log(Erbai.Contracts.Logging.LogLevel.Information, $"播放器已热切换：{playerKey}");
        return $"播放器已切换：{next.DisplayName}（{playerKey}）";
    }

    // ── 平台启停（概览页；经平台监督者统一管理） ────────────────────────────

    /// <summary>启动 B站弹幕监听；配置 RoomId 为空时抛错（UI 提示先填房间号）。</summary>
    public async Task<string> StartBilibiliAsync()
    {
        if (string.IsNullOrWhiteSpace(Config.Settings.RoomId))
        {
            BilibiliStatus = "启动失败：请先在设置中填写 B站房间号";
            return BilibiliStatus;
        }

        try
        {
            var result = await Supervisor.StartAsync("bilibili");
            BilibiliStatus = result.Running ? "运行中" : $"启动失败：{result.Error}";
            if (result.Error.Length > 0)
            {
                Logs.Log(Erbai.Contracts.Logging.LogLevel.Warning, $"[B站] 启动失败：{result.Error}");
            }

            return BilibiliStatus;
        }
        catch (Exception ex)
        {
            BilibiliStatus = $"启动失败：{ex.Message}";
            Logs.Log(Erbai.Contracts.Logging.LogLevel.Warning, $"[B站] 启动失败：{ex.Message}");
            return BilibiliStatus;
        }
    }

    public async Task StopBilibiliAsync()
    {
        await Supervisor.StopAsync("bilibili");
        BilibiliStatus = "已停止";
        Logs.Log(Erbai.Contracts.Logging.LogLevel.Information, "[B站] 平台已停止");
    }

    /// <summary>启动抖音监听（Grabber 子进程 + WS 插件）。</summary>
    public async Task<string> StartDouyinAsync()
    {
        if (!File.Exists(GrabberHost.ExecutablePath))
        {
            DouyinStatus = $"启动失败：抖音抓包器不存在 {GrabberHost.ExecutablePath}";
            Logs.Log(Erbai.Contracts.Logging.LogLevel.Warning, DouyinStatus);
            return DouyinStatus;
        }

        try
        {
            var result = await Supervisor.StartAsync("douyin");
            DouyinStatus = result.Running ? "运行中" : $"启动失败：{result.Error}";
            if (result.Error.Length > 0)
            {
                Logs.Log(Erbai.Contracts.Logging.LogLevel.Warning, $"[抖音] 启动失败：{result.Error}");
            }

            return DouyinStatus;
        }
        catch (Exception ex)
        {
            DouyinStatus = $"启动失败：{ex.Message}";
            Logs.Log(Erbai.Contracts.Logging.LogLevel.Warning, $"[抖音] 启动失败：{ex.Message}");
            return DouyinStatus;
        }
    }

    public async Task StopDouyinAsync()
    {
        await Supervisor.StopAsync("douyin");
        DouyinStatus = "已停止";
        Logs.Log(Erbai.Contracts.Logging.LogLevel.Information, "[抖音] 平台已停止");
    }

    /// <summary>
    /// 注入一条弹幕（本机 UI 手动输入框 / 测试按钮；真实弹幕走平台适配器，不经过这里）。
    /// <b>必须带主播特权</b>：本机操作者就是主播本人，而 QueuePage / OverviewPage 的
    /// 手动输入框明确支持「切歌」「设置管理员@XX」等管理命令（两处注释均写明）。
    /// 此前注入的上下文不带任何特权标志，手动输入「切歌」会被权限门一律拒掉
    /// （2026-09-18 用户实测「主播无法切歌」的本机路径）。
    /// </summary>
    public Task<bool> InjectDanmakuAsync(string text, string nickname = "测试观众")
    {
        var ctx = new DanmakuContext
        {
            Text = text,
            Nickname = nickname,
            Platform = "douyin",
            RoomId = "1",
            UserId = $"test-{Environment.TickCount64}",
            IsAnchor = true,
        };
        return Commands.HandleMessageAsync(ctx);
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // 幂等：Closing 事件（系统关机/任务栏关闭）可能并发触发两次
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _bridgeCts.Cancel();
        _bridgeCts.Dispose();

        // 先停连接器后台维护：它会在后台发起下载/解压，必须在进程退出前停掉，
        // 否则可能在磁盘写入中途被强杀，留下 staging 残留。
        ConnectorMaintenance.Dispose();

        // 先停功能模块（QueueUp/GiftFx 消费者 + 目录式插件已随 StartAllAsync 注册进同一宿主）
        await Modules.StopAllAsync();

        // Stage A：插件与内置模块同宿主，已随 StopAllAsync 停止；此处移除注册并回收各插件 ALC
        var pluginLoader = _provider.GetService<PluginLoader>();
        if (pluginLoader is not null)
        {
            await pluginLoader.UnloadAllAsync(Modules);
            await pluginLoader.DisposeAsync();
        }

        // 先停平台监督者（取消 runner → 插件/抓包器随 finally 释放），再停其余依赖
        await Supervisor.StopAllAsync();

        if (BiliApi is not null)
        {
            await BiliApi.DisposeAsync();
        }

        // 悬浮窗先关（还依赖 overlay 页面/端口），再停 Overlay 服务（决策 #17）
        if (OverlayWindows is not null)
        {
            await OverlayWindows.DisposeAsync();
        }

        if (Overlay is not null)
        {
            await Overlay.DisposeAsync();
        }

        if (Player is not null)
        {
            await Player.DeactivateAsync();
        }

        await Queue.StopAsync();
        await Storage.DisposeAsync();
        _liveLog?.Dispose();
        Logs.Dispose();
        // 容器不参与释放：以上已按依赖逆序显式释放全部单例，调 _provider.DisposeAsync()
        // 会双重释放（SqliteStorageEngine/LogBus 等已在此 dispose）。
    }
}
