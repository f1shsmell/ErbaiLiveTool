using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Erbai.Contracts.Live;

namespace Erbai.OverlayWpf;

/// <summary>
/// 弹幕 WS 数据源（自 Erbai.App.Overlay.OverlayFeed 迁入本库，随 WPF 弹幕悬浮窗走）：
/// 订阅 OverlayServer /api/v1/events，过滤 live.* 增量事件，断线 3s 重连。
/// </summary>
internal sealed class DanmakuOverlayFeed : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _wsUrl;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private ClientWebSocket? _ws;
    private bool _disposed;

    /// <summary>规范化直播事件（任意线程）。</summary>
    public event Action<LiveEvent>? LiveEventReceived;

    public DanmakuOverlayFeed(string baseUrl)
    {
        var scheme = baseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var hostPort = baseUrl[(baseUrl.IndexOf("://", StringComparison.Ordinal) + 3)..];
        _wsUrl = $"{scheme}://{hostPort}/api/v1/events"; // 2026-09 起无 token（回环绑定 + OBS 同机）
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(_wsUrl), _cts.Token);
                lock (_gate)
                {
                    _ws = ws;
                }

                var buffer = new byte[64 * 1024];
                while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    HandleMessage(json);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 连接失败/中断：等待重连
            }

            lock (_gate)
            {
                _ws = null;
            }

            if (!_cts.IsCancellationRequested && !_disposed)
            {
                try
                {
                    await Task.Delay(3000, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "ping")
            {
                return;
            }

            if (!root.TryGetProperty("event", out var evtProp))
            {
                return;
            }

            var eventName = evtProp.GetString() ?? "";
            if (!eventName.StartsWith("live.", StringComparison.Ordinal))
            {
                return; // 只关心 live.*（弹幕/礼物/进场等）
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var live = ParseLiveEvent(data);
            if (live is not null)
            {
                lock (_gate)
                {
                    LiveEventReceived?.Invoke(live);
                }
            }
        }
        catch
        {
            // 单帧解析失败不影响后续
        }
    }

    private static LiveEvent? ParseLiveEvent(JsonElement data)
    {
        var kindStr = data.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var kind = kindStr switch
        {
            "gift" => LiveEventKind.Gift,
            "guardbuy" => LiveEventKind.GuardBuy,
            "enter" => LiveEventKind.Enter,
            "follow" => LiveEventKind.Follow,
            "subscribe" => LiveEventKind.Subscribe,
            "like" => LiveEventKind.Like,
            "share" => LiveEventKind.Share,
            "livestate" => LiveEventKind.LiveState,
            _ => LiveEventKind.Danmaku,
        };

        return new LiveEvent
        {
            Platform = Get(data, "platform"),
            RoomId = "",
            Kind = kind,
            UserId = 0,
            Nickname = Get(data, "nickname"),
            Text = Get(data, "text"),
            GiftName = Get(data, "giftName"),
            GiftCount = GetInt(data, "giftCount"),
            IsAdmin = GetBool(data, "isAdmin"),
            IsAnchor = GetBool(data, "isAnchor"),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static string Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : 0;

    private static bool? GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            _ws?.Dispose();
        }
        catch
        {
        }
    }
}
