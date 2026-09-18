using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Erbai.Contracts.Players;

namespace Erbai.Player.Connectors;

/// <summary>
/// NDJSON-stdio 连接器客户端（决策 #15，协议 docs/04 §1.4）：
/// 拉起子进程 + ping 建连；请求/响应按 id 配对；垃圾非 JSON 行忽略；
/// 退出/协议超时自动重启（指数退避 1s→15s）；客户端读超时 25s（必须大于
/// 连接器侧 probe 6s / search 15s / execute 20s）；subscribe 事件驱动快照，
/// 否则回退 probe 轮询。deactivate 发 shutdown 再 terminate 兜底。
/// </summary>
public sealed class ConnectorClient : IAsyncDisposable
{
    /// <summary>客户端读超时（04 §1.4 CONNECTOR_REQUEST_TIMEOUT=25s）；测试可缩短。</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(25);

    private readonly string _executablePath;
    private readonly string _playerKey;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly Dictionary<string, TaskCompletionSource<ConnectorResponse>> _pending = new();
    private readonly object _pendingLock = new(); // 请求线程/读循环/超时清理跨线程访问
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _ensureLock = new(1, 1); // 进程重启单飞
    private readonly Channel<PlayerSnapshot> _eventChannel = Channel.CreateBounded<PlayerSnapshot>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private int _nextId;

    /// <param name="environment">
    /// 附加注入子进程的环境变量（LX_* / BILINCM_FOLIA_TOKEN 等，连接器侧
    /// 经 *Options.FromEnvironment() 读取）。null = 不注入（测试默认）。
    /// </param>
    public ConnectorClient(string executablePath, string playerKey,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _executablePath = executablePath;
        _playerKey = playerKey;
        _environment = environment;
    }

    public string PlayerKey => _playerKey;

    /// <summary>当前子进程（测试用）。</summary>
    internal Process? Process => _process;

    /// <summary>宿主加性能力（ping 建连时协商；snapshot-events-v1 等）。</summary>
    public IReadOnlyList<string> ProtocolCapabilities => _capabilities;

    /// <summary>
    /// ping 元数据（能力协商面，含上游规范形状的 capabilities 布尔对象与 features）。
    /// 未建连时为空对象。
    /// </summary>
    public ConnectorPingInfo Ping => _ping;

    private ConnectorPingInfo _ping = new();
    private IReadOnlyList<string> _capabilities = [];

    /// <summary>启动子进程并 ping 建连，返回首帧快照。</summary>
    public async Task<PlayerSnapshot> ActivateAsync(CancellationToken ct)
    {
        await EnsureProcessAsync(ct);
        var response = await RequestAsync("ping", null, ct);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"connector ping failed: {response.Error}");
        }

        // 能力协商：兼容上游规范形状（result.features / result.capabilities）
        // 与本仓库旧宿主形状（响应顶层 protocolCapabilities 字符串数组）
        _ping = ConnectorProtocol.ParsePing(response.Result, response.ProtocolCapabilities);
        _capabilities = _ping.Features;
        return await ProbeAsync(ct);
    }

    public async Task<PlayerSnapshot> ProbeAsync(CancellationToken ct)
    {
        var response = await RequestAsync("probe", null, ct);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"connector probe failed: {response.Error}");
        }

        return SnapshotParser.Parse(response.Result, _playerKey);
    }

    public async Task<IReadOnlyList<PlayerTrack>> SearchAsync(string query, CancellationToken ct)
    {
        var response = await RequestAsync("search", query, ct);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"connector search failed: {response.Error}");
        }

        var tracks = new List<PlayerTrack>();
        if (response.Result is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var track = SnapshotParser.DeserializeTrack(item, _playerKey);
                if (track is not null)
                {
                    tracks.Add(track);
                }
            }
        }

        return tracks;
    }

    public async Task<PlayerOperationResult> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct)
    {
        var response = await RequestAsync("execute", null, ct, command, track);
        if (!response.Ok)
        {
            return PlayerOperationResult.Failure(response.Error ?? "connector execute failed");
        }

        return ParseOperationResult(response.Result);
    }

    /// <summary>订阅快照事件流（宿主无事件源时事件通道为空，调用方回退轮询）。</summary>
    public async IAsyncEnumerable<PlayerSnapshot> WatchSnapshotsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var response = await RequestAsync("subscribe", null, ct);
        if (!response.Ok)
        {
            yield break;
        }

        while (!ct.IsCancellationRequested)
        {
            PlayerSnapshot snapshot;
            try
            {
                snapshot = await _eventChannel.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }

            yield return snapshot;
        }
    }

    public async Task DeactivateAsync()
    {
        if (_process is null)
        {
            return;
        }

        // 进程已死（崩溃/被 Kill）：不发送 shutdown——RequestAsync 内部会走
        // EnsureProcessAsync 用不可取消的 ct 重启进程（审计 T4-2：deactivate 变成
        // 重启循环、可能 NRE/留孤儿）。直接释放并失败在途请求。
        if (_process.HasExited)
        {
            await DisposeProcessAsync();
            return;
        }

        try
        {
            var shutdown = await RequestAsync("shutdown", null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            _ = shutdown;
        }
        catch
        {
        }

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        await DisposeProcessAsync();
    }

    private async Task EnsureProcessAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        // 单飞：并发请求都发现进程死亡时只允许一个执行重启，其余等
        // 双检后直接复用新进程（否则双开子进程，先起的变孤儿）
        await _ensureLock.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            // 指数退避 1s→15s（进程刚崩时避免疯狂重启）
            var delay = TimeSpan.FromSeconds(1);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    StartProcess();
                    var ping = await RequestAsync("ping", null, ct);
                    if (ping.Ok)
                    {
                        return;
                    }

                    // 宿主应答但拒绝 ping：进程还活着，Process.Dispose 不会终止它，
                    // 须先杀再退避重试（否则每轮泄漏一个活子进程）
                    await KillProcessAsync();
                }
                catch (Exception)
                {
                    await DisposeProcessAsync();
                }

                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    private void StartProcess()
    {
        var info = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };
        if (_environment is not null)
        {
            foreach (var (key, value) in _environment)
            {
                info.EnvironmentVariables[key] = value;
            }
        }

        _process = Process.Start(info) ?? throw new InvalidOperationException("failed to start connector process");
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token), CancellationToken.None);
        // 宿主 stderr 只作日志（不阻塞）；进程退出时 stderr 管道要读完
        _ = Task.Run(() => _process.StandardError.ReadToEndAsync());
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var reader = _stdout;
        if (reader is null)
        {
            return;
        }

        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // 事件推送（type=event）投递到事件通道（有界丢旧保新）；
                // 非 JSON 垃圾行忽略不中断
                if (line.Contains("\"type\":\"event\"", StringComparison.Ordinal))
                {
                    ConnectorEvent? ev;
                    try
                    {
                        ev = JsonSerializer.Deserialize<ConnectorEvent>(line, ConnectorProtocol.Json);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (ev is { Event: "snapshot" } && ev.Snapshot is { } snapshot)
                    {
                        _eventChannel.Writer.TryWrite(SnapshotParser.Parse(snapshot, _playerKey));
                    }

                    continue;
                }

                ConnectorResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<ConnectorResponse>(line, ConnectorProtocol.Json);
                }
                catch (JsonException)
                {
                    continue; // 垃圾非 JSON 行忽略不中断
                }

                if (response is null)
                {
                    continue;
                }

                lock (_pendingLock)
                {
                    if (_pending.Remove(response.Id, out var tcs))
                    {
                        tcs.TrySetResult(response);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }

        // 读循环退出 = 子进程 stdout 已关闭（进程崩溃或被 Kill）。立即失败所有
        // 在途请求并释放进程引用（审计 T4-1：否则在途请求空等 RequestTimeout 25s，
        // 且首命令必卡满超时才自愈）。下一次请求经 EnsureProcessAsync 直接重启。
        OnProcessExited();
    }

    /// <summary>子进程退出后的即时清理（读循环线程调用）。</summary>
    private void OnProcessExited()
    {
        // 读循环退出 = 子进程 stdout 关闭（崩溃/被 Kill）：立即失败所有在途请求
        // （审计 T4-1：否则空等 RequestTimeout 25s），下一次请求经 EnsureProcessAsync
        // 双检 HasExited 直接重启。不 dispose/置空 _process——保留进程对象使调用方
        // 的 kill→WaitForExit 模式仍可用（dispose 后访问 Process 成员会抛）；
        // 释放统一由 DisposeProcessAsync（Deactivate/Dispose/超时杀进程）负责。
        lock (_pendingLock)
        {
            foreach (var (_, tcs) in _pending)
            {
                tcs.TrySetException(new InvalidOperationException("connector process terminated"));
            }

            _pending.Clear();
        }
    }

    private async Task<ConnectorResponse> RequestAsync(
        string action,
        string? query,
        CancellationToken ct,
        PlayerCommand? command = null,
        PlayerTrack? track = null)
    {
        var (id, tcs) = await WriteRequestAsync(action, query, ct, command, track);
        try
        {
            return await tcs.Task.WaitAsync(RequestTimeout, ct);
        }
        catch (TimeoutException)
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }

            // 契约（docs/04 §1.4）：协议超时主动 terminate 僵死进程，否则
            // 子进程 hang 时每个请求固定空等 25s 且永不自愈
            await KillProcessAsync();
            throw new TimeoutException($"connector request {action} timed out after {RequestTimeout.TotalSeconds}s");
        }
        catch (OperationCanceledException)
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }

            throw;
        }
    }

    /// <summary>
    /// 序列化写请求并注册响应配对表。必须在 WriteLine/Flush 之前注册
    /// <see cref="_pending"/>：读循环在独立线程上，FlushAsync 一返回子进程即可
    /// 回包，若先写后注册，响应会早于注册到达而被读循环丢弃（25s 超时 +
    /// 杀进程重启）。写失败时回滚注册。
    /// </summary>
    private async Task<(string Id, TaskCompletionSource<ConnectorResponse> Tcs)> WriteRequestAsync(
        string action,
        string? query,
        CancellationToken ct,
        PlayerCommand? command,
        PlayerTrack? track)
    {
        await _writeLock.WaitAsync(ct);
        var lockHeld = true; // 锁归属跟踪：CurrentCount 无法区分持有者
        try
        {
            if (_process is not { HasExited: false })
            {
                // 进程已退出：释放锁后自动重启（EnsureProcess 内部会再发
                // ping 请求、重新拿锁），再继续本请求
                _writeLock.Release();
                lockHeld = false;
                await EnsureProcessAsync(ct);
                await _writeLock.WaitAsync(ct);
                lockHeld = true;
            }

            var id = Interlocked.Increment(ref _nextId).ToString();
            var request = new ConnectorRequest
            {
                Id = id,
                Action = action,
                Player = _playerKey,
                Query = query,
                Command = command is not null ? ConnectorProtocol.CommandName(command.Value) : null,
                Track = track,
                EventProtocolVersion = 1,
            };
            var line = JsonSerializer.Serialize(request, ConnectorProtocol.Json);
            var tcs = new TaskCompletionSource<ConnectorResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock)
            {
                _pending[id] = tcs;
            }

            try
            {
                await _stdin!.WriteLineAsync(line);
                await _stdin.FlushAsync(ct);
            }
            catch
            {
                lock (_pendingLock)
                {
                    _pending.Remove(id);
                }

                throw;
            }

            return (id, tcs);
        }
        finally
        {
            if (lockHeld)
            {
                _writeLock.Release();
            }
        }
    }

    private static PlayerOperationResult ParseOperationResult(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } obj)
        {
            return PlayerOperationResult.Success(PlayerOutcome.Indeterminate, "missing result");
        }

        var outcome = SnapshotParser.GetString(obj, "outcome") ?? "indeterminate";
        var message = SnapshotParser.GetString(obj, "message") ?? "";
        var parsed = outcome.ToLowerInvariant() switch
        {
            "accepted" => PlayerOutcome.Accepted,
            "applied" => PlayerOutcome.Applied,
            "verified" => PlayerOutcome.Verified,
            "indeterminate" => PlayerOutcome.Indeterminate,
            "rejected" => PlayerOutcome.Rejected,
            "unsupported" => PlayerOutcome.Unsupported,
            _ => PlayerOutcome.Indeterminate,
        };
        return new PlayerOperationResult(parsed, message);
    }

    private async Task DisposeProcessAsync()
    {
        if (_readerCts is not null)
        {
            _readerCts.Cancel();
            if (_readerTask is not null)
            {
                try
                {
                    await _readerTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }
            }

            _readerCts.Dispose();
            _readerCts = null;
        }

        if (_process is not null)
        {
            try
            {
                _process.Dispose();
            }
            catch
            {
            }

            _process = null;
        }

        _stdin = null;
        _stdout = null;
        // 未完成的请求全部失败（进程已死，不能挂死调用方）
        lock (_pendingLock)
        {
            foreach (var (_, tcs) in _pending)
            {
                tcs.TrySetException(new InvalidOperationException("connector process terminated"));
            }

            _pending.Clear();
        }
    }

    /// <summary>协议超时/僵死：直接 terminate 进程并释放（不等待 shutdown）。</summary>
    private async Task KillProcessAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        await DisposeProcessAsync();
    }

    public async ValueTask DisposeAsync() => await DeactivateAsync();
}
