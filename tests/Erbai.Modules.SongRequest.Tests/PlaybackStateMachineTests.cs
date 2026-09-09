using Erbai.Contracts.Players;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Core.Storage;
using Erbai.Modules.SongRequest.Playback;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest.Tests
{
    using SongRequest = Erbai.Contracts.Requests.SongRequest;

/// <summary>
/// 播放状态机判定矩阵（关键场景，docs/03 §1.5）：
/// 换源（error/起播超时/刚播即断）、预算、刚播即停双条件、暂停卡死观察窗、
/// 手动切歌识别、next 对账守卫、探测失败结算。时间参数全部注入缩短值。
/// </summary>
public class PlaybackStateMachineTests
{
    private static PlaybackOptions FastOptions() => new()
    {
        PlaybackStartTimeout = TimeSpan.FromMilliseconds(300),
        PlaybackTimeout = TimeSpan.FromSeconds(2),
        PlaybackFailSkip = true,
        PollInterval = TimeSpan.FromMilliseconds(40),
        ShortPlayFail = TimeSpan.FromMilliseconds(120),
        PausedStallGrace = TimeSpan.FromMilliseconds(400),
        SwitchSourceCooldown = TimeSpan.FromMilliseconds(20),
        // 宽限窗必须短于起播超时，否则 error 路径的测试会先被起播超时分支抢走
        PlaybackStartGrace = TimeSpan.FromMilliseconds(60),
        ErrorConfirmThreshold = 2,
        GuardResyncInterval = TimeSpan.FromMilliseconds(120),
        GuardArmThreshold = 2,
    };

    private static async Task<(SqliteStorageEngine Store, SongQueueService Queue, FakePlayer Player)>
        CreateMachineAsync(FakePlayer player, PlaybackOptions? options = null, bool withGuard = false)
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        queue.Player = player;
        queue.Options = options ?? FastOptions();
        if (withGuard)
        {
            player.Capabilities = PlayerCapabilities.QueueProgrammable;
        }

        return (store, queue, player);
    }

    /// <summary>插一条 dispatched 请求（状态机直接驱动，不经 worker）。</summary>
    private static async Task<SongRequest> InsertDispatchedAsync(SqliteStorageEngine store, string songName = "晴天")
    {
        return await store.InsertRequestAsync(TestHarness.NewRequest(songName, userId: "u1")
            with { Status = RequestStatus.Dispatched });
    }

    private static IReadOnlyList<SongSearchResult> Candidates(params string[] sources) =>
        sources.Select((source, i) => new SongSearchResult
        {
            Source = source,
            Name = "晴天",
            Singer = "周杰伦",
            SongMid = $"mid-{source}",
        }).ToList();

    private static async Task<RequestStatus> RunToTerminalAsync(SongQueueService queue, SongRequest request,
        FakePlayer player, int timeoutMs = 8000)
    {
        await new PlaybackStateMachine(queue, player, queue.Options!).RunAsync(request,
            Candidates("kugou", "netease"), CancellationToken.None);
        return (await queue.Store.GetRequestStatusAsync(request.RequestId!.Value))!.Value;
    }

    // ---- 未开播：起播超时换源 / 开播确认 / error 换源 ----

    [Fact]
    public async Task StartTimeout_SwitchesToNextCandidate_ThenStarts()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 先持续报 idle(超过起播超时)→ 换源执行 PlaySelected(netease) → 报 playing 匹配 → 正常播完
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            if (probes <= 10)
            {
                return FakePlayer.Snapshot(status: "idle"); // 覆盖 300ms 起播超时(40ms×10)
            }

            return probes <= 12
                ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦")
                : FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 200);
        };
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
        Assert.Contains(player.Executed, e => e.Command == PlayerCommand.PlaySelected && e.Track!.Platform == "netease");
    }

    [Fact]
    public async Task NotStarted_Error_WithNoCandidatesLeft_Fails()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 未开播就 error,且无候选可换 → failed/playback_failed
        player.FixedProbe = FakePlayer.Snapshot(status: "error");
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Failed, final);
        var persisted = await store.GetRequestAsync(request.RequestId!.Value);
        Assert.Equal("playback_failed", persisted!.FailureReason);
    }

    [Fact]
    public async Task StartConfirmation_RequiresSongMatch()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // playing 但歌名不匹配(别的歌)→ 不算开播 → 起播超时后换源 → 换源后才匹配开播
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            if (probes <= 10)
            {
                return FakePlayer.Snapshot(status: "playing", title: "完全不同的歌", artist: "别人");
            }

            return probes <= 12
                ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦")
                : FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 200);
        };
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
        // 换源命令确实发过（第一个候选被放弃）
        Assert.Contains(player.Executed, e => e.Command == PlayerCommand.PlaySelected);
    }

    // ---- 已开播：刚播即停 / 正常播完 / 中途 error / 暂停卡死 / 总预算 / 手动切歌 ----

    [Fact]
    public async Task AbortedPlayback_ShortStop_SwitchesSource_ThenFailsWhenNoCandidates()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 开播后立刻 stoped(progress≈0)→ 换源 netease → 又立刻 stoped → 无候选 → failed
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 0));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "stoped", title: "晴天", progress: 0));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "stoped", title: "晴天", progress: 0));
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Failed, final);
        Assert.Equal("playback_failed", (await store.GetRequestAsync(request.RequestId!.Value))!.FailureReason);
        Assert.Single(player.Executed, e => e.Command == PlayerCommand.PlaySelected); // 换源一次
    }

    [Fact]
    public async Task NormalFinish_StoppedAfterLongEnough_Completes()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 播放足够久后 stoped(进度远超阈值)→ completed
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 0));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 210));
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
    }

    [Fact]
    public async Task Idle_AfterLongPlayback_Completes()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 播完停止且 current 清空（rawStatus 非词表/空 → DeriveStatus 回退推断 idle）→ 立即 completed，
        // 不能等总预算兜底（用户实测 bug：播完要等好久才下一首）
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 0));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "idle", title: null, progress: null));
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
    }

    [Fact]
    public async Task Idle_ShortStop_SwitchesSource_ThenFailsWhenNoCandidates()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 开播后立刻 idle（进度≈0，刚播即停语义）→ 换源 netease → 又立刻 idle → 无候选 → failed
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 0));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "idle", title: null, progress: 0));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "idle", title: null, progress: 0));
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Failed, final);
        Assert.Equal("playback_failed", (await store.GetRequestAsync(request.RequestId!.Value))!.FailureReason);
        Assert.Single(player.Executed, e => e.Command == PlayerCommand.PlaySelected); // 换源一次
    }

    [Fact]
    public async Task Paused_AtSongEnd_Completes()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 播完列表自动暂停（网易云/QQ 音乐常见）：paused 且进度已到歌曲结尾 → completed
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 0, duration: 240));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "paused", title: "晴天", progress: 239, duration: 240));
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
    }

    [Fact]
    public async Task MidPlayError_SwitchesSource_OrFails()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 中途 error → 换源 netease 成功开播 → 正常播完
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 30));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "error", title: "晴天", progress: 30));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 35));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 240));
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
        Assert.Contains(player.Executed, e => e.Command == PlayerCommand.PlaySelected && e.Track!.Platform == "netease");
    }

    [Fact]
    public async Task PausedStall_BeyondGraceWindow_Fails()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 开播后一直 paused 且进度 0 → 超过观察窗 → failed/playback_failed
        player.FixedProbe = FakePlayer.Snapshot(status: "paused", title: "晴天", progress: 0);
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player, timeoutMs: 6000);

        Assert.Equal(RequestStatus.Failed, final);
        Assert.Equal("playback_failed", (await store.GetRequestAsync(request.RequestId!.Value))!.FailureReason);
    }

    [Fact]
    public async Task TotalBudgetExhausted_WhilePlaying_Completes()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 一直 playing 超总预算(时长+缓冲)→ completed,队列不卡死
        player.FixedProbe = FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 999);
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player, timeoutMs: 6000);

        Assert.Equal(RequestStatus.Completed, final);
    }

    [Fact]
    public async Task ManualSkip_DifferentSongPlaying_Skips()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        // 开播确认后播放器实报另一首歌 → skipped/skipped_manually
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 5));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "七里香", artist: "周杰伦", progress: 30));
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Skipped, final);
        Assert.Equal("skipped_manually", (await store.GetRequestAsync(request.RequestId!.Value))!.FailureReason);
    }

    // ---- 起播瞬态 / 播完归零（用户实测 2026-09-08，req205 + req206）----

    [Fact]
    public async Task NotStarted_TransientErrorWithinGrace_DoesNotSwitchSource_ThenStarts()
    {
        // 回归 req205：lxmusic music/searchPlay 是"自搜自播"，派发后到真正起播之间
        // /status 会残留 error。旧实现在派发 2.3s 内连吃两帧 error 就换源 → 无候选 →
        // 请求 3.3s 被秒杀成 playback_failed，而歌随后正常播起来了。
        // 宽限窗内 error 必须一律不信（不换源），等播放器自己起来。
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player, FastOptions() with
        {
            // 起播超时放宽到宽限窗之外：本用例只测"窗内 error 不换源"，
            // 不能让起播超时分支抢先裁决
            PlaybackStartTimeout = TimeSpan.FromSeconds(5),
            PlaybackStartGrace = TimeSpan.FromSeconds(30),
            ErrorConfirmThreshold = 1,
        });
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            return probes <= 2
                ? FakePlayer.Snapshot(status: "error")
                : probes == 3
                    ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: 5)
                    : FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 200);
        };
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
        // 核心断言：宽限窗内的 error 从未触发换源（没有额外 PlaySelected）
        Assert.DoesNotContain(player.Executed, e => e.Command == PlayerCommand.PlaySelected);
    }

    [Fact]
    public async Task NotStarted_SingleErrorAfterGrace_DoesNotSwitch_UntilConsecutiveConfirm()
    {
        // 窗后单次 error 只是抖动，不换源；连续达 ErrorConfirmThreshold 才认定音源坏了
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player, FastOptions() with
        {
            PlaybackStartGrace = TimeSpan.Zero,
            ErrorConfirmThreshold = 2,
        });
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "error"));   // 第 1 帧：未达阈值
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "error"));   // 第 2 帧：达阈值 → 换源
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: 5));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 200));
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
        Assert.Single(player.Executed, e => e.Command == PlayerCommand.PlaySelected); // 恰好换源一次
    }

    [Fact]
    public async Task Paused_PlayerZeroesProgressAndDuration_CompletesByWallClockSongEnd()
    {
        // 回归 req206：落雪播完自动暂停时把 progress 和 duration 一起归零——实测
        // /status 返回 {"status":"paused","name":"晴天","singer":"周杰伦","progress":0,"duration":0}。
        // 旧实现只信快照实报 duration（=0 → 进度判定不成立），progress=0 反而被当成
        // "刚开始播放就卡死"，整首歌播完（271s ≈ interval 269s）却记 playback_failed。
        // 修复后 duration 回落候选 interval，并按墙钟已播满整首判 completed。
        //
        // 用 interval 00:03（duration=3s）把 269s 的真实等待压缩到 ~1s：
        // 进度判定阈值 progress >= 1（0 不满足，确保走墙钟分支而非进度分支），
        // 墙钟判定阈值 elapsed >= 1s；PausedStallGrace 抬高到 30s 以隔离 stall 分支。
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player, FastOptions() with
        {
            PlaybackStartTimeout = TimeSpan.FromSeconds(5),
            PausedStallGrace = TimeSpan.FromSeconds(30),
        });
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            // 第 1 帧开播确认；此后一直是落雪播完归零帧（duration 实报 0）
            return probes == 1
                ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: 5)
                : FakePlayer.Snapshot(status: "paused", title: "晴天", artist: "周杰伦", progress: 0, duration: 0);
        };
        var candidates = new[]
        {
            new SongSearchResult
            {
                Source = "kugou", Name = "晴天", Singer = "周杰伦", SongMid = "k1", Interval = "00:03",
            },
        };
        var request = await InsertDispatchedAsync(store);

        await new PlaybackStateMachine(queue, player, queue.Options!).RunAsync(
            request, candidates, CancellationToken.None);
        var final = (await store.GetRequestStatusAsync(request.RequestId!.Value))!.Value;

        Assert.Equal(RequestStatus.Completed, final);
        Assert.DoesNotContain(player.Executed, e => e.Command == PlayerCommand.PlaySelected); // 播完不换源
    }

    [Fact]
    public async Task Paused_StalledAtZeroProgress_BeforeSongEnd_StillFails()
    {
        // 反向守卫：播完判定不能把"真卡死"也放过——先开播确认，之后一直 paused
        // 且 progress=0/duration=0，墙钟远未到歌曲时长（interval 04:29=269s）
        // → 仍按 stall 结算 playback_failed（真实 req206 的候选时长）
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            return probes == 1
                ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: 5)
                : FakePlayer.Snapshot(status: "paused", title: "晴天", artist: "周杰伦", progress: 0, duration: 0);
        };
        var candidates = new[]
        {
            new SongSearchResult
            {
                Source = "kugou", Name = "晴天", Singer = "周杰伦", SongMid = "k1", Interval = "04:29",
            },
        };
        var request = await InsertDispatchedAsync(store);

        await new PlaybackStateMachine(queue, player, queue.Options!).RunAsync(
            request, candidates, CancellationToken.None);
        var final = (await store.GetRequestStatusAsync(request.RequestId!.Value))!.Value;

        Assert.Equal(RequestStatus.Failed, final);
        Assert.Equal("playback_failed", (await store.GetRequestAsync(request.RequestId!.Value))!.FailureReason);
    }

    [Fact]
    public async Task SwitchSource_RewritesSearchResultJson_AsArray()
    {
        // 契约回归：Worker 派发时写入的 search_result_json 是候选数组，状态机换源回写
        // 必须保持数组形态。旧实现直接序列化单个 candidate → 同一列出现 "{" 开头的对象
        // （实测 req205 换源后 srj[0]='{'，未换源的 req204/206 srj[0]='['），
        // 按数组解析的消费方会炸。
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player, FastOptions() with
        {
            PlaybackStartGrace = TimeSpan.Zero,
            ErrorConfirmThreshold = 1,
        });
        var candidates = new[]
        {
            new SongSearchResult { Source = "netease", Name = "晴天", Singer = "周杰伦", SongMid = "n1" },
            new SongSearchResult { Source = "kugou", Name = "晴天", Singer = "周杰伦", SongMid = "k1" },
        };
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "error"));  // 触发换源到 kugou
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: 5));
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 200));
        var request = await InsertDispatchedAsync(store);

        await new PlaybackStateMachine(queue, player, queue.Options!).RunAsync(
            request, candidates, CancellationToken.None);

        var persisted = await store.GetRequestAsync(request.RequestId!.Value);
        var json = persisted!.SearchResultJson;
        Assert.NotNull(json);
        Assert.StartsWith("[", json); // 数组契约
        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<SongSearchResult>>(json!);
        Assert.NotNull(parsed);
        Assert.Single(parsed!);
        Assert.Equal("kugou", parsed![0].Source); // 回写的是实际换到的音源
    }

    // ---- 探测失败 / 操作员唤醒 ----

    [Fact]
    public async Task ProbeFailure_SettlesFailedPlaybackWaitError()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        var request = await InsertDispatchedAsync(store);
        // 探测抛异常 → playback_wait_error(幽灵正在播放防呆)
        player.ProbeFailure = new InvalidOperationException("probe boom");

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Failed, final);
        Assert.Equal("playback_wait_error", (await store.GetRequestAsync(request.RequestId!.Value))!.FailureReason);
    }

    // ---- 歌名匹配算法（docs/03 §1.5 全套）----

    [Theory]
    [InlineData("晴天", "周杰伦", "晴天", "周杰伦", true)]      // 完全相同
    [InlineData("晴天", "周杰伦", "晴 天", "周杰伦", true)]    // 空白折叠
    [InlineData("晴天 完整版", "周杰伦", "晴天", "周杰伦", true)] // 双向子串
    [InlineData("September (Inst.)", "", "September (纯音乐)", "", true)] // 版本注释
    [InlineData("七里香", "周杰伦", "晴天", "周杰伦", false)]   // 明显不同
    [InlineData("夜曲", "周杰伦", "夜曲 - 钢琴版", "周杰伦", true)] // 子串
    public void SongMatch_Matrix(string expectName, string expectSinger, string actualName, string actualSinger, bool expected)
    {
        var options = FastOptions();
        var result = new SongSearchResult { Source = "kugou", Name = expectName, Singer = expectSinger, SongMid = "m" };
        var request = TestHarness.NewRequest(expectName) with { Singer = expectSinger };
        var snapshot = FakePlayer.Snapshot(status: "playing", title: actualName, artist: actualSinger);
        var machine = new PlaybackStateMachine(null!, null!, options);
        Assert.Equal(expected, machine.SongIdentifiersMatch(snapshot, result, request));
    }

    [Fact]
    public void Similarity_RatioMatchesThresholdSemantics()
    {
        Assert.Equal(1.0, PlaybackStateMachine.Similarity("晴天 周杰伦", "晴天 周杰伦"));
        Assert.True(PlaybackStateMachine.Similarity("晴天", "晴 天") >= 0.62);
        Assert.True(PlaybackStateMachine.Similarity("abcdef", "abcxyz") < 0.62);
    }

    // ---- next 对账守卫（docs/03 §1.5；quirk #1：队首读 queued）----

    [Fact]
    public async Task Guard_InsertsNext_ThenArmsAfterConsecutiveMismatches()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player, withGuard: true);
        var request = await InsertDispatchedAsync(store);
        // 队里还有一首 queued(守卫的期望目标)
        await store.InsertRequestAsync(TestHarness.NewRequest("海阔天空", userId: "u2")
            with { Status = RequestStatus.Queued, Singer = "Beyond", CanonicalSongKey = "haikuotiankong-beyond" });
        var expectedNext = new PlayerTrack { Platform = "unknown", Title = "海阔天空", Artist = "Beyond" };

        // 播放中,next 始终不匹配(空)→ 限频内只插一次 → 连续 2 次 → ArmNextGuard
        // 注意:每轮主循环 + 守卫各 probe 一次,快照序列要补足
        for (var i = 0; i < 10; i++)
        {
            player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 5 + i * 5,
                nextObservation: NextObservation.Empty));
        }

        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 250));

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
        var inserts = player.Executed.Where(e => e.Command == PlayerCommand.InsertNext).ToList();
        Assert.NotEmpty(inserts);
        Assert.Equal(expectedNext.Title, inserts[0].Track!.Title);
        // 连续失配达到阈值 → ArmNextGuard 强制接管
        Assert.Contains(player.Executed, e => e.Command == PlayerCommand.ArmNextGuard);
    }

    [Fact]
    public async Task Guard_MatchedNext_ResetsMismatchCounter()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player, withGuard: true);
        var request = await InsertDispatchedAsync(store);
        await store.InsertRequestAsync(TestHarness.NewRequest("海阔天空", userId: "u2")
            with { Status = RequestStatus.Queued, Singer = "Beyond" });
        var nextMatch = new PlayerTrack { Platform = "fake", Title = "海阔天空", Artist = "Beyond" };

        // 序列:next 匹配(计数清零)→ 不匹配(InsertNext,计数 1)→ 不匹配(Arm,计数 2)
        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 5,
            next: nextMatch, nextObservation: NextObservation.Track));
        for (var i = 0; i < 12; i++)
        {
            player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 10 + i,
                nextObservation: NextObservation.Empty));
        }

        player.ProbeResults.Enqueue(FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 250));

        var final = await RunToTerminalAsync(queue, request, player);

        Assert.Equal(RequestStatus.Completed, final);
        // 匹配那一轮没有插；此后持续失配：第一次动作是 InsertNext（计数从 1
        // 起步，匹配已把计数清零），持续失配升级 ArmNextGuard 强制接管
        var guardActions = player.Executed
            .Where(e => e.Command is PlayerCommand.InsertNext or PlayerCommand.ArmNextGuard)
            .ToList();
        Assert.NotEmpty(guardActions);
        Assert.Equal(PlayerCommand.InsertNext, guardActions[0].Command);
        Assert.Contains(guardActions, a => a.Command == PlayerCommand.ArmNextGuard);
    }

    [Fact]
    public async Task Guard_NoQueuedSong_Skips()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player, withGuard: true);
        var request = await InsertDispatchedAsync(store); // 队列里只有当前请求,无队首

        player.FixedProbe = FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 10,
            nextObservation: NextObservation.Empty);

        var final = await RunToTerminalAsync(queue, request, player, timeoutMs: 6000);

        Assert.Equal(RequestStatus.Completed, final); // 总预算兜底
        Assert.DoesNotContain(player.Executed, e => e.Command is PlayerCommand.InsertNext or PlayerCommand.ArmNextGuard);
    }

    [Fact]
    public async Task Guard_UnknownObservation_DoesNotSubmit()
    {
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player, withGuard: true);
        var request = await InsertDispatchedAsync(store);
        await store.InsertRequestAsync(TestHarness.NewRequest("海阔天空", userId: "u2")
            with { Status = RequestStatus.Queued });

        player.FixedProbe = FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 10,
            nextObservation: NextObservation.Unknown);

        var final = await RunToTerminalAsync(queue, request, player, timeoutMs: 6000);

        Assert.Equal(RequestStatus.Completed, final); // 总预算兜底
        Assert.DoesNotContain(player.Executed, e => e.Command is PlayerCommand.InsertNext or PlayerCommand.ArmNextGuard);
    }

    [Fact]
    public async Task Guard_NoQueueProgrammableCapability_Skipped()
    {
        var player = new FakePlayer(); // Capabilities.None(默认,同 lxmusic)
        var (store, queue, _) = await CreateMachineAsync(player);
        var request = await InsertDispatchedAsync(store);
        await store.InsertRequestAsync(TestHarness.NewRequest("海阔天空", userId: "u2")
            with { Status = RequestStatus.Queued });

        player.FixedProbe = FakePlayer.Snapshot(status: "playing", title: "晴天", progress: 10,
            nextObservation: NextObservation.Empty);

        var final = await RunToTerminalAsync(queue, request, player, timeoutMs: 6000);

        Assert.Equal(RequestStatus.Completed, final);
        Assert.DoesNotContain(player.Executed, e => e.Command is PlayerCommand.InsertNext or PlayerCommand.ArmNextGuard);
    }

    // ---- RawStatus 词表归一化（Kugou/QQ/网易云/Folia 诊断文本回退）----

    [Fact]
    public void Vocabulary_NormalizesKnownStatuses_AndRejectsDiagnosticText()
    {
        // 词表内：原样归一化（含旧拼写 stoped 与大小写/空白）
        Assert.Equal("playing", PlayerStatusVocabulary.Normalize("playing"));
        Assert.Equal("stoped", PlayerStatusVocabulary.Normalize(" StopED "));
        Assert.Equal("idle", PlayerStatusVocabulary.Normalize("idle"));

        // 空 / 诊断文本（四个原生后端的实际输出形态）→ null（调用方回退 Current 推断）
        Assert.Null(PlayerStatusVocabulary.Normalize(null));
        Assert.Null(PlayerStatusVocabulary.Normalize(""));
        Assert.Null(PlayerStatusVocabulary.Normalize("控制及在线点歌 IPC 已连接（kugou.ini）"));
        Assert.Null(PlayerStatusVocabulary.Normalize("QQ 音乐窗口已连接"));
        Assert.Null(PlayerStatusVocabulary.Normalize("bridge 已连接（status）"));
        Assert.Null(PlayerStatusVocabulary.Normalize("folia track_changed"));
    }

    [Fact]
    public async Task DiagnosticRawStatus_FallsBackToCurrentInference_StillStarts()
    {
        // 回归：RawStatus 为人类可读诊断文本（非词表）时，旧逻辑直接把整串
        // 中文当状态比较——起播永远不命中，固定 15s 超时换源到失败。
        // 修复后按 Current 推断：Current 匹配 → 开播；Current 消失 → 播完。
        var player = new FakePlayer();
        var (store, queue, _) = await CreateMachineAsync(player);
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            return probes switch
            {
                // 诊断文本 + 正在放目标歌 → 推断 playing → 开播确认
                <= 6 => FakePlayer.Snapshot(status: "控制及在线点歌 IPC 已连接（kugou.ini）",
                    title: "晴天", artist: "周杰伦", progress: 1),
                // 诊断文本 + 歌播完（Current 为空）→ 推断 idle → 非播放中，
                // 总预算窗口内结算（自然结束，非手动切歌）
                _ => FakePlayer.Snapshot(status: "控制及在线点歌 IPC 已连接（kugou.ini）",
                    progress: 300),
            };
        };
        var request = await InsertDispatchedAsync(store);

        var final = await RunToTerminalAsync(queue, request, player, timeoutMs: 8000);

        // 不再是 playback_failed：诊断文本后端也能正常判定开播与结束
        Assert.Equal(RequestStatus.Completed, final);
    }
}

}
