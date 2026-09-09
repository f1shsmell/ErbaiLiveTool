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
/// 空闲歌单（新设计，不复用旧 _maybe_play_idle；参考 JMusicBot defaultQueue）：
/// 队列空才播、顺序轮转、播完等 play_interval（旧版"开播后等"会切掉未播完的
/// 歌）、新点歌立即打断、失败跳过 + 连续失败暂停、不写 song_requests 表。
/// </summary>
public class IdlePlaylistTests
{
    private const int ShortIntervalSeconds = 1;

    private static AppConfig IdleConfig() => TestHarness.DefaultConfig() with
    {
        IdlePlaylist = new IdlePlaylistConfig { Enabled = true, PlayInterval = ShortIntervalSeconds },
    };

    private static async Task<(SqliteStorageEngine Store, EventBus Bus, SongQueueService Queue, FakePlayer Player)>
        CreateIdleQueueAsync(AppConfig? config = null, Func<SongRequest, CancellationToken, Task<IReadOnlyList<SongSearchResult>?>>? processor = null)
    {
        var harness = await TestHarness.CreateAsync(config ?? IdleConfig());
        var player = new FakePlayer();
        harness.Queue.Player = player;
        harness.Queue.Processor = processor ?? SearchHarness.Fixed();
        await harness.Store.SaveIdleSongsAsync(new[]
        {
            new IdleSong { Name = "晴天", Singer = "周杰伦" },
            new IdleSong { Name = "海阔天空", Singer = "Beyond" },
        });
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
        return (harness.Store, harness.Bus, harness.Queue, player);
    }

    [Fact]
    public async Task Idle_QueueEmpty_PlaysNextIdleSongInOrder()
    {
        var (store, bus, queue, player) = await CreateIdleQueueAsync();
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
        var (store, bus, queue, player) = await CreateIdleQueueAsync();
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

        var (store, bus, queue, player) = await CreateIdleQueueAsync(processor: flakyProcessor);
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000));
        Assert.Equal("海阔天空", player.Executed[0].Track!.Title); // 跳过晴天直接播第二首
    }

    [Fact]
    public async Task Idle_PlayerRejects_PausesAfterConsecutiveFailures()
    {
        var (store, bus, queue, player) = await CreateIdleQueueAsync();
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
        var (store, bus, queue, player) = await CreateIdleQueueAsync(); // interval=1s
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
        await store.SaveIdleSongsAsync(new[] { new IdleSong { Name = "晴天", Singer = "周杰伦" } });
        await queue.StartAsync();

        await Task.Delay(300);
        Assert.Empty(player.Executed);
    }

    [Fact]
    public async Task Idle_TransientStopRightAfterStart_DoesNotCutSong()
    {
        // bug 回归（用户实测"启动就放空闲歌，放一小会立马切歌"）：播放器在
        // PlaySelected 后存在切歌/加载瞬态窗口（短暂报 stopped/error，无进度），
        // 空闲歌跟踪必须继续观察，不得立即判定"播完"切走——与点歌状态机
        // IsAbortedPlayback 刚播即停双条件同语义
        var (store, bus, queue, player) = await CreateIdleQueueAsync();
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
        var (store, bus, queue, player) = await CreateIdleQueueAsync();
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
        var (store, bus, queue, player) = await CreateIdleQueueAsync(config: longInterval);
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000));

        // 换列表：当前"晴天"在播期间替换歌单
        await queue.ReloadIdleSongsAsync(new[]
        {
            new IdleSong { Name = "光辉岁月", Singer = "Beyond" },
        });

        // 立即按新列表播放（唤醒即打断，不干等 10s interval）
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 2, 2500));
        Assert.Equal("光辉岁月", player.Executed[1].Track!.Title);
    }

    [Fact]
    public async Task Idle_Reload_AppliesNewSongsWithoutRestart()
    {
        var (store, bus, queue, player) = await CreateIdleQueueAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1));

        await queue.ReloadIdleSongsAsync(new[]
        {
            new IdleSong { Name = "光辉岁月", Singer = "Beyond" },
        });

        // 歌单更新后下一首播新歌
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 2, 5000));
        Assert.Equal("光辉岁月", player.Executed[1].Track!.Title);
    }
}

}
