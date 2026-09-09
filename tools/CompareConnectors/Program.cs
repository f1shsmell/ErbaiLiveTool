// 阶段6 退出标准：新旧连接器并行黑盒对照（docs/04 §1.4「新旧连接器并行黑盒对照——
// 同一命令序列分别打 vendor exe 与 Erbai.Connector，比对响应/快照」）。
//
// 原理：vendor 的 Awoo.Connector.*.exe 与 Erbai.Connector 都是 NDJSON-stdio
// 服务端（04 §1.4 线格式）。对同一平台发同一命令序列（ping/probe/search/execute），
// 收集两边响应，按归一化结构比对（ok/outcome 字段面、快照形状）。
// 目标播放器未运行时两边都返回错误路径——同样可对照"无客户端行为"的一致性。
//
// 用法:
//   dotnet run --project tools/CompareConnectors -- <player> [--query <搜索词>]
//     player = netease | kugou | qqmusic | folia（lxmusic 无 vendor exe 不参与）
//     [--old-exe <vendor exe 路径>] 默认 vendor/Awoo.Connector.<player>.exe
//     [--new-exe <连接器 exe 路径>] 默认 src/Erbai.Connector bin/Release 产物
//
// 注意（副作用）：
// - netease 会真实注入 bridge 到正在运行的网易云（精确测试构建才允许）；kugou
//   的 execute 会动真实桌面（点击）；请在有目标播放器的桌面会话中运行；
// - 对照只做只读面时（ping/probe/search）无副作用。
using System.Diagnostics;
using System.Text;
using System.Text.Json;

var player = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "netease";
var query = ArgValue(args, "--query") ?? "晴天";
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var oldExe = ArgValue(args, "--old-exe") ?? Path.Combine(repoRoot, "vendor", player, $"Awoo.Connector.{Capitalize(player)}.exe");
var newExe = ArgValue(args, "--new-exe") ?? Path.Combine(repoRoot, "src", "Erbai.Connector", "bin", "Release", "net8.0", "Erbai.Connector.exe");

Console.WriteLine("=== 新旧连接器黑盒对照（阶段6 退出标准） ===");
Console.WriteLine($"  平台: {player}");
Console.WriteLine($"  旧(vendor): {oldExe}");
Console.WriteLine($"  新:   {newExe}");

if (!File.Exists(oldExe))
{
    Console.WriteLine($"FAIL: 旧连接器不存在: {oldExe}");
    return 2;
}

if (!File.Exists(newExe))
{
    Console.WriteLine($"FAIL: 新连接器不存在（先 dotnet build src/Erbai.Connector）: {newExe}");
    return 2;
}

// 命令序列：ping / probe / search / execute(Next)
var commands = new (string Action, string? Command, string? Track)[]
{
    ("ping", null, null),
    ("probe", null, null),
    ("search", null, null),
    ("execute", "next", null),
};

var oldOut = new List<(string Request, JsonElement? Response, string? Error)>();
var newOut = new List<(string Request, JsonElement? Response, string? Error)>();

Console.WriteLine("\n--- 旧连接器 ---");
var oldProc = StartConnectorAsync(oldExe, player, player == "folia");
oldOut = await RunCommandsAsync(oldProc, player, commands);
StopConnector(oldProc);

Console.WriteLine("\n--- 新连接器 ---");
var newProc = StartConnectorAsync(newExe, player, player == "folia");
newOut = await RunCommandsAsync(newProc, player, commands);
StopConnector(newProc);

Console.WriteLine("\n=== 对照报告 ===");
var mismatches = 0;
for (var i = 0; i < commands.Length; i++)
{
    var (action, _, _) = commands[i];
    var oldItem = oldOut[i];
    var newItem = newOut[i];
    var oldShape = Describe(oldItem);
    var newShape = Describe(newItem);
    var match = string.Equals(oldShape, newShape, StringComparison.Ordinal);
    Console.WriteLine($"  [{action}] {(match ? "一致" : "**不一致**")}");
    Console.WriteLine($"    旧: {oldShape}");
    Console.WriteLine($"    新: {newShape}");
    if (!match)
    {
        mismatches++;
    }
}

Console.WriteLine($"\n结论: {(mismatches == 0 ? "全部命令响应形状一致" : $"{mismatches} 个命令响应形状不一致（注意：无播放器环境下错误文案差异属正常）")}");
return mismatches == 0 ? 0 : 1;

static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

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

static Process StartConnectorAsync(string exe, string player, bool foliaNeedsToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = exe,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true,
    };
    if (foliaNeedsToken)
    {
        // Folia 无 token 时 probe 返回"未配置 token"错误——保持与真实环境一致
        var token = Environment.GetEnvironmentVariable("BILINCM_FOLIA_TOKEN") ?? "";
        startInfo.Environment["BILINCM_FOLIA_TOKEN"] = token;
    }

    var process = Process.Start(startInfo) ?? throw new InvalidOperationException("连接器启动失败");
    return process;
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
    }
}

static async Task<List<(string, JsonElement?, string?)>> RunCommandsAsync(
    Process process,
    string player,
    (string Action, string? Command, string? Track)[] commands)
{
    var results = new List<(string, JsonElement?, string?)>();
    var reader = process.StandardOutput;

    var id = 1;
    foreach (var (action, command, _) in commands)
    {
        var request = new Dictionary<string, object?>
        {
            ["id"] = (id++).ToString(),
            ["action"] = action,
            ["player"] = player,
        };
        if (action == "search")
        {
            request["query"] = "晴天";
        }

        if (action == "execute")
        {
            request["command"] = command;
        }

        var line = JsonSerializer.Serialize(request);
        await process.StandardInput.WriteLineAsync(line);
        await process.StandardInput.FlushAsync();

        JsonElement? response = null;
        string? error = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var responseLine = await reader.ReadLineAsync().WaitAsync(timeout.Token);
            if (responseLine is not null)
            {
                using var doc = JsonDocument.Parse(responseLine);
                response = doc.RootElement.Clone();
            }
            else
            {
                error = "连接器无响应（进程退出）";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        results.Add((action, response, error));
    }

    return results;
}

/// <summary>响应归一化描述：ok / outcome / error 前缀 / 快照字段键集合。</summary>
static string Describe((string Request, JsonElement? Response, string? Error) item)
{
    if (item.Error is not null)
    {
        return $"error: {item.Error}";
    }

    if (item.Response is not { } response)
    {
        return "no-response";
    }

    if (response.TryGetProperty("ok", out var ok))
    {
        var parts = new List<string> { $"ok={ok.GetBoolean()}" };
        if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            parts.Add($"error={Prefix(error.GetString() ?? "")}");
        }

        if (response.TryGetProperty("result", out var result))
        {
            parts.Add($"result={Shape(result)}");
        }

        return string.Join("; ", parts);
    }

    return Shape(response);
}

static string Prefix(string value) => value.Length <= 24 ? value : value[..24];

static string Shape(JsonElement element)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Array:
            return $"array[{element.GetArrayLength()}]";
        case JsonValueKind.Object:
        {
            // 忽略加性能力字段（新旧协议演进差异，不影响核心面比对）
            var keys = element.EnumerateObject()
                .Where(p => p.Name != "protocolCapabilities")
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            var shape = string.Join(",", keys);
            if (keys.Length == 0)
            {
                return "{}";
            }

            // 快照：附 connected/nextSource 值
            if (element.TryGetProperty("connected", out var connected))
            {
                shape += $";connected={connected.GetBoolean()}";
            }

            if (element.TryGetProperty("nextSource", out var nextSource))
            {
                shape += $";nextSource={nextSource.GetString()}";
            }

            if (element.TryGetProperty("outcome", out var outcome))
            {
                shape += $";outcome={outcome.GetString()}";
            }

            return "{" + shape + "}";
        }

        default:
            return element.ToString();
    }
}
