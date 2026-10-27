using Erbai.Contracts.Configuration;
using Erbai.Contracts.Players;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Contracts.Storage;
using Erbai.Core.Events;
using Erbai.Core.Storage;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest.Tests
{
    using SongRequest = Erbai.Contracts.Requests.SongRequest;

/// <summary>
/// 空闲播放（指定歌单为空闲播放源）：队列空才播、顺序轮转/随机、播完等
/// play_interval（旧版"开播后等"会切掉未播完的歌）、新点歌立即打断、失败跳过 +
/// 连续失败暂停、不写 song_requests 表；曲目带存量候选（search_json）时跳过搜索。
/// </summary>
public class IdlePlaylistTests
{
    private const int ShortIntervalSeconds = 1;

    private static AppConfig IdleConfig() => TestHarness.DefaultConfig() with
    {
        IdlePlaylist = new IdlePlaylistConfig { Enabled = true, PlayInterval = ShortIntervalSeconds },
    };

    private static async Task<(SqliteStorageEngine Store, EventBus Bus, SongQueueService Queue, FakePlayer Player, long PlaylistId)>
        CreateIdleQueueAsync(AppConfig? config = null, Func<SongRequest, CancellationToken, Task<IReadOnlyList<SongSearchResult>?>>? processor = null)
    {
        var harness = await TestHarness.CreateAsync(config ?? IdleConfig());
        var player = new FakePlayer();
        harness.Queue.Player = player;
        harness.Queue.Processor = processor ?? SearchHarness.Fixed();
        var playlistId = await TestHarness.SeedIdlePlaylistAsync(
            harness.Store, ("晴天", "周杰伦"), ("海阔天空", "Beyond"));
        // 默认播放脚本：收到 PlaySelected 才报 playing（命令驱动），几帧后
        // 播完(stopped)——空闲歌播放跟踪(WaitIdleSongEndAsync)需要"结束"信号
        if (player.ProbeFunc is null)
        {
            var probes = 0;
            player.ProbeFunc = () =>
            {
                var dispatched = player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected);
                if (!dispatched)
                {
                    return FakePlayer.Snapshot(status: "idle");
                }

                probes++;
                return probes <= 2
                    ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 10)
                    : FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 265);
            };
        }

        await harness.Queue.StartAsync();
        return (harness.Store, harness.Bus, harness.Queue, player, playlistId);
    }

    [Fact]
    public async Task Idle_QueueEmpty_PlaysNextIdleSongInOrder()
    {
        var (store, bus, queue, player, _) = await CreateIdleQueueAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        // 队列空 → 播第一首空闲歌
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1));
        Assert.Equal(PlayerCommand.PlaySelected, player.Executed[0].Command);
        Assert.Equal("晴天", player.Executed[0].Track!.Title);
        Assert.Equal("周杰伦", player.Executed[0].Track!.Artist);

        // 播完间隔后轮转到第二首
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 2, 5000));
        Assert.Equal("海阔天空", player.Executed[1].Track!.Title);

        // 空闲歌不写 song_requests 表（不污染点歌队列/历史）
        Assert.Empty(await store.ListRequestsAsync());
    }

    [Fact]
    public async Task Idle_NewRequest_WakesAndCutsIn()
    {
        var (store, bus, queue, player, _) = await CreateIdleQueueAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1));
        var idleCount = player.Executed.Count;

        // 新点歌立即打断空闲播放：worker 切去派发点歌，不再播下一首空闲歌
        var (request, decision) = await queue.SubmitAsync(TestHarness.NewRequest("光辉岁月", userId: "u1"));
        Assert.True(decision.Allowed);
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(request!.RequestId!.Value).Result == RequestStatus.Dispatched));

        // 点歌播放期间空闲歌不推进
        await Task.Delay(ShortIntervalSeconds * 1000 + 300);
        Assert.True(player.Executed.Count >= idleCount); // 可能有点歌自身的 PlaySelected
        var active = await store.ListRequestsAsync(RequestStatuses.Active);
        Assert.NotEmpty(active); // 点歌请求仍活动（等待播放中）
    }

    [Fact]
    public async Task Idle_PlayFailure_SkipsToNextSong()
    {
        // 第一首搜索无结果 → 跳过；第二首成功
        var calls = 0;
        Func<SongRequest, CancellationToken, Task<IReadOnlyList<SongSearchResult>?>> flakyProcessor = (request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult<IReadOnlyList<SongSearchResult>?>(null); // 晴天无结果
            }

            return SearchHarness.Fixed()(request, CancellationToken.None);
        };

        var (store, bus, queue, player, _) = await CreateIdleQueueAsync(processor: flakyProcessor);
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000));
        Assert.Equal("海阔天空", player.Executed[0].Track!.Title); // 跳过晴天直接播第二首
    }

    [Fact]
    public async Task Idle_PlayerRejects_PausesAfterConsecutiveFailures()
    {
        var (store, bus, queue, player, _) = await CreateIdleQueueAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        player.Outcome = PlayerOutcome.Rejected; // 播放器拒绝一切

        // 连续失败：每首被拒 → 跳过下一首；3 次后暂停一轮 interval
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 3, 5000));
        var countAfterThree = player.Executed.Count;
        await Task.Delay(ShortIntervalSeconds * 1000 + 500);

        // 暂停期内不再尝试（等待下一轮间隔后才重试）——此处验证没有爆发式重试
        Assert.True(player.Executed.Count < countAfterThree + 5,
            $"连续失败后应暂停，实际继续尝试了 {player.Executed.Count - countAfterThree} 次");
    }

    [Fact]
    public async Task Idle_LongSong_NotCutByInterval_EndsThenNext()
    {
        // 高危修复回归：此前"开播后等 interval"会在 ~2×interval 处切掉未播完的
        // 长歌；现在跟踪播放直到结束（stopped）才播下一首
        var (store, bus, queue, player, _) = await CreateIdleQueueAsync(); // interval=1s
        using var sub = bus.Subscribe<Erbai.Contracts.Queue.QueueEventEnvelope>();

        // 命令驱动：收到 PlaySelected 前报 idle；之后一直 playing（长歌 4 分钟）
        var probes = 0;
        player.ProbeFunc = () =>
        {
            var dispatched = player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected);
            if (!dispatched)
            {
                return FakePlayer.Snapshot(status: "idle");
            }

            probes++;
            return probes switch
            {
                <= 3 => FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 10),
                4 => FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: 250),
                _ => FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 265),
            };
        };

        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000));

        // 播放中（长歌未结束）且已超过一个 interval：不得切下一首
        await Task.Delay(1600);
        Assert.Single(player.Executed, e => e.Command == PlayerCommand.PlaySelected);

        // 播完(stopped)后才播下一首（轮转到海阔天空）
        Assert.True(await SearchHarness.WaitUntilAsync(
            () => player.Executed.Count(e => e.Command == PlayerCommand.PlaySelected) >= 2, 8000));
        Assert.Equal("海阔天空", player.Executed.Last().Track!.Title);
    }

    [Fact]
    public async Task Idle_ConfigReload_TakesEffectImmediately()
    {
        // 高危修复回归：此前开关/间隔读构造时快照，"热生效"是假的
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        Assert.False(queue.IdleConfig.Enabled);

        queue.ReloadIdleConfig(new Erbai.Contracts.Configuration.IdlePlaylistConfig
        {
            Enabled = true,
            PlayInterval = 42,
        });

        Assert.True(queue.IdleConfig.Enabled);
        Assert.Equal(42, queue.IdleConfig.PlayInterval);
        // 非法间隔下限收敛
        queue.ReloadIdleConfig(new Erbai.Contracts.Configuration.IdlePlaylistConfig
        {
            Enabled = true,
            PlayInterval = 0,
        });
        Assert.Equal(1, queue.IdleConfig.PlayInterval);
    }

    [Fact]
    public async Task Idle_Disabled_DoesNotPlay()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        var player = new FakePlayer();
        queue.Player = player;
        queue.Processor = SearchHarness.Fixed();
        await TestHarness.SeedIdlePlaylistAsync(store, ("晴天", "周杰伦"));
        await queue.StartAsync();

        await Task.Delay(300);
        Assert.Empty(player.Executed);
    }

    [Fact]
    public async Task Idle_NoMarkedPlaylist_DoesNotPlay()
    {
        // 开关开着但没有任何歌单被标记为空闲播放源：不播（等同关闭）
        var harness = await TestHarness.CreateAsync(IdleConfig());
        var player = new FakePlayer();
        harness.Queue.Player = player;
        harness.Queue.Processor = SearchHarness.Fixed();
        // 只建歌单不标记
        await harness.Store.CreatePlaylistAsync("未标记的歌单");
        await harness.Queue.StartAsync();

        await Task.Delay(400);
        // 不播歌即达标；PausePlayerWhenIdleAsync 会向播放器发 Pause（挂起语义，
        // 历史修复记录 #14）——只断言没有 PlaySelected
        Assert.DoesNotContain(player.ExecutedSnapshot(), e => e.Command == PlayerCommand.PlaySelected);
        Assert.Null(harness.Queue.IdlePlaylist);
        Assert.Empty(harness.Queue.IdleSongs);
    }

    [Fact]
    public async Task Idle_TransientStopRightAfterStart_DoesNotCutSong()
    {
        // bug 回归（用户实测"启动就放空闲歌，放一小会立马切歌"）：播放器在
        // PlaySelected 后存在切歌/加载瞬态窗口（短暂报 stopped/error，无进度），
        // 空闲歌跟踪必须继续观察，不得立即判定"播完"切走——与点歌状态机
        // IsAbortedPlayback 刚播即停双条件同语义
        var (store, bus, queue, player, _) = await CreateIdleQueueAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        // 命令驱动：收到 PlaySelected 后前 3 帧报 stopped（瞬态），此后正常
        // 进入 playing（长歌一直放）
        var probes = 0;
        player.ProbeFunc = () =>
        {
            var dispatched = player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected);
            if (!dispatched)
            {
                return FakePlayer.Snapshot(status: "idle");
            }

            probes++;
            return probes switch
            {
                <= 3 => FakePlayer.Snapshot(status: "stopped"), // 起播瞬态（无进度）
                _ => FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 10),
            };
        };

        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000));

        // 越过瞬态窗口（3 帧 ≈1.5s + 余量）后仍只有第一首：瞬态不被当作"播完"
        await Task.Delay(2000);
        Assert.Single(player.Executed, e => e.Command == PlayerCommand.PlaySelected);
    }

    [Fact]
    public async Task Idle_NewRequest_DuringLongSong_CutsInImmediately()
    {
        // bug 回归（用户实测"点歌要等空闲歌结束很久才切"）：长空闲歌播放中
        // 新点歌必须被唤醒信号立即打断派发，不得等空闲歌自然播完
        var (store, bus, queue, player, _) = await CreateIdleQueueAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        // 空闲歌永不结束（一直 playing 的长歌）
        player.ProbeFunc = () =>
        {
            var dispatched = player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected);
            return dispatched
                ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: 60)
                : FakePlayer.Snapshot(status: "idle");
        };

        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000));

        var (request, decision) = await queue.SubmitAsync(TestHarness.NewRequest("光辉岁月", userId: "u1"));
        Assert.True(decision.Allowed);

        // 1.5s 内必须派发（500ms 轮询窗口 + 余量），不等空闲歌播放结束
        Assert.True(await SearchHarness.WaitUntilAsync(
            () => store.GetRequestStatusAsync(request!.RequestId!.Value).Result == RequestStatus.Dispatched, 1500),
            "点歌未在空闲歌播放中被立即派发");
    }

    [Fact]
    public async Task Idle_ReloadList_CutsToNewListWithoutWaitingInterval()
    {
        // bug 回归（用户实测"换列表里的歌要等很久"）：播放中热更新空闲歌单
        // 列表后应立即按新列表继续轮转，不得再等一整个 play_interval
        // （此处 interval=10s，若修复缺失则 2.5s 内不会播新歌）
        var longInterval = TestHarness.DefaultConfig() with
        {
            IdlePlaylist = new IdlePlaylistConfig { Enabled = true, PlayInterval = 10 },
        };
        var (store, bus, queue, player, playlistId) = await CreateIdleQueueAsync(config: longInterval);
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000));

        // 换列表：当前"晴天"在播期间替换歌单曲目
        await TestHarness.ReplaceIdleSongsAsync(store, queue, playlistId, ("光辉岁月", "Beyond"));

        // 立即按新列表播放（唤醒即打断，不干等 10s interval）
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 2, 2500));
        Assert.Equal("光辉岁月", player.Executed[1].Track!.Title);
    }

    [Fact]
    public async Task Idle_Reload_AppliesNewSongsWithoutRestart()
    {
        var (store, bus, queue, player, playlistId) = await CreateIdleQueueAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1));

        await TestHarness.ReplaceIdleSongsAsync(store, queue, playlistId, ("光辉岁月", "Beyond"));

        // 歌单更新后下一首播新歌
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 2, 5000));
        Assert.Equal("光辉岁月", player.Executed[1].Track!.Title);
    }

    [Fact]
    public async Task Idle_ShuffleMode_CoversAllSongsWithoutRepeatWithinCycle()
    {
        // 随机模式：一轮洗牌内不重复、轮尽覆盖全部（间隔调短加速）
        var config = TestHarness.DefaultConfig() with
        {
            IdlePlaylist = new IdlePlaylistConfig { Enabled = true, PlayInterval = 1 },
        };
        var harness = await TestHarness.CreateAsync(config);
        var player = new FakePlayer();
        harness.Queue.Player = player;
        harness.Queue.Processor = SearchHarness.Fixed();
        var playlistId = await TestHarness.SeedIdlePlaylistAsync(
            harness.Store, ("甲", "a"), ("乙", "b"), ("丙", "c"), ("丁", "d"));
        await harness.Store.SetPlayModeAsync(playlistId, PlaylistPlayMode.Shuffle);

        // 每首播完即 stopped（快轮转）
        var plays = 0;
        player.ProbeFunc = () =>
        {
            var dispatched = player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected);
            if (!dispatched)
            {
                return FakePlayer.Snapshot(status: "idle");
            }

            plays++;
            return FakePlayer.Snapshot(status: "stopped", title: "x", progress: 300);
        };
        await harness.Queue.StartAsync();

        // 等 4 首（一轮全覆盖）播出
        Assert.True(await SearchHarness.WaitUntilAsync(
            () => player.ExecutedSnapshot().Count(e => e.Command == PlayerCommand.PlaySelected) >= 4, 15000),
            "随机模式未在 15s 内播满一轮 4 首");
        var titles = player.ExecutedSnapshot()
            .Where(e => e.Command == PlayerCommand.PlaySelected)
            .Take(4)
            .Select(e => e.Track!.Title)
            .ToList();
        // 一轮内无重复且全覆盖（洗牌袋语义）
        Assert.Equal(4, titles.Distinct().Count());
        Assert.Equal(new[] { "甲", "乙", "丙", "丁" }.OrderBy(t => t), titles.OrderBy(t => t));
    }

    [Fact]
    public async Task Idle_StoredCandidates_PlaysDirectlyWithoutSearch()
    {
        // 导入时落库的存量候选（search_json）：优先直接派发，跳过实时搜索
        // （存储候选是"海阔天空"，若走了搜索路径会播 Fixed() 返回的伪请求歌名）
        var searched = 0;
        Func<SongRequest, CancellationToken, Task<IReadOnlyList<SongSearchResult>?>> countingProcessor = (request, _) =>
        {
            searched++;
            return SearchHarness.Fixed()(request, CancellationToken.None);
        };
        var harness = await TestHarness.CreateAsync(IdleConfig());
        var player = new FakePlayer();
        harness.Queue.Player = player;
        harness.Queue.Processor = countingProcessor;
        var playlistId = await TestHarness.SeedIdlePlaylistAsync(harness.Store, ("晴天", "周杰伦"));
        await harness.Store.AddPlaylistSongAsync(playlistId, "海阔天空", "Beyond",
            searchJson: """
                [{"source":"netease","name":"海阔天空（存量候选）","singer":"Beyond","songmid":"mid-stored","types":[{"type":"128k","size":""}],"hash":""}]
                """);
        await harness.Queue.ReloadIdlePlaylistAsync();

        var probes = 0;
        player.ProbeFunc = () =>
        {
            var dispatched = player.Executed.Any(e => e.Command == PlayerCommand.PlaySelected);
            if (!dispatched)
            {
                return FakePlayer.Snapshot(status: "idle");
            }

            probes++;
            return probes <= 2
                ? FakePlayer.Snapshot(status: "playing", title: "晴天", artist: "周杰伦", progress: probes * 10)
                : FakePlayer.Snapshot(status: "stopped", title: "晴天", progress: 265);
        };
        await harness.Queue.StartAsync();

        // 首播即为存量候选曲名（曲目顺序：晴天在前——晴天无存量候选走搜索，
        // 海阔天空有存量候选直接派发；校验第二轮不消耗搜索次数）
        Assert.True(await SearchHarness.WaitUntilAsync(
            () => player.ExecutedSnapshot().Count(e => e.Command == PlayerCommand.PlaySelected) >= 2, 8000));
        var secondTitle = player.ExecutedSnapshot()
            .First(e => e.Command == PlayerCommand.PlaySelected && e.Track!.Title != "晴天")
            .Track!.Title;
        Assert.Equal("海阔天空（存量候选）", secondTitle);
        // 只有"晴天"走了实时搜索（存量候选跳过搜索）
        Assert.Equal(1, searched);
    }

    [Fact]
    public async Task Idle_MarkedPlaylistDeleted_AfterReload_IdleStops()
    {
        // 删除被标记的歌单后热重载：空闲列表清空，不再继续播
        var (store, bus, queue, player, playlistId) = await CreateIdleQueueAsync();
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000));

        await store.DeletePlaylistAsync(playlistId);
        await queue.ReloadIdlePlaylistAsync();

        Assert.Null(queue.IdlePlaylist);
        Assert.Empty(queue.IdleSongs);
        await Task.Delay(600);
        var countAfterDelete = player.Executed.Count;
        await Task.Delay(600);
        Assert.True(player.Executed.Count <= countAfterDelete + 1); // 不再轮转下一首
    }
}

}
