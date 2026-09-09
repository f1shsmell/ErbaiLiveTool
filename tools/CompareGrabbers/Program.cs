// 阶段4 退出标准：新旧 Grabber 并行对比（docs/04 §3.1「新旧 exe 并行抓同一房间，对比输出报文」）。
//
// 原理：弹幕流是房间级广播，同一房间所有客户端收到同一份弹幕。旧 Grabber（系统代理 8827，
// 抓直播伴侣/系统流量）与新 Grabber（代理 18827，headless Edge 显式走该代理）并行抓取，
// 比对 {Type,Data} 报文面（Type 分布、Data 字段键集合、弹幕内容对齐）。
//
// 用法:
//   dotnet run --project tools/CompareGrabbers -- <直播间号> [抓取秒数] [--old-exe <路径>] [--new-exe <路径>]
//
// 注意（副作用）：
// - 旧 Grabber 会改写系统代理（8827）并可能安装/复用根证书；工具结束时按铁律还原系统代理
//   （仅当当前代理仍指向 8827/18827 才还原快照，绝不碰用户自己的代理）；
// - 需要 Edge 或 Chrome 可用（headless 打开直播间走新代理）；
// - 直播伴侣在跑时旧端才有数据；否则旧端计数为 0（报告中注明）。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Erbai.Live.Douyin.Hosting;
using Erbai.Live.Douyin.Messages;

var roomId = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "1";
var durationSeconds = 120;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--duration" && int.TryParse(args[i + 1], out var d))
    {
        durationSeconds = Math.Clamp(d, 20, 600);
    }
}

string? oldExe = ArgValue(args, "--old-exe");
string? newExe = ArgValue(args, "--new-exe");
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
oldExe ??= @"C:\Dev\diange\vendor\DouyinBarrageGrab\WssBarrageServer.exe";
newExe ??= Path.Combine(repoRoot, "src", "Erbai.Live.Douyin.Grabber", "bin", "Debug", "net8.0-windows", "WssBarrageServer.exe");

const int NewWsPort = 18888;
const int NewProxyPort = 18827;
const int OldWsPort = 8888;

Console.WriteLine("=== 新旧 Grabber 并行对比（阶段4 退出标准） ===");
Console.WriteLine($"  房间: {roomId}  时长: {durationSeconds}s");
Console.WriteLine($"  旧 Grabber: {oldExe}");
Console.WriteLine($"  新 Grabber: {newExe}");
Console.WriteLine($"  旧端 WS: ws://127.0.0.1:{OldWsPort}（系统代理 {8827}，抓直播伴侣/系统流量）");
Console.WriteLine($"  新端 WS: ws://127.0.0.1:{NewWsPort}（代理 {NewProxyPort}，headless Edge 显式走该代理）");
if (!File.Exists(oldExe))
{
    Console.WriteLine($"FAIL: 旧 Grabber 不存在: {oldExe}");
    return 2;
}

if (!File.Exists(newExe))
{
    Console.WriteLine($"FAIL: 新 Grabber 不存在（先构建）: {newExe}");
    return 2;
}

var browser = FindBrowser();
if (browser is null)
{
    Console.WriteLine("FAIL: 未找到 Edge/Chrome（headless 抓取需要）");
    return 2;
}

// ── 系统代理快照（铁律：结束时仅当仍指向抓包器端口才还原） ────────────────
var proxy = new WinInetProxyStore();
var proxySnapshot = proxy.Read();
Console.WriteLine($"  当前系统代理: enable={proxySnapshot.ProxyEnable} server={proxySnapshot.ProxyServer}");

var newConfig = new Dictionary<string, object>
{
    ["sysProxy"] = false,               // 新端走显式代理的 Edge，不碰系统代理
    ["proxyPort"] = NewProxyPort,
    ["wsListenPort"] = NewWsPort,
    ["pushFilter"] = "1,2,3,4,5,6,7,8,9", // 对比要全 Type（旧端 pushFilter=1,4,5,7 只推子集）
    ["printBarrage"] = false,
    ["hideConsole"] = true,
};

var grabbers = new List<IAsyncDisposable>();
var oldProcess = (Process?)null;
Process? browserProcess = null;
string userData = Path.Combine(Path.GetTempPath(), $"erbai-cmp-edge-{Guid.NewGuid():N}");
var oldMessages = new ConcurrentQueue<(int Type, string Data)>();
var newMessages = new ConcurrentQueue<(int Type, string Data)>();

try
{
    // ── 启动新 Grabber（宿主管理：配置下发 + 端口就绪 + 日志转发） ───────────
    var newHost = new DouyinGrabberHost(new DouyinGrabberHostOptions
    {
        ExecutablePath = newExe,
        AppSettings = newConfig,
        WsPort = NewWsPort,
        ProxyPort = NewProxyPort,
        ProxyStore = proxy,
        Logs = new ConsoleLogBus(),
    });
    grabbers.Add(newHost);
    var newStatus = await newHost.StartAsync();
    if (newStatus.Error.Length > 0)
    {
        Console.WriteLine($"FAIL: 新 Grabber 启动失败: {newStatus.Error}");
        return 2;
    }

    Console.WriteLine($"  新 Grabber 就绪（pid={newStatus.Pid}）");

    // ── 启动旧 Grabber（直接进程；不改其 .config） ─────────────────────────
    // 旧 exe 带 requireAdministrator manifest：无管理员 shell 启动会抛 Win32Exception(740)。
    // 工具必须整体以管理员运行（新 Grabber 抓包 + 系统代理同样需要提权）——这里捕获并给出明确提示。
    try
    {
        var oldPsi = new ProcessStartInfo
        {
            FileName = oldExe,
            WorkingDirectory = Path.GetDirectoryName(oldExe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        oldProcess = Process.Start(oldPsi);
    }
    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 5 or 740)
    {
        Console.WriteLine($"FAIL: 旧 Grabber 启动需要管理员权限（winerror {ex.NativeErrorCode}）。请用管理员终端运行本工具。");
        return 2;
    }

    var oldReady = await WaitPortAsync(OldWsPort, TimeSpan.FromSeconds(15));
    if (!oldReady)
    {
        Console.WriteLine("  警告: 旧 Grabber 端口 8888 未就绪（可能已有外部实例在跑，直接 adopt 连接）");
    }
    else
    {
        Console.WriteLine("  旧 Grabber 就绪");
    }

    // ── WS 客户端（各连一端，收集 {Type,Data}） ────────────────────────────
    var oldWsTask = CollectAsync($"ws://127.0.0.1:{OldWsPort}", oldMessages);
    var newWsTask = CollectAsync($"ws://127.0.0.1:{NewWsPort}", newMessages);

    // ── headless 浏览器走新代理打开直播间 ──────────────────────────────────
    var browserPsi = new ProcessStartInfo
    {
        FileName = browser,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    browserPsi.ArgumentList.Add("--headless=new");
    browserPsi.ArgumentList.Add("--disable-gpu");
    browserPsi.ArgumentList.Add("--no-first-run");
    browserPsi.ArgumentList.Add($"--proxy-server=127.0.0.1:{NewProxyPort}");
    browserPsi.ArgumentList.Add($"--user-data-dir={userData}");
    browserPsi.ArgumentList.Add($"https://live.douyin.com/{roomId}");
    browserProcess = Process.Start(browserPsi);
    Console.WriteLine($"  已启动 headless 浏览器打开直播间（走新代理 {NewProxyPort}）");

    // ── 抓取窗口 ──────────────────────────────────────────────────────────
    Console.WriteLine($"  抓取 {durationSeconds}s ...");
    await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
}
finally
{
    Console.WriteLine("  停止抓取...");
    if (browserProcess is not null && !browserProcess.HasExited)
    {
        browserProcess.Kill(entireProcessTree: true);
        browserProcess.WaitForExit(5000);
    }

    // 新 Grabber：宿主 stop（含其自身的代理快照还原——当前代理指向 8827 时它不动）
    foreach (var g in grabbers)
    {
        try
        {
            await ((IAsyncDisposable)g).DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  新 Grabber 停止异常: {ex.Message}");
        }
    }

    // 旧 Grabber：树杀（它设了系统代理 8827，强杀不还原 → 工具兜底还原）
    if (oldProcess is not null)
    {
        try
        {
            if (!oldProcess.HasExited)
            {
                oldProcess.Kill(entireProcessTree: true);
                oldProcess.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
        }
    }

    // 系统代理铁律还原：当前代理仍指向 8827 或 18827（抓包器所设）才还原快照
    var current = proxy.Read();
    var pointsAtGrabber = current.ProxyEnable == "1" &&
                          (current.ProxyServer.Contains("127.0.0.1:8827", StringComparison.OrdinalIgnoreCase) ||
                           current.ProxyServer.Contains($"127.0.0.1:{NewProxyPort}", StringComparison.OrdinalIgnoreCase));
    if (pointsAtGrabber)
    {
        proxy.Restore(proxySnapshot);
        Console.WriteLine("  已还原系统代理（残留抓包器代理已清理）");
    }
    else
    {
        Console.WriteLine("  系统代理未指向抓包器端口，未动（用户自己的代理保留）");
    }

    try
    {
        Directory.Delete(userData, recursive: true);
    }
    catch (Exception)
    {
    }
}

// ── 对比报告 ─────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("=== 对比报告 ===");
var oldByType = oldMessages.GroupBy(m => m.Type).ToDictionary(g => g.Key, g => g.Count());
var newByType = newMessages.GroupBy(m => m.Type).ToDictionary(g => g.Key, g => g.Count());
var allTypes = oldByType.Keys.Union(newByType.Keys).OrderBy(t => t);
Console.WriteLine($"  {"Type",-6} {"旧端",-8} {"新端",-8}");
foreach (var t in allTypes)
{
    Console.WriteLine($"  {t,-6} {oldByType.GetValueOrDefault(t),-8} {newByType.GetValueOrDefault(t),-8}");
}

// 字段键集合对比（同 Type 的 Data 键集合）
Console.WriteLine();
Console.WriteLine("  Data 字段键集合对比:");
var keyDiffs = new List<string>();
foreach (var t in allTypes.Where(t => t != 9))
{
    var oldKeys = CollectKeys(oldMessages.Where(m => m.Type == t));
    var newKeys = CollectKeys(newMessages.Where(m => m.Type == t));
    var onlyOld = oldKeys.Except(newKeys).ToList();
    var onlyNew = newKeys.Except(oldKeys).ToList();
    keyDiffs.AddRange(onlyOld.Select(k => $"Type={t} 仅旧端: {k}"));
    keyDiffs.AddRange(onlyNew.Select(k => $"Type={t} 仅新端: {k}"));
    Console.WriteLine($"  Type={t}: 旧 {oldKeys.Count} 键, 新 {newKeys.Count} 键{(onlyOld.Count + onlyNew.Count == 0 ? "（一致）" : $"（差异 {onlyOld.Count + onlyNew.Count}）")}");
}

// 弹幕内容对齐（(昵称, 内容) 交集）
var oldDanmaku = ExtractDanmaku(oldMessages);
var newDanmaku = ExtractDanmaku(newMessages);
var common = oldDanmaku.Intersect(newDanmaku).Count();
Console.WriteLine();
Console.WriteLine($"  弹幕对齐: 旧端 {oldDanmaku.Count} 条, 新端 {newDanmaku.Count} 条, 交集 {common} 条");

// 新端解析成功率（归一 LiveEvent）
var parsed = newMessages.Count(m => DouyinMessageParser.Parse(m.Data is "" ? $"{{\"Type\":{m.Type}}}" : $"{{\"Type\":{m.Type},\"Data\":{m.Data}}}") is not null);
Console.WriteLine($"  新端报文解析成功: {parsed}/{newMessages.Count}");

var passed = newMessages.Any(m => m.Type == 1) &&
             newByType.Keys.Any(t => t is 1 or 5 or 7) &&
             keyDiffs.Count(k => k.StartsWith("Type=1")) <= 2 &&
             (!oldDanmaku.Any() || newDanmaku.Any() &&
              common >= Math.Min(oldDanmaku.Count, newDanmaku.Count) * 0.5);
var reason = passed
    ? "PASS: 新 Grabber 收弹幕且报文结构与旧端一致"
    : "FAIL: 见上方报告（新端未收到弹幕 / 字段结构差异大 / 弹幕内容无法对齐）";
Console.WriteLine();
Console.WriteLine(passed ? reason : reason);

return passed ? 0 : 1;

// ── 辅助 ─────────────────────────────────────────────────────────────────

static string? ArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}

static string? FindBrowser()
{
    var candidates = new[]
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    };
    return candidates.FirstOrDefault(File.Exists);
}

static async Task<bool> WaitPortAsync(int port, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            if (task.Wait(400) && client.Connected)
            {
                return true;
            }
        }
        catch (Exception)
        {
        }

        await Task.Delay(250);
    }

    return false;
}

static async Task CollectAsync(string wsUrl, ConcurrentQueue<(int Type, string Data)> sink)
{
    using var ws = new ClientWebSocket();
    try
    {
        await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        Console.WriteLine($"  已连接 {wsUrl}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  连接失败 {wsUrl}: {ex.Message}");
        return;
    }

    var buffer = new byte[1 << 16];
    using var ms = new MemoryStream();
    while (ws.State == WebSocketState.Open)
    {
        ms.SetLength(0);
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            break;
        }

        var text = Encoding.UTF8.GetString(ms.ToArray());
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("Type", out var t))
            {
                var type = t.GetInt32();
                var data = doc.RootElement.TryGetProperty("Data", out var d)
                    ? (d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : d.GetRawText())
                    : "";
                sink.Enqueue((type, data));
            }
        }
        catch (JsonException)
        {
        }
    }
}

static HashSet<string> CollectKeys(IEnumerable<(int Type, string Data)> messages)
{
    var keys = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (_, data) in messages)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                keys.Add(prop.Name);
            }
        }
        catch (JsonException)
        {
        }
    }

    return keys;
}

static HashSet<(string Nickname, string Content)> ExtractDanmaku(IEnumerable<(int Type, string Data)> messages) =>
    messages
        .Where(m => m.Type == 1)
        .Select(m =>
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Data);
                var user = doc.RootElement.TryGetProperty("User", out var u) ? u : default;
                var nickname = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("Nickname", out var n)
                    ? n.GetString() ?? ""
                    : "";
                var content = doc.RootElement.TryGetProperty("Content", out var c) ? c.GetString() ?? "" : "";
                return (nickname, content);
            }
            catch (JsonException)
            {
                return ("", "");
            }
        })
        .ToHashSet();

/// <summary>工具内联日志（分级输出到控制台，不依赖 LogBus）。</summary>
internal sealed class ConsoleLogBus : Erbai.Contracts.Logging.ILogBus
{
    public void Log(Erbai.Contracts.Logging.LogLevel level, string message, string? exception = null)
    {
        var prefix = level switch
        {
            Erbai.Contracts.Logging.LogLevel.Error => "ERROR",
            Erbai.Contracts.Logging.LogLevel.Warning => "WARN",
            _ => "INFO",
        };
        Console.WriteLine($"  [grabber:{prefix}] {message}");
    }

    public Erbai.Contracts.Abstractions.Subscription<Erbai.Contracts.Logging.LogEntry> Subscribe(int capacity = 512) =>
        throw new NotSupportedException();
}
