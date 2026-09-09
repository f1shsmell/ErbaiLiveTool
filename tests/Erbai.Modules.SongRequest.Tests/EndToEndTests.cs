using Erbai.Contracts.Configuration;
using Erbai.Contracts.Players;
using Erbai.Contracts.Requests;
using Erbai.Core.Storage;
using Erbai.Modules.SongRequest.Playback;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest.Tests
{
    using SongRequest = Erbai.Contracts.Requests.SongRequest;

/// <summary>
/// 阶段 2 退出标准（docs/00）：测试弹幕注入可完成点歌→播放→切歌全链路。
/// dummy 平台注入（不经真实抖音/B站），搜索走 fake 处理器，播放走可编程
/// FakePlayer——整条链真实运转：弹幕命令 → 提交流水线 → worker 派发 →
/// 播放状态机 → 终态事件。
/// </summary>
public class EndToEndTests
{
    private static DanmakuContext Ctx(string text, bool isAdmin = false, string userId = "e2e-u1") =>
        new()
        {
            Text = text,
            Nickname = "观众",
            Platform = "douyin",
            RoomId = "1",
            UserId = userId,
            IsAdmin = isAdmin,
            FanLevel = 5,
        };

    private static PlaybackOptions FastOptions() => new()
    {
        PlaybackStartTimeout = TimeSpan.FromMilliseconds(300),
        PlaybackTimeout = TimeSpan.FromSeconds(3),
        PollInterval = TimeSpan.FromMilliseconds(40),
        ShortPlayFail = TimeSpan.FromMilliseconds(120),
        PausedStallGrace = TimeSpan.FromMilliseconds(400),
        SwitchSourceCooldown = TimeSpan.FromMilliseconds(20),
    };

    private static async Task<(SqliteStorageEngine Store, SongQueueService Queue, SongRequestService Commands,
        FakePlayer Player)> CreateE2eAsync(AppConfig? config = null)
    {
        var harness = await TestHarness.CreateAsync(config);
        var player = new FakePlayer();
        harness.Queue.Player = player;
        harness.Queue.Options = FastOptions();
        return (harness.Store, harness.Queue, harness.Commands, player);
    }

    /// <summary>命令驱动脚本：只有收到 PlaySelected 后才报 playing（真实播放器语义，
    /// 掩盖性测试的根因——此前 FakePlayer 自报播放，掩盖了首候选从未派发）。</summary>
    private static void ScriptNormalPlay(FakePlayer player)
    {
        var probes = 0;
        player.ProbeFunc = () =>
        {
            var dispatched = player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected);
            if (!dispatched)
            {
                return FakePlayer.Snapshot(status: "idle"); // 未收到命令前绝不"正在播放"
            }

            probes++;
            return probes <= 3
                ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 10)
                : FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 265);
        };
    }

    [Fact]
    public async Task Danmaku_Request_Plays_Completes_FullChain()
    {
        var (store, queue, commands, player) = await CreateE2eAsync();
        ScriptNormalPlay(player);

        var consumed = await commands.HandleMessageAsync(Ctx("点歌 晴天 - 周杰伦"));

        Assert.True(consumed);
        // 全链路终点：completed（提交→搜索→派发→开播→播完）
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.ListRequestsPageAsync(status: RequestStatus.Completed).Result.Total == 1, 8000));

        // 首候选确实下发给播放器（致命问题回归：此前全树只有空闲歌/换源发
        // PlaySelected，正常点歌从不派发）
        var playSelected = Assert.Single(player.Executed, e => e.Command == PlayerCommand.PlaySelected);
        Assert.Equal("晴天", playSelected.Track!.Title);
        Assert.Equal("周杰伦", playSelected.Track.Artist);
        Assert.NotNull(playSelected.Track.NativeData); // lxmusic 播放载荷完整

        var (rows, _) = await store.ListRequestsPageAsync(status: RequestStatus.Completed);
        var request = Assert.Single(rows);
        Assert.Equal("晴天", request.SongName);
        Assert.Equal("周杰伦", request.Singer);
        Assert.False(string.IsNullOrEmpty(request.SearchResultJson)); // 搜索候选已回写
        Assert.Null(queue.CurrentRequestId); // 终态清空
    }

    [Fact]
    public async Task Danmaku_Skip_WhilePlaying_NextRequestPlays()
    {
        var (store, queue, commands, player) = await CreateE2eAsync();
        // 第一首播放中(不结束);第二首排队
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            return FakePlayer.Snapshot(status: "playing", title: probes <= 5 ? "晴天" : "海阔天空",
                artist: probes <= 5 ? "周杰伦" : "Beyond", progress: probes * 5);
        };

        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天 - 周杰伦")));
        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 海阔天空 - Beyond", userId: "e2e-u2")));

        // 第一首进入播放等待
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(FirstId(store)).Result == RequestStatus.Dispatched));

        // 主播弹幕切歌 → skip_first；不向播放器发原生 Next（Next 切的是播放器自己
        // 列表的歌，会先放“别的歌”再轮到队列派发——2026-08-27 用户实测修订）
        Assert.True(await commands.HandleMessageAsync(Ctx("切歌", isAdmin: true)));

        // 第一首 skipped,第二首被派发
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.ListRequestsPageAsync(status: RequestStatus.Skipped).Result.Total == 1, 5000));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.ListRequestsPageAsync(status: RequestStatus.Dispatched).Result.Total == 1, 5000));
        // 第二首经 PlaySelected 直接切歌（不再有播放器原生 Next）
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Count(e => e.Command == PlayerCommand.PlaySelected) >= 2, 5000));
        Assert.DoesNotContain(player.Executed, e => e.Command == PlayerCommand.Next);
    }

    [Fact]
    public async Task IdlePlaylist_PlaysWhenQueueEmpty_NewRequestCutsIn()
    {
        var config = TestHarness.DefaultConfig() with
        {
            IdlePlaylist = new Erbai.Contracts.Configuration.IdlePlaylistConfig
            {
                Enabled = true,
                PlayInterval = 1,
            },
        };
        var (store, queue, commands, player) = await CreateE2eAsync(config);
        await store.SaveIdleSongsAsync(new[]
        {
            new Erbai.Contracts.Storage.IdleSong { Name = "晴天", Singer = "周杰伦" },
        });
        // 空闲歌播放也要开播确认脚本(PlaySelected 后报 playing)
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            return probes <= 2
                ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 10)
                : FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 265);
        };
        await queue.StartAsync();

        // 队列空 → 空闲歌被 PlaySelected 派发
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected), 5000));

        // 新点歌打断空闲播放:提交后进入派发链
        var consumed = await commands.HandleMessageAsync(Ctx("点歌 海阔天空 - Beyond", userId: "e2e-u2"));
        Assert.True(consumed);
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(FirstId(store)).Result == RequestStatus.Dispatched, 5000));
    }

    [Fact]
    public async Task IdlePlaylist_LongInterval_NewRequestDispatchedImmediately()
    {
        // 审计 T1-P1: 空闲歌播放中新点歌应立即打断派发, 不得再等一整个
        // play_interval(默认 30s)——修复前打断后仍进 WaitIdleIntervalAsync,
        // 唤醒信号已被消耗, 点歌被推迟最长 play_interval。
        var config = TestHarness.DefaultConfig() with
        {
            IdlePlaylist = new Erbai.Contracts.Configuration.IdlePlaylistConfig
            {
                Enabled = true,
                PlayInterval = 30, // 长间隔: 若打断后仍等 interval 必超 5s 断言窗口
            },
        };
        var (store, queue, commands, player) = await CreateE2eAsync(config);
        await store.SaveIdleSongsAsync(new[]
        {
            new Erbai.Contracts.Storage.IdleSong { Name = "晴天", Singer = "周杰伦" },
        });
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            return probes <= 2
                ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 10)
                : FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 265);
        };
        await queue.StartAsync();

        // 队列空 → 空闲歌开播
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected), 5000));

        // 空闲歌播放中新点歌: 必须在 5s 内被派发(而不是等满 30s 间隔)
        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 海阔天空 - Beyond", userId: "e2e-u2")));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(FirstId(store)).Result == RequestStatus.Dispatched, 5000),
            "空闲歌播放中点歌应被立即打断派发, 不得等完 play_interval");
    }

    [Fact]
    public async Task Danmaku_Skip_WhenNoRequestQueued_PausesPlayerWithoutNext()
    {
        // 用户实测（2026-08-27）：队列没有歌时切歌，旧实现仍向播放器发原生 Next，
        // 播放器会继续放（它自己列表/上一首），且永不暂停。
        // 新行为：队列无活动请求 → 不向播放器发 Next，显式触发空闲挂起（Pause）。
        var (store, queue, commands, player) = await CreateE2eAsync();

        Assert.True(await commands.HandleMessageAsync(Ctx("切歌", isAdmin: true)));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Any(e => e.Command == PlayerCommand.Pause), 5000),
            "队列无歌时切歌应收到 Pause（外部播放器不能继续放）");
        Assert.DoesNotContain(player.Executed, e => e.Command == PlayerCommand.Next);
    }

    [Fact]
    public async Task Skip_WhenQueueEmpty_PausesPlayer()
    {
        // 修复回归（docs/00 #14）：操作员"跳过"后队列空 → 播放器必须收到 Pause，
        // 否则 lxmusic 等外部播放器无限继续放最后那首歌
        var (store, queue, commands, player) = await CreateE2eAsync();
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            return FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 5);
        };

        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天 - 周杰伦")));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected), 5000));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(FirstId(store)).Result == RequestStatus.Dispatched));

        Assert.NotNull(await queue.SkipFirstAsync());
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Any(e => e.Command == PlayerCommand.Pause), 5000),
            "队列空后播放器应收到 Pause（外部播放器不能继续放）");
    }

    [Fact]
    public async Task Complete_WhenQueueEmpty_PausesPlayer()
    {
        var (store, queue, commands, player) = await CreateE2eAsync();
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            return FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 5);
        };

        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天 - 周杰伦")));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected), 5000));

        Assert.NotNull(await queue.CompleteAsync(FirstId(store)));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Any(e => e.Command == PlayerCommand.Pause), 5000),
            "队列空后播放器应收到 Pause");
    }

    [Fact]
    public async Task Skip_WhenNextQueued_DoesNotPauseBeforeNextPlays()
    {
        // 队列还有下一首时跳过：不暂停（下一首会经 PlaySelected 切歌），
        // 只允许出现 PlaySelected（新歌）后续
        var (store, queue, commands, player) = await CreateE2eAsync();
        var probes = 0;
        player.ProbeFunc = () =>
        {
            probes++;
            return FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 5);
        };

        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天 - 周杰伦")));
        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 海阔天空 - Beyond", userId: "e2e-u2")));
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected), 5000));

        Assert.NotNull(await queue.SkipFirstAsync());
        // 第二首被派发
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Count(e => e.Command == PlayerCommand.PlaySelected) >= 2, 5000));
        // 稳定窗口后再断言没有 Pause（active 非空时绝不暂停）
        await Task.Delay(200);
        Assert.DoesNotContain(player.Executed, e => e.Command == PlayerCommand.Pause);
    }

    [Fact]
    public async Task Dispatch_AllCandidatesRejected_ImmediatelyFails()
    {
        // 修复回归（docs/00 #21）：全部候选派发被拒 → 立即 failed（all_sources_rejected），
        // 不再死等 15s 起播超时（旧行为下 3s 内等不到 failed，此测试自然区分）
        var (store, queue, commands, player) = await CreateE2eAsync();
        player.Outcome = PlayerOutcome.Rejected; // 唯一候选(kugou)被拒
        player.ProbeFunc = () => FakePlayer.Snapshot(status: "idle");

        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天 - 周杰伦")));
        var requestId = FirstId(store);
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(requestId).Result == RequestStatus.Failed, 3000));
        Assert.Equal("all_sources_rejected",
            (await store.GetRequestAsync(requestId))!.FailureReason);
        // 全部被拒不得产生 dispatched 事件（未真正派发）
        Assert.DoesNotContain(player.Executed, e => e.Command == PlayerCommand.Pause);
        Assert.Null(queue.CurrentRequestId);
    }

    [Fact]
    public async Task Dispatch_FirstRejected_SecondAccepted_PlaysSecondCandidate()
    {
        // 修复回归（docs/00 #21）：首候选被拒 → 立即试下一候选并成功派发，
        // 状态机从成功候选继续（已拒的前置候选不进换源列表）
        var (store, queue, commands, player) = await CreateE2eAsync();
        queue.Processor = (_, _) => Task.FromResult<IReadOnlyList<SongSearchResult>?>(
        [
            new SongSearchResult { Source = "kugou", Name = "晴天", Singer = "周杰伦", SongMid = "k1", Interval = "04:29" },
            new SongSearchResult { Source = "netease", Name = "晴天", Singer = "周杰伦", SongMid = "n1", Interval = "04:29" },
        ]);
        player.ExecuteResultFunc = track => track.Platform == "kugou"
            ? new PlayerOperationResult(PlayerOutcome.Rejected, "窗口不可用")
            : new PlayerOperationResult(PlayerOutcome.Applied, "ok");
        player.ProbeFunc = () => FakePlayer.Snapshot(status: "idle");

        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天 - 周杰伦")));
        var requestId = FirstId(store);
        // 等两个 PlaySelected 都发生：Worker 是「先 CAS dispatched、后派发」，
        // 只等 dispatched 会在派发执行前就读 Executed（实测 flaky：Count 读到 0）。
        // PlaySelected 计数才是本用例要断言的真实信号。
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.PlaySelectedCount == 2, 3000));
        var second = player.ExecutedSnapshot().Last(e => e.Command == PlayerCommand.PlaySelected).Track;
        Assert.Equal("netease", second!.Platform);
        Assert.Equal("n1", second.Id);
        // 有候选被接受 → 不走 all_sources_rejected（首候选被拒后未立即失败）
        Assert.NotEqual("all_sources_rejected",
            (await store.GetRequestAsync(requestId))!.FailureReason);
    }

    [Fact]
    public async Task Dispatch_SingleSourcePlayer_FiltersCrossSourceCandidate()
    {
        // 修复回归（docs/00 #22）：播放器=netease 时，跨源候选（kugou）不被派发——
        // 旧行为把酷狗 hash 喂给网易云桥产生误导性 invalid-song-id；只试自家源候选
        var (store, queue, commands, player) = await CreateE2eAsync();
        player.Key = "netease";
        queue.Processor = (_, _) => Task.FromResult<IReadOnlyList<SongSearchResult>?>(
        [
            new SongSearchResult { Source = "kugou", Name = "火影忍者", Singer = "", SongMid = "k1" },
            new SongSearchResult { Source = "netease", Name = "火影忍者", Singer = "", SongMid = "n1" },
        ]);
        player.ExecuteResultFunc = _ => new PlayerOperationResult(PlayerOutcome.Applied, "ok");
        player.ProbeFunc = () => FakePlayer.Snapshot(status: "idle");

        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 火影忍者")));
        // 只派发 netease 候选（kugou 候选不进入派发尝试）
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected && e.Track!.Platform == "netease"), 3000));
        Assert.Equal(1, player.Executed.Count(e => e.Command == PlayerCommand.PlaySelected));
        Assert.Equal(RequestStatus.Dispatched,
            await store.GetRequestStatusAsync(FirstId(store)));
    }

    [Fact]
    public async Task Dispatch_SingleSourcePlayer_NoPlayableSource_FailsFast()
    {
        // 播放器=netease 但搜索只有跨源候选（kugou）→ no_playable_source 立即失败
        var (store, queue, commands, player) = await CreateE2eAsync();
        player.Key = "netease";
        queue.Processor = (_, _) => Task.FromResult<IReadOnlyList<SongSearchResult>?>(
        [
            new SongSearchResult { Source = "kugou", Name = "火影忍者", Singer = "", SongMid = "k1" },
        ]);

        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 火影忍者")));
        var requestId = FirstId(store);
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(requestId).Result == RequestStatus.Failed, 3000));
        Assert.Equal("no_playable_source", (await store.GetRequestAsync(requestId))!.FailureReason);
    }

    private static long FirstId(SqliteStorageEngine store) =>
        store.ListRequestsPageAsync(limit: 1, descending: false).Result.Rows[0].RequestId!.Value;
}

}
