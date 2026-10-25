using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Plugins;
using Erbai.Core.Hosting;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili;

/// <summary>认证失败（op=8 code!=0）→ 需要重新 init_room 拿新 token（blivedm AuthError 语义）。</summary>
public sealed class BilibiliAuthException : Exception
{
    public BilibiliAuthException(string message) : base(message)
    {
    }
}

/// <summary>收帧空闲超时：长时间收不到任何帧（含心跳回复）→ 判定半开连接失联，强制重连。</summary>
internal sealed class BilibiliIdleTimeoutException : Exception
{
    public BilibiliIdleTimeoutException(TimeSpan idle)
        : base($"超过 {idle.TotalSeconds:0.#}s 未收到任何帧")
    {
    }
}

/// <summary>
/// B站弹幕插件（阶段 3， + 04 §2）：房间初始化链 → WSS 连接 → op=7 认证
/// （匿名 uid=0 + buvid3 + Cookie；2025-06 风控下可收弹幕）→ 心跳 30s → 消息解析为
/// 规范化 <see cref="LiveEvent"/>。
/// 容错继承 blivedm 语义：InitError（房间元数据/服务器发现瞬时失败）保持客户端存活重试；
/// AuthError 重新 init_room；连接断开按指数退避重连（基值 1s，封顶 60s，±20% 抖动；
/// 连接稳定 ≥ <see cref="StableConnectionThreshold"/> 后失败计数清零，恢复基值）。
/// 收帧侧带空闲看门狗：<see cref="_readIdleTimeout"/> 内未收到任何帧（含心跳回复）
/// 判定半开连接失联，强制重连。
/// 循环内任何异常不得逃逸（worker 不变量）。
/// </summary>
public sealed class BilibiliLivePlugin : LivePluginBase
{
    private static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultReconnectMaxDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>收帧空闲超时默认值：3 × 心跳间隔——正常连接下心跳回复每 30s 必到，
    /// 超过 90s 一帧未见只可能是半开连接（TCP 未断但对端已失联）。</summary>
    private static readonly TimeSpan DefaultReadIdleTimeout = TimeSpan.FromSeconds(90);

    /// <summary>连接稳定判定阈值：单次连接存活超过该时长，重连失败计数清零（退避恢复基值）。</summary>
    internal static readonly TimeSpan StableConnectionThreshold = TimeSpan.FromSeconds(60);

    private readonly BilibiliApiClient _api;
    private readonly ILogBus? _logs;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _reconnectMaxDelay;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _readIdleTimeout;
    private readonly Func<string, int, Uri> _wsUriFactory;
    public BilibiliLivePlugin(
        BilibiliApiClient api,
        ILogBus? logs = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? heartbeatInterval = null,
        Func<string, int, Uri>? wsUriFactory = null,
        TimeSpan? reconnectMaxDelay = null,
        TimeSpan? readIdleTimeout = null)
    {
        _api = api;
        _logs = logs;
        _reconnectDelay = reconnectDelay ?? DefaultReconnectDelay;
        _reconnectMaxDelay = reconnectMaxDelay ?? DefaultReconnectMaxDelay;
        if (_reconnectMaxDelay < _reconnectDelay)
        {
            _reconnectMaxDelay = _reconnectDelay;
        }

        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        _readIdleTimeout = readIdleTimeout ?? DefaultReadIdleTimeout;
        _wsUriFactory = wsUriFactory ?? ((host, port) => new Uri($"wss://{host}:{port}/sub"));
    }

    public override string Key => "bilibili";

    public override string DisplayName => "B站弹幕";

    public override LiveCapabilities Capabilities =>
        LiveCapabilities.Danmaku | LiveCapabilities.Gift | LiveCapabilities.GuardBuy |
        LiveCapabilities.Enter | LiveCapabilities.Follow | LiveCapabilities.Share |
        LiveCapabilities.Like | LiveCapabilities.LiveState;

    public override Task StartAsync(AppConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.RoomId))
        {
            throw new ArgumentException("请先在设置中填写 B站房间号（RoomId）");
        }

        // LinkedCts：插件自有停机令牌 ⊕ 宿主启动令牌（StopAsync 走基类取消 _cts）
        StartLoop(ct => RunLoopAsync(config, CancellationTokenSource.CreateLinkedTokenSource(ct).Token));
        return Task.CompletedTask;
    }

    // ── 主循环：init（InitError 存活重试）→ 连接监听 → 断开重连（指数退避）──

    private async Task RunLoopAsync(AppConfig config, CancellationToken ct)
    {
        var roomId = config.RoomId.Trim();
        var needInit = true;
        BilibiliRoomInfo room = new() { RealRoomId = 0, OwnerUid = 0, LiveStatus = 0 };
        DanmuServerInfo server = DanmuServerInfo.Fallback;
        var failureStreak = 0;

        while (!ct.IsCancellationRequested)
        {
            var attemptStartedAt = DateTimeOffset.UtcNow;
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
                _logs?.Warning("[B站] 连接断开");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (BilibiliIdleTimeoutException ex)
            {
                // 空闲超时：半开连接（TCP 未断但服务器失联）→ 仅重连，无需重新 init
                _logs?.Warning($"[B站] {ex.Message}，判定连接失联，重连");
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
                _logs?.Warning($"[B站] 房间初始化失败（{ex.Message}），稍后重试");
            }
            catch (WebSocketException ex)
            {
                // 连接级失败（连不上/被重置）：仅重连，无需重新 init（blivedm ClientConnectionError 语义）
                _logs?.Warning($"[B站] 连接失败（{ex.Message}），稍后重连");
            }
            catch (IOException ex)
            {
                _logs?.Warning($"[B站] 连接中断（{ex.Message}），稍后重连");
            }
            catch (Exception ex)
            {
                needInit = true;
                _logs?.Error($"[B站] 监听循环异常（已隔离，重连）：{ex.Message}", ex.ToString());
            }

            // 稳定连接（≥ 阈值）后失败计数清零——直播间正常断流/关播场景退避回落到
            // 基值；连续快速失败（网络断/被风控）按 2^n 退避并封顶，避免每秒打满
            // init 链（buvid3 → get_info → getDanmuInfo + WBI 签名）触发风控
            failureStreak = DateTimeOffset.UtcNow - attemptStartedAt >= StableConnectionThreshold
                ? 0
                : failureStreak + 1;
            var delay = ComputeBackoffDelay(failureStreak, _reconnectDelay, _reconnectMaxDelay);
            _logs?.Information($"[B站] {delay.TotalSeconds:0.#}s 后重试（连续失败 {failureStreak} 次）");
            await DelayOrCancel(delay, ct);
        }
    }

    /// <summary>
    /// 重连退避：第 1 次失败用基值，之后 2^n 增长封顶 max；全程叠加 ±20% 抖动
    /// （防多客户端同步重连的惊群）。rng 可注入以便测试确定性。
    /// </summary>
    internal static TimeSpan ComputeBackoffDelay(int failureStreak, TimeSpan baseDelay, TimeSpan maxDelay, Random? rng = null)
    {
        var capped = failureStreak < 1
            ? baseDelay
            : TimeSpan.FromMilliseconds(Math.Min(
                baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(failureStreak - 1, 30)),
                maxDelay.TotalMilliseconds));
        return Jitter(capped, rng);
    }

    private static TimeSpan Jitter(TimeSpan delay, Random? rng)
    {
        var random = rng ?? Random.Shared;
        return TimeSpan.FromMilliseconds(Math.Max(0, delay.TotalMilliseconds * (0.8 + random.NextDouble() * 0.4)));
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
            // 兜底默认服务器
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
        // 空闲看门狗：正常连接下心跳回复每 30s 必到、弹幕持续流动；超时未收帧即半开
        // 连接（TCP 未断但服务器失联）——ReceiveAsync 会永远挂起，仅靠心跳写失败
        // 可能直到 TCP 超时（分钟级）才发现，此处主动判死重连
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    idleCts.CancelAfter(_readIdleTimeout);
                    try
                    {
                        result = await ws.ReceiveAsync(buffer, idleCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // idleCts 触发（ct 未取消）→ 空闲超时
                        throw new BilibiliIdleTimeoutException(_readIdleTimeout);
                    }
                    finally
                    {
                        // 收到帧即解除看门狗：解析/派发不得被残留计时器误杀
                        idleCts.CancelAfter(Timeout.InfiniteTimeSpan);
                    }

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
        finally
        {
            idleCts.CancelAfter(Timeout.InfiniteTimeSpan);
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
                EventWriter.TryWrite(evt);
            }
        }
        catch (JsonException ex)
        {
            // blivedm 语义：单条坏消息跳过，不拖垮连接（04 §2.2 消息解析面）
            _logs?.Warning($"[B站] 业务消息 JSON 解析失败（已跳过，连接保持）：{ex.Message}");
        }
    }

}
