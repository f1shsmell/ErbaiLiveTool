using Erbai.Contracts.Logging;

namespace Erbai.Core.Hosting;

/// <summary>平台监督者状态（UI/日志用）。</summary>
public sealed record PlatformStatus
{
    public required string Name { get; init; }

    public bool Running { get; init; }

    /// <summary>监听循环异常退出原因（异常退出后记录；停止/正常退出清除）。</summary>
    public string? LastError { get; init; }

    /// <summary>上一实例停止超时仍存活（期间禁止重启同平台，防双监听并发）。</summary>
    public bool Stuck { get; init; }
}

/// <summary>监督者操作结果。</summary>
public sealed record SupervisorResult
{
    public required string Name { get; init; }

    public required bool Running { get; init; }

    public string Error { get; init; } = "";
}

/// <summary>
/// 平台监督者（阶段 4，docs/01 §3.3「单平台重启隔离」）：
/// - 每个平台一个长驻监听任务（runner：启动 → 事件转发 → 随 ct 取消退出，finally 释放资源）；
/// - 停止超时（5s）的任务转入 stuck：真正结束后由回调清理；期间拒绝重启同平台
///   （否则新旧两个监听并发，弹幕被处理两次——重复点歌/重复入队）；
/// - 重启前短等（2s）上一实例退出；仍不结束才拒绝启动；
/// - runner 异常不逃逸：记录 LastError 后从托管表移除（UI 可见、可重试）；
/// - 操作串行化（单锁），幂等。
/// </summary>
public sealed class PlatformSupervisor
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Func<CancellationToken, Task>> _runners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _ctss = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _stuck = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _stopping = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastErrors = new(StringComparer.Ordinal);
    private readonly ILogBus? _logs;
    private readonly TimeSpan _stopTimeout;
    private readonly TimeSpan _stuckWait;

    public PlatformSupervisor(ILogBus? logs = null, TimeSpan? stopTimeout = null, TimeSpan? stuckWait = null)
    {
        _logs = logs;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        _stuckWait = stuckWait ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>注册平台 runner（监听循环；必须随 ct 取消退出，异常不逃逸由监督者兜底）。</summary>
    public void RegisterRunner(string name, Func<CancellationToken, Task> runner)
    {
        lock (_sync)
        {
            _runners[name] = runner;
        }
    }

    /// <summary>启动平台（幂等；上一实例 stopping/stuck 时短等后仍不结束则拒绝）。</summary>
    public async Task<SupervisorResult> StartAsync(string name, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_runners.ContainsKey(name))
            {
                return new SupervisorResult { Name = name, Running = false, Error = $"未注册平台: {name}" };
            }

            if (_tasks.TryGetValue(name, out var existing) && !existing.IsCompleted)
            {
                return new SupervisorResult { Name = name, Running = true };
            }
        }

        // 上一实例正在停止（StopAsync 已发起但旧 runner 的 finally 清理还没跑完）：
        // 短等它退出再拉新实例——否则短时间内新旧双监听，且旧实例的清理动作
        // （如抖音抓包器按端口回收进程）会反杀新实例刚拉起的资源
        var stoppingReject = await WaitUntilSettledAsync(
            _stopping, name, "上一实例仍在停止中（清理未完成），请稍后重试", ct);
        if (stoppingReject is not null)
        {
            return stoppingReject;
        }

        // 上一实例停止超时仍存活：短等它结束（防双监听）
        var stuckReject = await WaitUntilSettledAsync(
            _stuck, name, $"上一实例仍在停止中（停止超时 {_stopTimeout.TotalSeconds:0}s），请稍后重试", ct);
        if (stuckReject is not null)
        {
            return stuckReject;
        }

        lock (_sync)
        {
            // 复查：等待期间（锁已释放）可能有并发 StartAsync 已拉起新实例——
            // 不复查会覆盖 _tasks[name] 造成双监听重复事件（docs/00 修复记录 #7）
            if (_tasks.TryGetValue(name, out var running) && !running.IsCompleted)
            {
                return new SupervisorResult { Name = name, Running = true };
            }

            // 同理复查 stopping/stuck：等待期间并发 StopAsync 可能刚转入新实例
            if (_stopping.TryGetValue(name, out var stoppingNow) && !stoppingNow.IsCompleted)
            {
                return new SupervisorResult { Name = name, Running = false, Error = "上一实例仍在停止中（清理未完成），请稍后重试" };
            }

            if (_stuck.TryGetValue(name, out var stuckNow) && !stuckNow.IsCompleted)
            {
                return new SupervisorResult { Name = name, Running = false, Error = $"上一实例仍在停止中（停止超时 {_stopTimeout.TotalSeconds:0}s），请稍后重试" };
            }

            _stopping.Remove(name);
            _stuck.Remove(name);
            if (_tasks.TryGetValue(name, out var done) && done.IsCompleted)
            {
                _tasks.Remove(name);
            }

            _lastErrors.Remove(name);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = Task.Run(() => RunPlatformAsync(name, cts.Token), CancellationToken.None);
            _ctss[name] = cts;
            _tasks[name] = task;
            _ = task.ContinueWith(t => PlatformDone(name, t), TaskScheduler.Default);
            _logs?.Information($"[platform:{name}] 已启动");
            return new SupervisorResult { Name = name, Running = true };
        }
    }

    /// <summary>停止平台（取消 runner 并等 5s；超时转 stuck 并再次取消）。</summary>
    public async Task<SupervisorResult> StopAsync(string name)
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _tasks.TryGetValue(name, out task);
            _ctss.TryGetValue(name, out cts);
            if (task is not null)
            {
                // 立即移出运行表、转入"停止中"表：等待窗口（最长 stopTimeout）内
                // 并发 StartAsync 必须能看见它——否则窗口期拉起的新实例会被旧
                // runner 的 finally 清理反杀（如按端口回收），且新旧双监听
                _tasks.Remove(name);
                _stopping[name] = task;
            }

            _ctss.Remove(name);
            _lastErrors.Remove(name);
        }

        if (task is null)
        {
            cts?.Dispose();
            return new SupervisorResult { Name = name, Running = false };
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (!task.IsCompleted)
            {
                try
                {
                    await task.WaitAsync(_stopTimeout);
                }
                catch (TimeoutException)
                {
                    // 监听循环不配合（吞取消/卡同步阻塞）：从托管表移除，可重启新实例；
                    // 旧 task 挂到 stuck 等它真正结束（done_callback 清理），期间禁止重启同平台
                    _logs?.Warning($"[platform:{name}] 停止超时（>{_stopTimeout.TotalSeconds:0}s），任务已从托管表移除");
                    lock (_sync)
                    {
                        _stuck[name] = task;
                    }

                    _ = task.ContinueWith(t => StuckDone(name, t), TaskScheduler.Default);
                    try
                    {
                        cts?.Cancel(); // 再发一次取消信号
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                // 只清理自己放进来的那条（等待期间并发 Stop→Start→Stop 可能已
                // 换成另一个实例的 task，不能误删）
                if (_stopping.TryGetValue(name, out var stopping) && stopping == task)
                {
                    _stopping.Remove(name);
                }
            }

            cts?.Dispose();
        }

        _logs?.Information($"[platform:{name}] 已停止");
        return new SupervisorResult { Name = name, Running = false };
    }

    /// <summary>短等 table 中 name 的任务结束；仍在进行返回拒绝结果，否则 null（已结束/无条目）。</summary>
    private async Task<SupervisorResult?> WaitUntilSettledAsync(
        Dictionary<string, Task> table, string name, string rejectMessage, CancellationToken ct)
    {
        Task? pending;
        lock (_sync)
        {
            table.TryGetValue(name, out pending);
        }

        if (pending is null || pending.IsCompleted)
        {
            return null;
        }

        try
        {
            await pending.WaitAsync(_stuckWait, ct);
        }
        catch (TimeoutException)
        {
            return new SupervisorResult { Name = name, Running = false, Error = rejectMessage };
        }

        return null;
    }

    /// <summary>重启平台（先停后启；stop 超时则拒绝启动）。</summary>
    public async Task<SupervisorResult> RestartAsync(string name, CancellationToken ct = default)
    {
        var stopped = await StopAsync(name);
        if (stopped.Error.Length > 0)
        {
            return stopped;
        }

        return await StartAsync(name, ct);
    }

    /// <summary>当前平台状态快照（UI/日志查询）。</summary>
    public IReadOnlyList<PlatformStatus> Status()
    {
        lock (_sync)
        {
            var result = new List<PlatformStatus>();
            foreach (var name in _runners.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                var running = _tasks.TryGetValue(name, out var task) && !task.IsCompleted;
                var stuck = _stuck.TryGetValue(name, out var stuckTask) && !stuckTask.IsCompleted;
                _lastErrors.TryGetValue(name, out var error);
                result.Add(new PlatformStatus { Name = name, Running = running, LastError = error, Stuck = stuck });
            }

            return result;
        }
    }

    /// <summary>平台是否在运行。</summary>
    public bool IsRunning(string name)
    {
        lock (_sync)
        {
            return _tasks.TryGetValue(name, out var task) && !task.IsCompleted;
        }
    }

    /// <summary>停止所有平台（Dispose 用）。</summary>
    public async Task StopAllAsync()
    {
        string[] names;
        lock (_sync)
        {
            names = [.. _tasks.Keys];
        }

        foreach (var name in names)
        {
            await StopAsync(name);
        }
    }

    private async Task RunPlatformAsync(string name, CancellationToken ct)
    {
        Func<CancellationToken, Task> runner;
        lock (_sync)
        {
            runner = _runners[name];
        }

        try
        {
            await runner(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _lastErrors[name] = ex.Message;
            }

            _logs?.Error($"[platform:{name}] 监听异常退出: {ex.Message}", ex.ToString());
        }
    }

    private void PlatformDone(string name, Task task)
    {
        lock (_sync)
        {
            if (_tasks.TryGetValue(name, out var current) && current == task)
            {
                _tasks.Remove(name);
            }
        }

        if (task.IsFaulted && task.Exception is not null)
        {
            _logs?.Error($"[platform:{name}] 平台任务异常: {task.Exception.Message}");
        }
    }

    private void StuckDone(string name, Task task)
    {
        lock (_sync)
        {
            if (_stuck.TryGetValue(name, out var current) && current == task)
            {
                _stuck.Remove(name);
            }
        }

        _logs?.Information($"[platform:{name}] 停止超时的旧实例已真正结束，可重新启动");
    }
}
