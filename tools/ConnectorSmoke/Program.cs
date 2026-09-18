// P7 端到端验收：走**真实产品代码路径**（ConnectorHttp → 清单 → 下载 → 签名校验 → 解压 →
// 私有运行时 → 健康检查 → active.json → PlayerConnectorResolver → ConnectorClient → 能力协商）
// 安装并启动一个**真实的上游第三方连接器插件**。
//
// 为什么必须有这个工具：管理内核的 297 个单测**全部使用合成夹具**，从不碰真实上游。
// 于是"夹具与真实上游之间的落差"在单测里是**结构性不可见**的。P7 期间就撞上过这样一个落差：
// 曾观察到上游对无 UA 请求返回 403，据此怀疑产品"完全无法安装连接器"；后来该现象**未能复现**
// （见下方对照探测）。本工具存在的意义就是给这类落差一个**可执行的发现手段**——
// 结论对错都要能跑出来，而不是靠读代码猜。
//
// 用法：
//   dotnet run --project tools/ConnectorSmoke -- netease
//   dotnet run --project tools/ConnectorSmoke -- kugou            # 验 win-x86 私有运行时
//   dotnet run --project tools/ConnectorSmoke -- netease --keep   # 保留安装目录供排查
//
// 退出码：0 通过 / 1 失败 / 2 用法或环境错误。
using Erbai.Connectors.Management;
using Erbai.Connectors.Management.Catalog;
using Erbai.Connectors.Management.Runtime;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Players;
using Erbai.Player.Connectors;

const int ExitPass = 0;
const int ExitFail = 1;
const int ExitUsage = 2;

try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch
{
    // 输出被重定向到管道时可能不支持设置编码；不影响功能，只是中文可能显示为乱码。
}

// ───────────────────────── 参数 ─────────────────────────
string? playerKey = null;
string? installRootArg = null;
string? runtimeRootArg = null;
var keep = false;
var skipControl = false;

try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--keep":
                keep = true;
                break;
            case "--skip-control":
                skipControl = true;
                break;
            case "--root":
                installRootArg = Require(args, ++i, "--root");
                break;
            case "--runtime-root":
                runtimeRootArg = Require(args, ++i, "--runtime-root");
                break;
            case "-h" or "--help":
                PrintUsage();
                return ExitPass;
            default:
                if (args[i].StartsWith('-'))
                {
                    Console.Error.WriteLine($"未知参数：{args[i]}");
                    PrintUsage();
                    return ExitUsage;
                }

                playerKey ??= args[i];
                break;
        }
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    PrintUsage();
    return ExitUsage;
}

playerKey ??= "netease";

if (!ConnectorPlayers.IsPluginPlayerKey(playerKey))
{
    Console.Error.WriteLine(
        $"'{playerKey}' 不是插件平台。合法值：{string.Join(" / ", ConnectorPlayers.PluginPlayerKeys)}。" +
        "（lxmusic 是随包发布的内置连接器，不走安装管线，故不在本工具范围内。）");
    return ExitUsage;
}

// 安装根默认用临时目录：本工具会在里面落盘一个真实连接器，不该污染用户真实的
// %LOCALAPPDATA%\ErbaiLiveTool\player-connectors。
var ownsInstallRoot = installRootArg is null;
var root = installRootArg ?? Path.Combine(Path.GetTempPath(), "erbai-conn-smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

// 私有运行时根默认用**产品真实位置**：约 50MB 一次下载，放默认位置才有缓存、
// 才能反映"用户第二次启动"的真实耗时。要完全隔离就显式传 --runtime-root。
var runtimeLayout = new PrivateDotnetRuntimeLayout(runtimeRootArg);

Console.WriteLine("═══ 连接器插件端到端验收（P7）═══");
Console.WriteLine($"  平台           : {playerKey}");
Console.WriteLine($"  安装根         : {root}{(ownsInstallRoot ? "（临时）" : "（显式指定）")}");
Console.WriteLine($"  私有运行时根   : {runtimeLayout.Root}");
Console.WriteLine();

// ───────────────────────── 对照：User-Agent ─────────────────────────
if (!skipControl)
{
    var controlResult = await RunUserAgentControlAsync();
    if (controlResult != ExitPass)
    {
        return controlResult;
    }
}

// ───────────────────────── 组装真实组件 ─────────────────────────
// ★ 唯一的 HttpClient 来源就是 ConnectorHttp.Create()。若把它换成裸 new HttpClient()，
//   下面第 1 步会在真实上游上直接 403 —— 这正是本工具要守住的回归点。
using var http = ConnectorHttp.Create();

var layout = new ConnectorInstallLayout(root);
var runtimeManager = new PrivateDotnetRuntimeManager(runtimeLayout, http, Log);
var store = new ConnectorStore(layout, runtimeManager.IsAcceptableRuntimeRoot);
var downloader = new ConnectorDownloader(http);
var healthChecker = new ConnectorHealthChecker();
var installer = new ConnectorInstaller(layout, store, downloader, healthChecker, Log, runtimeManager);
var catalog = new ConnectorCatalogClient(http);

// ───────────────────────── 1/6 清单 ─────────────────────────
Console.WriteLine("[1/6] 拉取上游 v2 清单并校验");
ConnectorCatalogSnapshot snapshot;
try
{
    snapshot = await catalog.GetSnapshotAsync(forceRefresh: true, CancellationToken.None);
}
catch (Exception ex)
{
    Fail($"清单不可达或校验失败：{ex.GetType().Name}: {ex.Message}");
    return ExitFail;
}

var entry = snapshot.Find(playerKey);
if (entry is null)
{
    Fail($"清单里没有可用的 {playerKey}。");
    var reason = snapshot.FindRejection(playerKey);
    if (reason is not null)
    {
        Console.WriteLine($"      该条目被逐条拒绝：{reason}");
    }

    foreach (var rejection in snapshot.Rejected)
    {
        Console.WriteLine($"      其它被拒条目：{rejection.PlayerKey} —— {rejection.Reason}");
    }

    return ExitFail;
}

var package = entry.Package;
Pass($"命中 {playerKey} v{entry.Version}  protocolVersion={entry.ProtocolVersion}");
Console.WriteLine($"      deployment={package?.Deployment}  runtime={package?.Runtime}  channel={package?.RuntimeChannel}");
Console.WriteLine($"      asset={package?.Asset}");
Console.WriteLine($"      size={package?.Size}  sha256={Shorten(package?.Sha256)}");

if (package?.Deployment != "framework-dependent")
{
    Fail($"本工具只验 framework-dependent（私有运行时路径），实际是 '{package?.Deployment}'。");
    return ExitFail;
}

// ───────────────────────── 2/6 安装（下载 + 校验 + 解压 + 运行时 + 健康检查 + 激活）─────────────────────────
Console.WriteLine();
Console.WriteLine("[2/6] 安装（下载 → SHA-256 → Ed25519 → 解压 → 私有运行时 → 健康检查 → active.json）");

var lastPercent = -1;
var progress = new Progress<ConnectorDownloadProgress>(p =>
{
    // 每 10% 打一行，避免刷屏
    var bucket = p.Percent / 10;
    if (bucket == lastPercent)
    {
        return;
    }

    lastPercent = bucket;
    Console.WriteLine($"      下载 {p.Percent}%  ({p.Received}/{p.Total} 字节)");
});

ConnectorInstallResult install;
try
{
    install = await installer.InstallAsync(playerKey, entry, progress: progress, cancellationToken: CancellationToken.None);
}
catch (Exception ex)
{
    Fail($"安装失败：{ex.GetType().Name}: {ex.Message}");
    return ExitFail;
}

Pass($"安装完成 v{install.Version}  reused={install.ReusedExisting}  verified={install.Verified}");
Console.WriteLine($"      可执行文件：{install.ExecutablePath}");
if (!File.Exists(install.ExecutablePath))
{
    Fail("安装报告成功但可执行文件不存在。");
    return ExitFail;
}

// ───────────────────────── 3/6 active.json ─────────────────────────
Console.WriteLine();
Console.WriteLine("[3/6] 读回 active.json（记录自校验）");
var active = store.ReadActive(playerKey);
if (active is null)
{
    Fail("active.json 写出去后读不回来（记录自校验不通过）。");
    return ExitFail;
}

Pass($"记录可读：id={active.Id} version={active.Version} deployment={active.Deployment} rid={active.RuntimeRid} verified={active.Verified}");
Console.WriteLine($"      executable={active.Executable}");
Console.WriteLine($"      runtimeRoot={active.RuntimeRoot}");

if (active.RuntimeRid is null || active.RuntimeRoot is null)
{
    Fail("framework-dependent 记录缺少 runtimeRid / runtimeRoot —— P3 的运行时注入没有闭合。");
    return ExitFail;
}

if (!runtimeManager.IsAcceptableRuntimeRoot(active.RuntimeRid, active.RuntimeRoot))
{
    Fail($"runtimeRoot 未通过注入校验：{active.RuntimeRoot}");
    return ExitFail;
}

Pass("runtimeRoot 落在该 rid 的私有目录内且含 dotnet.exe（注入校验通过）");

// ───────────────────────── 4/6 Resolver ─────────────────────────
Console.WriteLine();
Console.WriteLine("[4/6] PlayerConnectorResolver 解析（设置页/启动序列实际走的就是这里）");

// baseDirectory 指向临时目录：本工具不验内置 lxmusic，那里没有 Erbai.Connector.exe 也无所谓。
var resolver = new PlayerConnectorResolver(layout, store, Path.Combine(root, "_appdir"), Log);
var resolution = resolver.Resolve(playerKey);

Pass($"Availability={resolution.Availability}  Version={resolution.Version}");
Console.WriteLine($"      ExecutablePath={resolution.ExecutablePath}");
Console.WriteLine($"      注入环境变量：{string.Join(", ", resolution.Environment.Select(kv => $"{kv.Key}={kv.Value}"))}");

if (!resolution.IsUsable)
{
    Fail($"解析结果不可用：{resolution.Detail}");
    return ExitFail;
}

if (resolution.Availability != PlayerConnectorAvailability.Installed)
{
    Fail($"Availability 应为 Installed，实际 {resolution.Availability}。");
    return ExitFail;
}

if (resolution.Environment.Count == 0)
{
    Fail("framework-dependent 插件解析后没有注入任何环境变量——私有运行时没接上。");
    return ExitFail;
}

// ───────────────────────── 5/6 启动 + 能力协商 ─────────────────────────
Console.WriteLine();
Console.WriteLine("[5/6] 启动连接器并做能力协商（全公开 API 路径）");

var caps = PlayerCapabilities.None;
try
{
    await using var client = new ConnectorClient(resolution.ExecutablePath!, playerKey, resolution.Environment);
    var plugin = new ConnectorPlayerPlugin(client, resolution.DisplayName);

    var snapshotFromPlayer = await plugin.ActivateAsync(AppConfig.CreateDefault(), CancellationToken.None);
    caps = plugin.Capabilities;

    Console.WriteLine($"      ping: protocolVersion={client.Ping.ProtocolVersion} connectorId={client.Ping.ConnectorId} connectorVersion={client.Ping.ConnectorVersion}");
    Console.WriteLine($"      features={string.Join(",", client.Ping.Features)}");
    Console.WriteLine($"      capabilities.search={client.Ping.Capabilities.Search} insertNext={client.Ping.Capabilities.InsertNext} pause={client.Ping.Capabilities.Pause} resume={client.Ping.Capabilities.Resume}");
    Console.WriteLine($"      probe: connected={snapshotFromPlayer.Connected}（播放器未运行 → false 属正常）");
    Pass($"推导出的 PlayerCapabilities = {caps}");

    await plugin.DeactivateAsync();
}
catch (Exception ex)
{
    Fail($"启动/协商失败：{ex.GetType().Name}: {ex.Message}");
    // 完整堆栈是这类"真实上游落差"唯一可靠的定位手段——只打 Message 会退化成猜。
    Console.WriteLine();
    Console.WriteLine("      ── 完整异常 ──");
    foreach (var line in ex.ToString().Split('\n'))
    {
        Console.WriteLine("      " + line.TrimEnd());
    }

    return ExitFail;
}

// 硬性条件：缺 QueueProgrammable 会让插播对账守卫**整体静默旁路**
// （PlaybackStateMachine 判据，无报错、无日志），所以必须硬失败。
if (!caps.HasFlag(PlayerCapabilities.QueueProgrammable))
{
    Fail("能力面缺 QueueProgrammable —— 插播对账守卫会被整体静默旁路（PlaybackStateMachine 判据）。");
    return ExitFail;
}

Pass("QueueProgrammable 在位：插播对账守卫生效，不会被旁路");

// 软性条件：缺 SnapshotEvents 只是回退 Probe 轮询（ConnectorPlayerPlugin.WatchSnapshotsAsync
// 按能力返回 null），IMusicPlayerPlugin 也明确把它标为「可选能力」——属受支持的降级，不是故障。
// kugou 实测 features 就是空数组，若把它当失败就会误报。
Console.WriteLine(caps.HasFlag(PlayerCapabilities.SnapshotEvents)
    ? "      · 快照事件流可用（snapshot-events-v1）。"
    : "      · 未声明 snapshot-events-v1 —— 宿主回退 Probe 轮询（受支持的降级，非故障）。");

// 其余能力仅作展示，不参与判定：
//   PauseResume 取决于播放器客户端自身限制（kugou 实测 pause/resume=false）；
//   Search 与宿主自带的三源搜索无关（IMusicPlayerPlugin.SearchAsync 无生产调用者）。

// ───────────────────────── 6/6 卸载清理 ─────────────────────────
Console.WriteLine();
Console.WriteLine("[6/6] 卸载与清理");
store.DeleteActive(playerKey);
Pass(store.ReadActive(playerKey) is null ? "active.json 已删除，记录读回为「未安装」" : "active.json 未能删除");

if (ownsInstallRoot && !keep)
{
    try
    {
        Directory.Delete(root, recursive: true);
        Pass("临时安装根已删除");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"      （临时目录删除失败，可手工清理：{root}）{ex.GetType().Name}: {ex.Message}");
    }
}
else
{
    Console.WriteLine($"      保留安装根供排查：{root}");
}

Console.WriteLine();
Console.WriteLine($"PASS: {playerKey} 连接器插件「清单 → 安装 → 解析 → 启动 → 能力协商」端到端打通");
return ExitPass;

// ───────────────────────── 本地函数 ─────────────────────────

static void Log(string message) => Console.WriteLine($"      · {message}");

static void Pass(string message) => Console.WriteLine($"  ✔ {message}");

static void Fail(string message) => Console.WriteLine($"  ✘ {message}");

static string Shorten(string? hash) =>
    string.IsNullOrEmpty(hash) ? "(无)" : hash.Length <= 16 ? hash : hash[..16] + "…";

static string Require(string[] argv, int index, string name)
{
    if (index >= argv.Length)
    {
        throw new ArgumentException($"{name} 缺少取值。");
    }

    return argv[index];
}

static string DescribeStatus(int status) => status < 0 ? "请求失败" : status.ToString();

static async Task<int> ProbeCatalogStatusAsync(HttpClient http)
{
    using (http)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ConnectorCatalogClient.DefaultCatalogUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return (int)response.StatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      （请求异常：{ex.GetType().Name}: {ex.Message}）");
            return -1;
        }
    }
}

static async Task<int> RunUserAgentControlAsync()
{
    Console.WriteLine("[对照] 无 UA 请求是否被上游拒绝（P7 期间曾观察到 403，后未能复现）");

    var bare = await ProbeCatalogStatusAsync(new HttpClient());
    var withUa = await ProbeCatalogStatusAsync(ConnectorHttp.Create());

    Console.WriteLine($"      裸 new HttpClient()（无 UA）    → HTTP {DescribeStatus(bare)}");
    Console.WriteLine($"      ConnectorHttp.Create()（带 UA） → HTTP {DescribeStatus(withUa)}");

    if (withUa != 200)
    {
        Fail($"带 UA 也拿不到清单（HTTP {DescribeStatus(withUa)}）——上游不可达或被拒，后续步骤必然失败。");
        return ExitFail;
    }

    if (bare == 403)
    {
        // 这次复现了：值得警惕，但**仍不能**直接断定"上游强制要求 UA"——
        // 本机出网经 HTTP 代理，403 也可能来自代理或 Cloudflare 边缘。
        Console.WriteLine("      注意：本次无 UA 复现了 403。这仍不足以断定上游强制要求 UA"
            + "（本机出网经代理，403 也可能来自代理/边缘）。");
    }
    else
    {
        Console.WriteLine($"      无 UA 本次返回 {DescribeStatus(bare)} —— "
            + "说明上游当前**不**强制要求 UA。ConnectorHttp 保留 UA 是与项目既有约定保持一致，"
            + "而非修复某个已确认的故障。");
    }

    Console.WriteLine();
    return ExitPass;
}

static void PrintUsage()
{
    Console.WriteLine("""
        连接器插件端到端验收（P7）

        用法：
          dotnet run --project tools/ConnectorSmoke -- [平台] [选项]

        平台（缺省 netease）：
          netease    win-x64   本机有 x64 .NET 8，可直接验完整链路
          kugou      win-x86   验证私有 x86 运行时确实解决了 hostfxr 解析失败
          qqmusic    win-x86   同上
          folia      win-x86   同上；另需 BILINCM_FOLIA_TOKEN 才能连上播放器

        选项：
          --root <目录>          连接器安装根（缺省：临时目录，跑完删除）
          --runtime-root <目录>  私有 .NET 运行时根（缺省：产品真实位置 %LOCALAPPDATA%）
          --keep                 保留临时安装根，便于事后排查
          --skip-control         跳过 User-Agent 对照探测
          -h, --help             显示本帮助

        退出码：0 通过 / 1 失败 / 2 用法或环境错误
        """);
}
