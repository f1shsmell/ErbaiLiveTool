using Erbai.Contracts.Players;
using Erbai.Contracts.Plugins;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest.Playback
{
    using SongRequest = Erbai.Contracts.Requests.SongRequest;

    /// <summary>
    /// 播放状态机（判定矩阵，docs/03 §1.5）：
    /// 候选源换源（error/起播超时/刚播即断 → 下一候选，1s 冷却）、起播 15s 超时、
    /// 总预算兜底（时长 + 缓冲，无时长仅缓冲，兜底 300s）、刚播即停双条件判定、
    /// 暂停卡死 10s 观察窗、手动切歌识别（相似度 0.62 阈值全套歌名匹配）、
    /// next 对账守卫（3s 限频 → InsertNext → 连续 2 次 ArmNextGuard）、
    /// 轮询 1.0s 兜底。所有时间参数经 PlaybackOptions 注入（可测性缝）。
    /// 不变量：等待期异常必须结算 failed/playback_wait_error，否则成"幽灵正在播放"。
    /// </summary>
    public sealed class PlaybackStateMachine
    {
        private readonly SongQueueService _queue;
        private readonly IMusicPlayerPlugin _player;
        private readonly PlaybackOptions _options;

        public PlaybackStateMachine(SongQueueService queue, IMusicPlayerPlugin player, PlaybackOptions options)
        {
            _queue = queue;
            _player = player;
            _options = options;
        }

        public async Task RunAsync(SongRequest request, IReadOnlyList<SongSearchResult> result, CancellationToken ct)
        {
            try
            {
                var candidates = result.Count > 0 ? result : [ToCandidate(request)];
                var ctx = new PlaybackContext
                {
                    CandidateIndex = 0,
                    Started = false,
                    StartedAt = DateTimeOffset.UtcNow,
                    StartDeadline = DateTimeOffset.UtcNow + _options.PlaybackStartTimeout,
                    // 进入状态机前 worker 刚派发过首个候选（PlaySelected），
                    // 因此宽限窗从此刻起算
                    LastDispatchAt = DateTimeOffset.UtcNow,
                };
                ctx.TotalDeadline = ComputeTotalDeadline(candidates[ctx.CandidateIndex]);

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // 操作员终态操作（skip/cancel/complete）已把请求移出 dispatched → 直接退出
                    var status = await _queue.Store.GetRequestStatusAsync(request.RequestId!.Value, ct);
                    if (status != RequestStatus.Dispatched)
                    {
                        return;
                    }

                    var snapshot = await ProbeSafeAsync(ct);
                    if (snapshot is null)
                    {
                        // 播放器探测不可用（未连接/超时）：按播放等待异常结算，
                        // 避免请求永远卡在 dispatched 的"幽灵正在播放"状态
                        await FinishAsync(request, RequestStatus.Failed, "playback_wait_error", ct);
                        return;
                    }

                    var playStatus = DeriveStatus(snapshot);
                    var handled = ctx.Started
                        ? await HandleStartedAsync(request, candidates, ctx, playStatus, snapshot, ct)
                        : await HandleNotStartedAsync(request, candidates, ctx, playStatus, snapshot, ct);
                    if (handled)
                    {
                        continue;
                    }

                    if (ctx.Started)
                    {
                        await GuardNextSongAsync(request, ct);
                    }

                    await WaitNextStateAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 等待期异常必须结算（worker 不变量）
                _queue.LogWarning($"播放等待异常，结算该请求：{ex.Message}");
                await FinishAsync(request, RequestStatus.Failed, "playback_wait_error", CancellationToken.None);
            }
        }

        /// <summary>状态机循环的可变状态（async 方法不能使用 ref 参数）。</summary>
        private sealed class PlaybackContext
        {
            public int CandidateIndex;
            public bool Started;
            public DateTimeOffset StartedAt;
            public DateTimeOffset StartDeadline;
            public DateTimeOffset TotalDeadline;

            /// <summary>最近一次派发（PlaySelected）时刻：起播瞬态宽限窗的起点。</summary>
            public DateTimeOffset LastDispatchAt;

            /// <summary>未开播期连续 error 帧计数（非 error 帧清零）。</summary>
            public int ConsecutiveErrors;
        }

        // ---- 未开播分支（docs/03 §1.5）----

        private async Task<bool> HandleNotStartedAsync(
            SongRequest request,
            IReadOnlyList<SongSearchResult> candidates,
            PlaybackContext ctx,
            string playStatus,
            PlayerSnapshot snapshot,
            CancellationToken ct)
        {
            if (playStatus != "error")
            {
                ctx.ConsecutiveErrors = 0;
            }

            if (playStatus == "error")
            {
                // 起播瞬态宽限窗：PlaySelected 刚下发，播放器还在搜索/解析音源，
                // /status 会残留上一个源的 error（lxmusic music/searchPlay 自搜自播
                // 尤其明显）。窗内 error 一律不信，交给起播超时统一裁决——否则请求
                // 在派发后 2~3s 就被秒杀成 playback_failed，而歌其实随后正常播起来
                // （用户实测 2026-09-08 14:28 req205）。对齐空闲歌路径的既有防护
                // Worker.IdleTransientGraceSeconds。
                if (DateTimeOffset.UtcNow - ctx.LastDispatchAt < _options.PlaybackStartGrace)
                {
                    return false;
                }

                // 窗后仍需连续多帧 error 才认定音源真的坏了（单次抖动不换源）
                if (++ctx.ConsecutiveErrors < _options.ErrorConfirmThreshold)
                {
                    return false;
                }

                // 播放器报错（未登录/无版权/音源损坏）：换下一候选源
                var switched = await TryNextCandidateAsync(request, candidates, ctx, ct);
                if (switched)
                {
                    ctx.StartDeadline = DateTimeOffset.UtcNow + _options.PlaybackStartTimeout;
                    ctx.TotalDeadline = ComputeTotalDeadline(candidates[ctx.CandidateIndex]);
                    _queue.LogWarning($"音源 {candidates[ctx.CandidateIndex].Source} 播放失败，切换下一音源");
                    await CooldownAsync(ct);
                    return true;
                }

                await FinishAsync(request, RequestStatus.Failed, "playback_failed", ct);
                return true;
            }

            if (playStatus == "playing" && SongIdentifiersMatch(snapshot, candidates[ctx.CandidateIndex], request))
            {
                ctx.Started = true;
                ctx.StartedAt = DateTimeOffset.UtcNow;
                return false;
            }

            if (DateTimeOffset.UtcNow >= ctx.StartDeadline)
            {
                // 起播超时：换下一候选源
                var switched = await TryNextCandidateAsync(request, candidates, ctx, ct);
                if (switched)
                {
                    ctx.StartDeadline = DateTimeOffset.UtcNow + _options.PlaybackStartTimeout;
                    ctx.TotalDeadline = ComputeTotalDeadline(candidates[ctx.CandidateIndex]);
                    _queue.LogWarning($"音源 {candidates[ctx.CandidateIndex].Source} 播放超时，切换下一音源");
                    await CooldownAsync(ct);
                    return true;
                }

                if (!_options.PlaybackFailSkip)
                {
                    // 主播关闭自动跳过：保持请求活动等手动处理，仍受总预算约束
                    if (DateTimeOffset.UtcNow >= ctx.TotalDeadline)
                    {
                        await FinishAsync(request, RequestStatus.Failed, "playback_failed", ct);
                        return true;
                    }

                    _queue.LogWarning($"播放未在 {_options.PlaybackStartTimeout.TotalSeconds}s 内开始，已关闭自动跳过，请手动跳过/取消该请求");
                    await WaitNextStateAsync(ct);
                    return true;
                }

                await FinishAsync(request, RequestStatus.Failed, "playback_failed", ct);
                return true;
            }

            return false;
        }

        // ---- 已开播分支 ----

        private async Task<bool> HandleStartedAsync(
            SongRequest request,
            IReadOnlyList<SongSearchResult> candidates,
            PlaybackContext ctx,
            string playStatus,
            PlayerSnapshot snapshot,
            CancellationToken ct)
        {
            var progress = snapshot.ProgressSeconds;
            var elapsed = (DateTimeOffset.UtcNow - ctx.StartedAt).TotalSeconds;
            // 完成判定：stoped/stopped（播放器报停）与 idle（播放器停止且 current 清空——
            // 非词表状态文本 + Current=null 时 DeriveStatus 回退推断为 idle）都是"播完"；
            // 此前 idle 未处理会一直等到总预算兜底（时长+缓冲 60s）才切下一首（用户实测 bug）。
            if (playStatus is "stoped" or "stopped" or "idle")
            {
                if (IsAbortedPlayback(progress, elapsed))
                {
                    // 刚开播就结束（音源失效/无版权/试听片段）：换源重试同一首歌
                    var switched = await TryNextCandidateAsync(request, candidates, ctx, ct);
                    if (switched)
                    {
                        ctx.StartDeadline = DateTimeOffset.UtcNow + _options.PlaybackStartTimeout;
                        ctx.TotalDeadline = ComputeTotalDeadline(candidates[ctx.CandidateIndex]);
                        ctx.Started = false;
                        _queue.LogInfo($"音源 {candidates[ctx.CandidateIndex].Source} 刚开播即失效，切换下一音源");
                        await CooldownAsync(ct);
                        return true;
                    }

                    await FinishAsync(request, RequestStatus.Failed, "playback_failed", ct);
                }
                else
                {
                    await FinishAsync(request, RequestStatus.Completed, ct: ct);
                }

                return true;
            }

            if (playStatus == "error")
            {
                // 播放中途报错（音源中断）：换源重试
                var switched = await TryNextCandidateAsync(request, candidates, ctx, ct);
                if (switched)
                {
                    ctx.StartDeadline = DateTimeOffset.UtcNow + _options.PlaybackStartTimeout;
                    ctx.TotalDeadline = ComputeTotalDeadline(candidates[ctx.CandidateIndex]);
                    ctx.Started = false;
                    _queue.LogInfo($"音源 {candidates[ctx.CandidateIndex].Source} 播放中断，切换下一音源");
                    await CooldownAsync(ct);
                    return true;
                }

                await FinishAsync(request, RequestStatus.Failed, "playback_failed", ct);
                return true;
            }

            if (playStatus is "paused" or "waiting")
            {
                // 播完列表自动暂停（网易云/QQ 音乐/落雪等常见行为）：进度已到歌曲结尾 → 按播完结算，
                // 否则要等总预算兜底（同样表现为"播完要等好久才下一首"）
                var duration = ResolveDurationSeconds(snapshot, candidates[ctx.CandidateIndex]);
                if (progress is not null && duration is > 0 && progress >= duration - 2)
                {
                    await FinishAsync(request, RequestStatus.Completed, ct: ct);
                    return true;
                }

                // 墙钟已播满整首 → 播完（落雪等播放器播完自动暂停时会把 progress/duration
                // 一起归零：实测 /status 返回 {"status":"paused","progress":0,"duration":0}，
                // 上面的进度判定永远不成立）。此时 progress=0 不是"刚开始播放"的证据，
                // 必须先看 elapsed 是否已达时长，否则整首歌播完反而被下面的 stall 判定
                // 误杀成 playback_failed（用户实测 2026-09-08 14:36 req206：
                // 生命周期 271s ≈ 候选 interval 269s，歌完整播完却记 failed）。
                if (duration is > 0 && elapsed >= duration - 2)
                {
                    await FinishAsync(request, RequestStatus.Completed, ct: ct);
                    return true;
                }

                // 主播暂停/正常缓冲不是结束；但开播后进度始终接近 0 且超过
                // 观察窗口（源失效卡死）必须结算失败，否则卡到总预算
                if (progress is not null &&
                    progress < _options.ShortPlayFail.TotalSeconds &&
                    elapsed > _options.PausedStallGrace.TotalSeconds)
                {
                    await FinishAsync(request, RequestStatus.Failed, "playback_failed", ct);
                    return true;
                }
            }

            if (DateTimeOffset.UtcNow >= ctx.TotalDeadline)
            {
                // 播放器一直报 playing 超过时长+缓冲：按播完结算，绝不让队列卡死
                await FinishAsync(request, RequestStatus.Completed, ct: ct);
                return true;
            }

            if (playStatus == "playing" && ManualSkipDetected(snapshot, candidates[ctx.CandidateIndex], request))
            {
                await FinishAsync(request, RequestStatus.Skipped, "skipped_manually", ct);
                return true;
            }

            return false;
        }

        // ---- next 对账守卫（docs/03 §1.5；quirk #1 修正：队首读 queued）----

        private DateTimeOffset _guardAttemptAt = DateTimeOffset.MinValue;
        private int _guardMismatchCount;

        private async Task GuardNextSongAsync(SongRequest request, CancellationToken ct)
        {
            // 队列可编程能力缺失（lxmusic）→ 守卫整体跳过，行为与旧版一致
            if (!_player.Capabilities.HasFlag(PlayerCapabilities.QueueProgrammable))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _guardAttemptAt < _options.GuardResyncInterval)
            {
                return;
            }

            _guardAttemptAt = now;
            var snapshot = await ProbeSafeAsync(ct);
            if (snapshot is null || snapshot.NextObservation == NextObservation.Unknown)
            {
                return; // 无法枚举队首（QQ 音乐等）→ 不提交
            }

            var first = await NextQueuedSongAsync(request, ct);
            if (first is null)
            {
                return;
            }

            var expected = ToPlayerTrack(first);
            if (snapshot.Next is not null && TrackIdentity.TracksRepresentSame(snapshot.Next, expected))
            {
                _guardMismatchCount = 0;
                return;
            }

            _guardMismatchCount++;
            try
            {
                if (_guardMismatchCount >= _options.GuardArmThreshold)
                {
                    await _player.ExecuteAsync(PlayerCommand.ArmNextGuard, expected, ct);
                }
                else
                {
                    await _player.ExecuteAsync(PlayerCommand.InsertNext, expected, ct);
                }
            }
            catch (Exception ex)
            {
                _queue.LogWarning($"队列守卫执行失败（尽力而为）：{ex.Message}");
            }
        }

        /// <summary>本地队列队首（当前请求之后的第一个 queued——quirk #1 修正旧版读 "pending"）。</summary>
        private async Task<SongRequest?> NextQueuedSongAsync(SongRequest request, CancellationToken ct)
        {
            var rows = await _queue.Store.ListRequestsAsync(
                new HashSet<RequestStatus> { RequestStatus.Queued }, ct);
            var first = rows.FirstOrDefault();
            return first is not null && first.RequestId != request.RequestId ? first : null;
        }

        // ---- 换源 ----

        private async Task<bool> TryNextCandidateAsync(
            SongRequest request,
            IReadOnlyList<SongSearchResult> candidates,
            PlaybackContext ctx,
            CancellationToken ct)
        {
            var index = ctx.CandidateIndex;
            while (index + 1 < candidates.Count)
            {
                index++;
                var candidate = candidates[index];
                try
                {
                    var op = await _player.ExecuteAsync(PlayerCommand.PlaySelected, ToPlayerTrack(candidate), ct);
                    if (!op.Successful)
                    {
                        _queue.LogWarning($"音源 {candidate.Source} 派发被拒（{op.Outcome}），继续找下一候选");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _queue.LogWarning($"音源 {candidate.Source} 派发失败：{ex.Message}");
                    continue;
                }

                // 回写 search_result 供快照/历史反映实际尝试的音源。
                // 必须保持数组契约：Worker 派发时写入的是候选数组（JsonSerialize(playable)），
                // 这里若直接序列化单个 candidate 会把列降级为对象——同一字段两种形态，
                // 任何按数组解析的消费方（历史页/导出/未来读取方）都会炸
                // （实测 2026-09-08：req205 换源后 srj 变成 "{" 开头的单对象，
                // 未换源的 req204/206 是 "[" 开头的数组）。
                try
                {
                    await _queue.Store.UpdateRequestAsync(request.RequestId!.Value,
                        searchResultJson: System.Text.Json.JsonSerializer.Serialize(new[] { candidate }),
                        expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Dispatched }, ct: ct);
                }
                catch (Exception)
                {
                }

                ctx.CandidateIndex = index;
                // 换源即一次新的派发：重置起播瞬态宽限窗与 error 连续计数，
                // 新源同样享有宽限期（否则上一源的残留 error 会立刻把新源判死）
                ctx.LastDispatchAt = DateTimeOffset.UtcNow;
                ctx.ConsecutiveErrors = 0;
                return true;
            }

            return false;
        }

        // ---- 结算与等待 ----

        /// <summary>终态结算（CAS expected=dispatched；发终态事件前清 current）。</summary>
        private async Task FinishAsync(SongRequest request, RequestStatus status, string failureReason = "", CancellationToken ct = default)
        {
            var current = await _queue.Store.UpdateRequestAsync(request.RequestId!.Value,
                status: status,
                failureReason: failureReason.Length > 0 ? failureReason : null,
                expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Dispatched },
                ct: ct);
            if (current is not null)
            {
                if (_queue.CurrentRequestId == request.RequestId)
                {
                    // 终态事件附带快照前清空正在播放指针（is_current 语义）
                    _queue.SetCurrentRequestId(null);
                }

                await _queue.EmitAsync($"queue.{SongQueueService.StatusName(status)}",
                    new QueueEventData { Request = current, Reason = failureReason.Length > 0 ? failureReason : null }, ct);
            }

            // 自然结算（播完/手动切歌/失败）后队列已空且无空闲歌可播 → 暂停播放器
            // （docs/00 修复记录 #14；lxmusic 等外部播放器播完列表不会自停）
            await _queue.PausePlayerWhenIdleAsync(ct);
        }

        /// <summary>
        /// 等待下一次状态更新：播放唤醒（操作员动作）优先，以轮询间隔兜底
        /// （SSE 事件驱动在 lxmusic 连接器接入时挂到 WaitPlaybackWakeupAsync）。
        /// </summary>
        private async Task WaitNextStateAsync(CancellationToken ct)
        {
            try
            {
                await _queue.WaitPlaybackWakeupAsync(ct).WaitAsync(_options.PollInterval);
            }
            catch (TimeoutException)
            {
            }
        }

        private async Task CooldownAsync(CancellationToken ct) => await Task.Delay(_options.SwitchSourceCooldown, ct);

        private async Task<PlayerSnapshot?> ProbeSafeAsync(CancellationToken ct)
        {
            try
            {
                return await _player.ProbeAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _queue.LogWarning($"获取播放状态失败：{ex.Message}");
                return null;
            }
        }

        private DateTimeOffset ComputeTotalDeadline(SongSearchResult candidate)
        {
            var duration = SongQueueService.ParseIntervalSeconds(candidate.Interval);
            var buffer = _options.PlaybackTimeout ?? TimeSpan.FromSeconds(300);
            var total = duration is not null ? TimeSpan.FromSeconds(duration.Value) + buffer : buffer;
            return DateTimeOffset.UtcNow + total;
        }

        // ---- 判定辅助 ----

        /// <summary>
        /// 从快照派生播放状态：RawStatus 仅在词表内才机器可读；空或诊断文本
        /// （Kugou/QQ/网易云/Folia 的人类可读描述）按 Current 推断——否则
        /// 起播判定永远不命中，每个请求都走 15s 起播超时。
        /// </summary>
        private static string DeriveStatus(PlayerSnapshot snapshot) =>
            PlayerStatusVocabulary.Normalize(snapshot.RawStatus)
                ?? (snapshot.Current is not null ? "playing" : "idle");

        /// <summary>刚播即停双条件：进度与墙钟都小于阈值。</summary>
        private bool IsAbortedPlayback(double? progress, double elapsed) =>
            progress is not null &&
            progress < _options.ShortPlayFail.TotalSeconds &&
            elapsed < _options.ShortPlayFail.TotalSeconds;

        /// <summary>
        /// 解析当前播放曲目的时长（秒）：优先用播放器实报 duration，缺失/为 0 时
        /// 回落候选的 interval（mm:ss）。落雪等播放器在播完自动暂停时会把
        /// progress 和 duration 一起归零，只信实报 duration 会让"播完"永远判不出来。
        /// </summary>
        private static int? ResolveDurationSeconds(PlayerSnapshot snapshot, SongSearchResult candidate)
        {
            var reported = snapshot.Current?.DurationSeconds;
            if (reported is > 0)
            {
                return reported;
            }

            return SongQueueService.ParseIntervalSeconds(candidate.Interval);
        }

        /// <summary>开播识别/手动切歌识别的歌名匹配（docs/03 §1.5 全套算法）。</summary>
        internal bool SongIdentifiersMatch(PlayerSnapshot snapshot, SongSearchResult result, SongRequest request)
        {
            var expectedName = SongKey.Normalize(result.Name.Length > 0 ? result.Name : request.SongName);
            var expectedSinger = SongKey.Normalize(result.Singer.Length > 0 ? result.Singer : request.Singer);
            var actualName = SongKey.Normalize(snapshot.Current?.Title ?? "");
            var actualSinger = SongKey.Normalize(snapshot.Current?.Artist ?? "");
            if (expectedName.Length == 0)
            {
                return true;
            }

            // 双向子串包含
            if (actualName.Length > 0 &&
                (expectedName.Contains(actualName, StringComparison.Ordinal) ||
                 actualName.Contains(expectedName, StringComparison.Ordinal)))
            {
                return true;
            }

            // 版本注释宽容：双方都带 (Inst.)/（纯音乐）等注释且主标题相等
            var rawExpected = result.Name.Length > 0 ? result.Name : request.SongName;
            var rawActual = snapshot.Current?.Title ?? "";
            if (TrackIdentity.HasVersionAnnotation(rawExpected) &&
                TrackIdentity.HasVersionAnnotation(rawActual) &&
                TrackIdentity.PrimaryTitle(rawExpected).Length >= 2 &&
                TrackIdentity.PrimaryTitle(rawExpected) == TrackIdentity.PrimaryTitle(rawActual))
            {
                return true;
            }

            // 相似度兜底（0.62 阈值）
            if (expectedName.Length > 0 && actualName.Length > 0 &&
                Similarity(expectedName, actualName) >= _options.SongMatchRatioThreshold)
            {
                return true;
            }

            // 歌手兜底：仅当播放器完全没报歌名；双方都有歌名且明显不同时，
            // 歌手相同不算匹配（防同歌手另一首被误判开播）
            if (expectedSinger.Length > 0 && actualSinger.Length > 0 && actualName.Length == 0 &&
                (expectedSinger.Contains(actualSinger, StringComparison.Ordinal) ||
                 actualSinger.Contains(expectedSinger, StringComparison.Ordinal)))
            {
                return true;
            }

            return false;
        }

        /// <summary>播放器实报与派发歌明显不同 → 手动切歌。</summary>
        internal bool ManualSkipDetected(PlayerSnapshot snapshot, SongSearchResult result, SongRequest request)
        {
            var actualName = snapshot.Current?.Title ?? "";
            var actualSinger = snapshot.Current?.Artist ?? "";
            if (actualName.Length == 0 && actualSinger.Length == 0)
            {
                return false;
            }

            return !SongIdentifiersMatch(snapshot, result, request);
        }

        /// <summary>
        /// SequenceMatcher.ratio 等价物（difflib 递归最长公共子串，2*M/T）。
        /// 用于歌名相似度兜底（0.62 阈值）。
        /// </summary>
        internal static double Similarity(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0)
            {
                return 1.0;
            }

            if (a.Length == 0 || b.Length == 0)
            {
                return 0.0;
            }

            var matched = MatchLength(a, b);
            return 2.0 * matched / (a.Length + b.Length);
        }

        private static int MatchLength(string a, string b)
        {
            var best = 0;
            var bestI = 0;
            var bestJ = 0;
            var dp = new int[a.Length + 1, b.Length + 1];
            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    if (a[i - 1] == b[j - 1])
                    {
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                        if (dp[i, j] > best)
                        {
                            best = dp[i, j];
                            bestI = i;
                            bestJ = j;
                        }
                    }
                }
            }

            if (best == 0)
            {
                return 0;
            }

            var left = MatchLength(a[..(bestI - best)], b[..(bestJ - best)]);
            var right = MatchLength(a[bestI..], b[bestJ..]);
            return best + left + right;
        }

        private static SongSearchResult ToCandidate(SongRequest request) => new()
        {
            Source = "unknown",
            Name = request.SongName,
            Singer = request.Singer,
            SongMid = "",
        };

        internal static PlayerTrack ToPlayerTrack(SongSearchResult result) => new()
        {
            Platform = result.Source,
            Title = result.Name,
            Artist = result.Singer,
            Id = result.SongMid,
            Album = result.AlbumName,
            // lxmusic 连接器播放载荷：完整 LX music/play 字段（source/types/hash/interval 等）。
            // 换源路径同样需要（与 Worker.ToPlayerTrack 保持一致）。
            NativeData = System.Text.Json.JsonSerializer.Serialize(result),
        };

        private static PlayerTrack ToPlayerTrack(SongRequest request) => new()
        {
            Platform = "unknown",
            Title = request.SongName,
            Artist = request.Singer,
        };
    }
}
