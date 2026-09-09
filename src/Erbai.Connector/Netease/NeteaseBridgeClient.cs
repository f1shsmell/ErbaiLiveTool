using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Erbai.Connector.Netease;

/// <summary>bridge 命令结果。</summary>
public sealed record NeteaseBridgeCommandResult(bool Success, string Message, int? ProcessId);

/// <summary>bridge 曲目事件（trackId/name/artist/album/coverUrl/next*）。</summary>
public sealed record NeteaseBridgeTrackEvent(
    bool Available,
    long Sequence,
    string Type,
    string TrackId,
    string Name,
    string Artist,
    string Album,
    string CoverUrl,
    string NextTrackId,
    string NextName,
    string NextArtist,
    string NextAlbum,
    string RawJson);

/// <summary>
/// 网易云 CEF bridge 客户端（机制 docs/04 §1.5.4，代码表达沿用上游）：
/// 命名管道 AwooNcmCefBridge-v1-{pid}（HELLO 1/PAUSE/RESUME/PLAY {id}/
/// ADD_NEXT {id}/GET_TRACK_EVENT，行协议）+ 事件管道
/// AwooNcmCefBridge-events-v1-{pid}（WAIT_EVENT {seq} {timeoutMs}，
/// 响应 OK EVENT {seq} {ageMs} {base64 JSON} / OK NO_EVENT / OK NO_CHANGE）。
/// 管道工厂可注入（自动化测试走 fake）。
/// </summary>
public static class NeteaseBridgeClient
{
    private const string ProtocolVersion = "1";
    private static readonly object RequestSync = new();

    public static NeteaseBridgeCommandResult Pause() => Send("PAUSE");

    public static NeteaseBridgeCommandResult Resume() => Send("RESUME");

    public static NeteaseBridgeCommandResult PlaySong(string songId) => Send($"PLAY {songId}");

    public static NeteaseBridgeCommandResult AddNext(string songId) => Send($"ADD_NEXT {songId}");

    /// <summary>探测 bridge 就绪（HELLO → "OK READY"）。</summary>
    public static NeteaseBridgeStatus Probe(int processId)
    {
        var response = Exchange(processId, $"HELLO {ProtocolVersion}", 350);
        var ready = response.Success && response.Response.StartsWith("OK READY", StringComparison.Ordinal);
        return new NeteaseBridgeStatus(
            ready,
            ready ? $"进程内 CEF 桥已连接：{response.Response}" : $"进程内 CEF 桥未就绪：{response.Response}");
    }

    public static NeteaseBridgeTrackEvent ReadLatestTrackEvent(int processId)
    {
        lock (RequestSync)
        {
            var exchange = Exchange(processId, "GET_TRACK_EVENT", 300);
            if (!exchange.Success)
            {
                return Unavailable(exchange.Response);
            }

            return ParseTrackEventResponse(exchange.Response);
        }
    }

    /// <summary>事件管道等待（WAIT_EVENT {seq} {timeoutMs}）。</summary>
    public static async Task<NeteaseBridgeTrackEvent> WaitForTrackEventAsync(
        int processId,
        long afterSequence,
        int timeoutMs,
        CancellationToken ct)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                $"AwooNcmCefBridge-events-v{ProtocolVersion}-{processId}",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(35, timeoutMs / 1000.0 + 5)));
            await pipe.ConnectAsync(1500, timeout.Token);

            var requestBytes = Encoding.UTF8.GetBytes($"WAIT_EVENT {Math.Max(0, afterSequence)} {timeoutMs}\n");
            await pipe.WriteAsync(requestBytes, timeout.Token);
            await pipe.FlushAsync(timeout.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var response = await reader.ReadLineAsync(timeout.Token) ?? string.Empty;
            return ParseTrackEventResponse(response.Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable("event-stream-timeout");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            return Unavailable($"event-stream-unavailable: {ex.Message}");
        }
    }

    private static NeteaseBridgeCommandResult Send(string command)
    {
        lock (RequestSync)
        {
            var endpoint = NeteaseNativeIpc.FindEndpoint();
            if (endpoint is null)
            {
                return new NeteaseBridgeCommandResult(false, "没有发现正在运行的网易云音乐。", null);
            }

            var exchange = Exchange(endpoint.Value.ProcessId, command, 3000);
            if (!exchange.Success)
            {
                return new NeteaseBridgeCommandResult(
                    false,
                    "进程内 CEF 桥尚未就绪或没有响应；已拒绝命令，且不会回退到会弹窗的旧通道。"
                    + (string.IsNullOrWhiteSpace(exchange.Response) ? "" : $" 桥响应：{exchange.Response}"),
                    endpoint.Value.ProcessId);
            }

            var accepted = exchange.Response.StartsWith("OK", StringComparison.Ordinal);
            return new NeteaseBridgeCommandResult(
                accepted,
                accepted
                    ? $"进程内 CEF 桥已接收 {command.Split(' ')[0]}；是否播放成功仍由实际歌曲状态确认。"
                    : $"进程内 CEF 桥拒绝命令：{exchange.Response}",
                endpoint.Value.ProcessId);
        }
    }

    private static NeteaseBridgeExchangeResult Exchange(int processId, string request, int timeoutMilliseconds)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                $"AwooNcmCefBridge-v{ProtocolVersion}-{processId}",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipe.Connect(timeoutMilliseconds);
            var requestBytes = Encoding.UTF8.GetBytes(request + "\n");
            pipe.Write(requestBytes, 0, requestBytes.Length);
            pipe.Flush();
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var readCancellation = new CancellationTokenSource(timeoutMilliseconds);
            var response = reader.ReadLineAsync(readCancellation.Token).GetAwaiter().GetResult() ?? string.Empty;
            return new NeteaseBridgeExchangeResult(true, response.Trim());
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return new NeteaseBridgeExchangeResult(false, ex.Message);
        }
    }

    private static NeteaseBridgeTrackEvent ParseTrackEventResponse(string response)
    {
        var parts = response.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0] == "OK" && parts[1] is "NO_EVENT" or "NO_CHANGE")
        {
            return Unavailable(parts[1] == "NO_CHANGE" ? "event-stream-no-change" : "watcher-initializing");
        }

        if (parts.Length != 5 || parts[0] != "OK" || parts[1] != "EVENT"
            || !long.TryParse(parts[2], out var sequence) || !long.TryParse(parts[3], out _))
        {
            return Unavailable(response);
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[4]));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new NeteaseBridgeTrackEvent(
                true,
                sequence,
                ReadJsonString(root, "type"),
                ReadJsonString(root, "trackId"),
                ReadJsonString(root, "name"),
                ReadJsonString(root, "artist"),
                ReadJsonString(root, "album"),
                ReadJsonString(root, "coverUrl"),
                ReadJsonString(root, "nextTrackId"),
                ReadJsonString(root, "nextName"),
                ReadJsonString(root, "nextArtist"),
                ReadJsonString(root, "nextAlbum"),
                json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return Unavailable($"invalid-event: {ex.Message}");
        }
    }

    private static string ReadJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;

    private static NeteaseBridgeTrackEvent Unavailable(string details) =>
        new(false, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, details);

    private sealed record NeteaseBridgeExchangeResult(bool Success, string Response);
}

/// <summary>bridge 就绪状态。</summary>
public sealed record NeteaseBridgeStatus(bool Ready, string Message);
