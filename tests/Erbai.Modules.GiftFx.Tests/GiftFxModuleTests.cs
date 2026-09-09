using System.Text.Json;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Plugins;
using Erbai.Core.Events;
using Erbai.Core.Logging;

namespace Erbai.Modules.GiftFx.Tests;

/// <summary>
/// 礼物特效框架通道（docs/01 §3.8）：GiftEvent → 串行化播放队列 →
/// OverlayHub `giftfx.play {template, nickname, gift, count, durationSeconds}`；
/// 开关热生效；非 Gift 忽略；overlay 未装配不崩溃；启停独立。
/// </summary>
public class GiftFxModuleTests
{
    private static AppConfig Config(bool enabled = true, int durationSeconds = 5) =>
        AppConfig.CreateDefault() with
        {
            GiftFx = new GiftFxConfig { Enabled = enabled, DurationSeconds = durationSeconds },
        };

    private static LiveEvent Gift(string giftName = "火箭", int count = 1, long userId = 1, string nickname = "送礼人") =>
        new()
        {
            Platform = "douyin",
            RoomId = "1",
            Kind = LiveEventKind.Gift,
            UserId = userId,
            Nickname = nickname,
            GiftName = giftName,
            GiftCount = count,
            TotalCoin = 1000,
            CoinType = "gold",
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static async Task<bool> PollAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static async Task<GiftFxModule> StartModuleAsync(
        EventBus bus, LogBus logs, AppConfig config, IOverlayHub? overlay, CancellationToken ct = default)
    {
        var module = new GiftFxModule(bus, logs, () => config);
        await module.StartAsync(new ModuleContext
        {
            EventBus = bus,
            Config = new Erbai.Core.Configuration.ConfigStore(Path.Combine(AppContext.BaseDirectory, "tmp",
                $"erbai-tests-{Guid.NewGuid():N}", "config.json")),
            Storage = null!, // GiftFx 不访问存储
            Logs = logs,
            Overlay = overlay,
        }, ct);
        return module;
    }

    [Fact]
    public async Task GiftEvent_PublishesGiftFxPlay_WithFullPayload()
    {
        var bus = new EventBus();
        var hub = new FakeOverlayHub();
        var module = await StartModuleAsync(bus, new LogBus(), Config(durationSeconds: 7), hub);
        try
        {
            bus.Publish(Gift(giftName: "小心心", count: 3, nickname: "小红"));

            Assert.True(await PollAsync(() => hub.Count == 1));
            var (channel, payload) = hub.At(0);
            Assert.Equal("giftfx.play", channel);
            Assert.Equal("banner", payload.GetProperty("template").GetString());
            Assert.Equal("小红", payload.GetProperty("nickname").GetString());
            Assert.Equal("小心心", payload.GetProperty("gift").GetString());
            Assert.Equal(3, payload.GetProperty("count").GetInt32());
            Assert.Equal(7, payload.GetProperty("durationSeconds").GetInt32());
        }
        finally
        {
            await module.DisposeAsync();
        }
    }

    [Fact]
    public async Task Serialization_OneAtATime_InOrder()
    {
        var bus = new EventBus();
        // 慢 hub（每个 40ms）：若串行化失效，顺序会乱或并发交错
        var hub = new FakeOverlayHub { DelayMs = 40 };
        var module = await StartModuleAsync(bus, new LogBus(), Config(), hub);
        try
        {
            for (var i = 1; i <= 5; i++)
            {
                bus.Publish(Gift(giftName: $"礼物{i}", userId: i, nickname: $"观众{i}"));
            }

            Assert.True(await PollAsync(() => hub.Count == 5, timeoutMs: 5000));
            for (var i = 0; i < 5; i++)
            {
                var (channel, payload) = hub.At(i);
                Assert.Equal("giftfx.play", channel);
                Assert.Equal($"礼物{i + 1}", payload.GetProperty("gift").GetString());
            }
        }
        finally
        {
            await module.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disabled_HotReload_StopsPublishing_ReEnableResumes()
    {
        var bus = new EventBus();
        var hub = new FakeOverlayHub();
        var config = Config();
        var module = new GiftFxModule(bus, new LogBus(), () => config);
        await module.StartAsync(new ModuleContext
        {
            EventBus = bus,
            Config = new Erbai.Core.Configuration.ConfigStore(Path.Combine(AppContext.BaseDirectory, "tmp",
                $"erbai-tests-{Guid.NewGuid():N}", "config.json")),
            Storage = null!,
            Logs = new LogBus(),
            Overlay = hub,
        }, CancellationToken.None);
        try
        {
            bus.Publish(Gift());
            Assert.True(await PollAsync(() => hub.Count == 1));

            // 热关闭：后续礼物丢弃
            config = Config(enabled: false);
            bus.Publish(Gift(giftName: "被丢弃", userId: 2));
            await Task.Delay(150);
            Assert.Equal(1, hub.Count);

            // 热重开：恢复发布
            config = Config();
            bus.Publish(Gift(giftName: "恢复", userId: 3));
            Assert.True(await PollAsync(() => hub.Count == 2));
            Assert.Equal("恢复", hub.At(1).Payload.GetProperty("gift").GetString());
        }
        finally
        {
            await module.DisposeAsync();
        }
    }

    [Fact]
    public async Task NonGiftEvents_Ignored()
    {
        var bus = new EventBus();
        var hub = new FakeOverlayHub();
        var module = await StartModuleAsync(bus, new LogBus(), Config(), hub);
        try
        {
            bus.Publish(new LiveEvent
            {
                Platform = "douyin", RoomId = "1", Kind = LiveEventKind.Danmaku,
                UserId = 1, Nickname = "甲", Text = "点歌 晴天", Timestamp = DateTimeOffset.UtcNow,
            });
            bus.Publish(new LiveEvent
            {
                Platform = "douyin", RoomId = "1", Kind = LiveEventKind.Enter,
                UserId = 2, Nickname = "乙", Timestamp = DateTimeOffset.UtcNow,
            });
            await Task.Delay(150);
            Assert.Equal(0, hub.Count);
        }
        finally
        {
            await module.DisposeAsync();
        }
    }

    [Fact]
    public async Task OverlayNotAssembled_NoCrash()
    {
        var bus = new EventBus();
        var logs = new LogBus();
        var module = await StartModuleAsync(bus, logs, Config(), overlay: null);
        try
        {
            bus.Publish(Gift());
            await Task.Delay(150); // 事件被消费并跳过，不异常不挂起
            Assert.Equal("giftfx", module.Key);
        }
        finally
        {
            await module.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_StopsConsumption_RestartResumes()
    {
        var bus = new EventBus();
        var hub = new FakeOverlayHub();
        var module = await StartModuleAsync(bus, new LogBus(), Config(), hub);
        try
        {
            bus.Publish(Gift(giftName: "第一个"));
            Assert.True(await PollAsync(() => hub.Count == 1));

            await module.DisposeAsync();

            bus.Publish(Gift(giftName: "停止后"));
            await Task.Delay(150);
            Assert.Equal(1, hub.Count);

            // 重启后恢复
            await module.StartAsync(new ModuleContext
            {
                EventBus = bus,
                Config = new Erbai.Core.Configuration.ConfigStore(Path.Combine(AppContext.BaseDirectory, "tmp",
                    $"erbai-tests-{Guid.NewGuid():N}", "config.json")),
                Storage = null!,
                Logs = new LogBus(),
                Overlay = hub,
            }, CancellationToken.None);
            bus.Publish(Gift(giftName: "重启后"));
            Assert.True(await PollAsync(() => hub.Count == 2));
            Assert.Equal("重启后", hub.At(1).Payload.GetProperty("gift").GetString());
        }
        finally
        {
            await module.DisposeAsync();
        }
    }
}
