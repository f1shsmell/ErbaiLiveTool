using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Plugins;
using Erbai.Contracts.QueueUp;
using Erbai.Core.Events;
using Erbai.Core.Logging;
using Erbai.Core.Storage;

namespace Erbai.Modules.QueueUp.Tests;

/// <summary>
/// 模块级端到端（docs/01 §3.7）：LiveEvent 弹幕命令（排队/取消排队/完成）→
/// 队列变化；「完成」仅 admin/anchor；Gift 事件走规则引擎（插队/授资格）；
/// 模块启停独立（Dispose 后事件不再消费，重启恢复队列）。
/// </summary>
public class QueueUpModuleTests
{
    private static string TempDb() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "test.db");

    private static async Task<(SqliteStorageEngine Store, EventBus Bus, LogBus Logs, AppConfig Config, QueueUpModule Module)> CreateAsync(
        AppConfig? config = null)
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new SqliteStorageEngine(path);
        await store.OpenAsync();
        var bus = new EventBus();
        var logs = new LogBus();
        var effective = config ?? AppConfig.CreateDefault();
        var module = new QueueUpModule(store, bus, logs, () => effective);
        await module.StartAsync(new ModuleContext
        {
            EventBus = bus,
            Config = new Erbai.Core.Configuration.ConfigStore(path),
            Storage = store,
            Logs = logs,
            Overlay = null,
        }, CancellationToken.None);
        return (store, bus, logs, effective, module);
    }

    private static LiveEvent Danmaku(string text, string nickname = "观众", long userId = 1,
        bool isAdmin = false, bool isAnchor = false) =>
        new()
        {
            Platform = "douyin",
            RoomId = "1",
            Kind = LiveEventKind.Danmaku,
            UserId = userId,
            Nickname = nickname,
            Text = text,
            IsAdmin = isAdmin,
            IsAnchor = isAnchor,
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static LiveEvent Gift(string giftName = "火箭", long totalCoin = 1000,
        string coinType = "gold", long userId = 2, string nickname = "送礼人") =>
        new()
        {
            Platform = "douyin",
            RoomId = "1",
            Kind = LiveEventKind.Gift,
            UserId = userId,
            Nickname = nickname,
            GiftName = giftName,
            GiftCount = 1,
            TotalCoin = totalCoin,
            CoinType = coinType,
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
    public async Task Danmaku_EnqueueAndCancel_ViaModule()
    {
        var (store, bus, _, _, module) = await CreateAsync();
        using var sub = bus.Subscribe<QueueUpEventEnvelope>();
        try
        {
            bus.Publish(Danmaku("排队 上麦唱一首", nickname: "甲", userId: 1));
            Assert.True(await PollAsync(() => module.Service.Count == 1));

            var ev = await PollEventAsync(sub, "queueup.added");
            Assert.NotNull(ev);
            Assert.Equal("甲", ev!.Data.Entry!.Nickname);
            Assert.Equal("上麦唱一首", ev.Data.Entry.Content);
            Assert.Equal(QueueUpSource.Danmaku, ev.Data.Entry.Source);

            bus.Publish(Danmaku("取消排队", nickname: "甲", userId: 1));
            Assert.True(await PollAsync(() => module.Service.Count == 0));
        }
        finally
        {
            await module.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Danmaku_Complete_RequiresAdminOrAnchor()
    {
        var (store, bus, _, _, module) = await CreateAsync();
        try
        {
            bus.Publish(Danmaku("排队 观众A", nickname: "甲", userId: 1));
            Assert.True(await PollAsync(() => module.Service.Count == 1));

            // 普通观众说「完成」→ 静默忽略
            bus.Publish(Danmaku("完成", nickname: "路人", userId: 99));
            await Task.Delay(150);
            Assert.Equal(1, module.Service.Count);

            // 管理员 → 队首出队
            bus.Publish(Danmaku("完成", nickname: "主播", userId: 100, isAnchor: true));
            Assert.True(await PollAsync(() => module.Service.Count == 0));

            // 主播也有效
            bus.Publish(Danmaku("排队 观众B", nickname: "乙", userId: 2));
            Assert.True(await PollAsync(() => module.Service.Count == 1));
            bus.Publish(Danmaku("完成", nickname: "管理", userId: 101, isAdmin: true));
            Assert.True(await PollAsync(() => module.Service.Count == 0));
        }
        finally
        {
            await module.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Gift_InsertAtRule_AutoInsertsAtPosition()
    {
        var config = AppConfig.CreateDefault() with
        {
            QueueUp = new QueueUpConfig
            {
                Rules =
                [
                    new QueueUpRuleConfig { MatchKind = "gift_name", MatchValue = "火箭", Action = "insert_at", Position = 1 },
                ],
            },
        };
        var (store, bus, _, _, module) = await CreateAsync(config);
        try
        {
            bus.Publish(Danmaku("排队 先来的人", nickname: "甲", userId: 1));
            Assert.True(await PollAsync(() => module.Service.Count == 1));

            // 火箭礼物 → 自动插队到第 1 位（队首）
            bus.Publish(Gift(giftName: "火箭"));
            Assert.True(await PollAsync(() => module.Service.Count == 2));
            var snapshot = module.Service.Snapshot();
            Assert.Equal("送礼人", snapshot.Items[0].Entry.Nickname);
            Assert.Equal(QueueUpSource.Gift, snapshot.Items[0].Entry.Source);
            Assert.Equal("", snapshot.Items[0].Entry.Content);
            Assert.Equal("甲", snapshot.Items[1].Entry.Nickname);

            // 未命中规则（大火箭）不插队
            bus.Publish(Gift(giftName: "大火箭", userId: 3, nickname: "路人"));
            await Task.Delay(150);
            Assert.Equal(2, module.Service.Count);
        }
        finally
        {
            await module.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Gift_GrantEligibility_UnlocksGate()
    {
        var config = AppConfig.CreateDefault() with
        {
            QueueUp = new QueueUpConfig
            {
                EligibilityGate = true,
                Rules =
                [
                    new QueueUpRuleConfig { MatchKind = "coin_threshold", MatchValue = "1000", Action = "grant_eligibility" },
                ],
            },
        };
        var (store, bus, _, _, module) = await CreateAsync(config);
        try
        {
            // 无资格 → 拒绝
            bus.Publish(Danmaku("排队 想上麦", nickname: "甲", userId: 1));
            await Task.Delay(150);
            Assert.Equal(0, module.Service.Count);

            // 送礼 ≥1000 gold 电池 → 获得资格 → 排队成功
            bus.Publish(Gift(totalCoin: 1000, userId: 1, nickname: "甲"));
            Assert.True(await PollAsync(() => module.Service.HasEligibility("douyin:1:1")));

            bus.Publish(Danmaku("排队 想上麦", nickname: "甲", userId: 1));
            Assert.True(await PollAsync(() => module.Service.Count == 1));

            // 银瓜子 5000 不满足电池阈值规则
            bus.Publish(Gift(coinType: "silver", totalCoin: 5000, userId: 2, nickname: "乙"));
            await Task.Delay(150);
            Assert.False(module.Service.HasEligibility("douyin:1:2"));
        }
        finally
        {
            await module.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartFailure_DoesNotBlockRetry()
    {
        // 启动失败（存储未打开）后 _started 必须保持 false：
        // 否则 ModuleHost 标 StartFailed 后重试 StartModuleAsync 撞 _started 早退，
        // 状态显示 Started 实际无订阅循环（🟡 阶段 5 修复）。
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new SqliteStorageEngine(path); // 未 Open
        var bus = new EventBus();
        var logs = new LogBus();
        var module = new QueueUpModule(store, bus, logs, () => AppConfig.CreateDefault());
        var context = new ModuleContext
        {
            EventBus = bus,
            Config = new Erbai.Core.Configuration.ConfigStore(path),
            Storage = store,
            Logs = logs,
            Overlay = null,
        };

        // 第一次启动：存储未打开 → 抛异常（_started 不应被置位）
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            module.StartAsync(context, CancellationToken.None));
        Assert.Contains("not open", ex.Message);

        // 修复存储后重试：必须真正启动（订阅循环生效，事件被消费）
        await store.OpenAsync();
        await module.StartAsync(context, CancellationToken.None);
        bus.Publish(Danmaku("排队 恢复成功", nickname: "甲", userId: 1));
        Assert.True(await PollAsync(() => module.Service.Count == 1));

        await module.DisposeAsync();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Module_DisposeStopsConsumption_RestartRecovers()
    {
        var (store, bus, _, _, module) = await CreateAsync();
        try
        {
            bus.Publish(Danmaku("排队 内容", nickname: "甲", userId: 1));
            Assert.True(await PollAsync(() => module.Service.Count == 1));

            await module.DisposeAsync();

            // 停止后事件不再被消费
            bus.Publish(Danmaku("排队 内容2", nickname: "乙", userId: 2));
            await Task.Delay(150);
            Assert.Equal(1, module.Service.Count);

            // 重启：从存储恢复 + 恢复消费
            await module.StartAsync(new ModuleContext
            {
                EventBus = bus,
                Config = new Erbai.Core.Configuration.ConfigStore(Path.Combine(TempDb())),
                Storage = store,
                Logs = new LogBus(),
                Overlay = null,
            }, CancellationToken.None);
            Assert.Equal(1, module.Service.Count);
            bus.Publish(Danmaku("排队 内容3", nickname: "丙", userId: 3));
            Assert.True(await PollAsync(() => module.Service.Count == 2));
        }
        finally
        {
            await module.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    private static async Task<QueueUpEventEnvelope?> PollEventAsync(
        Subscription<QueueUpEventEnvelope> sub, string eventName, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            QueueUpEventEnvelope ev;
            try
            {
                ev = await sub.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (ev.Event == eventName)
            {
                return ev;
            }
        }
    }
}
