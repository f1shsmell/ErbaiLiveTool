using System.Text.Json;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Core.Logging;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Tests;

/// <summary>
/// 插件生命周期与协议链路（本地假 HTTP + 假 WS 服务器，不打真网）：
/// 房间初始化 → op=7 认证（匿名 buvid3）→ 弹幕帧 → LiveEvent；
/// InitError 存活重试 / AuthError 重新 init / 心跳 / 启停幂等。
/// </summary>
public class BilibiliLivePluginTests
{
    private static AppConfig Config(string roomId = "5050") =>
        AppConfig.CreateDefault() with { Platform = "bilibili", RoomId = roomId };

    private const string DanmakuJson =
        """{"cmd":"DANMU_MSG","info":[[0,25,16777215,0,1700000000,0,0,0,0,0,0,0,0,0,0,{}],"点歌 晴天 - 周杰伦",[123456,"爱上一只猫",0,0,0,10000,1,""],[5,"粉丝牌","房主",5050,0,0],[30,0,5805050,">50000"],0,0,0,0,0,0,0,[1]]}""";

    private static (FakeBiliWsServer Ws, FakeBiliHttpHandler Http, BilibiliLivePlugin Plugin) CreateAsync(
        TimeSpan? heartbeatInterval = null)
    {
        var ws = new FakeBiliWsServer();
        var http = new FakeBiliHttpHandler(ws.Port);
        var api = new BilibiliApiClient(http);
        var plugin = new BilibiliLivePlugin(
            api,
            new LogBus(),
            reconnectDelay: TimeSpan.FromMilliseconds(50),
            heartbeatInterval: heartbeatInterval,
            wsUriFactory: (host, port) => new Uri($"ws://{host}:{port}/sub"));
        return (ws, http, plugin);
    }

    [Fact]
    public async Task Start_AuthWithBuvid3AndRoom_ReceivesDanmakuEvent()
    {
        var (ws, http, plugin) = CreateAsync();
        await using (ws)
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);

            await ws.WaitForOpAsync(BiliFrame.OperationAuth);

            // 认证包：匿名 uid=0 + 房间号 + token + buvid
            var auth = ws.Received.First(x => x.Op == BiliFrame.OperationAuth).Json!;
            using var doc = JsonDocument.Parse(auth);
            Assert.Equal(0, doc.RootElement.GetProperty("uid").GetInt64());
            Assert.Equal(5050, doc.RootElement.GetProperty("roomid").GetInt64());
            Assert.Equal("tok-123", doc.RootElement.GetProperty("key").GetString());
            Assert.Equal("test-buvid-123", doc.RootElement.GetProperty("buvid").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("protover").GetInt32());

            // WS 握手必须带 buvid3 cookie（2025-06 风控）
            Assert.Contains(ws.CookieHeaders, h => h.Contains("buvid3=test-buvid-123", StringComparison.Ordinal));

            await ws.PushAsync(DanmakuJson);

            var evt = await plugin.Events.ReadAsync(new CancellationTokenSource(5000).Token);
            Assert.Equal(LiveEventKind.Danmaku, evt.Kind);
            Assert.Equal("爱上一只猫", evt.Nickname);
            Assert.Equal("点歌 晴天 - 周杰伦", evt.Text);
            Assert.Equal(123456L, evt.UserId);
            Assert.Equal(5, evt.MedalLevel);
            Assert.Equal("5050", evt.RoomId);
            Assert.Equal("bilibili", evt.Platform);
        }
    }

    [Fact]
    public async Task Start_BrotliFrame_ReceivesDanmaku()
    {
        var (ws, http, plugin) = CreateAsync();
        await using (ws)
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);
            await ws.WaitForOpAsync(BiliFrame.OperationAuth);
            await ws.PushAsync(DanmakuJson, brotli: true);

            var evt = await plugin.Events.ReadAsync(new CancellationTokenSource(5000).Token);
            Assert.Equal(LiveEventKind.Danmaku, evt.Kind);
            Assert.Equal("爱上一只猫", evt.Nickname);
        }
    }

    [Fact]
    public async Task Heartbeat_SentPeriodically()
    {
        var (ws, http, plugin) = CreateAsync(heartbeatInterval: TimeSpan.FromMilliseconds(200));
        await using (ws)
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);
            await ws.WaitForOpAsync(BiliFrame.OperationHeartbeat, timeoutMs: 5000);
        }
    }

    [Fact]
    public async Task InitError_RoomInfoFails_KeepsClientAliveAndRetries()
    {
        var (ws, http, plugin) = CreateAsync();
        http.FailRoomInfoTimes = 2; // 前两次失败（InitError 语义：保持存活重试）
        await using (ws)
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);

            // 重试后最终连上并认证
            await ws.WaitForOpAsync(BiliFrame.OperationAuth, timeoutMs: 10000);

            Assert.True(http.RoomInfoCalls >= 3, $"get_info 应重试至少 3 次，实际 {http.RoomInfoCalls}");
            Assert.True(plugin.IsRunning);
        }
    }

    [Fact]
    public async Task AuthRejected_ReInitsRoom_ThenRecovers()
    {
        var (ws, http, plugin) = CreateAsync();
        ws.AuthReject = true;
        await using (ws)
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);

            // 认证失败 → 重新 init_room（getDanmuInfo 再被调用）→ 重连
            await ws.WaitForCountAsync(BiliFrame.OperationAuth, 2, timeoutMs: 10000);
            Assert.True(http.DanmuInfoCalls >= 2, $"认证失败后应重新 init_room，getDanmuInfo 实际 {http.DanmuInfoCalls} 次");

            // 恢复正常：等第 3 次认证成功（重连完成）后再推弹幕
            ws.AuthReject = false;
            await ws.WaitForCountAsync(BiliFrame.OperationAuth, 3, timeoutMs: 10000);
            await ws.PushAsync(DanmakuJson);
            var evt = await plugin.Events.ReadAsync(new CancellationTokenSource(5000).Token);
            Assert.Equal("爱上一只猫", evt.Nickname);
        }
    }

    [Fact]
    public async Task Disconnect_ReconnectsWithoutReinit()
    {
        var (ws, http, plugin) = CreateAsync();
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);
            await ws.WaitForOpAsync(BiliFrame.OperationAuth);

            // 服务器主动断开 → 插件重连到同端口新服务器（连接断开无需重新 init_room）
            var callsAfterDisconnect = http.DanmuInfoCalls;
            var port = ws.Port;
            await ws.DisposeAsync();

            var ws2 = new FakeBiliWsServer(port);
            await using (ws2)
            {
                await ws2.WaitForOpAsync(BiliFrame.OperationAuth, timeoutMs: 10000);

                Assert.Equal(callsAfterDisconnect, http.DanmuInfoCalls);
                await ws2.PushAsync(DanmakuJson);
                var evt = await plugin.Events.ReadAsync(new CancellationTokenSource(5000).Token);
                Assert.Equal("爱上一只猫", evt.Nickname);
            }
        }
    }

    [Fact]
    public async Task StopAsync_Idempotent_CompletesChannel()
    {
        var (ws, http, plugin) = CreateAsync();
        await using (ws)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);
            await ws.WaitForOpAsync(BiliFrame.OperationAuth);

            await plugin.StopAsync();
            await plugin.StopAsync(); // 幂等

            Assert.False(plugin.IsRunning);
            await Assert.ThrowsAnyAsync<Exception>(() => plugin.Events.ReadAsync(new CancellationTokenSource(500).Token).AsTask());
        }
    }

    [Fact]
    public async Task Restart_AfterStop_RecreatesChannelAndConnects()
    {
        var (ws, http, plugin) = CreateAsync();
        await using (ws)
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);
            await ws.WaitForOpAsync(BiliFrame.OperationAuth);
            await plugin.StopAsync();

            await plugin.StartAsync(Config(), CancellationToken.None);
            await ws.WaitForCountAsync(BiliFrame.OperationAuth, 2);

            await ws.PushAsync(DanmakuJson);
            var evt = await plugin.Events.ReadAsync(new CancellationTokenSource(5000).Token);
            Assert.Equal(LiveEventKind.Danmaku, evt.Kind);
        }
    }

    [Fact]
    public async Task StartAsync_WithoutRoomId_Throws()
    {
        var (ws, http, plugin) = CreateAsync();
        await using (ws)
        await using (plugin)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => plugin.StartAsync(Config(roomId: ""), CancellationToken.None));
        }
    }

    [Fact]
    public async Task MalformedBusinessMessage_Skipped_ConnectionStaysAlive()
    {
        // blivedm 语义：单条坏消息跳过，不触发整链重连/重新 init_room（04 §2.2）
        var (ws, http, plugin) = CreateAsync();
        await using (ws)
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);
            await ws.WaitForOpAsync(BiliFrame.OperationAuth);
            var danmuInfoCalls = http.DanmuInfoCalls;

            // 非法 JSON 的业务帧 → 解析失败应被跳过
            await ws.PushAsync("not-json{{{");
            // 紧接的合法弹幕必须仍然到达（连接未被重置）
            await ws.PushAsync(DanmakuJson);

            var evt = await plugin.Events.ReadAsync(new CancellationTokenSource(5000).Token);
            Assert.Equal(LiveEventKind.Danmaku, evt.Kind);
            Assert.Equal("爱上一只猫", evt.Nickname);

            // 给重连留观察窗：若坏消息曾逃逸触发 needInit，getDanmuInfo 会被再次调用
            await Task.Delay(300);
            Assert.Equal(danmuInfoCalls, http.DanmuInfoCalls);
        }
    }

    [Fact]
    public async Task Capabilities_IncludeDanmakuAndGift()
    {
        var (ws, http, plugin) = CreateAsync();
        await using (ws)
        await using (plugin)
        {
            Assert.True(plugin.Capabilities.HasFlag(LiveCapabilities.Danmaku));
            Assert.True(plugin.Capabilities.HasFlag(LiveCapabilities.Gift));
            Assert.True(plugin.Capabilities.HasFlag(LiveCapabilities.LiveState));
        }
    }
}
