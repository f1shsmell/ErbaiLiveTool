using System.Threading.Channels;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Players;
using Erbai.Contracts.Plugins;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Contracts.Storage;
using Erbai.Modules.SongRequest.Permissions;
using Erbai.Modules.SongRequest.Playback;
using Erbai.Modules.SongRequest.Search;

namespace Erbai.Modules.SongRequest.Services
{
    using SongRequest = Erbai.Contracts.Requests.SongRequest;

/// <summary>
/// 点歌队列服务：提交流水线 / 终态操作 / 快照投影 / 事件广播。
/// 检查顺序不可变（docs/03 §1.2）：用户黑名单 → 重复点歌 → 队满 → 单用户上限；
/// 每条拒绝都持久化 rejected 记录 + 发 queue.rejected 事件。
/// Worker 派发循环与空闲歌单见 Worker（同程序集），播放状态机里程碑 4 接入，
/// 搜索处理器由组合根注入。
/// </summary>
public sealed class SongQueueService
{
    private readonly IStorageEngine _store;
    private readonly IEventBus _eventBus;
    private readonly UserService _userService;
    private readonly Channel<SongRequest> _pending;
    private readonly SemaphoreSlim _submitGate = new(1, 1);
    private IdlePlaylistConfig _idleConfig;

    public RequestPolicy Policy { get; }

    /// <summary>搜索处理器（三源编排，里程碑 5 注入；null = 未配置）。</summary>
    public Func<SongRequest, CancellationToken, Task<IReadOnlyList<SongSearchResult>?>>? Processor { get; set; }

    /// <summary>热更新队列/权限策略（设置页保存后调用；docs/00 修复记录 #16——默认 max_per_user=1
    /// 且无 UI 入口，用户测试/实际点歌数量都受限）。</summary>
    public void ReloadPolicy(AppConfig config) => Policy.Reload(config);

    /// <summary>当前播放器插件（空闲歌单与播放状态机使用）。</summary>
    public IMusicPlayerPlugin? Player { get; set; }

    /// <summary>进程内日志总线（组合根注入；无控制台的 WinUI 宿主下关键错误必须落盘才能事后排查）。</summary>
    public ILogBus? Logs { get; set; }

    /// <summary>正在播放的请求 id（is_current 语义：设于发 dispatched 事件前，清于发终态事件前）。</summary>
    public long? CurrentRequestId { get; private set; }

    /// <summary>内部置位（Worker 派发成功时调用；测试经 InternalsVisibleTo 使用）。</summary>
    internal void SetCurrentRequestId(long? requestId) => CurrentRequestId = requestId;

    public SongQueueService(IStorageEngine store, IEventBus eventBus, AppConfig config, UserService? userService = null)
    {
        _store = store;
        _eventBus = eventBus;
        Policy = new RequestPolicy(config);
        _userService = userService ?? new UserService(store);
        _idleConfig = config.IdlePlaylist;
        _config = config;
        _pending = Channel.CreateUnbounded<SongRequest>();
    }

    private readonly AppConfig _config;

    /// <summary>播放状态机时间参数（默认从配置构建；测试注入缩短值）。</summary>
    public PlaybackOptions? Options { get; set; }

    internal Channel<SongRequest> Pending => _pending;

    internal IdlePlaylistConfig IdleConfig => _idleConfig;

    internal IStorageEngine Store => _store;

    // ---- 提交流水线（顺序不可变，docs/03 §1.2）----

    public async Task<(SongRequest? Request, PermissionDecision Decision)> SubmitAsync(SongRequest request, CancellationToken ct = default)
    {
        await _submitGate.WaitAsync(ct);
        try
        {
            if (!_started)
            {
                await StartAsync(ct);
            }

            var canonical = string.IsNullOrEmpty(request.CanonicalSongKey)
                ? SongKey.Normalize(request.SongName, request.Singer)
                : request.CanonicalSongKey;
            request = request with { CanonicalSongKey = canonical };

            // B 站弹幕回调同步拿不到存储特权：统一在提交时合并（B 站无"显式
            // 标志"降级语义，or 合并正确；撤销 admin 后存储为 False 即不升）。
            // 抖音路径已在弹幕处理时精细合并，此处不动，避免破坏显式标志语义。
            if (request.Platform == "bilibili" && !string.IsNullOrEmpty(request.UserId))
            {
                var stored = await _store.GetUserAsync("bilibili", request.RoomId, request.UserId, ct);
                if (stored is { IsAdmin: true })
                {
                    request = request with { IsAdmin = true };
                }
            }

            // 用户黑名单优先于一切其他规则（docs/03 §1.2）——必须在等级门槛之前，
            // 否则被拉黑但等级不足的用户得到 level_too_low 而非 user_banned（审计 T1-P6）
            if (!string.IsNullOrEmpty(request.UserId) &&
                await _store.IsUserBannedAsync(request.Platform, request.RoomId, request.UserId, ct))
            {
                return await RejectAsync(request,
                    PermissionDecision.Deny("user_banned", "你已被主播加入黑名单，无法点歌"), ct);
            }

            var decision = Policy.CheckUser(ToUser(request));
            if (!decision.Allowed)
            {
                return await RejectAsync(request, decision, ct);
            }

            // 重复点歌必须在单用户上限之前：已有一首在队的用户重复点同一首
            // 歌要看到 duplicate_song 而不是 user_limit_reached
            if (Policy.DedupeEnabled && !string.IsNullOrEmpty(canonical) &&
                await _store.CanonicalExistsAsync(canonical, ct))
            {
                return await RejectAsync(request,
                    PermissionDecision.Deny("duplicate_song", "这首歌已经在点歌队列中"), ct);
            }

            var active = await _store.ListRequestsAsync(RequestStatuses.Active, ct);
            decision = Policy.CheckQueue(request, active);
            if (!decision.Allowed)
            {
                return await RejectAsync(request, decision, ct);
            }

            var queued = await _store.InsertRequestAsync(request with { Status = RequestStatus.Queued }, ct);
            await _pending.Writer.WriteAsync(queued, ct);
            SignalIdleWakeup(); // 新点歌立即切走空闲歌单
            // 用户保存走 UserService 单一来源：普通消息不得清掉已持久化的特权
            await _userService.SaveWithPrivilegeMergeAsync(ToUser(queued), incrementRequestCount: true, ct);
            await EmitAsync("queue.added", new QueueEventData { Request = queued }, ct);
            return (queued, PermissionDecision.Allow());
        }
        finally
        {
            _submitGate.Release();
        }
    }

    /// <summary>流水线外的拒绝（歌曲黑名单等路径）：与权限拒绝一致地持久化 + 发事件。</summary>
    public async Task RecordRejectedAsync(SongRequest request, string reasonCode, string message, CancellationToken ct = default)
    {
        var decision = PermissionDecision.Deny(reasonCode, message);
        var persisted = await PersistRejectedAsync(request, decision, ct);
        await EmitAsync("queue.rejected", new QueueEventData { Request = persisted, Decision = decision }, ct);
    }

    // ---- 操作员终态操作（docs/03 §1.6）----

    public Task<SongRequest?> CompleteAsync(long requestId, CancellationToken ct = default) =>
        SetTerminalAsync(requestId, RequestStatus.Completed, ct);

    public Task<SongRequest?> SkipAsync(long requestId, CancellationToken ct = default) =>
        SetTerminalAsync(requestId, RequestStatus.Skipped, ct);

    public Task<SongRequest?> CancelAsync(long requestId, CancellationToken ct = default) =>
        SetTerminalAsync(requestId, RequestStatus.Cancelled, ct);

    public async Task<SongRequest?> SkipFirstAsync(CancellationToken ct = default)
    {
        var items = await _store.ListRequestsAsync(RequestStatuses.Active, ct);
        var first = items.FirstOrDefault();
        return first?.RequestId is null ? null : await SetTerminalAsync(first.RequestId.Value, RequestStatus.Skipped, ct);
    }

    private async Task<SongRequest?> SetTerminalAsync(long requestId, RequestStatus status, CancellationToken ct)
    {
        if (!OperatorTerminalStatuses.Contains(status))
        {
            throw new ArgumentException($"unsupported operator terminal status: {status}", nameof(status));
        }

        var request = await _store.UpdateRequestAsync(requestId, status: status,
            expectedStatuses: RequestStatuses.Active, ct: ct);
        if (request is not null)
        {
            if (requestId == CurrentRequestId)
            {
                SignalPlaybackWakeup();
                // 终态事件附带快照前清空正在播放指针，避免把已完成/已取消的
                // 歌标成 is_current（推送驱动下前端直接消费该快照）
                CurrentRequestId = null;
            }

            await EmitAsync($"queue.{StatusName(status)}", new QueueEventData { Request = request }, ct);
        }

        // 终态后队列已空（无活动/在途/空闲歌可播）→ 暂停播放器。否则 lxmusic
        // 等外部播放器会无限继续放最后那首歌（docs/00 修复记录 #14）
        await PausePlayerWhenIdleAsync(ct);
        return request;
    }

    /// <summary>
    /// 队列空闲挂起：无活动请求、无在途 pending、且无空闲歌可播时向播放器下发
    /// Pause（幂等；失败忽略）。供操作员终态、状态机自然结算、空闲歌单空/连续
    /// 失败三处调用——任一活动/在途/空闲歌存在时不动（下一首会经 PlaySelected
    /// 切歌，空闲歌会接管播放）。
    /// </summary>
    internal async Task PausePlayerWhenIdleAsync(CancellationToken ct = default)
    {
        var player = Player;
        if (player is null)
        {
            return;
        }

        var active = await _store.ListRequestsAsync(RequestStatuses.Active, ct);
        if (active.Count > 0)
        {
            return; // 即将派发新歌（PlaySelected 切歌），不暂停
        }

        if (_pending.Reader.Count > 0)
        {
            return; // 在途请求，worker 即将派发，不暂停
        }

        if (_idleConfig.Enabled && IdleSongs.Count > 0)
        {
            return; // 空闲歌单会接管播放，不暂停
        }

        try
        {
            await player.ExecuteAsync(PlayerCommand.Pause, null, ct);
            LogInfo("队列已空且无空闲歌单，已暂停播放器");
        }
        catch (Exception ex)
        {
            LogWarning($"队列空闲暂停播放器失败（忽略）：{ex.Message}");
        }
    }

    // ---- 只读查询 ----

    public Task<SongRequest?> GetRequestAsync(long requestId, CancellationToken ct = default) =>
        _store.GetRequestAsync(requestId, ct);

    public Task<RequestStatus?> GetRequestStatusAsync(long requestId, CancellationToken ct = default) =>
        _store.GetRequestStatusAsync(requestId, ct);

    public Task<IReadOnlyDictionary<RequestStatus, int>> CountRequestsByStatusAsync(CancellationToken ct = default) =>
        _store.CountRequestsByStatusAsync(ct);

    public Task<(IReadOnlyList<SongRequest> Rows, int Total)> ListRequestsPageAsync(
        string? platform = null,
        RequestStatus? status = null,
        bool descending = true,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default) =>
        _store.ListRequestsPageAsync(platform, status, descending, limit, offset, ct);

    // ---- 快照投影（docs/03 §1.6：position 动态重编号；派发固定 FIFO）----

    public async Task<QueueSnapshot> GetSnapshotAsync(int? limit = null, string? order = null, CancellationToken ct = default)
    {
        var active = (await _store.ListRequestsAsync(RequestStatuses.Active, ct)).ToList();
        var effectiveOrder = order ?? Policy.DisplayOrder;
        var reverse = !string.Equals(effectiveOrder, "asc", StringComparison.OrdinalIgnoreCase);
        active.Sort((a, b) =>
        {
            var bySequence = (a.Sequence ?? 0).CompareTo(b.Sequence ?? 0);
            return reverse ? -bySequence : bySequence;
        });

        var currentId = CurrentRequestId;
        var displayLimit = Math.Max(Policy.DisplayLimit, 1);
        var items = new List<QueueItem>();
        for (var position = 0; position < active.Count; position++)
        {
            var item = active[position];
            items.Add(new QueueItem
            {
                Request = item,
                Position = position + 1,
                IsCurrent = item.RequestId == currentId,
            });
        }

        var requestedLimit = limit is null ? items.Count : Math.Max(limit.Value, 1);
        return new QueueSnapshot
        {
            Items = items.Take(requestedLimit).ToList(),
            QueueTotal = items.Count,
            QueueLimit = Policy.MaxQueueSize,
            DisplayLimit = displayLimit,
            CurrentRequestId = currentId,
            Player = CachedPlayerStatus(),
        };
    }

    /// <summary>快照热路径的 player 字段：只读缓存，绝不发起网络请求。</summary>
    public QueuePlayerStatus CachedPlayerStatus()
    {
        if (Player is { } player)
        {
            var idle = CurrentIdleSong;
            return new QueuePlayerStatus
            {
                Key = player.Key,
                Label = player.DisplayName,
                Connected = false,
                // 空闲歌单在播时填入当前空闲歌，供概览页/悬浮窗在队列无
                // is_current 项时回落显示（Player.SongName 实为预留空槽）。
                // 无空闲歌时保持 null，维持概览页既有 "暂无" 的 UI。
                SongName = string.IsNullOrEmpty(idle.SongName) ? null : idle.SongName,
                Singer = string.IsNullOrEmpty(idle.Singer) ? null : idle.Singer,
            };
        }

        return new QueuePlayerStatus { Connected = false };
    }

    /// <summary>当前空闲歌单正在播放的歌曲（不占 queue，仅供快照/UI 展示）。</summary>
    public (string SongName, string Singer) CurrentIdleSong { get; private set; }

    /// <summary>worker 在空闲歌播放开始/结束时设/清当前空闲歌（值改变后由
    /// worker 发 queue.idle_playing 事件广播一次快照，悬浮窗据此刷新）。</summary>
    internal void SetCurrentIdleSong(string songName, string singer)
    {
        CurrentIdleSong = (songName ?? "", singer ?? "");
    }

    // ---- 事件广播（每个 queue.* 事件附完整快照；快照失败退化空快照）----

    public async Task EmitAsync(string eventName, QueueEventData data, CancellationToken ct = default)
    {
        QueueSnapshot snapshot;
        try
        {
            snapshot = await GetSnapshotAsync(ct: ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[队列] 生成事件快照失败：{ex.Message}");
            // 容量字段用 null（而非 0）：前端以 '-' 渲染，避免失败路径显示
            // 成 "0/0" 这种误导性数值
            snapshot = new QueueSnapshot
            {
                Items = [],
                QueueTotal = 0,
                QueueLimit = null,
                DisplayLimit = null,
                CurrentRequestId = null,
                Player = new QueuePlayerStatus { Connected = false },
            };
        }

        var envelope = new QueueEventEnvelope
        {
            Event = eventName,
            Timestamp = DateTimeOffset.UtcNow,
            Data = data with { QueueSnapshot = snapshot },
        };
        _eventBus.Publish(envelope);
    }

    internal void LogWarning(string message)
    {
        Console.WriteLine($"[队列] {message}");
        // WinUI 宿主无控制台：关键错误必须落盘（logs/erbai-*.log）便于事后定位
        // （docs/00 修复记录 #15：网易云"点不了歌"首候选派发被拒/播放等待异常等）
        Logs?.Warning($"[队列] {message}");
    }

    internal void LogInfo(string message)
    {
        Console.WriteLine($"[队列] {message}");
        Logs?.Information($"[队列] {message}");
    }

    // ---- 播放状态机协作点 ----

    private readonly Channel<bool> _playbackWakeup = Channel.CreateUnbounded<bool>();

    internal void SignalPlaybackWakeup() => _playbackWakeup.Writer.TryWrite(true);

    /// <summary>等一次播放唤醒（操作员终态操作时信号化；Channel 无丢失窗口——TCS
    /// 每次重建时 Signal 落在间隙会丢，新点歌可能被空闲歌压住一个间隔）。</summary>
    internal async Task WaitPlaybackWakeupAsync(CancellationToken ct)
    {
        try
        {
            await _playbackWakeup.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
        }
    }

    // ---- 空闲歌单协作点（新点歌立即切走空闲歌单；Worker 消费）----

    private readonly Channel<bool> _idleWakeup = Channel.CreateUnbounded<bool>();

    internal void SignalIdleWakeup() => _idleWakeup.Writer.TryWrite(true);

    internal async Task WaitIdleWakeupAsync(CancellationToken ct)
    {
        try
        {
            await _idleWakeup.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
        }
    }

    // ---- 生命周期（worker 派发 + 重启恢复 + 空闲歌单）----

    private bool _started;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private int _lastRecovered;

    /// <summary>空闲歌单（从存储加载；ReloadIdleSongsAsync 热更新）。</summary>
    public IReadOnlyList<Erbai.Contracts.Storage.IdleSong> IdleSongs { get; private set; } = [];

    private int _idleReloadVersion;

    /// <summary>空闲歌单热更新版本（ReloadIdleSongsAsync 递增；Worker 据此在
    /// 播放被打断后跳过 play_interval 立即按新列表继续）。</summary>
    internal int IdleReloadVersion => Interlocked.CompareExchange(ref _idleReloadVersion, 0, 0);

    internal bool IsStarted => _started;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            IdleSongs = await _store.LoadIdleSongsAsync(ct);
            await RecoverAsync(ct);
        }
        catch
        {
            // 初始化失败必须复位：否则 _started 恒 true 而无 worker——
            // 后续 StartAsync 全部 no-op、SubmitAsync 直接写入无人消费的
            // _pending，队列整场静默死亡（QueueUpModule 同款教训）
            _started = false;
            throw;
        }

        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(() => Worker.RunAsync(this, _workerCts.Token), CancellationToken.None);
        await EmitAsync("queue.started", new QueueEventData { Recovered = _lastRecovered }, ct);
    }

    /// <summary>热更新空闲歌单（保存后立即生效无需重启；新歌单从轮转位置继续）。</summary>
    public async Task ReloadIdleSongsAsync(IReadOnlyList<Erbai.Contracts.Storage.IdleSong> songs, CancellationToken ct = default)
    {
        IdleSongs = songs;
        await _store.SaveIdleSongsAsync(songs, ct);
        Interlocked.Increment(ref _idleReloadVersion);
        SignalIdleWakeup();
    }

    /// <summary>
    /// 播放等待：播放器已接入时走播放状态机（判定矩阵，docs/03 §1.5）；
    /// 未接入时降级为时长定时器 fallback——等时长（无时长用总预算兜底），
    /// 可被操作员终态/新请求唤醒提前返回。等待期异常必须结算，
    /// 否则成"幽灵正在播放"。
    /// </summary>
    internal async Task WaitForPlaybackAsync(SongRequest request, IReadOnlyList<SongSearchResult> result, CancellationToken ct)
    {
        if (Player is null)
        {
            await FallbackPlaybackWaitAsync(request, result, ct);
            return;
        }

        var options = Options;
        if (options is null)
        {
            options = PlaybackOptions.FromConfig(_config);
        }

        await new PlaybackStateMachine(this, Player, options).RunAsync(request, result, ct);
    }

    /// <summary>无状态客户端时的时长定时器（等 duration，无则总预算；可被唤醒打断）。</summary>
    private async Task FallbackPlaybackWaitAsync(SongRequest request, IReadOnlyList<SongSearchResult> result, CancellationToken ct)
    {
        var duration = ParseIntervalSeconds(result.FirstOrDefault()?.Interval);
        var timeout = TimeSpan.FromSeconds(duration ?? 300);
        try
        {
            await WaitPlaybackWakeupAsync(ct).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // 总预算耗尽仍按 completed 结算，绝不让队列卡死
            var finished = await _store.UpdateRequestAsync(request.RequestId!.Value,
                status: RequestStatus.Completed, expectedStatuses: RequestStatuses.Active, ct: ct);
            if (finished is not null)
            {
                CurrentRequestId = null;
                await EmitAsync("queue.completed", new QueueEventData { Request = finished }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>解析 MM:SS / HH:MM:SS / 秒数（平移旧 queue_playback._interval_seconds）。</summary>
    internal static int? ParseIntervalSeconds(string? interval)
    {
        if (string.IsNullOrWhiteSpace(interval))
        {
            return null;
        }

        var parts = interval.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var minutes) && int.TryParse(parts[1], out var seconds))
        {
            return minutes * 60 + seconds;
        }

        if (parts.Length == 3 && int.TryParse(parts[0], out var hours) &&
            int.TryParse(parts[1], out minutes) && int.TryParse(parts[2], out seconds))
        {
            return hours * 3600 + minutes * 60 + seconds;
        }

        return int.TryParse(interval.Trim(), out var total) ? total : null;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _started = false;
        await _submitGate.WaitAsync(ct);
        try
        {
            if (_workerCts is not null)
            {
                _workerCts.Cancel();
                if (_workerTask is not null)
                {
                    try
                    {
                        await _workerTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                _workerCts.Dispose();
                _workerCts = null;
                _workerTask = null;
            }

            // 清空内存 pending：DB 状态仍是 queued，start() 会按恢复逻辑重新
            // 入队；不清空则同一实例 stop→start 会让同一请求入队两次被播放两次
            while (_pending.Reader.TryRead(out _))
            {
            }

            // 清空唤醒信号（stop 期间的信号与下次 start 无关）
            while (_playbackWakeup.Reader.TryRead(out _))
            {
            }

            while (_idleWakeup.Reader.TryRead(out _))
            {
            }
        }
        finally
        {
            _submitGate.Release();
        }
    }

    /// <summary>热更新空闲歌单配置（enabled/play_interval 立即生效，不再读构造时快照）。</summary>
    public void ReloadIdleConfig(Erbai.Contracts.Configuration.IdlePlaylistConfig config)
    {
        _idleConfig = config with { PlayInterval = Math.Max(config.PlayInterval, 1) };
        SignalIdleWakeup();
    }

    /// <summary>重启恢复（docs/03 §1.4）：queued 重入队；searching/ready/dispatched 直接置 failed/recovered_after_restart。</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        var recovered = await _store.ListRequestsAsync(RequestStatuses.Active, ct);
        var requeued = 0;
        foreach (var request in recovered)
        {
            if (request.RequestId is null)
            {
                continue;
            }

            if (request.Status == RequestStatus.Queued)
            {
                await _pending.Writer.WriteAsync(request, ct);
                requeued++;
                continue;
            }

            // searching/ready/dispatched 说明上次运行中断在半途，歌可能已发到
            // 播放器；重放会重复播，直接置 failed 让主播重新点
            await _store.UpdateRequestAsync(request.RequestId.Value, status: RequestStatus.Failed,
                failureReason: "recovered_after_restart", ct: ct);
        }

        _lastRecovered = requeued;
    }

    private static User ToUser(SongRequest request) => new()
    {
        Platform = request.Platform,
        RoomId = request.RoomId,
        UserId = request.UserId,
        Nickname = request.Nickname,
        IsAdmin = request.IsAdmin,
        IsAnchor = request.IsAnchor,
        FanLevel = request.FanLevel,
        MedalLevel = request.MedalLevel,
    };

    private async Task<(SongRequest?, PermissionDecision)> RejectAsync(
        SongRequest request, PermissionDecision decision, CancellationToken ct)
    {
        var persisted = await PersistRejectedAsync(request, decision, ct);
        await EmitAsync("queue.rejected", new QueueEventData { Request = persisted, Decision = decision }, ct);
        return (persisted, decision);
    }

    private async Task<SongRequest> PersistRejectedAsync(SongRequest request, PermissionDecision decision, CancellationToken ct)
    {
        // insert_request 在自己的事务里原子分配 sequence，无需单独取号
        return await _store.InsertRequestAsync(request with
        {
            Status = RequestStatus.Rejected,
            FailureReason = decision.ReasonCode,
        }, ct);
    }

    internal static string StatusName(RequestStatus status) => status.ToString().ToLowerInvariant();

    private static readonly IReadOnlySet<RequestStatus> OperatorTerminalStatuses = new HashSet<RequestStatus>
    {
        RequestStatus.Completed,
        RequestStatus.Skipped,
        RequestStatus.Cancelled,
    };
}
}
