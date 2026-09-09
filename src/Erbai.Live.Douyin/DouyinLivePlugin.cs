using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Plugins;
using Erbai.Live.Douyin.Messages;

namespace Erbai.Live.Douyin;

/// <summary>
/// 抖音弹幕插件（阶段 4，docs/01 §3.3 + 04 §3）：连 Grabber 的本地 WS（默认 ws://127.0.0.1:8888），
/// <c>{Type, Data}</c> 报文经 <see cref="DouyinMessageParser"/> 归一为 <see cref="LiveEvent"/>。
/// - WS 连接工厂可注入（对齐 BilibiliLivePlugin 的 wsUriFactory 先例；测试用假 WS 服务器）；
/// - 断线固定延迟重连（默认 5s，旧 run_douyin_client 语义）；
/// - 循环内任何异常不得逃逸（worker 不变量）。
/// </summary>
public sealed class DouyinLivePlugin : ILivePlatformPlugin
{
    private static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly ILogBus? _logs;
    private readonly TimeSpan _reconnectDelay;
    private readonly Func<Uri, ClientWebSocket> _wsFactory;
    private volatile IReadOnlySet<string> _allowedRoomIds;
    private Channel<LiveEvent> _events;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DouyinLivePlugin(
        ILogBus? logs = null,
        TimeSpan? reconnectDelay = null,
        Func<Uri, ClientWebSocket>? wsFactory = null,
        IReadOnlySet<string>? allowedRoomIds = null)
    {
        _logs = logs;
        _reconnectDelay = reconnectDelay ?? DefaultReconnectDelay;
        _wsFactory = wsFactory ?? (_ => new ClientWebSocket());
        _allowedRoomIds = allowedRoomIds ?? new HashSet<string>(StringComparer.Ordinal);
        _events = CreateEventChannel();
    }

    /// <summary>运行时更新房间白名单（重启平台前由组合根按最新配置调用；原子替换引用）。</summary>
    public void UpdateRoomFilter(IEnumerable<string> roomIds)
    {
        _allowedRoomIds = roomIds.ToHashSet(StringComparer.Ordinal);
    }

    public string Key => "douyin";

    public string DisplayName => "抖音弹幕";

    public LiveCapabilities Capabilities =>
        LiveCapabilities.Danmaku | LiveCapabilities.Gift | LiveCapabilities.Enter |
        LiveCapabilities.Follow | LiveCapabilities.Share | LiveCapabilities.Like |
        LiveCapabilities.LiveState;

    public ChannelReader<LiveEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.Reader;
            }
        }
    }

    /// <summary>监听循环是否在跑（UI 启停按钮状态用）。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    public Task StartAsync(AppConfig config, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_loop is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _events = CreateEventChannel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wsUrl = string.IsNullOrWhiteSpace(config.DouyinWsUrl)
                ? new Uri("ws://127.0.0.1:8888")
                : new Uri(config.DouyinWsUrl);
            _loop = Task.Run(() => RunLoopAsync(wsUrl, _cts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        Channel<LiveEvent>? events;
        lock (_sync)
        {
            loop = _loop;
            cts = _cts;
            events = _events;
            _loop = null;
            _cts = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        events.Writer.TryComplete();
        cts.Dispose();
    }

    private static Channel<LiveEvent> CreateEventChannel() =>
        Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    public async ValueTask DisposeAsync() => await StopAsync();

    // ── 主循环：连接 → 收报文解析 → 断开重连 ────────────────────────────────

    private async Task RunLoopAsync(Uri wsUrl, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(wsUrl, ct);
                _logs?.Warning($"[抖音] 连接断开，{_reconnectDelay.TotalSeconds:0.#}s 后重连");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logs?.Warning($"[抖音] 连接失败（{ex.Message}），{_reconnectDelay.TotalSeconds:0.#}s 后重连");
            }
            catch (IOException ex)
            {
                _logs?.Warning($"[抖音] 连接中断（{ex.Message}），{_reconnectDelay.TotalSeconds:0.#}s 后重连");
            }
            catch (Exception ex)
            {
                _logs?.Error($"[抖音] 监听循环异常（已隔离，重连）：{ex.Message}", ex.ToString());
            }

            await DelayOrCancel(_reconnectDelay, ct);
        }
    }

    private async Task ConnectAndListenAsync(Uri wsUrl, CancellationToken ct)
    {
        using var ws = _wsFactory(wsUrl);
        await ws.ConnectAsync(wsUrl, ct);
        _logs?.Information($"[抖音] 已连接 {wsUrl}，等待直播弹幕");

        var buffer = new byte[1 << 16];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(ms.ToArray());
            var evt = DouyinMessageParser.Parse(
                text,
                _allowedRoomIds,
                error => _logs?.Warning($"[抖音] {error}"));
            if (evt is not null)
            {
                _events.Writer.TryWrite(evt);
            }
        }
    }

    private async Task DelayOrCancel(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
