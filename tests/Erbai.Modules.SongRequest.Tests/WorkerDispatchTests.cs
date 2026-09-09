using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Modules.SongRequest.Search;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>
/// Worker 派发循环（派发域，docs/03 §1.3）：
/// FIFO 按 sequence、CAS 置 searching/dispatched、先置 current 再发事件、
/// 异常显式结算绝不逃逸。
/// </summary>
public class WorkerDispatchTests
{
    [Fact]
    public async Task Dispatch_FifoOrder_SetsCurrentBeforeEvent()
    {
        var (store, bus, logs, config, queue, _, _) = await TestHarness.CreateAsync();
        queue.Processor = SearchHarness.Fixed();
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        var (a, _) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));
        var (b, _) = await queue.SubmitAsync(TestHarness.NewRequest("海阔天空", userId: "u2"));

        // FIFO 串行：a 先 dispatched（进入播放等待），b 仍 queued
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(a!.RequestId!.Value).Result == RequestStatus.Dispatched));
        // 等 a 的 searching/dispatched 事件已入队后清掉，只保留后续事件
        await TestHarness.DrainEventsAsync(sub);
        Assert.Equal(RequestStatus.Queued, await store.GetRequestStatusAsync(b!.RequestId!.Value));
        Assert.Equal(a!.RequestId, queue.CurrentRequestId);

        // 操作员跳过 a（唤醒播放等待）→ worker 派发 b
        await queue.SkipAsync(a!.RequestId!.Value);
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(b!.RequestId!.Value).Result == RequestStatus.Dispatched));
        Assert.Equal(b.RequestId, queue.CurrentRequestId);

        // 事件序列末尾为 skipped(a) → searching(b) → dispatched(b)
        var events = new List<string>();
        while (events.Count < 3)
        {
            var ev = await TestHarness.NextEventAsync(sub);
            if (ev is null)
            {
                break;
            }

            events.Add(ev.Event);
        }

        Assert.Equal(new[]
        {
            "queue.skipped",
            "queue.searching",
            "queue.dispatched",
        }, events);

        // dispatched 事件附带的快照 is_current 指向刚派发的请求
        var snapshot = await queue.GetSnapshotAsync();
        Assert.Equal(b.RequestId, snapshot.CurrentRequestId);
    }

    [Fact]
    public async Task Dispatch_ProcessorReturnsNull_MarksFailedSongNotFound()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        queue.Processor = (_, _) => Task.FromResult<IReadOnlyList<SongSearchResult>?>(null);
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        var (request, _) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));

        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(request!.RequestId!.Value).Result == RequestStatus.Failed));
        var failed = await store.GetRequestAsync(request!.RequestId!.Value);
        Assert.Equal("song_not_found", failed!.FailureReason);

        var ev = await TestHarness.AwaitEventAsync(sub, "queue.failed");
        Assert.NotNull(ev);
        Assert.Equal("song_not_found", ev.Data.Reason);
    }

    [Fact]
    public async Task Dispatch_RecoverableError_MarksFailedSearchFailed()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        queue.Processor = (_, _) => Task.FromException<IReadOnlyList<SongSearchResult>?>(
            new RecoverableSearchException("provider timeout"));
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        var (request, _) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));

        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(request!.RequestId!.Value).Result == RequestStatus.Failed));
        var failed = await store.GetRequestAsync(request!.RequestId!.Value);
        Assert.Equal("search_failed", failed!.FailureReason);

        var ev = await TestHarness.AwaitEventAsync(sub, "queue.failed");
        Assert.NotNull(ev);
        Assert.Equal("search_failed", ev.Data.Reason);
    }

    [Fact]
    public async Task Dispatch_GenericException_MarksFailedWithMessage_WorkerKeepsRunning()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        queue.Processor = (_, _) => Task.FromException<IReadOnlyList<SongSearchResult>?>(
            new InvalidOperationException("boom"));
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        var (a, _) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));

        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(a!.RequestId!.Value).Result == RequestStatus.Failed));
        var failed = await store.GetRequestAsync(a!.RequestId!.Value);
        Assert.Equal("boom", failed!.FailureReason);

        // worker 未被异常打死：换正常处理器后新请求仍能派发
        queue.Processor = SearchHarness.Fixed();
        var (b, _) = await queue.SubmitAsync(TestHarness.NewRequest("海阔天空", userId: "u2"));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(b!.RequestId!.Value).Result == RequestStatus.Dispatched));
    }

    [Fact]
    public async Task Dispatch_StaleRequest_NotQueued_IsDropped()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        queue.Processor = SearchHarness.Fixed();
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        // 直接插库一个 completed 请求并塞入 pending（模拟重复消费场景）
        var request = await store.InsertRequestAsync(TestHarness.NewRequest("晴天", userId: "u1")
            with { Status = RequestStatus.Completed });
        await queue.Pending.Writer.WriteAsync(request);
        await queue.StartAsync();

        // 定向状态查询发现非 queued → 丢弃，不产生 searching/dispatched
        await Task.Delay(200);
        var status = await store.GetRequestStatusAsync(request.RequestId!.Value);
        Assert.Equal(RequestStatus.Completed, status);
        var events = await TestHarness.DrainEventsAsync(sub);
        Assert.DoesNotContain(events, ev => ev.Event is "queue.searching" or "queue.dispatched");
    }

    [Fact]
    public async Task Dispatch_WaitForPlayback_FallbackCompletesAfterTimeout()
    {
        // 播放等待 fallback：无时长候选 → 300s 预算，太长；这里用带时长
        // 的候选（00:00:01）验证等待后按 completed 结算
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        queue.Processor = (request, _) => Task.FromResult<IReadOnlyList<SongSearchResult>?>(
        [
            new SongSearchResult
            {
                Source = "kugou",
                Name = request.SongName,
                Singer = request.Singer,
                SongMid = "mid",
                Interval = "00:00:01",
            },
        ]);
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        var (request, _) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));

        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(request!.RequestId!.Value).Result == RequestStatus.Completed, 5000));
        var ev = await TestHarness.AwaitEventAsync(sub, "queue.completed");
        Assert.NotNull(ev);
        Assert.Equal(request!.RequestId, ev.Data.Request!.RequestId);
    }

    [Fact]
    public async Task Dispatch_OperatorTerminal_WakesPlaybackWait()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        queue.Processor = SearchHarness.Fixed();
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        var (request, _) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(request!.RequestId!.Value).Result == RequestStatus.Dispatched));

        // 操作员跳过正在播放的请求 → 唤醒播放等待并结算
        var skipped = await queue.SkipAsync(request!.RequestId!.Value);
        Assert.NotNull(skipped);
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(request.RequestId.Value).Result == RequestStatus.Skipped));
        Assert.Null(queue.CurrentRequestId);
    }
}
