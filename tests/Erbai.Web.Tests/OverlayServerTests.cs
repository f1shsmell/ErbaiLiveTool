using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Queue;
using Erbai.Contracts.QueueUp;
using Erbai.Contracts.Requests;
using Erbai.Core.Events;

namespace Erbai.Web.Tests;

/// <summary>
/// Overlay HTTP/WS（docs/04 §4.1）：
/// 公开端点（2026-09 起无 token——回环绑定 + OBS 同机）、端口上探、
/// WS 事件信封（connected + queue.* 附快照）。
/// </summary>
public class OverlayServerTests
{
    private static AppConfig Config(int? port = null) =>
        AppConfig.CreateDefault() with
        {
            Ui = new UiConfig
            {
                Port = port ?? Random.Shared.Next(21000, 28000), // 测试并行互不冲突
                Host = "127.0.0.1",
            },
        };

    private static async Task<(OverlayServer Server, EventBus Bus, string BaseUrl)> StartAsync(AppConfig config)
    {
        var bus = new EventBus();
        var server = new OverlayServer(config, bus);
        await server.StartAsync();
        server.StartSnapshotCache();
        return (server, bus, $"http://127.0.0.1:{server.BoundPort}");
    }

    [Fact]
    public async Task QueueEndpoint_OpenWithoutToken()
    {
        // 2026-09：overlay token 已移除——无 token 直接 200（回环绑定已隔离外网）
        var (server, bus, baseUrl) = await StartAsync(Config());
        using var client = new HttpClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{baseUrl}/api/v1/queue")).StatusCode);

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OverlayConfig_Public_ReturnsFields()
    {
        var (server, bus, baseUrl) = await StartAsync(Config());
        using var client = new HttpClient();

        var body = await client.GetStringAsync($"{baseUrl}/api/v1/overlay/config");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("clash", doc.RootElement.GetProperty("theme").GetString());
        Assert.True(doc.RootElement.GetProperty("showQueue").GetBoolean());

        // 页面
        var html = await client.GetStringAsync($"{baseUrl}/overlay");
        Assert.Contains("点歌 Overlay", html);
        Assert.Contains("/api/v1/events", html);
        // 空状态/错误提示（2026-09：空白可诊断——空队列显示占位、token 缺失显示错误）
        Assert.Contains("等待点歌", html);
        Assert.Contains("Overlay 连接失败", html);

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OverlayWindowPages_AllServed()
    {
        var (server, bus, baseUrl) = await StartAsync(Config());
        using var client = new HttpClient();

        // 独立悬浮窗页面（决策 #17）：点歌/排队/弹幕各一个 HTML，前端按事件前缀过滤
        var queueHtml = await client.GetStringAsync($"{baseUrl}/overlay/queue");
        Assert.Contains("点歌悬浮窗", queueHtml);
        Assert.Contains("正在播放", queueHtml);
        Assert.Contains("/api/v1/events", queueHtml);

        var queueUpHtml = await client.GetStringAsync($"{baseUrl}/overlay/queueup");
        Assert.Contains("排队看板", queueUpHtml);
        Assert.Contains("queueup", queueUpHtml);

        var danmakuHtml = await client.GetStringAsync($"{baseUrl}/overlay/danmaku");
        Assert.Contains("弹幕悬浮窗", danmakuHtml);
        Assert.Contains("live.danmaku", danmakuHtml);
        Assert.Contains("TrackPool", danmakuHtml); // danmaku-kernel YSlot 移植的轨道分配

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PortOccupied_BindsNextPort()
    {
        // 占住一个随机端口，验证 overlay 逐级上探到 port+1
        var port = Random.Shared.Next(21000, 28000);
        using var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        blocker.Start();

        var (server, bus, baseUrl) = await StartAsync(Config(port: port));

        Assert.Equal(port + 1, server.BoundPort); // 逐级上探
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{baseUrl}/api/v1/overlay/config")).StatusCode);

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        blocker.Stop();
    }

    [Fact]
    public async Task WebSocket_ConnectedEnvelope_ThenQueueEventsWithSnapshot()
    {
        var (server, bus, baseUrl) = await StartAsync(Config());
        var wsUrl = baseUrl.Replace("http://", "ws://") + "/api/v1/events";
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // 连接即发 connected
        var connected = await ReceiveTextAsync(socket).WaitAsync(TimeSpan.FromSeconds(5));
        using var connectedDoc = JsonDocument.Parse(connected);
        Assert.Equal("connected", connectedDoc.RootElement.GetProperty("event").GetString());
        Assert.Equal(1, connectedDoc.RootElement.GetProperty("version").GetInt32());

        // 发布 queue.added → WS 收到信封（内嵌完整快照）
        bus.Publish(new QueueEventEnvelope
        {
            Event = "queue.added",
            Timestamp = DateTimeOffset.UtcNow,
            Data = new QueueEventData
            {
                Request = new SongRequest
                {
                    Platform = "douyin",
                    UserId = "u1",
                    SongName = "晴天",
                    Singer = "周杰伦",
                    Status = RequestStatus.Queued,
                },
                QueueSnapshot = new QueueSnapshot
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Request = new SongRequest
                            {
                                Platform = "douyin",
                                UserId = "u1",
                                SongName = "晴天",
                                Singer = "周杰伦",
                                Status = RequestStatus.Queued,
                            },
                            Position = 1,
                            IsCurrent = true,
                        },
                    ],
                    QueueTotal = 1,
                    QueueLimit = 20,
                    DisplayLimit = 5,
                    CurrentRequestId = 1,
                },
            },
        });

        var envelope = await ReceiveTextAsync(socket).WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("queue.added", doc.RootElement.GetProperty("event").GetString());
        var snapshot = doc.RootElement.GetProperty("data").GetProperty("queueSnapshot");
        Assert.Equal(1, snapshot.GetProperty("queueTotal").GetInt32());
        Assert.Equal(20, snapshot.GetProperty("queueLimit").GetInt32());

        // /api/v1/queue 返回缓存的最新快照
        using var client = new HttpClient();
        var queueBody = await client.GetStringAsync($"{baseUrl}/api/v1/queue");
        using var queueDoc = JsonDocument.Parse(queueBody);
        Assert.Equal(1, queueDoc.RootElement.GetProperty("queueTotal").GetInt32());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var builder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return builder.ToString();
    }

    // ---- 阶段 5：queueup.* 事件流 + IOverlayHub 通道广播 + /api/v1/queueup ----

    [Fact]
    public async Task WebSocket_QueueUpEvents_ForwardedWithSnapshot()
    {
        var (server, bus, baseUrl) = await StartAsync(Config());
        var wsUrl = baseUrl.Replace("http://", "ws://") + "/api/v1/events";
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var connected = await ReceiveTextAsync(socket).WaitAsync(TimeSpan.FromSeconds(5));
        using var connectedDoc = JsonDocument.Parse(connected);
        Assert.Equal("connected", connectedDoc.RootElement.GetProperty("event").GetString());

        bus.Publish(new QueueUpEventEnvelope
        {
            Event = "queueup.added",
            Timestamp = DateTimeOffset.UtcNow,
            Data = new QueueUpEventData
            {
                Entry = new QueueUpEntry
                {
                    Id = 1,
                    UserId = "douyin:1:1",
                    Nickname = "甲",
                    Content = "上麦唱一首",
                    Source = QueueUpSource.Danmaku,
                    Status = QueueUpStatus.Queued,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                Snapshot = new QueueUpSnapshot
                {
                    Items =
                    [
                        new QueueUpItem
                        {
                            Entry = new QueueUpEntry
                            {
                                Id = 1,
                                UserId = "douyin:1:1",
                                Nickname = "甲",
                                Content = "上麦唱一首",
                                Source = QueueUpSource.Danmaku,
                                Status = QueueUpStatus.Queued,
                                CreatedAt = DateTimeOffset.UtcNow,
                            },
                            Position = 1,
                        },
                    ],
                    Total = 1,
                    MaxEntries = 50,
                },
            },
        });

        var envelope = await ReceiveTextAsync(socket).WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("queueup.added", doc.RootElement.GetProperty("event").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("甲", data.GetProperty("entry").GetProperty("nickname").GetString());
        Assert.Equal(1, data.GetProperty("snapshot").GetProperty("total").GetInt32());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WebSocket_HubChannelBroadcast_GiftFxPlayForwarded()
    {
        var (server, bus, baseUrl) = await StartAsync(Config());
        var wsUrl = baseUrl.Replace("http://", "ws://") + "/api/v1/events";
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var connected = await ReceiveTextAsync(socket).WaitAsync(TimeSpan.FromSeconds(5));

        // 模块经 IOverlayHub 广播 giftfx.play
        await ((Erbai.Contracts.Abstractions.IOverlayHub)server).PublishAsync("giftfx.play", new
        {
            template = "banner",
            nickname = "小红",
            gift = "小心心",
            count = 3,
            durationSeconds = 7,
        });

        var envelope = await ReceiveTextAsync(socket).WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("giftfx.play", doc.RootElement.GetProperty("event").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("banner", data.GetProperty("template").GetString());
        Assert.Equal("小红", data.GetProperty("nickname").GetString());
        Assert.Equal("小心心", data.GetProperty("gift").GetString());
        Assert.Equal(3, data.GetProperty("count").GetInt32());
        Assert.Equal(7, data.GetProperty("durationSeconds").GetInt32());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QueueUpEndpoint_OpenAndSnapshotCache()
    {
        var (server, bus, baseUrl) = await StartAsync(Config());
        using var client = new HttpClient();

        // 2026-09：无 token，直接 200
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{baseUrl}/api/v1/queueup")).StatusCode);

        // 发布 queueup.added → 快照缓存 → 端点返回
        bus.Publish(new QueueUpEventEnvelope
        {
            Event = "queueup.added",
            Timestamp = DateTimeOffset.UtcNow,
            Data = new QueueUpEventData
            {
                Snapshot = new QueueUpSnapshot
                {
                    Items = [],
                    Total = 3,
                    MaxEntries = 50,
                },
            },
        });
        await Task.Delay(200); // 缓存异步写入

        var body = await client.GetStringAsync($"{baseUrl}/api/v1/queueup");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(50, doc.RootElement.GetProperty("maxEntries").GetInt32());

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_ReleasesSnapshotCacheSubscriptions()
    {
        // 🟡 阶段 5 修复：StartSnapshotCache 的两条后台循环必须随 Dispose 退出并
        // 释放订阅（修复前循环无取消、Subscription 不释放，反复创建 OverlayServer 泄漏）
        var (server, bus, baseUrl) = await StartAsync(Config());
        Assert.Equal(2, bus.SubscriberCount); // queue + queueup 快照缓存订阅

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // 取消 _serverCts → 循环异步退出 → 订阅释放（轮询等待）
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (bus.SubscriberCount > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(0, bus.SubscriberCount);
    }

    [Fact]
    public async Task WebSocket_LiveEvents_ForwardedAsLiveChannel()
    {
        // 决策 #17：平台事件流（B站/抖音 LiveEvent）→ live.* 信封，弹幕悬浮窗数据源
        var (server, bus, baseUrl) = await StartAsync(Config());
        var wsUrl = baseUrl.Replace("http://", "ws://") + "/api/v1/events";
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // 连接即发 connected
        var connected = await ReceiveTextAsync(socket).WaitAsync(TimeSpan.FromSeconds(5));
        using (var connectedDoc = JsonDocument.Parse(connected))
        {
            Assert.Equal("connected", connectedDoc.RootElement.GetProperty("event").GetString());
        }

        bus.Publish(new LiveEvent
        {
            Platform = "bilibili",
            RoomId = "123",
            Kind = LiveEventKind.Danmaku,
            UserId = 42,
            Nickname = "测试观众",
            Text = "点一首歌",
            IsAdmin = true,
            Timestamp = DateTimeOffset.UtcNow,
        });

        var envelope = await ReceiveTextAsync(socket).WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal("live.danmaku", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("测试观众", data.GetProperty("nickname").GetString());
        Assert.Equal("点一首歌", data.GetProperty("text").GetString());
        Assert.True(data.GetProperty("isAdmin").GetBoolean());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}

