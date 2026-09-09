using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.QueueUp;
using Erbai.Core.Events;
using Erbai.Core.Logging;
using Erbai.Core.Storage;

namespace Erbai.Modules.QueueUp.Tests;

/// <summary>
/// 排队队列核心（docs/01 §3.7 最小版）：FIFO 入队 / 重复排队更新内容（用户已确认）/
/// 取消 / 完成队首 / 队满拒绝 / 资格门槛 / 礼物插队名次 / queueup.* 事件附快照 /
/// 重启恢复（created_at 序，插队位置不跨重启）。
/// </summary>
public class QueueUpServiceTests
{
    private static string TempDb() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "test.db");

    private static async Task<(SqliteStorageEngine Store, EventBus Bus, LogBus Logs, QueueUpService Service)> CreateAsync(
        AppConfig? config = null)
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new SqliteStorageEngine(path);
        await store.OpenAsync();
        var bus = new EventBus();
        var logs = new LogBus();
        var effective = config ?? AppConfig.CreateDefault();
        var service = new QueueUpService(store, bus, logs, () => effective);
        await service.StartAsync();
        return (store, bus, logs, service);
    }

    private static async Task<QueueUpEventEnvelope?> NextEventAsync(Subscription<QueueUpEventEnvelope> sub, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await sub.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Enqueue_FifoOrder_PositionsNumbered()
    {
        var (store, bus, _, service) = await CreateAsync();
        using var sub = bus.Subscribe<QueueUpEventEnvelope>();
        try
        {
            await service.EnqueueAsync("douyin:1:1", "甲", "第一个");
            await service.EnqueueAsync("douyin:1:2", "乙", "第二个");

            var snapshot = service.Snapshot();
            Assert.Equal(2, snapshot.Total);
            Assert.Equal("甲", snapshot.Items[0].Entry.Nickname);
            Assert.Equal(1, snapshot.Items[0].Position);
            Assert.Equal("乙", snapshot.Items[1].Entry.Nickname);
            Assert.Equal(2, snapshot.Items[1].Position);
            Assert.Equal(50, snapshot.MaxEntries);

            // 事件顺序 added ×2，均附快照
            var first = await NextEventAsync(sub);
            Assert.Equal("queueup.added", first!.Event);
            Assert.Equal(1, first.Data.Snapshot!.Total);
            var second = await NextEventAsync(sub);
            Assert.Equal("queueup.added", second!.Event);
            Assert.Equal(2, second.Data.Snapshot!.Total);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enqueue_DuplicateUser_UpdatesOwnContent_NoNewEntry()
    {
        var (store, bus, _, service) = await CreateAsync();
        using var sub = bus.Subscribe<QueueUpEventEnvelope>();
        try
        {
            var first = await service.EnqueueAsync("douyin:1:1", "甲", "旧内容");
            Assert.True(first.Accepted);

            var updated = await service.EnqueueAsync("douyin:1:1", "甲", "新内容");
            Assert.True(updated.Accepted);
            Assert.Equal("新内容", updated.Entry!.Content);

            var snapshot = service.Snapshot();
            Assert.Single(snapshot.Items); // 不新增条目
            Assert.Equal("新内容", snapshot.Items[0].Entry.Content);

            var ev = await NextEventAsync(sub); // added
            Assert.Equal("queueup.added", ev!.Event);
            ev = await NextEventAsync(sub);     // updated
            Assert.Equal("queueup.updated", ev!.Event);
            Assert.Equal(1, ev.Data.Snapshot!.Total);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enqueue_EligibilityGateOn_RestoredQueueUserStillUpdates()
    {
        // 审计 T2-2: 资格门槛只约束新入队。重启恢复后资格集(_eligible)已清空,
        // 已入队用户必须仍能更新自己的内容(重复排队语义)——修复前在队用户被
        // not_eligible 拒绝(资格检查先于"已在队"查找)。
        var config = AppConfig.CreateDefault() with
        {
            QueueUp = new QueueUpConfig { Enabled = true, EligibilityGate = true, MaxEntries = 50 },
        };
        var (store, bus, _, service) = await CreateAsync(config);
        try
        {
            service.GrantEligibility("douyin:1:1");
            Assert.True((await service.EnqueueAsync("douyin:1:1", "甲", "旧内容")).Accepted);

            // 模拟重启: StartAsync 即恢复逻辑(清空内存队列/资格集后从存储重载)
            await service.StartAsync();

            var updated = await service.EnqueueAsync("douyin:1:1", "甲", "新内容");
            Assert.True(updated.Accepted);
            Assert.Equal("新内容", updated.Entry!.Content);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enqueue_QueueFull_RejectsWithEvent()
    {
        var config = AppConfig.CreateDefault() with { QueueUp = new QueueUpConfig { MaxEntries = 2 } };
        var (store, bus, _, service) = await CreateAsync(config);
        using var sub = bus.Subscribe<QueueUpEventEnvelope>();
        try
        {
            await service.EnqueueAsync("douyin:1:1", "甲", "a");
            await service.EnqueueAsync("douyin:1:2", "乙", "b");

            var result = await service.EnqueueAsync("douyin:1:3", "丙", "c");
            Assert.False(result.Accepted);
            Assert.Equal("queue_full", result.Reason);

            var ev = await NextEventAsync(sub); // added
            await NextEventAsync(sub);          // added
            ev = await NextEventAsync(sub);     // rejected
            Assert.Equal("queueup.rejected", ev!.Event);
            Assert.Equal("queue_full", ev.Data.Reason);
            Assert.Equal(2, ev.Data.Snapshot!.Total);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelByUser_RemovesOwnEntry_KeepsOthers()
    {
        var (store, bus, _, service) = await CreateAsync();
        using var sub = bus.Subscribe<QueueUpEventEnvelope>();
        try
        {
            await service.EnqueueAsync("douyin:1:1", "甲", "a");
            await service.EnqueueAsync("douyin:1:2", "乙", "b");

            var result = await service.CancelByUserAsync("douyin:1:1");
            Assert.True(result.Accepted);
            Assert.Equal(QueueUpStatus.Cancelled, result.Entry!.Status);

            var snapshot = service.Snapshot();
            Assert.Single(snapshot.Items);
            Assert.Equal("乙", snapshot.Items[0].Entry.Nickname);
            Assert.Equal(1, snapshot.Items[0].Position); // 位置重编号

            // 不在队中 → 静默 false
            Assert.False((await service.CancelByUserAsync("douyin:1:1")).Accepted);

            var ev = await NextEventAsync(sub); // added ×2
            await NextEventAsync(sub);
            ev = await NextEventAsync(sub);     // cancelled
            Assert.Equal("queueup.cancelled", ev!.Event);
            Assert.Equal(1, ev.Data.Snapshot!.Total);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompleteNext_CompletesHead_EmptyQueue_NoOp()
    {
        var (store, bus, _, service) = await CreateAsync();
        using var sub = bus.Subscribe<QueueUpEventEnvelope>();
        try
        {
            await service.EnqueueAsync("douyin:1:1", "甲", "a");
            await service.EnqueueAsync("douyin:1:2", "乙", "b");

            var result = await service.CompleteNextAsync();
            Assert.True(result.Accepted);
            Assert.Equal(QueueUpStatus.Completed, result.Entry!.Status);
            Assert.Equal("甲", result.Entry.Nickname);
            Assert.Equal("乙", service.Snapshot().Items[0].Entry.Nickname); // 乙顶上队首

            // 空队 no-op
            await service.CompleteNextAsync();
            Assert.False((await service.CompleteNextAsync()).Accepted);

            var ev = await NextEventAsync(sub); // added ×2
            await NextEventAsync(sub);
            ev = await NextEventAsync(sub);     // completed
            Assert.Equal("queueup.completed", ev!.Event);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task GiftInsert_InsertsAtPosition_ClampsOutOfRange()
    {
        var (store, bus, _, service) = await CreateAsync();
        try
        {
            await service.EnqueueAsync("douyin:1:1", "甲", "a");
            await service.EnqueueAsync("douyin:1:2", "乙", "b");
            await service.EnqueueAsync("douyin:1:3", "丙", "c");

            // 插到第 2 位（1 起）→ 甲/礼物/乙/丙
            var result = await service.EnqueueAsync("douyin:1:4", "丁", "", QueueUpSource.Gift, insertPosition: 2);
            Assert.True(result.Accepted);
            Assert.Equal(QueueUpSource.Gift, result.Entry!.Source);
            Assert.Equal("丁", service.Snapshot().Items[1].Entry.Nickname);

            // 名次超过队尾 → 收敛队尾；负数 → 队首
            await service.EnqueueAsync("douyin:1:5", "戊", "", QueueUpSource.Gift, insertPosition: 999);
            Assert.Equal("戊", service.Snapshot().Items[^1].Entry.Nickname);
            await service.EnqueueAsync("douyin:1:6", "己", "", QueueUpSource.Gift, insertPosition: 0);
            Assert.Equal("己", service.Snapshot().Items[0].Entry.Nickname);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task EligibilityGate_OnlyGrantsCanEnqueue()
    {
        var config = AppConfig.CreateDefault() with { QueueUp = new QueueUpConfig { EligibilityGate = true } };
        var (store, bus, _, service) = await CreateAsync(config);
        try
        {
            var denied = await service.EnqueueAsync("douyin:1:1", "甲", "a");
            Assert.False(denied.Accepted);
            Assert.Equal("not_eligible", denied.Reason);

            service.GrantEligibility("douyin:1:1");
            var accepted = await service.EnqueueAsync("douyin:1:1", "甲", "a");
            Assert.True(accepted.Accepted);

            // 其他人仍被拒
            Assert.Equal("not_eligible", (await service.EnqueueAsync("douyin:1:2", "乙", "b")).Reason);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisabledModule_RejectsAll()
    {
        var config = AppConfig.CreateDefault() with { QueueUp = new QueueUpConfig { Enabled = false } };
        var (store, _, _, service) = await CreateAsync(config);
        try
        {
            var result = await service.EnqueueAsync("douyin:1:1", "甲", "a");
            Assert.False(result.Accepted);
            Assert.Equal("disabled", result.Reason);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task GiftInsert_AlreadyQueued_MovesPosition_NoDuplicate()
    {
        var (store, bus, _, service) = await CreateAsync();
        using var sub = bus.Subscribe<QueueUpEventEnvelope>();
        try
        {
            await service.EnqueueAsync("douyin:1:1", "甲", "a"); // 位 1
            await service.EnqueueAsync("douyin:1:2", "乙", "b"); // 位 2
            await service.EnqueueAsync("douyin:1:3", "丙", "c"); // 位 3

            // 甲（位 1）送礼命中 insert_at 3 → 移动到第 3 位，不产生第二条
            var result = await service.EnqueueAsync("douyin:1:1", "甲", "", QueueUpSource.Gift, insertPosition: 3);
            Assert.True(result.Accepted);

            var snapshot = service.Snapshot();
            Assert.Equal(3, snapshot.Total); // 条目数不变
            Assert.Equal("乙", snapshot.Items[0].Entry.Nickname);
            Assert.Equal("丙", snapshot.Items[1].Entry.Nickname);
            Assert.Equal("甲", snapshot.Items[2].Entry.Nickname);
            Assert.Equal(3, snapshot.Items[2].Position);
            Assert.Equal(QueueUpSource.Danmaku, snapshot.Items[2].Entry.Source); // 来源保持原样

            // 事件：移动发 queueup.updated（附快照），无第二条 added
            var ev = await NextEventAsync(sub); // added 甲
            Assert.Equal("queueup.added", ev!.Event);
            ev = await NextEventAsync(sub);     // added 乙
            ev = await NextEventAsync(sub);     // added 丙
            ev = await NextEventAsync(sub);     // updated（移动）
            Assert.Equal("queueup.updated", ev!.Event);
            Assert.Equal(3, ev.Data.Snapshot!.Total);
            Assert.Equal("甲", ev.Data.Entry!.Nickname);

            // 边界：insert_at 1（已在位 3 → 移回队首）；insert_at 999 → 队尾
            await service.EnqueueAsync("douyin:1:1", "甲", "", QueueUpSource.Gift, insertPosition: 1);
            Assert.Equal("甲", service.Snapshot().Items[0].Entry.Nickname);
            Assert.Equal(3, service.Snapshot().Total);

            await service.EnqueueAsync("douyin:1:1", "甲", "", QueueUpSource.Gift, insertPosition: 999);
            Assert.Equal("甲", service.Snapshot().Items[^1].Entry.Nickname);
            Assert.Equal(3, service.Snapshot().Total);

            // 取消只删自己那一条（无幽灵残留）
            await service.CancelByUserAsync("douyin:1:1");
            Assert.Equal(2, service.Snapshot().Total);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Restart_RecoversQueuedEntries_FifoByCreatedAt()
    {
        var (store, bus, _, service) = await CreateAsync();
        try
        {
            await service.EnqueueAsync("douyin:1:1", "甲", "a");
            var toComplete = await service.EnqueueAsync("douyin:1:2", "乙", "b");
            await service.CompleteNextAsync(); // 甲出队（completed 不入恢复）
            Assert.Equal(toComplete.Entry!.Id, service.Snapshot().Items[0].Entry.Id);

            // 模拟重启：新 service 实例从存储恢复
            var logs = new LogBus();
            var restarted = new QueueUpService(store, bus, logs, () => AppConfig.CreateDefault());
            await restarted.StartAsync();

            Assert.Equal(1, restarted.Count);
            Assert.Equal("乙", restarted.Snapshot().Items[0].Entry.Nickname);
            Assert.Equal(QueueUpStatus.Queued, restarted.Snapshot().Items[0].Entry.Status);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enqueue_PersistsToStorage_AndStatusTransitions()
    {
        var (store, bus, _, service) = await CreateAsync();
        try
        {
            await service.EnqueueAsync("douyin:1:1", "甲", "a");
            await service.CompleteNextAsync();

            Assert.Empty(await store.ListQueueUpEntriesAsync()); // 默认只取 queued
            var history = await store.ListQueueUpEntriesAsync(QueueUpStatus.Completed);
            var entry = Assert.Single(history);
            Assert.Equal("甲", entry.Nickname);
            Assert.Equal(QueueUpStatus.Completed, entry.Status);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }
}
