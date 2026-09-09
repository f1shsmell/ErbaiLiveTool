using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Plugins;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili;

/// <summary>认证失败（op=8 code!=0）→ 需要重新 init_room 拿新 token（blivedm AuthError 语义）。</summary>
public sealed class BilibiliAuthException : Exception
{
    public BilibiliAuthException(string message) : base(message)
    {
    }
}

/// <summary>
/// B站弹幕插件（阶段 3，docs/01 §3.2 + 04 §2）：房间初始化链 → WSS 连接 → op=7 认证
/// （匿名 uid=0 + buvid3 + Cookie；2025-06 风控下可收弹幕）→ 心跳 30s → 消息解析为
/// 规范化 <see cref="LiveEvent"/>。
/// 容错继承 blivedm 语义：InitError（房间元数据/服务器发现瞬时失败）保持客户端存活重试；
/// AuthError 重新 init_room；连接断开按固定间隔重连（DEFAULT_RECONNECT_POLICY = 1s）。
/// 循环内任何异常不得逃逸（worker 不变量）。
/// </summary>
public sealed class BilibiliLivePlugin : ILivePlatformPlugin
{
    private static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly BilibiliApiClient _api;
    private readonly ILogBus? _logs;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _heartbeatInterval;
    private readonly Func<string, int, Uri> _wsUriFactory;
    private Channel<LiveEvent> _events;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BilibiliLivePlugin(
        BilibiliApiClient api,
        ILogBus? logs = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? heartbeatInterval = null,
        Func<string, int, Uri>? wsUriFactory = null)
    {
        _api = api;
        _logs = logs;
        _reconnectDelay = reconnectDelay ?? DefaultReconnectDelay;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        _wsUriFactory = wsUriFactory ?? ((host, port) => new Uri($"wss://{host}:{port}/sub"));
        _events = CreateEventChannel();
    }

    public string Key => "bilibili";

    public string DisplayName => "B站弹幕";

    public LiveCapabilities Capabilities =>
        LiveCapabilities.Danmaku | LiveCapabilities.Gift | LiveCapabilities.GuardBuy |
        LiveCapabilities.Enter | LiveCapabilities.Follow | LiveCapabilities.Share |
        LiveCapabilities.Like | LiveCapabilities.LiveState;

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
        if (string.IsNullOrWhiteSpace(config.RoomId))
        {
            throw new ArgumentException("请先在设置中填写 B站房间号（RoomId）");
        }

        lock (_sync)
        {
            if (_loop is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _events = CreateEventChannel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loop = Task.Run(() => RunLoopAsync(config, _cts.Token), CancellationToken.None);
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

    // ── 主循环：init（InitError 存活重试）→ 连接监听 → 断开重连 ─────────────────

    private async Task RunLoopAsync(AppConfig config, CancellationToken ct)
    {
        var roomId = config.RoomId.Trim();
        var needInit = true;
        BilibiliRoomInfo room = new() { RealRoomId = 0, OwnerUid = 0, LiveStatus = 0 };
        DanmuServerInfo server = DanmuServerInfo.Fallback;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (needInit)
                {
                    (room, server) = await InitRoomAsync(roomId, ct);
                    needInit = false;
                    _logs?.Information($"[B站] 房间 {room.RealRoomId} 初始化完成（主播 uid={room.OwnerUid}，弹幕服务器 {server.Host}:{server.WssPort}）");
                }

                await ConnectAndListenAsync(room, server, ct);
                // 连接正常关闭/断开 → 直接重连（无需重新 init）
                _logs?.Warning($"[B站] 连接断开，{_reconnectDelay.TotalSeconds:0.#}s 后重连");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (BilibiliAuthException ex)
            {
                // 认证失败：重新 init_room 拿新 token（blivedm AuthError 语义）
                needInit = true;
                _logs?.Warning($"[B站] 认证失败（{ex.Message}），重新初始化房间");
            }
            catch (BilibiliApiException ex)
            {
                // 房间元数据/服务器发现瞬时失败：保持客户端存活重试（blivedm InitError 语义）
                needInit = true;
                _logs?.Warning($"[B站] 房间初始化失败（{ex.Message}），{_reconnectDelay.TotalSeconds:0.#}s 后重试");
            }
            catch (WebSocketException ex)
            {
                // 连接级失败（连不上/被重置）：仅重连，无需重新 init（blivedm ClientConnectionError 语义）
                _logs?.Warning($"[B站] 连接失败（{ex.Message}），{_reconnectDelay.TotalSeconds:0.#}s 后重连");
            }
            catch (IOException ex)
            {
                _logs?.Warning($"[B站] 连接中断（{ex.Message}），{_reconnectDelay.TotalSeconds:0.#}s 后重连");
            }
            catch (Exception ex)
            {
                needInit = true;
                _logs?.Error($"[B站] 监听循环异常（已隔离，重连）：{ex.Message}", ex.ToString());
            }

            await DelayOrCancel(_reconnectDelay, ct);
        }
    }

    /// <summary>房间初始化链：buvid3 → get_info → getDanmuInfo（失败降级兜底服务器）。</summary>
    private async Task<(BilibiliRoomInfo Room, DanmuServerInfo Server)> InitRoomAsync(string roomId, CancellationToken ct)
    {
        var buvid3 = await _api.EnsureBuvid3Async(ct);
        if (buvid3 is null)
        {
            throw new BilibiliApiException("拿不到 buvid3（访问 bilibili.com 失败）");
        }

        var room = await _api.GetRoomInfoAsync(roomId, ct);
        if (room.RealRoomId <= 0)
        {
            throw new BilibiliApiException($"房间 {roomId} 不存在或解析失败");
        }

        DanmuServerInfo server;
        try
        {
            server = await _api.GetDanmuInfoAsync(room.RealRoomId, ct);
        }
        catch (Exception ex) when (ex is BilibiliApiException or HttpRequestException)
        {
            // 兜底默认服务器（docs/04 §2.2）
            server = DanmuServerInfo.Fallback;
            _logs?.Warning($"[B站] getDanmuInfo 失败（{ex.Message}），兜底 {server.Host}:{server.WssPort}");
        }

        return (room, server);
    }

    /// <summary>连接 + 认证 + 心跳 + 收帧解析；断开/认证失败时退出（由主循环决定重连策略）。</summary>
    private async Task ConnectAndListenAsync(BilibiliRoomInfo room, DanmuServerInfo server, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", BilibiliApiClient.UserAgent);
        ws.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
        var cookieHeader = _api.Cookies.ToCookieHeader(_api.Buvid3);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            ws.Options.SetRequestHeader("Cookie", cookieHeader);
        }

        await ws.ConnectAsync(_wsUriFactory(server.Host, server.WssPort), ct);

        // op=7 认证（2025-06 起必须带 buvid3/SESSDATA/bili_jct cookie；匿名 uid=0 + buvid3 也可收弹幕）
        var auth = JsonSerializer.Serialize(new
        {
            uid = AnonymousUid(),
            roomid = room.RealRoomId,
            protover = 3,
            platform = "web",
            type = 2,
            buvid = _api.Buvid3 ?? "",
            key = server.Token,
        });
        await ws.SendAsync(BiliFrame.Wrap(BiliFrame.OperationAuth, Encoding.UTF8.GetBytes(auth)), WebSocketMessageType.Binary, true, ct);
        _logs?.Debug($"[B站] 已连接 {server.Host}:{server.WssPort} 并发送认证（cookie: {(cookieHeader.Length > 0 ? cookieHeader.Length + " 字节" : "无")}）");

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(() => SendHeartbeatsAsync(ws, heartbeatCts.Token), CancellationToken.None);
        try
        {
            await ReceiveLoopAsync(ws, room, ct);
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private long AnonymousUid()
    {
        // 有登录态时用 DedeUserId 作为 uid；匿名保持 0
        return long.TryParse(_api.Cookies.DedeUserId, out var uid) && uid > 0 ? uid : 0;
    }

    private async Task SendHeartbeatsAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var hb = BiliFrame.Wrap(BiliFrame.OperationHeartbeat, []);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, ct);
                if (ws.State != WebSocketState.Open)
                {
                    break;
                }

                await ws.SendAsync(hb, WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 心跳失败不逃逸（连接断开由收帧循环发现）
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, BilibiliRoomInfo room, CancellationToken ct)
    {
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

            foreach (var (op, body) in BiliFrame.Unwrap(ms.ToArray()))
            {
                switch (op)
                {
                    case BiliFrame.OperationAuthReply:
                        HandleAuthReply(body);
                        break;
                    case BiliFrame.OperationSendMsgReply:
                        HandleBusinessMessage(body, room);
                        break;
                    case BiliFrame.OperationHeartbeatReply:
                        // 人气值：仅日志页展示用，忽略
                        break;
                    default:
                        _logs?.Debug($"[B站] 忽略未知 op={op}");
                        break;
                }
            }
        }
    }

    private void HandleAuthReply(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            if (code != 0)
            {
                throw new BilibiliAuthException($"认证被拒绝 code={code}");
            }

            _logs?.Debug("[B站] 认证通过（op=8）");
        }
        catch (JsonException ex)
        {
            throw new BilibiliAuthException($"认证回复解析失败：{ex.Message}");
        }
    }

    private void HandleBusinessMessage(byte[] body, BilibiliRoomInfo room)
    {
        if (body.Length == 0)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var evt = BilibiliMessageParser.Parse(
                doc.RootElement,
                room.OwnerUid,
                room.RealRoomId.ToString(),
                error => _logs?.Warning($"[B站] {error}"));
            if (evt is not null)
            {
                _events.Writer.TryWrite(evt);
            }
        }
        catch (JsonException ex)
        {
            // blivedm 语义：单条坏消息跳过，不拖垮连接（04 §2.2 消息解析面）
            _logs?.Warning($"[B站] 业务消息 JSON 解析失败（已跳过，连接保持）：{ex.Message}");
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
