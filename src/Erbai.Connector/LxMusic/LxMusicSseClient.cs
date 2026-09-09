using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connector.LxMusic;

/// <summary>
/// LX Music 播放器状态 SSE 订阅客户端（官方 GET /subscribe-player-status，
/// LXMusicSseClient）：连接建立即全量推送当前值，此后按
/// 字段推送独立事件（event: &lt;字段&gt; / data: &lt;JSON&gt;）；断线指数退避
/// 重连 1s→15s；事件队列有界 8 满丢最旧（消费方只关心最新状态）。
/// 留 HttpMessageHandler 注入点（自动化测试走 fake）。
/// </summary>
public sealed class LxMusicSseClient
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly System.Threading.Channels.Channel<PlayerSnapshot> _events;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, JsonElement> _state = new();

    public LxMusicSseClient(string baseUrl = "http://127.0.0.1:23330", HttpMessageHandler? handler = null)
    {
        _baseUrl = (baseUrl ?? "").TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _events = System.Threading.Channels.Channel.CreateBounded<PlayerSnapshot>(
            new System.Threading.Channels.BoundedChannelOptions(8)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });
    }

    public System.Threading.Channels.ChannelReader<PlayerSnapshot> Events => _events.Reader;

    public bool Connected { get; private set; }

    /// <summary>后台订阅循环：连上后持续读 SSE 事件块，断线按 1s→15s 退避重连。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/subscribe-player-status?filter=status,name,singer,duration,progress");
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"sse status {response.StatusCode}");
                }

                Connected = true;
                delay = TimeSpan.FromSeconds(1); // 连接成功重置退避
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                await ReadStreamAsync(reader, ct);
                Connected = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                Connected = false;
            }

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }

    /// <summary>SSE 块解析：`event: &lt;字段&gt;` + `data: &lt;JSON&gt;` + 空行提交。</summary>
    private async Task ReadStreamAsync(StreamReader reader, CancellationToken ct)
    {
        string? eventName = null;
        var dataParts = new List<string>();
        string? line;
        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                // 空行 = 一个 SSE 事件块结束
                if (eventName is not null && dataParts.Count > 0)
                {
                    ApplyEvent(eventName, string.Join("\n", dataParts));
                }

                eventName = null;
                dataParts.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataParts.Add(line["data:".Length..].Trim());
            }
        }

        // 流结束未提交的块（无空行结尾）
        if (eventName is not null && dataParts.Count > 0)
        {
            ApplyEvent(eventName, string.Join("\n", dataParts));
        }
    }

    /// <summary>字段事件：data 为字段值的 JSON（"playing"/"晴天"/265），合并进状态快照并投递。</summary>
    private void ApplyEvent(string key, string rawData)
    {
        lock (_stateLock)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawData);
                _state[key] = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                _state[key] = JsonDocument.Parse($"\"{rawData.Replace("\"", "\\\"")}\"").RootElement.Clone();
            }
        }

        _events.Writer.TryWrite(Snapshot());
    }

    /// <summary>合并当前字段状态为播放器快照（缺失字段按未知处理）。</summary>
    public PlayerSnapshot Snapshot()
    {
        lock (_stateLock)
        {
            var status = GetString("status");
            var name = GetString("name");
            var singer = GetString("singer");
            var duration = GetNumber("duration");
            var progress = GetNumber("progress");
            return new PlayerSnapshot
            {
                Connected = Connected,
                Version = "lxmusic-sse",
                Current = string.IsNullOrEmpty(name)
                    ? null
                    : new PlayerTrack { Platform = "lxmusic", Title = name, Artist = singer ?? "" },
                NextObservation = NextObservation.Unknown,
                RawStatus = LxMusicConnector.MapStatus(string.IsNullOrEmpty(status) ? null : status),
                ProgressSeconds = progress,
            };
        }
    }

    private string? GetString(string key) =>
        _state.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private double? GetNumber(string key) =>
        _state.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.Number
            && value.TryGetDouble(out var d)
            ? d
            : null;
}
