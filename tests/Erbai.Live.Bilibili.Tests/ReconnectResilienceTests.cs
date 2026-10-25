using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Core.Logging;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Tests;

/// <summary>
/// 连接韧性（2026-10 改进）：指数退避重连 + 收帧空闲看门狗。
/// 旧行为：断开后固定 1s 重连——网络断/房间关闭时每秒打满 init 链（buvid3 →
/// get_info → getDanmuInfo + WBI 签名），有触发风控风险；半开连接（TCP 未断但
/// 服务器失联）永远挂起在 ReceiveAsync 上。新行为见 BilibiliLivePlugin 类注释。
/// </summary>
public class ReconnectResilienceTests
{
    // ---- ComputeBackoffDelay（纯函数，确定性 rng）----

    [Fact]
    public void Backoff_FirstFailure_UsesBaseDelay()
    {
        var delay = BilibiliLivePlugin.ComputeBackoffDelay(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), new Random(42));
        Assert.InRange(delay.TotalMilliseconds, 800, 1200); // 基值 ±20%
    }

    [Fact]
    public void Backoff_GrowsExponentially()
    {
        var rng = new Random(42);
        var d1 = BilibiliLivePlugin.ComputeBackoffDelay(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), rng);
        var d2 = BilibiliLivePlugin.ComputeBackoffDelay(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), rng);
        var d3 = BilibiliLivePlugin.ComputeBackoffDelay(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), rng);
        Assert.InRange(d2.TotalSeconds, 1.6, 2.4); // 2s ±20%
        Assert.InRange(d3.TotalSeconds, 3.2, 4.8); // 4s ±20%
    }

    [Fact]
    public void Backoff_CappedAtMaxDelay()
    {
        var rng = new Random(42);
        for (var streak = 1; streak <= 30; streak++)
        {
            var delay = BilibiliLivePlugin.ComputeBackoffDelay(streak, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), rng);
            Assert.InRange(delay.TotalSeconds, 0.8, 60 * 1.2); // 永不超过 max ±20% 抖动
        }
    }

    [Fact]
    public void Backoff_CappedAtMaxEvenIfMaxBelowBase()
    {
        // 构造参数守卫保证 max ≥ base（生产不可达）；纯函数面对 max < base 也安全：封顶在 max
        var delay = BilibiliLivePlugin.ComputeBackoffDelay(5, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), new Random(42));
        Assert.InRange(delay.TotalMilliseconds, 800, 1200);
    }

    [Fact]
    public void Backoff_JitterStaysWithin20Percent()
    {
        var rng = new Random(7);
        for (var i = 0; i < 50; i++)
        {
            var delay = BilibiliLivePlugin.ComputeBackoffDelay(1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), rng);
            Assert.InRange(delay.TotalMilliseconds, 8000, 12000);
        }
    }

    // ---- 收帧空闲看门狗（假服务器连接后保持静默）----

    private static AppConfig Config(string roomId = "5050") =>
        AppConfig.CreateDefault() with { Platform = "bilibili", RoomId = roomId };

    [Fact]
    public async Task IdleTimeout_SilentServer_ReconnectsWithoutReinit()
    {
        var (ws, http, plugin) = CreateAsync(readIdleTimeout: TimeSpan.FromMilliseconds(200));
        await using (ws)
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);
            await ws.WaitForOpAsync(BiliFrame.OperationAuth);

            // 服务器此后保持完全静默（假服务器只应答认证，不回复心跳）：
            // 200ms 无任何帧 → 看门狗判死 → 重连（同端口新连接再认证）
            await ws.WaitForCountAsync(BiliFrame.OperationAuth, 2, timeoutMs: 10000);

            // 无需重新 init_room：连接失联不重走 HTTP 初始化链
            // （首次 init 链计 1 次；若看门狗误触发 needInit 则会变为 2+）
            Assert.Equal(1, http.DanmuInfoCalls);
        }
    }

    [Fact]
    public async Task IdleTimeout_ActiveTraffic_KeepsConnectionAlive()
    {
        var (ws, http, plugin) = CreateAsync(readIdleTimeout: TimeSpan.FromMilliseconds(300));
        await using (ws)
        await using (plugin)
        {
            await plugin.StartAsync(Config(), CancellationToken.None);
            await ws.WaitForOpAsync(BiliFrame.OperationAuth);

            // 持续有帧流动（间隔远小于看门狗阈值）→ 连接必须保持，不重连
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(100);
                await ws.PushAsync(
                    """{"cmd":"DANMU_MSG","info":[[0,25,16777215,0,1700000000,0,0,0,0,0,0,0,0,0,0,{}],"hi",[123456,"猫",0,0,0,10000,1,""],[5,"粉丝牌","房主",5050,0,0],[30,0,5805050,">50000"],0,0,0,0,0,0,0,[1]]}""");
            }

            Assert.Equal(1, http.DanmuInfoCalls); // 从未重新 init
            var evt = await plugin.Events.ReadAsync(new CancellationTokenSource(5000).Token);
            Assert.Equal(LiveEventKind.Danmaku, evt.Kind);
        }
    }

    private static (FakeBiliWsServer Ws, FakeBiliHttpHandler Http, BilibiliLivePlugin Plugin) CreateAsync(
        TimeSpan? readIdleTimeout = null)
    {
        var ws = new FakeBiliWsServer();
        var http = new FakeBiliHttpHandler(ws.Port);
        var api = new BilibiliApiClient(http);
        var plugin = new BilibiliLivePlugin(
            api,
            new LogBus(),
            reconnectDelay: TimeSpan.FromMilliseconds(50),
            wsUriFactory: (host, port) => new Uri($"ws://{host}:{port}/sub"),
            readIdleTimeout: readIdleTimeout);
        return (ws, http, plugin);
    }
}
