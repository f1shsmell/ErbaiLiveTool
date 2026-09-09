using Erbai.Core.Hosting;

namespace Erbai.Core.Tests;

/// <summary>平台监督者（阶段 4，docs/01 §3.3 单平台重启隔离）：生命周期 / 异常隔离 / 停止超时防双监听。</summary>
public class PlatformSupervisorTests
{
    private static Func<CancellationToken, Task> RunUntilCanceled() =>
        async ct => await Task.Delay(Timeout.InfiniteTimeSpan, ct);

    [Fact]
    public async Task 启动与停止生命周期()
    {
        var supervisor = new PlatformSupervisor(stopTimeout: TimeSpan.FromSeconds(2));
        supervisor.RegisterRunner("douyin", RunUntilCanceled());

        var started = await supervisor.StartAsync("douyin");
        Assert.True(started.Running);
        Assert.True(supervisor.IsRunning("douyin"));
        Assert.Contains(supervisor.Status(), s => s.Name == "douyin" && s.Running);

        var stopped = await supervisor.StopAsync("douyin");
        Assert.False(stopped.Running);
        Assert.False(supervisor.IsRunning("douyin"));
    }

    [Fact]
    public async Task 重复启动幂等()
    {
        var supervisor = new PlatformSupervisor(stopTimeout: TimeSpan.FromSeconds(2));
        var starts = 0;
        supervisor.RegisterRunner("p", async ct =>
        {
            Interlocked.Increment(ref starts);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        await supervisor.StartAsync("p");
        // 等 runner 真正开始执行（Task.Run 调度异步）
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Volatile.Read(ref starts) < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await supervisor.StartAsync("p");
        Assert.Equal(1, Volatile.Read(ref starts));
        await supervisor.StopAsync("p");
    }

    [Fact]
    public async Task runner异常记录LastError且可重启()
    {
        var supervisor = new PlatformSupervisor(stopTimeout: TimeSpan.FromSeconds(2));
        var attempts = 0;
        supervisor.RegisterRunner("p", _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("boom");
        });

        await supervisor.StartAsync("p");
        // 等任务结束
        await Task.Delay(300);

        var status = supervisor.Status().Single(s => s.Name == "p");
        Assert.False(status.Running);
        Assert.Equal("boom", status.LastError);

        // 异常后可重启（换正常 runner 也行——重注册）
        supervisor.RegisterRunner("p", RunUntilCanceled());
        var restarted = await supervisor.StartAsync("p");
        Assert.True(restarted.Running);
        await supervisor.StopAsync("p");
    }

    [Fact]
    public async Task 停止超时的旧实例转入stuck并拒绝并发重启()
    {
        var supervisor = new PlatformSupervisor(
            stopTimeout: TimeSpan.FromMilliseconds(200),
            stuckWait: TimeSpan.FromMilliseconds(200));

        // 吞取消的 runner：永不停
        supervisor.RegisterRunner("p", async ct =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
        });

        await supervisor.StartAsync("p");
        await supervisor.StopAsync("p");

        // stop 超时：任务已从托管表移除，但旧 task 仍存活（stuck）
        var status = supervisor.Status().Single(s => s.Name == "p");
        Assert.False(status.Running);
        Assert.True(status.Stuck);

        // 重启被拒绝（旧实例仍在停止中，防双监听并发处理弹幕）
        var restart = await supervisor.StartAsync("p");
        Assert.False(restart.Running);
        Assert.Contains("上一实例仍在停止中", restart.Error);
    }

    [Fact]
    public async Task 停止窗口期并发启动不产生双监听()
    {
        // H3：旧实现 StopAsync 先把 task 移出 _tasks 再等它退出——等待窗口内
        // 并发 StartAsync 看到空表直接拉新实例：新旧双监听，且旧 runner 的
        // finally 清理（如抖音抓包器按端口回收）会反杀新实例刚拉起的进程。
        // 修复后窗口期实例保留在"停止中"表，Start 短等它退出再拉起。
        var gate = new object();
        var active = 0;
        var maxActive = 0;
        var inCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new PlatformSupervisor(
            stopTimeout: TimeSpan.FromSeconds(2),
            stuckWait: TimeSpan.FromSeconds(2));
        supervisor.RegisterRunner("p", async ct =>
        {
            lock (gate)
            {
                active++;
                maxActive = Math.Max(maxActive, active);
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                inCleanup.TrySetResult(); // 已进入清理（此刻 StopAsync 必然还在等待）
                await Task.Delay(300);    // 模拟 finally 清理：关抓包器 / 还原代理
                lock (gate)
                {
                    active--;
                }
            }
        });

        await supervisor.StartAsync("p");

        var stopTask = supervisor.StopAsync("p");
        await inCleanup.Task; // 确定性卡进停止等待窗口（旧 runner 清理中）
        var restart = await supervisor.StartAsync("p"); // 窗口期内并发启动

        await stopTask;
        Assert.True(restart.Running); // 等旧实例清理完才拉起
        Assert.Equal(1, maxActive);   // 任意时刻至多一个 runner 存活（双监听 = 重复点歌）

        await supervisor.StopAsync("p");
    }

    [Fact]
    public async Task 平台间互不影响()
    {
        var supervisor = new PlatformSupervisor(stopTimeout: TimeSpan.FromSeconds(2));
        supervisor.RegisterRunner("bilibili", RunUntilCanceled());
        supervisor.RegisterRunner("douyin", _ => throw new InvalidOperationException("douyin 崩了"));

        await supervisor.StartAsync("bilibili");
        await supervisor.StartAsync("douyin");
        await Task.Delay(300);

        // douyin 异常退出不影响 bilibili
        Assert.True(supervisor.IsRunning("bilibili"));
        Assert.False(supervisor.IsRunning("douyin"));

        await supervisor.StopAllAsync();
        Assert.False(supervisor.IsRunning("bilibili"));
    }

    [Fact]
    public async Task 并发启动同名平台只拉起一次()
    {
        // #7：StartAsync 是跨锁 check-then-act——并发调用会覆盖 _tasks 造成双监听。
        // 修复后第二次加锁复查运行态，只允许一个实例。
        var supervisor = new PlatformSupervisor(stopTimeout: TimeSpan.FromSeconds(2));
        var starts = 0;
        supervisor.RegisterRunner("douyin", async ct =>
        {
            Interlocked.Increment(ref starts);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => supervisor.StartAsync("douyin")));

        // 等 runner 真正开始执行
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Volatile.Read(ref starts) < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await Task.Delay(200); // 给并发窗口收尾
        Assert.Equal(1, Volatile.Read(ref starts)); // 双监听 = 弹幕被处理两次
        await supervisor.StopAsync("douyin");
    }
}
