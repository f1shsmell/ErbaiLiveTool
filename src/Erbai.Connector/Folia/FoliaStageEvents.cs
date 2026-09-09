using System.Net.WebSockets;
using System.Threading.Channels;

namespace Erbai.Connector.Folia;

/// <summary>
/// Folia Stage WS 套接字抽象（可测性缝：测试注入 fake 套接字）。
/// ReceiveAsync 返回 (文本, 是否已关闭)。
/// </summary>
public interface IFoliaStageSocket : IAsyncDisposable
{
    Task<(string? Text, bool Closed)> ReceiveAsync(CancellationToken ct);
}

/// <summary>真实 ClientWebSocket 适配（ws://127.0.0.1:32107/stage/player/ws?token= + Bearer 头）。</summary>
public sealed class FoliaClientWebSocket : IFoliaStageSocket
{
    private readonly ClientWebSocket _socket;

    public FoliaClientWebSocket(string baseUrl, string token)
    {
        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        var uri = new Uri($"{baseUrl.TrimEnd('/')}/stage/player/ws?token={Uri.EscapeDataString(token)}");
        _socket.ConnectAsync(uri, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<(string? Text, bool Closed)> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var payload = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (null, true);
            }

            payload.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        if (result.MessageType != WebSocketMessageType.Text)
        {
            return (null, false);
        }

        return (System.Text.Encoding.UTF8.GetString(payload.ToArray()), false);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "connector shutdown", timeout.Token)
                    .GetAwaiter().GetResult();
            }
        }
        catch
        {
            // 关闭本地套接字不得阻塞进程退出
        }

        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Folia Stage WS 事件订阅（机制 docs/04 §1.5.1）：连接 ws://127.0.0.1:32107/
/// stage/player/ws，读 STATUS / TRACK_CHANGED 事件 → 解析曲目快照投递到有界
/// 通道（8，满丢最旧）；断线指数退避重连 1s→15s，连接状态暴露 Connected。
/// 套接字工厂可注入（自动化测试走 fake）。
/// </summary>
public sealed class FoliaStageEvents
{
    private readonly Func<string, string, IFoliaStageSocket> _socketFactory;
    private readonly Channel<FoliaStageEvent> _events;
    private readonly string _baseUrl;
    private readonly string _token;

    public FoliaStageEvents(string baseUrl, string token, Func<string, string, IFoliaStageSocket>? socketFactory = null)
    {
        _baseUrl = baseUrl;
        _token = token;
        _socketFactory = socketFactory ?? ((url, tok) => new FoliaClientWebSocket(url, tok));
        _events = Channel.CreateBounded<FoliaStageEvent>(
            new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
            });
    }

    public ChannelReader<FoliaStageEvent> Events => _events.Reader;

    public volatile bool Connected;

    public async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var socket = _socketFactory(_baseUrl, _token);
                Connected = true;
                delay = TimeSpan.FromSeconds(1); // 连接成功重置退避
                while (!ct.IsCancellationRequested)
                {
                    var (text, closed) = await socket.ReceiveAsync(ct);
                    if (closed)
                    {
                        break;
                    }

                    if (text is null)
                    {
                        continue;
                    }

                    var stageEvent = FoliaStageJson.ParseEvent(text);
                    if (stageEvent is not null)
                    {
                        _events.Writer.TryWrite(stageEvent);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 连接失败/断开：更新状态后按退避重连
            }
            finally
            {
                Connected = false;
            }

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }
}
