using System.Threading.Channels;
using Erbai.Contracts.Players;
using Erbai.Contracts.Plugins;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Contracts.Storage;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest
{
    using SongRequest = Erbai.Contracts.Requests.SongRequest;

    /// <summary>
/// 队列 worker 循环：取单、派发、空闲歌单。
/// 不变量：任何异常都不得逃逸 worker——必须显式结算当前请求为 failed 并继续
/// 循环，否则整个队列永久停摆。
///
/// 空闲歌单为新设计（旧 _maybe_play_idle 不复用，参考 JMusicBot defaultQueue
/// 机制 + 修复旧版缺陷）：
/// 1. 触发 = 请求队列空（无 active 请求且 pending 空），不是轮询队列长度；
/// 2. 空闲歌单与请求队列分离（不写 song_requests 表，不污染点歌队列/历史）；
/// 3. 顺序轮转，播完一首后等 play_interval 再播下一首（旧版是开播后等
///     interval，歌未播完就被切——间隔语义修正）；
/// 4. 新请求到达立即打断空闲播放（SignalIdleWakeup，worker 切去派发点歌）；
/// 5. 播放失败跳过该歌继续下一首；连续失败达阈值暂停一段时间（防死循环
///    刷播放器，旧版失败只记日志照样空转是反例）。
/// </summary>
internal static class Worker
{
    /// <summary>空闲歌连续播放失败阈值：超过后暂停一轮 interval 再试。</summary>
    private const int MaxConsecutiveIdleFailures = 3;

    private sealed class IdleState
    {
        public int Index;
        public bool Waiting;
        public int ConsecutiveFailures;
    }

    public static async Task RunAsync(SongQueueService queue, CancellationToken ct)
    {
        var idle = new IdleState();
        while (!ct.IsCancellationRequested)
        {
            if (queue.IdleConfig.Enabled)
            {
                try
                {
                    var idleHandled = await MaybePlayIdleAsync(queue, idle, ct);
                    if (idleHandled)
                    {
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    queue.LogWarning($"空闲歌单循环异常（继续运行）：{ex.Message}");
                }
            }

            SongRequest request;
            try
            {
                request = await queue.Pending.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }

            await DispatchOneAsync(queue, request, ct);
        }
    }

    /// <summary>
    /// 派发单个请求（docs/03 §1.3）：定向状态查询 → CAS searching → processor
    /// → CAS dispatched → 先置 _current_request_id 再发事件 → 等待播放。
    /// 任何异常显式结算 failed 并继续（search_failed / 原异常文本）。
    /// </summary>
    private static async Task DispatchOneAsync(SongQueueService queue, SongRequest request, CancellationToken ct)
    {
        if (request.RequestId is null)
        {
            return;
        }

        try
        {
            // 定向状态查询：不因历史记录积累而加载整张请求表；非 queued 直接丢弃（防重复消费）
            var currentStatus = await queue.Store.GetRequestStatusAsync(request.RequestId.Value, ct);
            if (currentStatus != RequestStatus.Queued)
            {
                return;
            }

            var searching = await queue.Store.UpdateRequestAsync(request.RequestId.Value,
                status: RequestStatus.Searching, expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Queued },
                ct: ct);
            if (searching is null)
            {
                return; // CAS 失败，其他人已消费
            }

            await queue.EmitAsync("queue.searching", new QueueEventData { Request = searching }, ct);

            var processor = queue.Processor;
            if (processor is null)
            {
                var failed = await queue.Store.UpdateRequestAsync(request.RequestId.Value,
                    status: RequestStatus.Failed, failureReason: "search_unavailable",
                    expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Searching }, ct: ct);
                if (failed is not null)
                {
                    await queue.EmitAsync("queue.failed",
                        new QueueEventData { Request = failed, Reason = "search_unavailable" }, ct);
                }

                return;
            }

            var result = await processor(request, ct);
            if (result is null || result.Count == 0)
            {
                var failed = await queue.Store.UpdateRequestAsync(request.RequestId.Value,
                    status: RequestStatus.Failed, failureReason: "song_not_found",
                    expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Searching }, ct: ct);
                if (failed is not null)
                {
                    await queue.EmitAsync("queue.failed",
                        new QueueEventData { Request = failed, Reason = "song_not_found" }, ct);
                }

                return;
            }

            // 候选按播放器源过滤（docs/00 修复记录 #22）：网易云/酷狗/QQ 音乐这类单源
            // 客户端只能播自家源——跨源候选会被路由到当前播放器连接器、产生
            // "CEF 桥拒绝:ERR invalid-song-id"（酷狗 hash 当网易云 id）等误拒；
            // lxmusic（落雪）多源播放器保留全源候选
            var playable = FilterPlayableCandidates(queue.Player, result);
            if (playable.Count == 0)
            {
                // 日志盲区修复：此分支历史上零日志（失败码只在历史页展示），
                // 搜索源悄悄失效时（非 200/解析空、接口改版）用户完全无法判断
                // 原因。打出播放器 key 与全部跨源候选明细，定位"点歌失败
                // no playable source"（QQ/酷狗单源播放器最常见）。
                var detail = string.Join("、", result.Select(c => $"{c.Source}[{c.Name}-{c.Singer}]"));
                queue.LogWarning($"点歌 {request.SongName} 失败：播放器 {queue.Player?.Key} 无匹配候选，" +
                    $"共搜索到 {result.Count} 个跨源候选（{detail}）");
                var failed = await queue.Store.UpdateRequestAsync(request.RequestId.Value,
                    status: RequestStatus.Failed, failureReason: "no_playable_source",
                    expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Searching, RequestStatus.Ready },
                    ct: ct);
                if (failed is not null)
                {
                    await queue.EmitAsync("queue.failed",
                        new QueueEventData { Request = failed, Reason = $"当前播放器没有可播放的搜索结果源（搜索到 {result.Count} 个跨源候选：{detail}）" }, ct);
                }

                return;
            }

            var canonical = SongKey.Normalize(
                playable[0].Name.Length > 0 ? playable[0].Name : request.SongName,
                playable[0].Singer.Length > 0 ? playable[0].Singer : request.Singer);
            var dispatched = await queue.Store.UpdateRequestAsync(request.RequestId.Value,
                status: RequestStatus.Dispatched,
                searchResultJson: JsonSerialize(playable),
                canonicalSongKey: canonical,
                expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Searching, RequestStatus.Ready },
                ct: ct);
            if (dispatched is null || dispatched.RequestId is not { } requestId)
            {
                return;
            }

            // 先置正在播放指针再发 dispatched 事件，保证事件附带的快照
            // is_current 正确指向刚派发的歌曲
            queue.SetCurrentRequestId(dispatched.RequestId);
            // 逐个候选立即派发：任一成功即停；全部被拒 → 立即失败结算，
            // 不再死等 15s 起播超时（docs/00 修复记录 #21；旧实现只试首候选，
            // 被拒后状态机空等 → "点歌后 15 秒才有结果"）
            var (accepted, acceptedIndex) = await DispatchFirstCandidateAsync(queue, playable, ct);
            if (!accepted)
            {
                var failed = await queue.Store.UpdateRequestAsync(requestId,
                    status: RequestStatus.Failed, failureReason: "all_sources_rejected",
                    expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Dispatched }, ct: ct);
                if (failed is not null)
                {
                    queue.SetCurrentRequestId(null);
                    await queue.EmitAsync("queue.failed",
                        new QueueEventData { Request = failed, Reason = "all_sources_rejected" }, ct);
                }

                return;
            }

            // 已试过且被拒的前置候选不再进状态机（否则换源会重播已拒候选）；
            // 从成功候选起作为剩余候选
            var remaining = acceptedIndex > 0 ? playable.Skip(acceptedIndex).ToList() : playable;
            await queue.EmitAsync("queue.dispatched",
                new QueueEventData { Request = dispatched, ResultJson = JsonSerialize(playable) }, ct);
            await queue.WaitForPlaybackAsync(dispatched, remaining, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 停止信号
        }
        catch (Exception ex)
        {
            // 任何环节抛异常（存储抖动/磁盘满/processor 崩溃）都必须显式
            // 结算并继续循环：异常一旦逃逸，worker 任务结束且无人重建，
            // 整个队列从此停摆（新点歌永远停在 queued）
            queue.LogWarning($"处理请求异常，标记该请求失败：{ex.Message}");
            try
            {
                var reason = ex is RecoverableSearchException ? "search_failed" : ex.Message;
                var failed = await queue.Store.UpdateRequestAsync(request.RequestId.Value,
                    status: RequestStatus.Failed, failureReason: reason,
                    expectedStatuses: new HashSet<RequestStatus>
                    {
                        RequestStatus.Queued, RequestStatus.Searching,
                        RequestStatus.Ready, RequestStatus.Dispatched,
                    }, ct: CancellationToken.None);
                if (failed is not null)
                {
                    await queue.EmitAsync("queue.failed",
                        new QueueEventData { Request = failed, Reason = reason }, ct);
                }
            }
            catch (Exception inner)
            {
                queue.LogWarning($"标记失败也失败（worker 继续运行）：{inner.Message}");
            }
        }
    }

    /// <summary>
/// 候选按播放器源过滤（docs/00 修复记录 #22）：单源客户端（netease/kugou/qqmusic）
/// 只能播自家源的歌——跨源候选会被路由到当前播放器连接器、产生误导性的
/// "CEF 桥拒绝: invalid-song-id"（酷狗 hash 被当网易云 id）；lxmusic（落雪）是
/// 多源播放器，保留全源候选。FakePlayer 等非单源 key 不过滤（测试/未知播放器
/// 用旧语义）。Folia 不在过滤名单：三源不产生 folia 源，若过滤会导致其点歌全空
/// （与既有行为一致地继续失败在派发阶段，见 docs/00 #22）。
/// </summary>
    private static IReadOnlyList<SongSearchResult> FilterPlayableCandidates(
        IMusicPlayerPlugin? player, IReadOnlyList<SongSearchResult> result)
    {
        if (player is null || result.Count == 0)
        {
            return result;
        }

        var key = player.Key;
        if (!SingleSourcePlayers.Contains(key) || key == "lxmusic")
        {
            return result;
        }

        return result.Where(candidate =>
            string.Equals(candidate.Source, key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static readonly IReadOnlySet<string> SingleSourcePlayers = new HashSet<string>(
        ["netease", "kugou", "qqmusic"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 逐个候选立即派发（docs/00 修复记录 #21）：任一 PlaySelected 成功即停；被拒
    /// 立即试下一候选（旧实现只试第一个，被拒后状态机死等 15s 起播超时才换源）。
    /// 返回 (是否至少一个候选被接受, 被接受候选的索引)。
    /// </summary>
    private static async Task<(bool Accepted, int Index)> DispatchFirstCandidateAsync(
        SongQueueService queue, IReadOnlyList<SongSearchResult> result, CancellationToken ct)
    {
        var player = queue.Player;
        if (player is null)
        {
            // 无播放器（未接入）：保持旧语义——不派发、请求继续 dispatched，
            // 状态机走 fallback 时长等待（提交/权限类测试依赖该活动状态）
            return (true, 0);
        }

        if (result.Count == 0)
        {
            return (false, 0);
        }

        for (var i = 0; i < result.Count; i++)
        {
            try
            {
                var op = await player.ExecuteAsync(PlayerCommand.PlaySelected, ToPlayerTrack(result[i]), ct);
                if (op.Successful)
                {
                    return (true, i);
                }

                queue.LogWarning($"候选 {result[i].Source} 派发被拒（{op.Outcome}）：{op.Message}（立即试下一候选）");
            }
            catch (Exception ex)
            {
                queue.LogWarning($"候选 {result[i].Source} 派发失败：{ex.Message}（立即试下一候选）");
            }
        }

        return (false, 0);
    }

    /// <summary>
    /// 空闲歌单播放（新设计，见类注释）。返回 true 表示空闲循环处理过一轮
    /// （worker 应重新检查），false 表示有点歌请求等待处理。
    /// </summary>
    private static async Task<bool> MaybePlayIdleAsync(SongQueueService queue, IdleState state, CancellationToken ct)
    {
        // 请求队列非空 → 不播空闲（活动请求或在途 pending 都优先）
        var active = await queue.Store.ListRequestsAsync(RequestStatuses.Active, ct);
        if (active.Count > 0)
        {
            state.Waiting = false;
            return false;
        }

        if (queue.Pending.Reader.Count > 0)
        {
            state.Waiting = false;
            return false;
        }

        var songs = queue.IdleSongs;
        if (songs.Count == 0)
        {
            // 空闲歌单为空（或已清空）：队列无事可做时要挂起播放器，
            // 否则最后一首空闲歌会无限继续放（docs/00 修复记录 #14）
            await queue.PausePlayerWhenIdleAsync(ct);
            return false;
        }

        // 上一首正在间隔等待：等 play_interval 或新请求唤醒（唤醒后立即
        // 返回让 worker 派发点歌，打断空闲播放；超时则复位等待标志，下一
        // 轮播下一首——旧版"开播后等 interval"切掉未播完的歌，此处改为
        // 播完后的间隔）
        if (state.Waiting)
        {
            await WaitIdleIntervalAsync(queue, ct);
            state.Waiting = false;
            return true;
        }

        // 连续失败达到阈值：暂停一轮 interval 防死循环刷播放器（同时挂起播放器）
        if (state.ConsecutiveFailures >= MaxConsecutiveIdleFailures)
        {
            state.ConsecutiveFailures = 0;
            await queue.PausePlayerWhenIdleAsync(ct);
            await WaitIdleIntervalAsync(queue, ct);
            return true;
        }

        var song = songs[state.Index % songs.Count];
        state.Index++;
        var (ok, result) = await PlayIdleSongAsync(queue, song, ct);
        if (!ok)
        {
            state.ConsecutiveFailures++;
            return true; // 失败立即试下一首（跳过失败歌）
        }

        state.ConsecutiveFailures = 0;
        // 空闲歌在播：置当前空闲歌位并广播一次快照，让概览页/悬浮窗在队列
        // 无 is_current 项时回落显示"正在播放空闲歌"（overlay 靠快照缓存轮询，
        // 空闲歌不写 song_requests 表、不入 items，必须有事件触发快照刷新）。
        queue.SetCurrentIdleSong(song.Name, song.Singer);
        await queue.EmitAsync("queue.idle_playing", new QueueEventData { }, ct);

        // 记录开播时的歌单版本：期间若主播热更新列表（ReloadIdleSongsAsync
        // 唤醒打断），结束后直接按新列表继续，不干等 play_interval（用户实测
        // "换列表里的歌要等很久"——打断后无点歌时旧逻辑还要等一整轮间隔）
        var reloadVersionAtPlay = queue.IdleReloadVersion;

        // 先跟踪播放直到结束（播完/失败/预算，期间新点歌唤醒立即切走），
        // 再等一轮 play_interval——"播完等间隔"名副其实（旧版开播后等
        // interval 会把长歌在 ~2×interval 处切掉）
        state.Waiting = true;
        await WaitIdleSongEndAsync(queue, result, ct);
        state.Waiting = false;

        // 空闲歌结束（播完/被打断切去点歌）：清空闲歌位并广播一次，恢复
        // 悬浮窗"正在播放"为空/点歌状态
        queue.SetCurrentIdleSong("", "");
        await queue.EmitAsync("queue.idle_playing", new QueueEventData { }, ct);

        // 审计 T1-P1：空闲歌若是被新点歌唤醒打断（队列已有待派发/活动请求），
        // 必须立即交还 worker 派发点歌——否则"播完等间隔"会再等整整一个
        // play_interval（默认 30s），观众点歌在空闲播放中被推迟最长 30s
        //（唤醒信号已在上一步被消费，WaitIdleIntervalAsync 无信号可等）。
        var moreActive = await queue.Store.ListRequestsAsync(RequestStatuses.Active, ct);
        if (moreActive.Count > 0 || queue.Pending.Reader.Count > 0)
        {
            return true;
        }

        // 空闲歌单列表在播放期间被热更新：立即按新列表轮转下一首（跳过间隔）。
        // 唤醒信号已被 WaitIdleSongEndAsync 消费，若不显式交还，这里会再等
        // 一整轮 interval 才轮到新列表的歌
        if (reloadVersionAtPlay != queue.IdleReloadVersion)
        {
            return true;
        }

        await WaitIdleIntervalAsync(queue, ct);
        return true;
    }

    /// <summary>
    /// 跟踪空闲歌播放直到结束：轮询播放器状态（stopped/error 即结束；总预算
    /// 兜底防卡死），期间新点歌唤醒立即返回打断。探测失败按已结束处理。
    ///
    /// 瞬态保护（用户实测"启动就放空闲歌，放一小会立马切歌"）：播放器在
    /// PlaySelected 后存在切歌/加载窗口（短暂报 stopped/error、或探测 HTTP
    /// 失败），此时不得判定"播完"——开播未满瞬态窗且无有效进度时继续观察；
    /// 探测失败需连续 N 次才放弃（播放器未就绪时一轮一轮切歌的旧行为去掉）。
    /// </summary>
    private static async Task WaitIdleSongEndAsync(
        SongQueueService queue, SongSearchResult? result, CancellationToken ct)
    {
        var player = queue.Player;
        if (player is null)
        {
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var duration = SongQueueService.ParseIntervalSeconds(result?.Interval);
        var deadline = DateTimeOffset.UtcNow +
                       TimeSpan.FromSeconds((duration ?? 300) + PlaybackBufferSeconds);
        var probeFailures = 0;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            // 新点歌唤醒 → 立即返回打断空闲播放。注意：唤醒信号是单次可消费的
            // Channel bool，绝不能在消费后继续循环——否则信号被本次读掉、下一轮
            // WaitIdleWakeupAsync 无信号可等，只能等空闲歌自然播完才切到点歌队列
            //（观众点歌被长时间推迟，docs/00 修复记录：空闲歌单不立即切走）。
            // 因此这里用唤醒信号与 500ms 轮询窗口竞争，谁先到处理谁。
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wake = queue.WaitIdleWakeupAsync(pollCts.Token);
            var poll = Task.Delay(TimeSpan.FromMilliseconds(500), pollCts.Token);
            var wouldWait = await Task.WhenAny(wake, poll);
            pollCts.Cancel(); // 取消未完成的那一侧，避免残留等待/延迟泄漏
            if (ct.IsCancellationRequested)
            {
                return;
            }

            // 唤醒判定不能用 WhenAny 的返回顺序：信号可能在 poll 先完成、
            // Cancel() 执行前已到达（ReadAsync 已完成、信号被消费），此时
            // WhenAny 仍按 poll 返回——必须检查 wake 实际完成态，否则点歌
            // 信号被吞、空闲歌要等自然播完才切（用户实测"点歌要等很久"）
            if (wake.IsCompleted)
            {
                return; // 被新点歌唤醒，立即打断空闲播放交还 worker 派发
            }

            PlayerSnapshot snapshot;
            try
            {
                snapshot = await player.ProbeAsync(ct);
                probeFailures = 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 探测失败不立即判结束（播放器启动/切歌窗口常见 HTTP 失败）：
                // 连续失败达阈值才按失败跳过——否则空闲歌在播放器就绪前被
                // 一轮一轮切走（用户实测"启动就放空闲歌，放一小会立马切歌"）
                if (++probeFailures >= MaxConsecutiveIdleProbeFailures)
                {
                    return;
                }

                continue;
            }

            // 空闲歌结束检测：RawStatus 仅词表内机器可读，诊断文本按 Current 推断。
            // idle 也按播完处理（与点歌状态机一致——播放器播完清 current 后不
            // 再回词表 stopped 的场景，旧实现会干等到预算兜底才轮转）
            var status = PlayerStatusVocabulary.Normalize(snapshot.RawStatus)
                ?? (snapshot.Current is not null ? "playing" : "idle");
            if (status is "stoped" or "stopped" or "error" or "idle")
            {
                // 刚播即停双条件保护：开播未满瞬态窗且无有效进度（播放器切歌/
                // 加载窗口短暂报 stopped）不算播完，继续观察；开播已超窗或有
                // 推进过的进度（真播完/真失败）才结束——对齐点歌状态机的
                // IsAbortedPlayback 语义
                var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
                var progress = snapshot.ProgressSeconds;
                if (elapsed < IdleTransientGraceSeconds &&
                    (progress is null || progress < IdleTransientGraceSeconds))
                {
                    continue;
                }

                return; // 播完 / 播放失败
            }
        }
    }

    /// <summary>空闲歌起播瞬态保护窗（秒）：窗内 stopped/error/探测失败不判播完。</summary>
    private const int IdleTransientGraceSeconds = 5;

    /// <summary>空闲歌探测连续失败阈值：超过才按播放失败跳过（跳过仍走失败计数）。</summary>
    private const int MaxConsecutiveIdleProbeFailures = 3;

    /// <summary>空闲歌播放跟踪的预算缓冲（对齐点歌 PlaybackTimeout 默认 60s）。</summary>
    private const int PlaybackBufferSeconds = 60;

    /// <summary>
    /// 播放一首空闲歌：走与点歌相同的搜索路径（三源），取第一候选派发给
    /// 播放器；失败返回 false（调用方跳过该歌）。空闲歌不写 song_requests
    /// 表、不设 _current_request_id（不占队列、不参与历史/快照）。
    /// </summary>
    private static async Task<(bool Ok, SongSearchResult? Result)> PlayIdleSongAsync(
        SongQueueService queue, IdleSong song, CancellationToken ct)
    {
        try
        {
            var processor = queue.Processor;
            if (processor is null)
            {
                return (false, null);
            }

            var pseudoRequest = new SongRequest
            {
                Platform = "idle",
                UserId = "",
                SongName = song.Name,
                Singer = song.Singer,
                Status = RequestStatus.Received,
            };
            var result = await processor(pseudoRequest, ct);
            if (result is null || result.Count == 0)
            {
                queue.LogWarning($"空闲歌单播放失败（无搜索结果）：{song.Name}");
                return (false, null);
            }

            var player = queue.Player;
            if (player is null)
            {
                queue.LogWarning($"空闲歌单播放失败（播放器未接入）：{song.Name}");
                return (false, null);
            }

            var track = ToPlayerTrack(result[0]);
            var op = await player.ExecuteAsync(PlayerCommand.PlaySelected, track, ct);
            if (!IsSuccessful(op))
            {
                queue.LogWarning($"空闲歌单播放被拒（{op.Outcome}）：{song.Name}");
                return (false, null);
            }

            queue.LogInfo($"空闲歌单播放：{song.Name} - {song.Singer}");
            return (true, result[0]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            queue.LogWarning($"空闲歌单播放失败：{ex.Message}");
            return (false, null);
        }
    }

    /// <summary>等一轮空闲间隔（play_interval 或新请求唤醒）。</summary>
    private static async Task WaitIdleIntervalAsync(SongQueueService queue, CancellationToken ct)
    {
        try
        {
            await queue.WaitIdleWakeupAsync(ct)
                .WaitAsync(TimeSpan.FromSeconds(Math.Max(queue.IdleConfig.PlayInterval, 1)));
        }
        catch (TimeoutException)
        {
        }
    }

    private static PlayerTrack ToPlayerTrack(SongSearchResult result) => new()
    {
        Platform = result.Source,
        Title = result.Name,
        Artist = result.Singer,
        Id = result.SongMid,
        Album = result.AlbumName,
        // lxmusic 连接器播放载荷：完整 LX music/play 字段（source/types/hash/interval 等）
        NativeData = System.Text.Json.JsonSerializer.Serialize(result),
    };

    private static bool IsSuccessful(PlayerOperationResult result) => result.Successful;

    private static string JsonSerialize(IReadOnlyList<SongSearchResult> results) =>
        System.Text.Json.JsonSerializer.Serialize(results);
}

/// <summary>可恢复的搜索错误（连接/超时）→ 归为 search_failed 分类码（里程碑 5 使用）。</summary>
public sealed class RecoverableSearchException : Exception
{
    public RecoverableSearchException(string message) : base(message)
    {
    }
}

}
