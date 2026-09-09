using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connector.Tests;

/// <summary>
/// 宿主 NDJSON 服务端循环（协议域，docs/04 §1.4）：
/// ping/probe/search/execute 路由、垃圾行容忍、未知 action/player、命令映射。
/// 真实 Erbai.Connector.exe 子进程直驱（与生产形态一致）。
/// </summary>
public class ConnectorHostTests
{
    private static string ConnectorExePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Erbai.Connector.exe");
        Assert.True(File.Exists(path), $"connector exe not found: {path}");
        return path;
    }

    private static (Process Process, StreamWriter Input, StreamReader Output) StartHost()
    {
        var info = new ProcessStartInfo
        {
            FileName = ConnectorExePath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };
        var process = Process.Start(info)!;
        _ = process.StandardError.ReadToEndAsync(); // 防阻塞
        return (process, process.StandardInput, process.StandardOutput);
    }

    private static async Task<JsonDocument> SendAsync(StreamWriter input, StreamReader output,
        string action, string? player = "dummy", string? command = null)
    {
        var request = JsonSerializer.Serialize(new ConnectorRequest
        {
            Id = "t1",
            Action = action,
            Player = player,
            Command = command,
        }, ConnectorProtocol.Json);
        await input.WriteLineAsync(request);

        while (true)
        {
            var line = await output.ReadLineAsync();
            if (line is null)
            {
                throw new InvalidOperationException("host closed without response");
            }

            try
            {
                return JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // 垃圾行或空行,继续读
            }
        }
    }

    [Fact]
    public async Task Ping_ReturnsOkWithCapabilities()
    {
        var (process, input, output) = StartHost();
        try
        {
            using var doc = await SendAsync(input, output, "ping");
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("t1", root.GetProperty("id").GetString());
        }
        finally
        {
            await ShutdownAsync(input, output, process);
        }
    }

    [Fact]
    public async Task Probe_ReturnsSnapshotJson()
    {
        var (process, input, output) = StartHost();
        try
        {
            using var doc = await SendAsync(input, output, "probe");
            var result = doc.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("connected").GetBoolean());
            Assert.Equal("dummy-1", result.GetProperty("version").GetString());
        }
        finally
        {
            await ShutdownAsync(input, output, process);
        }
    }

    [Fact]
    public async Task Search_ReturnsBareArray()
    {
        var (process, input, output) = StartHost();
        try
        {
            using var doc = await SendAsync(input, output, "search");
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("result").ValueKind);
        }
        finally
        {
            await ShutdownAsync(input, output, process);
        }
    }

    [Fact]
    public async Task Execute_PlaySelected_RoutesCommandAndTrack()
    {
        var (process, input, output) = StartHost();
        try
        {
            var track = new PlayerTrack { Platform = "kugou", Title = "晴天", Artist = "周杰伦", Id = "H1" };
            var request = JsonSerializer.Serialize(new ConnectorRequest
            {
                Id = "t2",
                Action = "execute",
                Player = "dummy",
                Command = "playSelected",
                Track = track,
            }, ConnectorProtocol.Json);
            await input.WriteLineAsync(request);

            var line = await output.ReadLineAsync();
            using var doc = JsonDocument.Parse(line!);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("applied", doc.RootElement.GetProperty("result").GetProperty("outcome").GetString());
        }
        finally
        {
            await ShutdownAsync(input, output, process);
        }
    }

    [Fact]
    public async Task Execute_UnknownCommand_ReturnsError()
    {
        var (process, input, output) = StartHost();
        try
        {
            using var doc = await SendAsync(input, output, "execute", command: "flyToMoon");
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("unsupported command", doc.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            await ShutdownAsync(input, output, process);
        }
    }

    [Fact]
    public async Task UnknownPlayer_ReturnsError()
    {
        var (process, input, output) = StartHost();
        try
        {
            using var doc = await SendAsync(input, output, "ping", player: "nobody");
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("unknown player", doc.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            await ShutdownAsync(input, output, process);
        }
    }

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var (process, input, output) = StartHost();
        try
        {
            using var doc = await SendAsync(input, output, "dance");
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("unknown action", doc.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            await ShutdownAsync(input, output, process);
        }
    }

    [Fact]
    public async Task GarbageLine_IsIgnored_HostKeepsRunning()
    {
        var (process, input, output) = StartHost();
        try
        {
            await input.WriteLineAsync("this is not json {{{");

            // 垃圾行后正常请求仍被处理
            var request = JsonSerializer.Serialize(new ConnectorRequest
            {
                Id = "t3",
                Action = "ping",
                Player = "dummy",
            }, ConnectorProtocol.Json);
            await input.WriteLineAsync(request);
            var line = await output.ReadLineAsync();
            using var doc = JsonDocument.Parse(line!);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("t3", doc.RootElement.GetProperty("id").GetString());
        }
        finally
        {
            await ShutdownAsync(input, output, process);
        }
    }

    private static async Task ShutdownAsync(StreamWriter input, StreamReader output, Process process)
    {
        try
        {
            var request = JsonSerializer.Serialize(new ConnectorRequest
            {
                Id = "bye",
                Action = "shutdown",
                Player = "dummy",
            }, ConnectorProtocol.Json);
            await input.WriteLineAsync(request);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }
}
