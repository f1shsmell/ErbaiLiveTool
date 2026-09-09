using Erbai.Contracts.Live;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>LiveEvent → 点歌命令桥接（阶段 3）。</summary>
public class LiveEventBridgeTests
{
    private static LiveEvent Danmaku(string text, string platform = "bilibili", long uid = 100,
        string nickname = "观众甲", int? medalLevel = null, bool? isAdmin = null, bool? isAnchor = null) =>
        new()
        {
            Platform = platform,
            RoomId = "5050",
            Kind = LiveEventKind.Danmaku,
            UserId = uid,
            Nickname = nickname,
            Text = text,
            IsAdmin = isAdmin,
            IsAnchor = isAnchor,
            MedalLevel = medalLevel,
            Timestamp = DateTimeOffset.UtcNow,
        };

    [Fact]
    public void ToDanmakuContext_MapsAllFields()
    {
        var evt = Danmaku("点歌 晴天 - 周杰伦", uid: 42, medalLevel: 7, isAdmin: true, isAnchor: false);

        var ctx = LiveEventBridge.ToDanmakuContext(evt);

        Assert.Equal("点歌 晴天 - 周杰伦", ctx.Text);
        Assert.Equal("bilibili", ctx.Platform);
        Assert.Equal("5050", ctx.RoomId);
        Assert.Equal("42", ctx.UserId);
        Assert.Equal("观众甲", ctx.Nickname);
        Assert.True(ctx.IsAdmin);
        Assert.False(ctx.IsAnchor);
        Assert.Equal(7, ctx.MedalLevel);
        Assert.Null(ctx.FanLevel);
    }

    [Fact]
    public async Task RunAsync_DanmakuRequest_ReachesQueue()
    {
        var (store, bus, logs, config, queue, _, commands) = await TestHarness.CreateAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = LiveEventBridge.RunAsync(bus, commands, logs, cts.Token);
        using var sub = bus.Subscribe<Erbai.Contracts.Queue.QueueEventEnvelope>();

        bus.Publish(Danmaku("点歌 晴天 - 周杰伦", uid: 123, nickname: "弹幕观众"));

        var envelope = await TestHarness.AwaitEventAsync(sub, "queue.added", timeoutMs: 5000);

        Assert.NotNull(envelope);
        var request = envelope!.Data.QueueSnapshot!.Items.First().Request;
        Assert.Equal("晴天", request.SongName);
        Assert.Equal("bilibili", request.Platform);
        Assert.Equal("5050", request.RoomId);
        Assert.Equal("123", request.UserId);
        Assert.Equal("弹幕观众", request.Nickname);
    }

    [Fact]
    public async Task RunAsync_NonDanmakuEvents_AreIgnored()
    {
        var (store, bus, logs, config, queue, _, commands) = await TestHarness.CreateAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = LiveEventBridge.RunAsync(bus, commands, logs, cts.Token);

        bus.Publish(new LiveEvent
        {
            Platform = "bilibili",
            RoomId = "5050",
            Kind = LiveEventKind.Gift,
            UserId = 1,
            Nickname = "送礼人",
            GiftName = "辣条",
            GiftCount = 1,
            TotalCoin = 100,
            CoinType = "gold",
            Timestamp = DateTimeOffset.UtcNow,
        });

        await Task.Delay(200);

        var snapshot = await queue.GetSnapshotAsync();
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public async Task RunAsync_UnrelatedDanmaku_NotConsumed()
    {
        var (store, bus, logs, config, queue, _, commands) = await TestHarness.CreateAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = LiveEventBridge.RunAsync(bus, commands, logs, cts.Token);

        bus.Publish(Danmaku("普通闲聊弹幕"));

        await Task.Delay(200);

        var snapshot = await queue.GetSnapshotAsync();
        Assert.Empty(snapshot.Items);
    }
}
