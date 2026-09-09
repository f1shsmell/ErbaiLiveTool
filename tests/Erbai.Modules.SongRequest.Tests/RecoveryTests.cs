using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>
/// 重启恢复（恢复域，docs/03 §1.4）：
/// queued 重入内存 pending；searching/ready/dispatched → failed/recovered_after_restart
/// （歌可能已发到播放器，重放会重复播）；stop 清空内存 pending 防 stop→start 双入队。
/// </summary>
public class RecoveryTests
{
    [Fact]
    public async Task Start_RequeuesQueued_AndFailsMidFlightStatuses()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        // 直接插库模拟上次运行的遗留状态
        var queued = await store.InsertRequestAsync(TestHarness.NewRequest("晴天", userId: "u1")
            with { Status = RequestStatus.Queued });
        var midFlight = await store.InsertRequestAsync(TestHarness.NewRequest("海阔天空", userId: "u2")
            with { Status = RequestStatus.Searching });

        await queue.StartAsync();

        // searching → failed/recovered_after_restart（防重复播放）
        var failed = await store.GetRequestAsync(midFlight.RequestId!.Value);
        Assert.NotNull(failed);
        Assert.Equal(RequestStatus.Failed, failed.Status);
        Assert.Equal("recovered_after_restart", failed.FailureReason);

        // queued 恢复重入队 → worker 派发（FIFO 串行，最终 dispatched/completed）
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(queued.RequestId!.Value).Result is
                RequestStatus.Dispatched or RequestStatus.Completed));

        // started 事件带恢复数
        var ev = await TestHarness.AwaitEventAsync(sub, "queue.started");
        Assert.NotNull(ev);
        Assert.Equal(1, ev.Data.Recovered);
    }

    [Fact]
    public async Task Stop_ClearsPending_SoStartRequeuesOnce()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        var (request, _) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));
        Assert.NotNull(request);

        await queue.StopAsync();
        // 内存 pending 已清空
        Assert.False(queue.Pending.Reader.TryRead(out _));

        // stop→start：同一请求只被恢复一次（DB 中只有一条记录，不出现双
        // 派发；若 stop 时已 dispatched，重启恢复按契约置 failed/
        // recovered_after_restart，同样只结算一次）
        await queue.StartAsync();
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(request.RequestId!.Value).Result is
                RequestStatus.Dispatched or RequestStatus.Completed or RequestStatus.Failed));
        var counts = await store.CountRequestsByStatusAsync();
        Assert.Equal(1, counts.Values.Sum());
        Assert.False(queue.Pending.Reader.TryRead(out _)); // 无双入队残留
    }

    [Fact]
    public async Task Submit_ImplicitlyStarts_AndStopIsIdempotent()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        // submit 隐式 start
        var (request, decision) = await queue.SubmitAsync(TestHarness.NewRequest("晴天"));
        Assert.True(decision.Allowed);
        Assert.True(queue.IsStarted);

        // stop 幂等（重复 stop 不抛）
        await queue.StopAsync();
        await queue.StopAsync();
        Assert.False(queue.IsStarted);
    }

    [Fact]
    public async Task Start_WhenInitFails_ResetsStartedFlag_SoRetrySucceeds()
    {
        // H2 回归：_started 在初始化前置位——LoadIdleSongs/Recover 抛异常后
        // 队列永久死亡（后续 Start 全 no-op、请求写入无人消费的 pending）
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        await store.CloseAsync(); // 存储关闭 → LoadIdleSongsAsync 确定性抛错

        await Assert.ThrowsAnyAsync<Exception>(() => queue.StartAsync());
        Assert.False(queue.IsStarted); // 必须复位，否则整个会话点歌失效

        // 恢复存储后重试启动应成功（修复前：no-op 直接返回，worker 永不启动）
        await store.OpenAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        await queue.StartAsync();
        Assert.True(queue.IsStarted);
        var ev = await TestHarness.AwaitEventAsync(sub, "queue.started");
        Assert.NotNull(ev);
    }
}
