using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>
/// 提交流水线与权限（提交/权限域）。
/// 检查顺序不可变（docs/03 §1.2）：黑名单 → 重复点歌 → 队满 → 单用户上限；
/// 拒绝必须持久化 + 发事件。
/// </summary>
public class QueueSubmitTests
{
    [Fact]
    public async Task Submit_CheckOrder_UserBannedWinsOverEverything()
    {
        var (store, bus, logs, config, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        await store.BanUserAsync("douyin", "1", "u1", nickname: "观众");

        // 重复 + 队满都满足，但黑名单优先
        var (_, decision) = await queue.SubmitAsync(TestHarness.NewRequest());
        Assert.False(decision.Allowed);
        Assert.Equal("user_banned", decision.ReasonCode);
    }

    [Fact]
    public async Task Submit_CheckOrder_DuplicateBeforeUserLimit()
    {
        var (store, bus, logs, config, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        var first = await queue.SubmitAsync(TestHarness.NewRequest("晴天"));
        Assert.True(first.Decision.Allowed);

        // 同一用户重复点同一首歌：必须看到 duplicate_song 而不是 user_limit_reached
        var (_, decision) = await queue.SubmitAsync(TestHarness.NewRequest("晴天"));
        Assert.False(decision.Allowed);
        Assert.Equal("duplicate_song", decision.ReasonCode);
    }

    [Fact]
    public async Task Submit_QueueFull_ThenUserLimit()
    {
        var config = TestHarness.DefaultConfig() with
        {
            Queue = TestHarness.DefaultConfig().Queue with { MaxSize = 2, MaxPerUser = 1 },
        };
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync(config);
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        Assert.True((await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"))).Decision.Allowed);
        Assert.True((await queue.SubmitAsync(TestHarness.NewRequest("海阔天空", userId: "u2"))).Decision.Allowed);

        // 队满：任何人再点都是 queue_full（优先于单用户上限）
        var (_, fullDecision) = await queue.SubmitAsync(TestHarness.NewRequest("光辉岁月", userId: "u1"));
        Assert.Equal("queue_full", fullDecision.ReasonCode);
    }

    [Fact]
    public async Task Submit_UserLimitReached()
    {
        var config = TestHarness.DefaultConfig() with
        {
            Queue = TestHarness.DefaultConfig().Queue with { UserLimitEnabled = true, MaxPerUser = 1 },
        };
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync(config);
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        Assert.True((await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"))).Decision.Allowed);
        // 同用户第二首（不同歌）→ 单用户上限
        var (_, decision) = await queue.SubmitAsync(TestHarness.NewRequest("海阔天空", userId: "u1"));
        Assert.False(decision.Allowed);
        Assert.Equal("user_limit_reached", decision.ReasonCode);
    }

    [Fact]
    public async Task Submit_UserLimitDisabled_AllowsMultiple()
    {
        var config = TestHarness.DefaultConfig() with
        {
            Queue = TestHarness.DefaultConfig().Queue with { UserLimitEnabled = false },
        };
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync(config);
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        Assert.True((await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"))).Decision.Allowed);
        var (_, decision) = await queue.SubmitAsync(TestHarness.NewRequest("海阔天空", userId: "u1"));
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Submit_AdminAndAnchor_BypassLevelGates_ButNotQueueRules()
    {
        // 等级门槛只对普通观众生效（admin/anchor 直通）；队满/上限/去重
        // 是队列管理规则，对所有人生效（docs/03 §1.2 重复检查语义同样覆盖 admin）。
        var config = TestHarness.DefaultConfig() with
        {
            Permissions = TestHarness.DefaultConfig().Permissions with { DouyinMinFanLevel = 10 },
            Queue = TestHarness.DefaultConfig().Queue with { MaxSize = 2, MaxPerUser = 1 },
        };
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync(config);
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        // 普通观众等级不足被拒
        var (_, viewer) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1", fanLevel: 5));
        Assert.False(viewer.Allowed);
        Assert.Equal("fan_level_too_low", viewer.ReasonCode);

        // admin/anchor 绕过等级门槛
        var (_, admin) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u2", isAdmin: true, fanLevel: 0));
        Assert.True(admin.Allowed);
        var (_, anchor) = await queue.SubmitAsync(TestHarness.NewRequest("海阔天空", userId: "u3", isAnchor: true, fanLevel: 0));
        Assert.True(anchor.Allowed);

        // 队满后（含 admin）一律 queue_full
        var (_, full) = await queue.SubmitAsync(TestHarness.NewRequest("光辉岁月", userId: "u4", isAdmin: true, fanLevel: 0));
        Assert.Equal("queue_full", full.ReasonCode);
    }

    [Fact]
    public async Task Submit_LevelGates_DouyinFanLevel()
    {
        var config = TestHarness.DefaultConfig() with
        {
            Permissions = TestHarness.DefaultConfig().Permissions with { DouyinMinFanLevel = 10 },
        };
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync(config);
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        var (_, low) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", fanLevel: 5));
        Assert.False(low.Allowed);
        Assert.Equal("fan_level_too_low", low.ReasonCode);

        var (_, pass) = await queue.SubmitAsync(TestHarness.NewRequest("海阔天空", fanLevel: 10));
        Assert.True(pass.Allowed);
    }

    [Fact]
    public async Task Submit_BannedUser_PriorityOverLevelGate()
    {
        // 审计 T1-P6: 用户黑名单优先于一切(含等级门槛)——被拉黑且等级不足的
        // 用户必须得到 user_banned 而非 fan_level_too_low(否则拒绝原因码语义泄漏)
        var config = TestHarness.DefaultConfig() with
        {
            Permissions = TestHarness.DefaultConfig().Permissions with { DouyinMinFanLevel = 10 },
        };
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync(config);
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        await store.BanUserAsync("douyin", "1", "u9", "小明", reason: "test", bannedBy: "t");

        var (request, decision) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u9", fanLevel: 5));

        Assert.False(decision.Allowed);
        Assert.Equal("user_banned", decision.ReasonCode);
        Assert.NotNull(request);
        Assert.Equal("user_banned", request!.FailureReason);

        var ev = await TestHarness.AwaitEventAsync(sub, "queue.rejected");
        Assert.NotNull(ev);
        Assert.Equal("user_banned", ev.Data.Decision!.ReasonCode);
    }

    [Fact]
    public async Task Submit_LevelGates_BilibiliMedalLevel_AndUnknownDeny()
    {
        var config = TestHarness.DefaultConfig() with
        {
            Permissions = TestHarness.DefaultConfig().Permissions with { BilibiliMinMedalLevel = 10 },
        };
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync(config);
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        var (_, unknown) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", platform: "bilibili", medalLevel: null));
        Assert.False(unknown.Allowed);
        Assert.Equal("level_unknown", unknown.ReasonCode);

        var (_, low) = await queue.SubmitAsync(TestHarness.NewRequest("海阔天空", platform: "bilibili", medalLevel: 9));
        Assert.Equal("medal_level_too_low", low.ReasonCode);

        var (_, pass) = await queue.SubmitAsync(TestHarness.NewRequest("光辉岁月", platform: "bilibili", medalLevel: 10));
        Assert.True(pass.Allowed);
    }

    [Fact]
    public async Task Submit_LevelUnknownPolicyAllow()
    {
        var config = TestHarness.DefaultConfig() with
        {
            Permissions = TestHarness.DefaultConfig().Permissions with
            {
                DouyinMinFanLevel = 10,
                LevelUnknownPolicy = "allow",
            },
        };
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync(config);
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        var (_, decision) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", fanLevel: null));
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Submit_Rejected_PersistedWithReasonAndEmitsEvent()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        await store.BanUserAsync("douyin", "1", "u1", nickname: "观众");

        var (request, _) = await queue.SubmitAsync(TestHarness.NewRequest());

        Assert.NotNull(request);
        Assert.Equal(RequestStatus.Rejected, request.Status);
        Assert.Equal("user_banned", request.FailureReason);
        // 持久化
        var persisted = await store.GetRequestAsync(request.RequestId!.Value);
        Assert.NotNull(persisted);
        Assert.Equal(RequestStatus.Rejected, persisted.Status);
        // 事件
        var ev = await TestHarness.AwaitEventAsync(sub, "queue.rejected");
        Assert.NotNull(ev);
        Assert.Equal("user_banned", ev.Data.Decision!.ReasonCode);
        Assert.NotNull(ev.Data.QueueSnapshot);
    }

    [Fact]
    public async Task Submit_Queued_PersistsAndEmitsAddedWithSnapshot()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        var (request, decision) = await queue.SubmitAsync(TestHarness.NewRequest());

        Assert.True(decision.Allowed);
        Assert.NotNull(request);
        Assert.Equal(RequestStatus.Queued, request.Status);
        Assert.False(string.IsNullOrEmpty(request.CanonicalSongKey));
        // 用户计数 +1
        var user = await store.GetUserAsync("douyin", "1", "u1");
        Assert.NotNull(user);
        Assert.Equal(1, user.RequestCount);
        // 事件附快照
        var ev = await TestHarness.AwaitEventAsync(sub, "queue.added");
        Assert.NotNull(ev);
        var snapshot = ev.Data.QueueSnapshot!;
        Assert.Equal(1, snapshot.QueueTotal);
        Assert.Single(snapshot.Items);
        Assert.Equal(1, snapshot.Items[0].Position);
    }

    [Fact]
    public async Task Submit_BilibiliAdminMerge_FromStoredPrivileges()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        // 存储中是管理员，但弹幕未带 admin 标志
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "bilibili",
            RoomId = "1017",
            UserId = "b1",
            Nickname = "房管",
            IsAdmin = true,
        });

        var (request, decision) = await queue.SubmitAsync(TestHarness.NewRequest(
            "晴天", platform: "bilibili", roomId: "1017", userId: "b1", isAdmin: false));

        Assert.True(decision.Allowed);
        Assert.NotNull(request);
        Assert.True(request.IsAdmin); // 提交时合并存储特权
    }

    [Fact]
    public async Task Snapshot_PositionRenumbers_IsCurrentFollowsCurrentRequest()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        var (a, _) = await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));
        var (b, _) = await queue.SubmitAsync(TestHarness.NewRequest("海阔天空", userId: "u2"));
        var (c, _) = await queue.SubmitAsync(TestHarness.NewRequest("光辉岁月", userId: "u3"));
        await TestHarness.DrainEventsAsync(sub);

        // worker 已把 a 派发为 dispatched（FIFO 串行：a 播放期间 b/c 仍在队）
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(a!.RequestId!.Value).Result == RequestStatus.Dispatched));
        Assert.Equal(RequestStatus.Queued, await store.GetRequestStatusAsync(b!.RequestId!.Value));

        queue.SetCurrentRequestId(a!.RequestId!.Value);
        var snapshot = await queue.GetSnapshotAsync();
        Assert.Equal(3, snapshot.QueueTotal);
        Assert.Equal(1, snapshot.Items[0].Position);
        Assert.True(snapshot.Items[0].IsCurrent);
        Assert.False(snapshot.Items[1].IsCurrent);

        // 完成 a 后 position 动态重编号（worker 继续派发 b，队列仍满 2 条活动）
        await queue.CompleteAsync(a.RequestId.Value);
        snapshot = await queue.GetSnapshotAsync();
        Assert.Equal(2, snapshot.QueueTotal);
        Assert.Equal(1, snapshot.Items[0].Position);
        Assert.Equal(b.RequestId, snapshot.Items[0].Request.RequestId);
    }

    [Fact]
    public async Task SetTerminal_OperatorOnly_ReturnsNullWhenAlreadyTerminal()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        var (request, _) = await queue.SubmitAsync(TestHarness.NewRequest());
        Assert.True(await SearchHarness.WaitUntilAsync(() =>
            store.GetRequestStatusAsync(request!.RequestId!.Value).Result == RequestStatus.Dispatched));

        var skipped = await queue.SkipAsync(request!.RequestId!.Value);
        Assert.NotNull(skipped);
        Assert.Equal(RequestStatus.Skipped, skipped.Status);

        // 已终态再操作 → null（CAS expected=ACTIVE 失败）
        var again = await queue.SkipAsync(request.RequestId.Value);
        Assert.Null(again);
    }

    [Fact]
    public async Task SetTerminal_ClearsCurrentBeforeEmittingSnapshot()
    {
        var (store, bus, logs, _, queue, _, _) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<QueueEventEnvelope>();
        var (request, _) = await queue.SubmitAsync(TestHarness.NewRequest());
        // 竞态消除：先等 flow dispatched 事件真正发出再 Drain——此前只等 store 状态，
        // dispatched 事件可能晚于 Drain 到达被清掉，首个事件被误判（flaky 见 2026-08-27）
        while (true)
        {
            var seen = await TestHarness.NextEventAsync(sub);
            if (seen is null)
            {
                break;
            }

            if (seen.Event == "queue.dispatched")
            {
                break;
            }
        }

        Assert.Equal(RequestStatus.Dispatched, await store.GetRequestStatusAsync(request!.RequestId!.Value));
        await TestHarness.DrainEventsAsync(sub);
        queue.SetCurrentRequestId(request.RequestId.Value);

        var skipped = await queue.SkipAsync(request.RequestId.Value);
        Assert.NotNull(skipped);
        Assert.Null(queue.CurrentRequestId); // 发终态事件前清空
        var ev = await TestHarness.NextEventAsync(sub);
        Assert.NotNull(ev);
        Assert.Equal("queue.skipped", ev.Event);
        // 事件附带的快照中不得把已终态的歌标成 is_current
        Assert.Null(ev.Data.QueueSnapshot!.CurrentRequestId);
        Assert.All(ev.Data.QueueSnapshot.Items, item => Assert.False(item.IsCurrent));
    }
}
