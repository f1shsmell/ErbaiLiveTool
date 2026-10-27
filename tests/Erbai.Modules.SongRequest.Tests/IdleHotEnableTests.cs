using Erbai.Contracts.Configuration;
using Erbai.Contracts.Players;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Contracts.Storage;
using Erbai.Core.Events;
using Erbai.Core.Storage;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>
/// 空闲歌单热启用（2026-10 修复）：主播把空闲歌单从关切到开（ReloadIdleConfig）
/// 必须立即开播——旧实现 worker 只阻塞在 pending 通道上，唤醒信号无人消费，
/// 要等下一首点歌才会真正生效。
/// </summary>
public class IdleHotEnableTests
{
    private static async Task<(SqliteStorageEngine Store, EventBus Bus, SongQueueService Queue, FakePlayer Player)>
        CreateDisabledIdleQueueAsync()
    {
        // 空闲歌单默认关闭（Enabled=false），但空闲播放源歌单已标记好、歌曲列表已备好
        var harness = await TestHarness.CreateAsync(TestHarness.DefaultConfig());
        var player = new FakePlayer();
        harness.Queue.Player = player;
        harness.Queue.Processor = SearchHarness.Fixed();
        await TestHarness.SeedIdlePlaylistAsync(
            harness.Store, ("晴天", "周杰伦"), ("海阔天空", "Beyond"));
        // 收到 PlaySelected 才报 playing，两帧后 stopped——等待结束跟踪需要"播完"信号
        var probes = 0;
        player.ProbeFunc = () =>
        {
            var dispatched = player.Executed.Count > 0;
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
        return (harness.Store, harness.Bus, harness.Queue, player);
    }

    [Fact]
    public async Task ReloadIdleConfig_EnableWhileRunning_StartsPlayingImmediately()
    {
        var (store, bus, queue, player) = await CreateDisabledIdleQueueAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        // 关闭状态下 worker 挂起，不得自动开播
        Assert.True(await SearchHarness.WaitUntilAsync(() => queue.IsStarted));
        await Task.Delay(300);
        Assert.Empty(player.Executed);

        // 热启用 → 唤醒信号必须立即被消费并开始播放空闲歌
        queue.ReloadIdleConfig(new IdlePlaylistConfig { Enabled = true, PlayInterval = 1 });
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000),
            "热启用空闲歌单后 5s 内应开始播放");
        Assert.Equal(PlayerCommand.PlaySelected, player.Executed[0].Command);
        Assert.Equal("晴天", player.Executed[0].Track!.Title);
    }

    [Fact]
    public async Task ReloadIdleConfig_EnableAfterRequestsStarted_StillWorks()
    {
        // 先用点歌把 worker 跑起来（走点歌路径），点歌结算后热启用空闲歌单
        var config = TestHarness.DefaultConfig() with
        {
            // 短播放超时：派发的点歌快速结算，worker 尽快回到等待位
            Queue = TestHarness.DefaultConfig().Queue with { PlaybackStartTimeout = 1, PlaybackTimeout = 2 },
        };
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync(config);
        var player = new FakePlayer();
        queue.Player = player;
        queue.Processor = SearchHarness.Fixed();
        await TestHarness.SeedIdlePlaylistAsync(store, ("晴天", "周杰伦"));
        await queue.StartAsync();

        var (request, decision) = await queue.SubmitAsync(TestHarness.NewRequest("光辉岁月", userId: "u1"));
        Assert.True(decision.Allowed);
        // FakePlayer 未设 ProbeFunc → 状态机按起播超时结算（failed），终态即可：
        // 本用例关注的是 worker 回到等待位后热启用能开播，不关心结算方向
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
        {
            var status = queue.GetRequestStatusAsync(request!.RequestId!.Value).Result;
            return status is not null && !RequestStatuses.IsActive(status.Value);
        }, 10000), "点歌应在短预算内进入终态");

        // 队列已空，热启用 → 空闲歌单接管
        queue.ReloadIdleConfig(new IdlePlaylistConfig { Enabled = true, PlayInterval = 1 });
        Assert.True(await SearchHarness.WaitUntilAsync(() => player.Executed.Count >= 1, 5000),
            "点歌路径结束后热启用空闲歌单应立即开播");
    }
}
