using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Plugins;
using Erbai.Core.Events;
using Erbai.Core.Hosting;
using Erbai.Core.Logging;
using Erbai.Core.Storage;
using Erbai.Modules.GiftFx;

namespace Erbai.Modules.QueueUp.Tests;

/// <summary>
/// 阶段 5 退出标准「插件启停互不影响」：QueueUp 与 GiftFx 经 ModuleHost 并行装配，
/// 停止/重启任一模块不影响另一个的事件消费与队列状态。
/// </summary>
public class ModuleIsolationTests
{
    private static string TempDb() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "test.db");

    private static LiveEvent Danmaku(string text, long userId, string nickname = "观众") =>
        new()
        {
            Platform = "douyin",
            RoomId = "1",
            Kind = LiveEventKind.Danmaku,
            UserId = userId,
            Nickname = nickname,
            Text = text,
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static LiveEvent Gift(long userId, string nickname = "送礼人") =>
        new()
        {
            Platform = "douyin",
            RoomId = "1",
            Kind = LiveEventKind.Gift,
            UserId = userId,
            Nickname = nickname,
            GiftName = "小心心",
            GiftCount = 1,
            TotalCoin = 100,
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

    [Fact]
    public async Task StopOneModule_OtherKeepsWorking_RestartResumes()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var store = new SqliteStorageEngine(path);
        await store.OpenAsync();
        var bus = new EventBus();
        var logs = new LogBus();
        var config = AppConfig.CreateDefault() with
        {
            QueueUp = new QueueUpConfig
            {
                Rules =
                [
                    new QueueUpRuleConfig { MatchKind = "gift_name", MatchValue = "小心心", Action = "insert_at", Position = 1 },
                ],
            },
        };

        var queueUp = new QueueUpModule(store, bus, logs, () => config);
        var giftFx = new GiftFxModule(bus, logs, () => config);
        var hub = new FakeHub();

        var host = new ModuleHost();
        host.RegisterFeatureModule(queueUp);
        host.RegisterFeatureModule(giftFx);
        var context = new ModuleContext
        {
            EventBus = bus,
            Config = new Erbai.Core.Configuration.ConfigStore(path),
            Storage = store,
            Logs = logs,
            Overlay = hub,
        };
        await host.StartAllAsync(context, CancellationToken.None);

        // 双模块并行工作
        bus.Publish(Danmaku("排队 上麦", userId: 1));
        Assert.True(await PollAsync(() => queueUp.Service.Count == 1));
        bus.Publish(Gift(userId: 2)); // 同时触发：QueueUp 礼物插队 + GiftFx 广播
        Assert.True(await PollAsync(() => queueUp.Service.Count == 2));
        Assert.True(await PollAsync(() => hub.Count == 1));

        // 停 GiftFx → QueueUp 不受影响
        await host.StopModuleAsync("giftfx");
        Assert.Equal(ModuleState.Stopped, host.Modules.First(m => m.Key == "giftfx").State);
        Assert.Equal(ModuleState.Started, host.Modules.First(m => m.Key == "queueup").State);

        bus.Publish(Danmaku("排队 再来", userId: 3));
        Assert.True(await PollAsync(() => queueUp.Service.Count == 3));
        bus.Publish(Gift(userId: 4)); // QueueUp 礼物规则照常插队
        Assert.True(await PollAsync(() => queueUp.Service.Count == 4));
        await Task.Delay(150);
        Assert.Equal(1, hub.Count); // GiftFx 已停：不再发布

        // 停 QueueUp → 事件不再入队；重启 QueueUp 恢复
        await host.StopModuleAsync("queueup");
        bus.Publish(Danmaku("排队 停止后", userId: 5));
        await Task.Delay(150);
        Assert.Equal(4, queueUp.Service.Count);

        await host.StartModuleAsync("queueup", context, CancellationToken.None);
        bus.Publish(Danmaku("排队 恢复后", userId: 6));
        Assert.True(await PollAsync(() => queueUp.Service.Count == 5));

        // 重启 GiftFx 也恢复
        await host.StartModuleAsync("giftfx", context, CancellationToken.None);
        bus.Publish(Gift(userId: 7));
        Assert.True(await PollAsync(() => queueUp.Service.Count == 6));
        Assert.True(await PollAsync(() => hub.Count == 2));

        await host.DisposeAsync();
    }

    /// <summary>计数型 fake OverlayHub（GiftFx 测试用）。</summary>
    private sealed class FakeHub : Erbai.Contracts.Abstractions.IOverlayHub
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task PublishAsync(string channel, object payload, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }
}
