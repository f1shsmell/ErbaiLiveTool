using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Queue;
using Erbai.Contracts.QueueUp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erbai.Web;

/// <summary>Overlay Hub 通用广播信封（IOverlayHub 通道 → WS 的进程内中转）。</summary>
internal sealed record OverlayHubEnvelope(string Channel, JsonElement Payload, DateTimeOffset Timestamp);

/// <summary>
/// Overlay HTTP/WS 服务（docs/04 §4.1 保留设计，决策 #6 只服务 overlay）：
/// - 页面 /overlay、/overlay/default.css；公开端点 /api/v1/overlay/config；
/// - /api/v1/* 与 /api/v1/events(WS) 无鉴权（2026-09 移除 overlay token：服务器回环绑定
///   127.0.0.1 已隔离外部网络；OBS 浏览器源为同机应用；业界弹幕姬/点歌机本地页均无鉴权）；
///   回环绑定；
/// - WS 事件信封 {event, version, timestamp, data}，连接即发 connected，
///   心跳 30s；慢客户端保护：每 socket 发送锁 + 5s 超时即断开；
/// - 阶段 5 扩展：实现 IOverlayHub（giftfx.play 等模块通道广播）+
///   queueup.* 事件流 + queueup 快照缓存端点；
/// - 端口被占逐级上探 20 个。
/// </summary>
public sealed class OverlayServer : IOverlayHub, IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;
    private readonly CancellationTokenSource _serverCts = new();
    private WebApplication? _app;
    private int _boundPort;

    public OverlayServer(AppConfig config, IEventBus eventBus)
    {
        _config = config;
        _eventBus = eventBus;
    }

    public int BoundPort => _boundPort;

    /// <summary>启动（端口上探 20 个）；失败抛异常由组合根处理。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var basePort = Math.Clamp(_config.Ui.Port, 1, 65535);
        Exception? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var port = basePort + attempt;
            if (port > 65535)
            {
                break;
            }

            try
            {
                _app = BuildApp(port);
                await _app.StartAsync(ct);
                _boundPort = port;
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (_app is not null)
                {
                    await _app.DisposeAsync();
                }

                _app = null;
            }
        }

        throw new InvalidOperationException(
            $"无法绑定 overlay 端口（{basePort}–{basePort + 19} 全部被占）: {lastError?.Message}");
    }

    private WebApplication BuildApp(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port); // 回环绑定
        });

        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/overlay", () => Results.Content(OverlayHtml, "text/html; charset=utf-8"));
        app.MapGet("/overlay/default.css", () => Results.Content(OverlayCss, "text/css; charset=utf-8"));
        // 独立悬浮窗页面（决策 #17：点歌/排队/弹幕各一个 HTML，前端按事件前缀过滤；/overlay 保留 OBS 全合一）
        app.MapGet("/overlay/queue", () => Results.Content(QueueOverlayHtml, "text/html; charset=utf-8"));
        app.MapGet("/overlay/queueup", () => Results.Content(QueueUpOverlayHtml, "text/html; charset=utf-8"));
        app.MapGet("/overlay/danmaku", () => Results.Content(DanmakuOverlayHtml, "text/html; charset=utf-8"));
        app.MapGet("/api/v1/overlay/config", () => Results.Json(OverlayConfigJson()));

        app.MapGet("/api/v1/queue", () => Results.Json(LastSnapshot));

        app.MapGet("/api/v1/queueup", () => Results.Json(LastQueueUpSnapshot));

        app.MapGet("/api/v1/overlay/lyric", () =>
        {
            // MVP：歌词服务（lxmusic /lyric 代理）阶段 2 后接入，先返回空歌词
            return Results.Json(new { lines = Array.Empty<object>(), metadata = new { } });
        });

        app.Map("/api/v1/events", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await RunWebSocketLoopAsync(socket, context.RequestAborted);
        });

        return app;
    }

    // ---- WS 事件循环（信封 + 心跳 + 慢客户端保护）----

    private async Task RunWebSocketLoopAsync(WebSocket socket, CancellationToken ct)
    {
        // 消费接收端（处理控制帧/close 帧），连接关闭时结束——不消费则
        // close 握手不完成，Kestrel 停止时挂住
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _serverCts.Token);
        var receiveTask = ReceiveLoopAsync(socket, linked.Token);

        // 连接即发 connected
        var connected = new
        {
            @event = "connected",
            version = 1,
            timestamp = DateTimeOffset.UtcNow,
        };
        if (!await TrySendAsync(socket, connected, ct))
        {
            return;
        }

        using var subscription = _eventBus.Subscribe<QueueEventEnvelope>(capacity: 128);
        using var queueUpSubscription = _eventBus.Subscribe<QueueUpEventEnvelope>(capacity: 128);
        using var hubSubscription = _eventBus.Subscribe<OverlayHubEnvelope>(capacity: 128);
        // 直播事件流（决策 #17：弹幕悬浮窗经 live.* 事件驱动；与 queue.*/queueup.*/hub 同信封）
        using var liveSubscription = _eventBus.Subscribe<LiveEvent>(capacity: 256);
        using var heartbeatCts = new CancellationTokenSource();
        using var linkedHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, heartbeatCts.Token);
        var heartbeatTask = HeartbeatLoopAsync(socket, heartbeatCts, linkedHeartbeat.Token);
        // 接收端完成（客户端已发 Close/断线）→ 取消主循环，尽快回帧完成握手
        _ = receiveTask.ContinueWith(_ => heartbeatCts.Cancel(), TaskScheduler.Default);

        try
        {
            // 四流合并转发（queue.* / queueup.* / IOverlayHub 通道 / live.*），事件信封同构。
            // 每个流的 ReadAsync 任务必须"完成后再补充下一读"——若每轮循环新建，
            // 上一轮悬挂的 ReadAsync 会抢走新到达的数据且无人 await（数据静默丢失）。
            var queueRead = subscription.Reader.ReadAsync(linkedHeartbeat.Token).AsTask();
            var queueUpRead = queueUpSubscription.Reader.ReadAsync(linkedHeartbeat.Token).AsTask();
            var hubRead = hubSubscription.Reader.ReadAsync(linkedHeartbeat.Token).AsTask();
            var liveRead = liveSubscription.Reader.ReadAsync(linkedHeartbeat.Token).AsTask();

            while (!linkedHeartbeat.IsCancellationRequested)
            {
                var done = await Task.WhenAny(queueRead, queueUpRead, hubRead, liveRead);

                object payload;
                if (done == queueRead)
                {
                    var envelope = await queueRead;
                    payload = ToEnvelope(envelope.Event, envelope.Version, envelope.Timestamp, envelope.Data);
                    queueRead = subscription.Reader.ReadAsync(linkedHeartbeat.Token).AsTask();
                }
                else if (done == queueUpRead)
                {
                    var envelope = await queueUpRead;
                    payload = ToEnvelope(envelope.Event, envelope.Version, envelope.Timestamp, envelope.Data);
                    queueUpRead = queueUpSubscription.Reader.ReadAsync(linkedHeartbeat.Token).AsTask();
                }
                else if (done == liveRead)
                {
                    var liveEvent = await liveRead;
                    payload = ToEnvelope(
                        $"live.{liveEvent.Kind.ToString().ToLowerInvariant()}",
                        1,
                        liveEvent.Timestamp,
                        ToLivePayload(liveEvent));
                    liveRead = liveSubscription.Reader.ReadAsync(linkedHeartbeat.Token).AsTask();
                }
                else
                {
                    var envelope = await hubRead;
                    payload = ToEnvelope(envelope.Channel, 1, envelope.Timestamp, envelope.Payload);
                    hubRead = hubSubscription.Reader.ReadAsync(linkedHeartbeat.Token).AsTask();
                }

                if (!await TrySendAsync(socket, payload, linkedHeartbeat.Token))
                {
                    break; // 发送超时/断开 → 断开此客户端（慢客户端保护）
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            try
            {
                await receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            // 客户端已发 Close 帧（CloseReceived）：必须回帧完成握手，否则
            // 客户端 CloseAsync 挂住；Open 状态则主动关闭
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closing",
                        CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>后台读 socket：处理控制帧，收到 Close 帧/异常即结束。</summary>
    private static async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private async Task HeartbeatLoopAsync(WebSocket socket, CancellationTokenSource heartbeatCts, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                // 应用层心跳：特殊 JSON，overlay JS 过滤 type=ping 不触发 reload。
                // 走发送锁+5s 超时（慢客户端保护同样约束心跳，不绕过）。
                if (!await TrySendAsync(socket, new { type = "ping" }, ct))
                {
                    heartbeatCts.Cancel(); // 心跳失败 → 断开
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>发送锁 + 5s 超时（防慢客户端阻塞广播方）。</summary>
    private async Task<bool> TrySendAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var sendLock = _sendLocks.GetOrAdd(socket, _ => new SemaphoreSlim(1, 1));
        if (!await sendLock.WaitAsync(0))
        {
            // 上一个发送还在进行（5s 内未完成）→ 视为慢客户端
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            sendLock.Release();
            if (socket.State != WebSocketState.Open)
            {
                _sendLocks.TryRemove(socket, out _);
            }
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<WebSocket, SemaphoreSlim> _sendLocks = new();

    // ---- 快照缓存（事件附快照 → 最新快照供 /api/v1/queue、/api/v1/queueup 与 overlay 初载）----

    private readonly object _snapshotLock = new();
    private QueueSnapshot? _lastSnapshot;
    private QueueUpSnapshot? _lastQueueUpSnapshot;

    private static object ToEnvelope(string eventName, int version, DateTimeOffset timestamp, object data) =>
        new
        {
            @event = eventName,
            version,
            timestamp,
            data,
        };

    /// <summary>LiveEvent → live.* 事件负载（选择悬浮窗 UI 需要的字段；平台无关）。</summary>
    private static object ToLivePayload(LiveEvent evt) => new
    {
        platform = evt.Platform,
        kind = evt.Kind.ToString().ToLowerInvariant(),
        nickname = evt.Nickname,
        text = evt.Text,
        giftName = evt.GiftName,
        giftCount = evt.GiftCount,
        totalCoin = evt.TotalCoin,
        coinType = evt.CoinType,
        isAdmin = evt.IsAdmin,
        isAnchor = evt.IsAnchor,
        fanLevel = evt.FanLevel,
        medalLevel = evt.MedalLevel,
        timestamp = evt.Timestamp,
    };

    private QueueSnapshot LastSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _lastSnapshot ?? new QueueSnapshot
                {
                    Items = [],
                    QueueTotal = 0,
                    QueueLimit = null,
                    DisplayLimit = null,
                    CurrentRequestId = null,
                };
            }
        }
    }

    private QueueUpSnapshot LastQueueUpSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _lastQueueUpSnapshot ?? new QueueUpSnapshot
                {
                    Items = [],
                    Total = 0,
                    MaxEntries = 0,
                };
            }
        }
    }

    /// <summary>订阅队列/排队事件并缓存最新快照（订阅同步建立，StartAsync 后立即发布不丢）。
    /// 循环绑定 _serverCts：Dispose 后退出并释放订阅，避免反复创建 OverlayServer 泄漏后台任务。</summary>
    public void StartSnapshotCache()
    {
        var sub = _eventBus.Subscribe<QueueEventEnvelope>(capacity: 128);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in sub.Reader.ReadAllAsync(_serverCts.Token))
                {
                    if (envelope.Data.QueueSnapshot is { } snapshot)
                    {
                        lock (_snapshotLock)
                        {
                            _lastSnapshot = snapshot;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                sub.Dispose();
            }
        });

        var queueUpSub = _eventBus.Subscribe<QueueUpEventEnvelope>(capacity: 128);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in queueUpSub.Reader.ReadAllAsync(_serverCts.Token))
                {
                    if (envelope.Data.Snapshot is { } snapshot)
                    {
                        lock (_snapshotLock)
                        {
                            _lastQueueUpSnapshot = snapshot;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                queueUpSub.Dispose();
            }
        });
    }

    /// <summary>IOverlayHub：模块通道广播（giftfx.play 等）→ WS 事件信封 {event: channel}。</summary>
    public Task PublishAsync(string channel, object payload, CancellationToken ct = default)
    {
        _eventBus.Publish(new OverlayHubEnvelope(
            channel,
            JsonSerializer.SerializeToElement(payload, JsonOptions),
            DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    private object OverlayConfigJson() => new
    {
        theme = _config.Overlay.Theme,
        customCss = _config.Overlay.CustomCss,
        showCurrent = _config.Overlay.ShowCurrent,
        showQueue = _config.Overlay.ShowQueue,
        showLyric = _config.Overlay.ShowLyric,
        transparent = _config.Overlay.Transparent,
    };

    public async ValueTask DisposeAsync()
    {
        _serverCts.Cancel(); // 先取消 WS 处理循环，Kestrel 才能干净停止
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }

        _serverCts.Dispose();
    }

    // ---- overlay 页面（MVP 最小面：当前播放 + 队列；WS 收到任何消息即 reload）----

    // ---- 独立悬浮窗页面（决策 #17）：与 /overlay 同源页面，前端按事件前缀过滤 ----
    // 点歌悬浮窗：正在播放 + 点歌队列。

    private const string QueueOverlayHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>点歌悬浮窗</title>
<style>
  html, body { margin: 0; background: transparent; overflow: hidden; pointer-events: none;
               font-family: "Microsoft YaHei", sans-serif; color: #fff; }
  /* 透明底悬浮（决策 #17 悬浮窗透明底，2026-08-29）：去容器底色/边框/阴影，
     仅文字悬浮；白色文字靠 text-shadow 保证叠在任意桌面上可读 */
  .card {
    background: transparent;
    padding: 12px 16px; border-radius: 12px; margin: 8px;
  }
  .label { font-size: 11px; letter-spacing: 2px; opacity: .55; margin-bottom: 6px;
           text-shadow: 0 1px 3px rgba(0,0,0,.8); }
  #current { border-left: 3px solid #ff7b54; }
  .playing-note { color: #ff7b54; }
  .title {
    font-size: 23px; font-weight: 800; white-space: nowrap;
    overflow: hidden; text-overflow: ellipsis; text-shadow: 0 2px 6px rgba(0,0,0,.9);
  }
  .singer { font-size: 13px; opacity: .78; margin-top: 2px; text-shadow: 0 1px 3px rgba(0,0,0,.9); }
  #queue-list { margin: 6px 0 0; padding: 0; list-style: none; }
  #queue-list li {
    padding: 5px 9px; margin: 2px 0; border-radius: 7px; font-size: 13.5px;
    display: flex; gap: 8px; align-items: baseline;
    text-shadow: 0 1px 3px rgba(0,0,0,.9);
    animation: fadein .35s ease;
  }
  #queue-list .pos { color: #7fd0ff; font-weight: 700; min-width: 22px; text-align: right; flex: none; }
  #queue-list .song { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .hidden { display: none; }
  @keyframes fadein { from { opacity: 0; transform: translateY(4px); } to { opacity: 1; } }
</style>
</head>
<body>
<div id="app">
  <div id="current" class="card hidden"><div class="label"><span class="playing-note">♪</span> 正在播放</div>
    <div id="current-title" class="title">-</div><div id="current-singer" class="singer">-</div></div>
  <div id="queue" class="card hidden"><div class="label">点歌队列</div><ol id="queue-list"></ol></div>
</div>
<script>
const TOKEN = new URLSearchParams(location.search).get('token') || '';
const api = (path) => fetch(path + (TOKEN ? '?token=' + encodeURIComponent(TOKEN) : ''));
function load() { api('/api/v1/queue').then(r => r.json()).then(render).catch(() => {}); }
function render(snapshot) {
  const cur = (snapshot.items || []).find(i => i.is_current);
  const showCurrent = document.querySelector('#current');
  if (cur) {
    showCurrent.classList.remove('hidden');
    document.querySelector('#current-title').textContent = cur.request.songName;
    document.querySelector('#current-singer').textContent = cur.request.singer || '';
  } else { showCurrent.classList.add('hidden'); }
  const list = document.querySelector('#queue-list');
  list.innerHTML = '';
  for (const item of (snapshot.items || []).slice(0, snapshot.displayLimit || 5)) {
    const li = document.createElement('li');
    const pos = document.createElement('span');
    pos.className = 'pos';
    pos.textContent = item.position + '.';
    const song = document.createElement('span');
    song.className = 'song';
    song.textContent = item.request.songName + (item.request.singer ? ' - ' + item.request.singer : '');
    li.appendChild(pos);
    li.appendChild(song);
    list.appendChild(li);
  }
  document.querySelector('#queue').classList.toggle('hidden', !(snapshot.items && snapshot.items.length));
}
function connect() {
  const ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/api/v1/events' + (TOKEN ? '?token=' + encodeURIComponent(TOKEN) : ''));
  ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    if (msg.type === 'ping') return;
    if (msg.event && msg.event.startsWith('queue.')) load();
  };
  ws.onclose = () => setTimeout(connect, 3000);
}
load(); connect();
</script>
</body>
</html>
""";

    // 排队看板悬浮窗：排队队列全量展示（渐入刷新，适合放直播间角落）。

    private const string QueueUpOverlayHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>排队看板</title>
<style>
  html, body { margin: 0; background: transparent; overflow: hidden; pointer-events: none;
               font-family: "Microsoft YaHei", sans-serif; color: #fff; }
  /* 排队看板透明底（2026-08-29）：去容器底色/边框/阴影，仅文字悬浮 */
  #queueup {
    background: transparent;
    padding: 14px 16px 12px; border-radius: 12px; margin: 8px;
    max-height: calc(100vh - 16px); display: flex; flex-direction: column;
    overflow: hidden;
  }
  .label { font-size: 11px; letter-spacing: 2px; opacity: .55; margin-bottom: 8px; flex: none;
           text-shadow: 0 1px 3px rgba(0,0,0,.8); }
  #queueup-list { margin: 0; padding: 0; list-style: none; font-size: 14.5px; line-height: 1.55;
                  overflow: hidden; display: flex; flex-direction: column; gap: 3px;
                  /* 底部渐隐蒙版（BiliQueue 防伪循环闪烁） */
                  -webkit-mask-image: linear-gradient(#000 78%, transparent);
                  mask-image: linear-gradient(#000 78%, transparent); }
  #queueup-list li { display: flex; align-items: center; gap: 8px;
                     padding: 4px 8px; border-radius: 7px;
                     text-shadow: 0 1px 3px rgba(0,0,0,.9);
                     animation: fadein .35s ease; }
  .badge {
    min-width: 24px; height: 24px; border-radius: 50%; flex: none;
    display: inline-flex; align-items: center; justify-content: center;
    background: linear-gradient(135deg, #ffd76a, #ff9a3c); color: #241c00;
    font-size: 12px; font-weight: 800;
  }
  .nick { font-weight: 600; max-width: 35%; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .content { opacity: .9; flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .gift-tag { flex: none; color: #ff9a6c; font-size: 12px; font-weight: 700;
              background: rgba(255,120,80,.14); padding: 1px 8px; border-radius: 6px; }
  @keyframes fadein { from { opacity: 0; transform: translateX(8px); } to { opacity: 1; } }
  .hidden { display: none; }
</style>
</head>
<body>
<div id="queueup" class="card hidden"><div class="label">排队队列</div><ol id="queueup-list"></ol></div>
<script>
const TOKEN = new URLSearchParams(location.search).get('token') || '';
const api = (path) => fetch(path + (TOKEN ? '?token=' + encodeURIComponent(TOKEN) : ''));
function render(snapshot) {
  const card = document.querySelector('#queueup');
  const list = document.querySelector('#queueup-list');
  list.innerHTML = '';
  for (const item of (snapshot.items || [])) {
    const li = document.createElement('li');
    const badge = document.createElement('span');
    badge.className = 'badge';
    badge.textContent = item.position;
    const nick = document.createElement('span');
    nick.className = 'nick';
    nick.textContent = item.entry.nickname || '';
    const content = document.createElement('span');
    content.className = 'content';
    content.textContent = item.entry.content || '(占位)';
    li.appendChild(badge);
    li.appendChild(nick);
    li.appendChild(content);
    if (item.entry.source === 'gift') {
      const tag = document.createElement('span');
      tag.className = 'gift-tag';
      tag.textContent = '礼物';
      li.appendChild(tag);
    }
    list.appendChild(li);
  }
  card.classList.toggle('hidden', !(snapshot.items && snapshot.items.length));
}
function load() { api('/api/v1/queueup').then(r => r.json()).then(render).catch(() => {}); }
function connect() {
  const ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/api/v1/events' + (TOKEN ? '?token=' + encodeURIComponent(TOKEN) : ''));
  ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    if (msg.type === 'ping') return;
    if (msg.event && msg.event.startsWith('queueup.')) render(msg.data.snapshot);
  };
  ws.onclose = () => setTimeout(connect, 3000);
}
load(); connect();
</script>
</body>
</html>
""";

    // OBS 弹幕页（浏览器源 /overlay/danmaku；桌面悬浮窗已是 WPF bililive_dm 同款，见
    // Erbai.OverlayWpf——此处 Web 页保留轨道滚动供 OBS 采集）：滚动弹幕（TrackPool 轨道
    // 防碰撞 —— danmaku-kernel DanmakuYSlotManager 的 JS 移植：像素行占位数组 + 线性扫描
    // 空行带，找不到空轨道退化为随机 Y；节点池 = danmaku-kernel ObjectPoolManager 的
    // 对象复用思想）+ 礼物横幅。

    private const string DanmakuOverlayHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>弹幕悬浮窗</title>
<style>
  html, body { margin: 0; background: transparent; overflow: hidden; height: 100%; pointer-events: none;
               font-family: "Microsoft YaHei", sans-serif; }
  #stage { position: fixed; inset: 0; pointer-events: none; }
  /* 弹幕透明气泡（2026-08-29）：去底色/边框/阴影，仅文字悬浮；双 text-shadow
     做描边感+光晕，保证叠加在直播画面/桌面上可读 */
  .danmaku {
    position: absolute; left: 0; top: 0; white-space: nowrap;
    display: inline-flex; align-items: center;
    border-radius: 10px;
    padding: 4px 12px;
    font-size: 26px; font-weight: 700; color: #fff; will-change: transform;
    text-shadow: 0 2px 3px rgba(0,0,0,.9), 0 0 10px rgba(0,0,0,.45);
  }
  .danmaku .badge {
    font-size: 12px; font-weight: 700; padding: 0 7px; border-radius: 4px;
    margin-right: 6px; flex: none;
  }
  .badge.admin  { background: rgba(90,220,255,.22); color: #6adcff; }
  .badge.anchor { background: rgba(255,184,77,.24); color: #ffc46b; }
  .danmaku .who { margin-right: 4px; }
  .who.admin  { color: #6adcff; }
  .who.anchor { color: #ffc46b; }
  .gift-banner {
    position: fixed; left: 50%; top: 12%; transform: translateX(-50%);
    background: linear-gradient(135deg, rgba(255,90,120,.92), rgba(255,150,60,.92));
    padding: 12px 26px; border-radius: 12px; color: #fff;
    font-size: 22px; font-weight: 700; white-space: nowrap;
    box-shadow: 0 8px 30px rgba(0,0,0,.45); pointer-events: none;
  }
</style>
</head>
<body>
<div id="stage"></div>
<script>
// ---- TrackPool：danmaku-kernel/src/Danmaku.Core/DanmakuYSlotManager.cs 的 JS 移植 ----
// 像素行占位数组；GetY 线性扫描找连续 height 空行（撞上占用行跳到该行末尾剪枝），
// 找不到空槽退化为随机 Y（允许短暂重叠）；ReleaseY 归还占位。
const pool = {
  total: 0,
  occupied: new Uint8Array(0),
  updateSize(h) {
    this.total = Math.max(1, Math.floor(h || window.innerHeight));
    this.occupied = new Uint8Array(this.total);
  },
  getY(height) {
    height = Math.max(1, Math.floor(height));
    if (height >= this.total) return { y: 0, ok: false };
    let index = 0;
    // <=：index+height == total 的贴底轨道也合法（danmaku-kernel 的 < 边界会漏掉最底部一条）
    while (index + height <= this.total) {
      let found = true;
      for (let i = 0; i < height; i++) {
        if (this.occupied[index + i] > 0) { found = false; index = index + i + 1; break; }
      }
      if (found) {
        for (let i = 0; i < height; i++) this.occupied[index + i] = 1;
        return { y: index, ok: true };
      }
    }
    return { y: Math.floor(Math.random() * Math.max(0, this.total - height)), ok: false };
  },
  releaseY(y, height) {
    height = Math.max(1, Math.floor(height));
    for (let i = 0; i < height; i++) {
      const row = y + i;
      if (row >= 0 && row < this.total) this.occupied[row] = 0;
    }
  }
};

// ---- 节点池（danmaku-kernel ObjectPoolManager 的 DOM 版：复用节点避免重复 layout）----
const nodePool = [];
function rentNode() { return nodePool.pop() || document.createElement('div'); }
function returnNode(n) {
  n.textContent = ''; n.className = ''; n.style.cssText = ''; n.remove();
  if (nodePool.length < 200) nodePool.push(n);
}

const stage = document.getElementById('stage');
let activeCount = 0;
const MAX_ACTIVE = 300; // 弹幕风暴保护：同时滚动上限，超出丢弃（防止无限 append）

function fireDanmaku(d) {
  const text = String(d.text || '').trim();
  if (!text) return;
  if (activeCount >= MAX_ACTIVE) return;
  activeCount++;
  const node = rentNode();
  node.className = 'danmaku';
  // 昵称带身份徽章（管理员 青 / 主播 金橙；blivechat 式用户徽章）
  if (d.nickname) {
    const badge = document.createElement('span');
    badge.className = 'badge';
    if (d.isAdmin) { badge.classList.add('admin'); badge.textContent = '管理员'; }
    else if (d.isAnchor) { badge.classList.add('anchor'); badge.textContent = '主播'; }
    const who = document.createElement('span');
    who.className = 'who';
    if (d.isAdmin) who.classList.add('admin');
    else if (d.isAnchor) who.classList.add('anchor');
    who.textContent = d.nickname + '：';
    if (badge.textContent) node.appendChild(badge);
    node.appendChild(who);
  }
  const content = document.createElement('span');
  content.textContent = text;
  node.appendChild(content);
  stage.appendChild(node);
  const w = node.offsetWidth, h = node.offsetHeight;
  const { y } = pool.getY(h);
  node.style.top = y + 'px';
  const vw = window.innerWidth;
  const speed = 140; // px/s
  const anim = node.animate(
    [{ transform: 'translate3d(' + vw + 'px,0,0)' }, { transform: 'translate3d(' + (-w) + 'px,0,0)' }],
    { duration: ((vw + w) / speed) * 1000, easing: 'linear' });
  anim.onfinish = () => { activeCount--; pool.releaseY(y, h); returnNode(node); };
}

function fireGift(d) {
  const banner = document.createElement('div');
  banner.className = 'gift-banner';
  banner.textContent = (d.nickname || '') + ' 送出 ' + (d.giftName || '礼物') + ' ×' + (d.giftCount || 1);
  document.body.appendChild(banner);
  setTimeout(() => banner.remove(), 4000);
}

function connect() {
  const TOKEN = new URLSearchParams(location.search).get('token') || '';
  const ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/api/v1/events' + (TOKEN ? '?token=' + encodeURIComponent(TOKEN) : ''));
  ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    if (msg.type === 'ping') return;
    if (msg.event === 'live.danmaku') fireDanmaku(msg.data);
    else if (msg.event === 'live.gift') fireGift(msg.data);
  };
  ws.onclose = () => setTimeout(connect, 3000);
}

pool.updateSize(window.innerHeight);
window.addEventListener('resize', () => pool.updateSize(window.innerHeight));
connect();
</script>
</body>
</html>
""";

    private const string OverlayHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>点歌 Overlay</title>
<link rel="stylesheet" href="/overlay/default.css">
<style>
  /* 空状态/错误提示（2026-09：页面空白可诊断——空队列或服务未启动不再无声） */
  #hint { position: fixed; left: 14px; bottom: 12px; font-size: 12px;
          color: rgba(255,255,255,.45); text-shadow: 0 1px 3px rgba(0,0,0,.9); pointer-events: none; }
  #hint.err { color: rgba(255,140,120,.95); font-weight: 600; }
</style>
</head>
<body>
<div id="app">
  <div id="current" class="card hidden"><div class="label">正在播放</div>
    <div id="current-title" class="title">-</div><div id="current-singer" class="singer">-</div></div>
  <div id="queue" class="card hidden"><div class="label">点歌队列</div><ol id="queue-list"></ol></div>
  <div id="queueup" class="card hidden"><div class="label">排队队列</div><ol id="queueup-list"></ol></div>
</div>
<div id="hint" class="hint">等待点歌 / 排队…</div>
<div id="giftfx-layer"></div>
<script>
const TOKEN = new URLSearchParams(location.search).get('token') || '';
const api = (path) => fetch(path + (TOKEN ? '?token=' + encodeURIComponent(TOKEN) : ''));
function load() {
  api('/api/v1/queue').then(r => {
    if (!r.ok) throw new Error('http ' + r.status); // 401（无 token/错 token）→ 走错误提示
    return r.json();
  }).then(render).catch(() => {
    showHint('Overlay 连接失败：请确认主程序已启动（概览页「复制 Overlay URL」）', true);
  });
  api('/api/v1/queueup').then(r => {
    if (!r.ok) throw new Error('http ' + r.status);
    return r.json();
  }).then(renderQueueUp).catch(() => {});
}
// 空状态/错误提示：queue 与 queueup 都空 → 「等待点歌 / 排队…」；任一有数据 → 隐藏
let queueEmpty = true, queueUpEmpty = true;
function showHint(text, isErr) {
  const hint = document.querySelector('#hint');
  hint.textContent = text;
  hint.classList.toggle('err', !!isErr);
  hint.classList.remove('hidden');
}
function updateHint() {
  const hint = document.querySelector('#hint');
  if (queueEmpty && queueUpEmpty) {
    hint.textContent = '等待点歌 / 排队…';
    hint.classList.remove('err');
    hint.classList.remove('hidden');
  } else {
    hint.classList.add('hidden');
  }
}
function render(snapshot) {
  const showCurrent = document.querySelector('#current');
  const showQueue = document.querySelector('#queue');
  const cur = snapshot.items && snapshot.items.find(i => i.is_current);
  if (cur) {
    showCurrent.classList.remove('hidden');
    document.querySelector('#current-title').textContent = cur.request.songName;
    document.querySelector('#current-singer').textContent = cur.request.singer || '';
  } else { showCurrent.classList.add('hidden'); }
  const list = document.querySelector('#queue-list');
  list.innerHTML = '';
  for (const item of (snapshot.items || []).slice(0, snapshot.displayLimit || 5)) {
    const li = document.createElement('li');
    li.textContent = `${item.position}. ${item.request.songName}${item.request.singer ? ' - ' + item.request.singer : ''}`;
    list.appendChild(li);
  }
  showQueue.classList.toggle('hidden', !(snapshot.items && snapshot.items.length));
  queueEmpty = !(snapshot.items && snapshot.items.length);
  updateHint();
}
function renderQueueUp(snapshot) {
  const card = document.querySelector('#queueup');
  const list = document.querySelector('#queueup-list');
  list.innerHTML = '';
  for (const item of (snapshot.items || [])) {
    const li = document.createElement('li');
    const content = item.entry.content || '(占位)';
    const tag = item.entry.source === 'gift' ? ' [礼物]' : '';
    li.textContent = `${item.position}. ${item.entry.nickname}：${content}${tag}`;
    list.appendChild(li);
  }
  card.classList.toggle('hidden', !(snapshot.items && snapshot.items.length));
  queueUpEmpty = !(snapshot.items && snapshot.items.length);
  updateHint();
}
function playGiftFx(payload) {
  const layer = document.querySelector('#giftfx-layer');
  const banner = document.createElement('div');
  banner.className = 'giftfx-banner';
  banner.textContent = `${payload.nickname} 送出 ${payload.gift} ×${payload.count}`;
  layer.appendChild(banner);
  const duration = Math.max(1, payload.durationSeconds || 5) * 1000;
  setTimeout(() => banner.remove(), duration);
}
function connect() {
  const ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/api/v1/events' + (TOKEN ? '?token=' + encodeURIComponent(TOKEN) : ''));
  ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    if (msg.type === 'ping') return;                       // 心跳不触发
    if (msg.event === 'giftfx.play') { playGiftFx(msg.data); return; }
    if (msg.event && msg.event.startsWith('queueup.')) { renderQueueUp(msg.data.snapshot); return; }
    load();                                                 // queue.* 等 → 整体 reload（旧版语义）
  };
  ws.onclose = () => setTimeout(connect, 3000);
}
load(); connect();
</script>
</body>
</html>
""";

    private const string OverlayCss = """

body { margin: 0; font-family: "Microsoft YaHei", sans-serif; background: transparent; }
.card { background: rgba(0,0,0,.55); color: #fff; padding: 10px 14px; border-radius: 8px; margin: 8px; }
.label { font-size: 12px; opacity: .7; margin-bottom: 4px; }
.title { font-size: 22px; font-weight: 700; }
.singer { font-size: 14px; opacity: .85; }
#queue-list, #queueup-list { margin: 0; padding-left: 18px; font-size: 14px; }
.hidden { display: none; }

/* 礼物特效渲染层（阶段 5 演示横幅：CSS 动画 + 资产扩展点；素材体系后续版本） */
#giftfx-layer { position: fixed; inset: 0; pointer-events: none; overflow: hidden; z-index: 100; }
.giftfx-banner {
  position: absolute; left: 50%; top: 22%;
  transform: translateX(-50%);
  background: linear-gradient(135deg, rgba(255,90,120,.92), rgba(255,150,60,.92));
  padding: 16px 30px; border-radius: 14px;
  color: #fff; font-size: 24px; font-weight: 700;
  box-shadow: 0 8px 30px rgba(0,0,0,.45);
  white-space: nowrap;
  animation: giftfx-in .45s cubic-bezier(.2,1.4,.4,1);
}
@keyframes giftfx-in {
  from { opacity: 0; transform: translateX(-50%) scale(.6); }
  to   { opacity: 1; transform: translateX(-50%) scale(1); }
}
""";
}


