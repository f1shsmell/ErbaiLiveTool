// 连接器协议一致性探针（P6 新增，取代原 tools/CompareConnectors）。
//
// 为什么取代：原工具做的是「新旧连接器并行黑盒对照」——把 vendor 的
// Awoo.Connector.*.exe 与**本仓库自己实现的**同平台连接器打同一串命令做比对。
// 连接器插件化之后本仓库不再实现这四个平台，"新旧对照"这个语义已不存在。
//
// 但有一件事反而变得更重要：**任意一个第三方连接器 exe，装进来之后本应用到底能不能
// 正确识别它**。这是插件化引入的一类新故障，而且是最隐蔽的一类：
//
//   上游连接器把能力放在 ping 的 result 内（capabilities 布尔对象 + features 数组），
//   本仓库旧宿主放在响应顶层 protocolCapabilities 字符串数组。两者形状不同，而
//   ConnectorPlayerPlugin.DeriveCapabilities 认的是前者的语义。一旦解析不出
//   QueueProgrammable，插播对账守卫（GuardNextSong）会被**整体旁路**且没有任何报错——
//   "看起来一切正常、但功能悄悄没了"。请求面（ping/probe/search）却是完全兼容的，
//   所以单看"能不能回包"根本发现不了。
//
// 因此本探针的输出重点只有一个：**这台连接器会让本应用算出哪些 PlayerCapabilities**。
// 推导直接调 ConnectorPlayerPlugin.DeriveCapabilities，不复制口径。
//
// 用法:
//   dotnet run --project tools/ProbeConnector                          # 列出已安装的连接器
//   dotnet run --project tools/ProbeConnector -- --player netease      # 按安装记录解析 exe 再探
//   dotnet run --project tools/ProbeConnector -- --exe <连接器 exe 路径> [--player <平台键>]
//   可选: --no-probe（只 ping，不探快照）  --query <搜索词>
//
// 退出码: 0 = 协议版本兼容且能力面完整；1 = 不兼容或能力面缺失；2 = 用法/环境错误。
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Erbai.Connectors.Management;
using Erbai.Contracts.Players;
using Erbai.Player.Connectors;

var exePath = ArgValue(args, "--exe");
var playerKey = ArgValue(args, "--player");
var query = ArgValue(args, "--query") ?? "晴天";
var skipProbe = args.Contains("--no-probe");

var layout = new ConnectorInstallLayout();
var store = new ConnectorStore(layout);

Console.WriteLine("=== 连接器协议探针 ===");
Console.WriteLine($"  安装根: {layout.Root}");

// 没给 exe 也没给 player：列出安装状态，供人工挑一个再探
if (exePath is null && playerKey is null)
{
    Console.WriteLine("\n已安装的插件平台连接器：");
    var any = false;
    foreach (var key in ConnectorPlayers.PluginPlayerKeys)
    {
        var active = store.ReadActive(key);
        if (active is null)
        {
            Console.WriteLine($"  {key,-8} —（未安装）");
            continue;
        }

        any = true;
        Console.WriteLine($"  {key,-8} v{active.Version}  {active.Executable}");
    }

    Console.WriteLine(any
        ? "\n再跑一次并带 --player <平台键> 或 --exe <路径> 即可探活。"
        : "\n没有任何已安装记录。先在应用的插件页安装，或用 --exe 直接指向任意连接器 exe。");
    return 0;
}

// 只给了 player：走真实的安装记录解析 exe，顺带验证"记录能不能读回来"
if (exePath is null)
{
    if (playerKey is null)
    {
        Console.WriteLine("FAIL: 需要 --exe 或 --player 之一。");
        return 2;
    }

    if (!ConnectorPlayers.IsPluginPlayerKey(playerKey))
    {
        Console.WriteLine($"FAIL: {playerKey} 不是本应用认可的插件平台（可选：{string.Join(" / ", ConnectorPlayers.PluginPlayerKeys)}）。");
        return 2;
    }

    var active = store.ReadActive(playerKey);
    if (active is null)
    {
        Console.WriteLine($"FAIL: {playerKey} 没有可用的安装记录（active.json 缺失或不合法）。");
        return 2;
    }

    exePath = active.Executable;
    Console.WriteLine($"  平台:   {playerKey}");
    Console.WriteLine($"  版本:   {active.Version}  ({active.Deployment}{(active.RuntimeRid is null ? "" : $", {active.RuntimeRid}")})");
    Console.WriteLine($"  签名:   {(active.Verified ? "已校验" : "**未校验**（本地 ZIP 安装且用户已接受）")}");
}

if (!File.Exists(exePath))
{
    Console.WriteLine($"FAIL: 连接器 exe 不存在: {exePath}");
    return 2;
}

Console.WriteLine($"  exe:    {exePath}");
Console.WriteLine($"  player: {playerKey ?? "(未指定，按 ping 的 connectorId 判定)"}");

using var process = StartConnector(exePath, playerKey);
if (process is null)
{
    Console.WriteLine("FAIL: 连接器进程启动失败。");
    return 2;
}

try
{
    // ---------- ping：能力协商面 ----------
    Console.WriteLine("\n--- ping ---");
    var (pingResponse, pingError) = await SendAsync(process, new Dictionary<string, object?>
    {
        ["id"] = "1",
        ["action"] = "ping",
        ["player"] = playerKey,
    });

    if (pingResponse is not { } response)
    {
        Console.WriteLine($"FAIL: ping 无响应或超时: {pingError}");
        return 2;
    }

    if (!response.Ok)
    {
        Console.WriteLine($"FAIL: ping 返回 ok=false: {response.Error}");
        return 2;
    }

    var ping = ConnectorProtocol.ParsePing(response.Result, response.LegacyCapabilities);
    PrintPing(ping);

    // ---------- 能力面推导（口径来自产品代码本身） ----------
    var caps = ConnectorPlayerPlugin.DeriveCapabilities(ping);
    Console.WriteLine("\n--- 推导出的 PlayerCapabilities ---");
    PrintFlag("SnapshotEvents", caps.HasFlag(PlayerCapabilities.SnapshotEvents),
        "快照事件流（缺失则回退轮询）");
    PrintFlag("QueueProgrammable", caps.HasFlag(PlayerCapabilities.QueueProgrammable),
        "队列可编程——缺失会让插播对账守卫整体旁路");
    PrintFlag("Search", caps.HasFlag(PlayerCapabilities.Search), "连接器内搜索");
    PrintFlag("PauseResume", caps.HasFlag(PlayerCapabilities.PauseResume), "暂停/继续");

    // ---------- 可选：probe ----------
    string? probeNote = null;
    if (!skipProbe)
    {
        Console.WriteLine("\n--- probe ---");
        var (probeResponse, probeError) = await SendAsync(process, new Dictionary<string, object?>
        {
            ["id"] = "2",
            ["action"] = "probe",
            ["player"] = playerKey,
        });

        if (probeResponse is { } pr && pr.Ok)
        {
            var keys = pr.Result is { ValueKind: JsonValueKind.Object } obj
                ? string.Join(", ", obj.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))
                : pr.Result?.ValueKind.ToString() ?? "(空)";
            Console.WriteLine($"  快照字段: {keys}");
            probeNote = "probe 正常回包";
        }
        else
        {
            // 目标播放器没运行时 probe 报错是正常现象（无法区分"连接器坏了"与"播放器没开"）
            Console.WriteLine($"  未回包（{probeError ?? probeResponse?.Error ?? "未知"}）——目标播放器未运行时属正常");
            probeNote = "probe 未回包（多半是目标播放器没运行）";
        }
    }

    // ---------- 结论 ----------
    Console.WriteLine("\n--- 结论 ---");
    var problems = new List<string>();

    if (ping.ProtocolVersion == ConnectorProtocol.ProtocolVersion)
    {
        Console.WriteLine($"  ✓ 协议版本兼容（{ping.ProtocolVersion} == {ConnectorProtocol.ProtocolVersion}）");
    }
    else
    {
        var line = $"✗ 协议版本不兼容：连接器 {ping.ProtocolVersion}，本应用只支持 {ConnectorProtocol.ProtocolVersion}";
        Console.WriteLine("  " + line);
        problems.Add(line);
    }

    if (caps == PlayerCapabilities.None)
    {
        var line = "✗ 能力面全空——ping 的能力协商形状本应用认不出来，插播对账守卫会被整体旁路。";
        Console.WriteLine("  " + line);
        Console.WriteLine("    （检查 result 内是否有 capabilities 布尔对象 / features 数组；"
            + "只有顶层 protocolCapabilities 的话，features 应为空且各布尔位缺省 false。）");
        problems.Add(line);
    }
    else if (caps.HasFlag(PlayerCapabilities.QueueProgrammable))
    {
        Console.WriteLine("  ✓ 能力面完整：插播对账守卫生效"
            + (caps.HasFlag(PlayerCapabilities.SnapshotEvents) ? "，快照事件流可用" : "；无快照事件流，回退轮询"));
    }
    else if (string.Equals(ping.ConnectorId, "lxmusic", StringComparison.Ordinal))
    {
        // lxmusic 队列不可编程是**设计如此**（PlaybackStateMachine 守卫处有同名注释：
        // "队列可编程能力缺失（lxmusic）→ 守卫整体跳过，行为与旧版一致"），不是故障。
        Console.WriteLine("  · 无 QueueProgrammable——lxmusic 队列不可编程，插播对账守卫按设计跳过（非故障）。");
        Console.WriteLine("    " + (caps.HasFlag(PlayerCapabilities.SnapshotEvents)
            ? "快照事件流可用。"
            : "也无快照事件流，回退轮询。"));
    }
    else
    {
        // 上游四个平台的连接器**都**声明 queue-programmable-v1；这里缺了，
        // 基本就是能力协商面没被识别到，而不是这台连接器真的不支持。
        var line = "✗ 缺 QueueProgrammable——上游连接器本该声明它，多半是能力协商面没被识别到"
            + "（插播对账守卫会被整体旁路，且不报错）。";
        Console.WriteLine("  " + line);
        Console.WriteLine("    （需要 result.capabilities.insertNext=true，或 features 含 queue-programmable-v1。）");
        problems.Add(line);
    }

    if (probeNote is not null)
    {
        Console.WriteLine($"  · {probeNote}");
    }

    if (playerKey is not null && ping.ConnectorId is not null
        && !string.Equals(playerKey, ping.ConnectorId, StringComparison.Ordinal))
    {
        // 不是致命项：下游按请求里的 player 路由，connectorId 只用于展示与诊断
        Console.WriteLine($"  ! connectorId（{ping.ConnectorId}）与请求的 player（{playerKey}）不一致——仅影响诊断可读性。");
    }

    return problems.Count == 0 ? 0 : 1;
}
finally
{
    StopConnector(process);
}

// ---------------------------------------------------------------- 辅助

static void PrintPing(ConnectorPingInfo ping)
{
    Console.WriteLine($"  connectorId          {ping.ConnectorId ?? "(无)"}");
    Console.WriteLine($"  connectorVersion     {ping.ConnectorVersion ?? "(无)"}");
    Console.WriteLine($"  protocolVersion      {ping.ProtocolVersion}");
    Console.WriteLine($"  eventProtocolVersion {ping.EventProtocolVersion}");
    Console.WriteLine($"  features             [{(ping.Features.Count == 0 ? "" : string.Join(", ", ping.Features))}]");
    Console.WriteLine("  capabilities");
    PrintCap("search", ping.Capabilities.Search);
    PrintCap("playSelected", ping.Capabilities.PlaySelected);
    PrintCap("previous", ping.Capabilities.Previous);
    PrintCap("pause", ping.Capabilities.Pause);
    PrintCap("resume", ping.Capabilities.Resume);
    PrintCap("toggle", ping.Capabilities.Toggle);
    PrintCap("next", ping.Capabilities.Next);
    PrintCap("insertNext", ping.Capabilities.InsertNext);
    if (ping.Capabilities.InsertNextLevel is { } level)
    {
        Console.WriteLine($"    insertNextLevel    {level}");
    }
}

static void PrintCap(string name, bool value) =>
    Console.WriteLine($"    {name,-18} {(value ? "true" : "false")}");

static void PrintFlag(string name, bool on, string meaning) =>
    Console.WriteLine($"  {(on ? "✓" : "✗")} {name,-18} {meaning}");

static Process? StartConnector(string exe, string? playerKey)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = exe,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    // Folia 的 token 走环境变量；无 token 时 probe 返回"未配置 token"错误，
    // 保持与真实运行环境一致（否则探针会给出比产品更乐观的结论）。
    if (string.Equals(playerKey, "folia", StringComparison.Ordinal))
    {
        startInfo.Environment["BILINCM_FOLIA_TOKEN"] =
            Environment.GetEnvironmentVariable("BILINCM_FOLIA_TOKEN") ?? "";
    }

    try
    {
        return Process.Start(startInfo);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  启动异常: {ex.Message}");
        return null;
    }
}

static void StopConnector(Process process)
{
    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
    }
    catch
    {
        // 探针的清理失败不该影响退出码
    }
}

/// <summary>发一条请求并读一行响应；同时把响应顶层的旧宿主能力面取出来。</summary>
static async Task<(ProbeResponse? Response, string? Error)> SendAsync(
    Process process, Dictionary<string, object?> request)
{
    var line = JsonSerializer.Serialize(request);
    await process.StandardInput.WriteLineAsync(line);
    await process.StandardInput.FlushAsync();

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var responseLine = await process.StandardOutput.ReadLineAsync().WaitAsync(timeout.Token);
        if (responseLine is null)
        {
            return (null, "连接器无响应（进程已退出）");
        }

        using var doc = JsonDocument.Parse(responseLine);
        var root = doc.RootElement;

        var legacy = new List<string>();
        if (root.TryGetProperty("protocolCapabilities", out var legacyElement)
            && legacyElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in legacyElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                {
                    legacy.Add(value);
                }
            }
        }

        return (new ProbeResponse(
            root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
            root.TryGetProperty("result", out var result) ? result.Clone() : null,
            root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null,
            legacy), null);
    }
    catch (Exception ex)
    {
        return (null, ex.Message);
    }
}

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

/// <summary>探针内部用的响应投影：<c>result</c> + 顶层旧宿主能力面。</summary>
internal sealed record ProbeResponse(
    bool Ok,
    JsonElement? Result,
    string? Error,
    IReadOnlyList<string> LegacyCapabilities);
