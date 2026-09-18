using Erbai.Contracts.Requests;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>
/// 弹幕命令处理（命令/黑名单域）：
/// 管理命令权限、昵称模糊匹配（精确优先/多命中拒绝/0 命中静默）、
/// 歌曲黑名单拦截、切歌、点歌全流程。
/// </summary>
public class SongRequestCommandTests
{
    private static DanmakuContext Ctx(string text, string nickname = "观众", bool isAdmin = false,
        bool isAnchor = false, string userId = "u1", string platform = "douyin") =>
        new()
        {
            Text = text,
            Nickname = nickname,
            Platform = platform,
            RoomId = "1",
            UserId = userId,
            IsAdmin = isAdmin,
            IsAnchor = isAnchor,
            FanLevel = 5,
        };

    [Fact]
    public async Task Request_SubmitsThroughPipeline()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<Erbai.Contracts.Queue.QueueEventEnvelope>();

        var consumed = await commands.HandleMessageAsync(Ctx("点歌 晴天 - 周杰伦"));

        Assert.True(consumed);
        var requests = await store.ListRequestsAsync(RequestStatuses.Active);
        var request = Assert.Single(requests);
        Assert.Equal("晴天", request.SongName);
        Assert.Equal("周杰伦", request.Singer);
        Assert.True(RequestStatuses.IsActive(request.Status));
    }

    [Fact]
    public async Task Request_Blacklist_ExactAndKeyword_RejectsWithRecordedEvent()
    {
        var (store, bus, logs, _, queue, blacklist, commands) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<Erbai.Contracts.Queue.QueueEventEnvelope>();
        await blacklist.AddRuleAsync("*小苹果");
        await blacklist.AddRuleAsync("晴天");

        // 精确命中
        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天")));
        // 关键词子串命中（大小写不敏感）
        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 广场舞小苹果DJ版")));

        var ev = await TestHarness.AwaitEventAsync(sub, "queue.rejected");
        Assert.NotNull(ev);
        Assert.Equal("blacklisted", ev.Data.Decision!.ReasonCode);
        Assert.Equal("blacklisted", ev.Data.Request!.FailureReason);

        var ev2 = await TestHarness.AwaitEventAsync(sub, "queue.rejected");
        Assert.NotNull(ev2);
    }

    [Fact]
    public async Task Request_NonBlacklisted_Passes()
    {
        var (store, bus, logs, _, queue, blacklist, commands) = await TestHarness.CreateAsync();
        using var sub = bus.Subscribe<Erbai.Contracts.Queue.QueueEventEnvelope>();
        await blacklist.AddRuleAsync("*小苹果");

        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天")));
        Assert.Single(await store.ListRequestsAsync(RequestStatuses.Active));
    }

    [Fact]
    public async Task AdminCommand_RequiresPrivilege()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u2", Nickname = "小明",
        });

        // 普通观众尝试设置管理员 → 消费但不生效
        Assert.True(await commands.HandleMessageAsync(Ctx("设置管理员@小明", nickname: "路人", userId: "u9")));
        var user = await store.GetUserAsync("douyin", "1", "u2");
        Assert.NotNull(user);
        Assert.False(user.IsAdmin);
    }

    [Fact]
    public async Task AdminCommand_SetByExactNickname()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u2", Nickname = "小明",
        });

        Assert.True(await commands.HandleMessageAsync(Ctx("设置管理员@小明", isAdmin: true)));
        var user = await store.GetUserAsync("douyin", "1", "u2");
        Assert.NotNull(user);
        Assert.True(user.IsAdmin);
        Assert.False(user.IsAnchor); // 只动 is_admin

        // 取消
        Assert.True(await commands.HandleMessageAsync(Ctx("取消管理员@小明", isAdmin: true)));
        user = await store.GetUserAsync("douyin", "1", "u2");
        Assert.NotNull(user);
        Assert.False(user.IsAdmin);
    }

    [Fact]
    public async Task AdminCommand_ExactWinsOverFuzzy()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u2", Nickname = "小明",
        });
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u3", Nickname = "小明同学",
        });

        // "小明" 精确命中 u2（模糊候选 u2/u3 都含），精确优先
        Assert.True(await commands.HandleMessageAsync(Ctx("设置管理员@小明", isAdmin: true)));
        Assert.True((await store.GetUserAsync("douyin", "1", "u2"))!.IsAdmin);
        Assert.False((await store.GetUserAsync("douyin", "1", "u3"))!.IsAdmin);
    }

    [Fact]
    public async Task AdminCommand_AmbiguousMatch_Refused()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u2", Nickname = "小明A",
        });
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u3", Nickname = "小明B",
        });

        // 模糊命中 2 人且无精确命中 → 拒绝操作
        Assert.True(await commands.HandleMessageAsync(Ctx("设置管理员@小明", isAdmin: true)));
        Assert.False((await store.GetUserAsync("douyin", "1", "u2"))!.IsAdmin);
        Assert.False((await store.GetUserAsync("douyin", "1", "u3"))!.IsAdmin);
    }

    [Fact]
    public async Task AdminCommand_NoHit_Silent()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        // 0 命中不抛异常、不写库
        Assert.True(await commands.HandleMessageAsync(Ctx("设置管理员@不存在的人", isAdmin: true)));
        Assert.Empty(await store.ListUsersAsync());
    }

    [Fact]
    public async Task BanCommand_ByNickname_BansAndUnbans()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u2", Nickname = "小明",
        });

        Assert.True(await commands.HandleMessageAsync(Ctx("拉黑@小明", isAdmin: true)));
        Assert.True(await store.IsUserBannedAsync("douyin", "1", "u2"));

        // 被拉黑后点歌被拒
        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天", userId: "u2")));
        Assert.Empty(await store.ListRequestsAsync(RequestStatuses.Active));

        Assert.True(await commands.HandleMessageAsync(Ctx("取消拉黑@小明", isAdmin: true)));
        Assert.False(await store.IsUserBannedAsync("douyin", "1", "u2"));
    }

    [Fact]
    public async Task SkipCommand_RequiresPrivilege_AndSkipsFirst()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));

        // 普通观众切歌 → 消费但不动队列
        Assert.True(await commands.HandleMessageAsync(Ctx("切歌", nickname: "路人", userId: "u9")));
        Assert.Single(await store.ListRequestsAsync(RequestStatuses.Active));

        // 管理切歌 → 队首 skipped
        Assert.True(await commands.HandleMessageAsync(Ctx("切歌", isAdmin: true)));
        var active = await store.ListRequestsAsync(RequestStatuses.Active);
        Assert.Empty(active);
        var history = await store.ListRequestsPageAsync(status: RequestStatus.Skipped);
        Assert.Equal(1, history.Total);
    }

    [Fact]
    public async Task BanSongCommand_AddsAndRemovesBlacklist()
    {
        var (store, bus, logs, _, queue, blacklist, commands) = await TestHarness.CreateAsync();

        // 管理员拉黑歌曲（精确）
        Assert.True(await commands.HandleMessageAsync(Ctx("拉黑歌曲 晴天", isAdmin: true)));
        Assert.True(blacklist.IsBlacklisted("晴天"));

        // 已拉黑的歌被拦截
        Assert.True(await commands.HandleMessageAsync(Ctx("点歌 晴天")));
        Assert.Empty(await store.ListRequestsAsync(RequestStatuses.Active));

        // 取消拉黑
        Assert.True(await commands.HandleMessageAsync(Ctx("取消拉黑歌曲 晴天", isAdmin: true)));
        Assert.False(blacklist.IsBlacklisted("晴天"));
    }

    [Fact]
    public async Task BanSongCommand_KeywordRule()
    {
        var (store, bus, logs, _, queue, blacklist, commands) = await TestHarness.CreateAsync();

        // *关键词 走子串规则
        Assert.True(await commands.HandleMessageAsync(Ctx("拉黑歌曲 *小苹果", isAdmin: true)));
        Assert.True(blacklist.IsBlacklisted("广场舞小苹果DJ版"));
    }

    [Fact]
    public async Task BanSongCommand_RequiresPrivilege()
    {
        var (store, bus, logs, _, queue, blacklist, commands) = await TestHarness.CreateAsync();

        // 普通观众拉黑歌曲 → 消费但不生效
        Assert.True(await commands.HandleMessageAsync(Ctx("拉黑歌曲 晴天", nickname: "路人", userId: "u9")));
        Assert.False(blacklist.IsBlacklisted("晴天"));
    }

    [Fact]
    public async Task BanSongCommand_DoesNotBanUser()
    {
        var (store, bus, logs, _, queue, blacklist, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u2", Nickname = "歌曲 晴天",
        });

        // 「拉黑歌曲 晴天」是歌曲命令，不是「拉黑用户 @歌曲 晴天」
        Assert.True(await commands.HandleMessageAsync(Ctx("拉黑歌曲 晴天", isAdmin: true)));
        Assert.False(await store.IsUserBannedAsync("douyin", "1", "u2"));
        Assert.True(blacklist.IsBlacklisted("晴天"));
    }

    [Fact]
    public async Task IrrelevantDanmaku_NotConsumed()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        Assert.False(await commands.HandleMessageAsync(Ctx("你好呀")));
        Assert.Empty(await store.ListRequestsAsync());
    }

    // ---- 存储特权回归（2026-09-18 用户实测：主播与设置的管理员均无法切歌）----
    // 特权有两个来源：弹幕事件里的实时标志（平台给的房管/主播标记）与 users 表里
    // 持久化的 is_admin/is_anchor（「设置管理员@XX」写的就是它；管理页的「主播/管理员」
    // 角色列也读它）。平台事件对「应用内设置的管理员」永远不会打标，抖音主播弹幕也
    // 常常不带 anchor 标志——只看事件标志会让这两类人一律切不了歌。

    [Fact]
    public async Task SkipCommand_StoredAdmin_WithoutPlatformFlag_SkipsFirst()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u2", Nickname = "小明", IsAdmin = true,
        });
        await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));

        Assert.True(await commands.HandleMessageAsync(Ctx("切歌", nickname: "小明", userId: "u2")));

        Assert.Empty(await store.ListRequestsAsync(RequestStatuses.Active));
        Assert.Equal(1, (await store.ListRequestsPageAsync(status: RequestStatus.Skipped)).Total);
    }

    [Fact]
    public async Task SkipCommand_StoredAnchor_WithoutPlatformFlag_SkipsFirst()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u1", Nickname = "主播", IsAnchor = true,
        });
        await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));

        Assert.True(await commands.HandleMessageAsync(Ctx("切歌", nickname: "主播", userId: "u1")));

        Assert.Empty(await store.ListRequestsAsync(RequestStatuses.Active));
    }

    [Fact]
    public async Task SkipCommand_StoredPrivilege_DoesNotLeakToOthers()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u2", Nickname = "小明", IsAdmin = true,
        });
        await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));

        // 另一个没有特权（且库里无记录）的用户切歌 → 消费但不动队列
        Assert.True(await commands.HandleMessageAsync(Ctx("切歌", nickname: "路人", userId: "u9")));

        Assert.Single(await store.ListRequestsAsync(RequestStatuses.Active));
    }

    /// <summary>
    /// 复刻生产库真实形态（2026-09-16 实测数据）：主播「白眉神探」在
    /// room_id=54380982833 有 is_anchor=1 的行，同时因弹幕事件缺 RoomId 又落了一行
    /// room_id=''（is_anchor=0）。切歌弹幕走的正是空 RoomId 那条——特权查询必须
    /// 跨房间取 MAX，按 room_id 精确匹配会命中无特权那行，主播照样切不了歌。
    /// </summary>
    [Fact]
    public async Task SkipCommand_StoredPrivilege_SurvivesRoomIdMismatch()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "54380982833", UserId = "2810778114588685",
            Nickname = "白眉神探", IsAdmin = true, IsAnchor = true,
        });
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "", UserId = "2810778114588685", Nickname = "白眉神探",
        });
        await queue.SubmitAsync(TestHarness.NewRequest("晴天", userId: "u1"));

        // 弹幕事件 RoomId 为空（实测报文如此）且不带任何平台标志
        Assert.True(await commands.HandleMessageAsync(
            Ctx("切歌", nickname: "白眉神探", userId: "2810778114588685") with { RoomId = "" }));

        Assert.Empty(await store.ListRequestsAsync(RequestStatuses.Active));
    }

    [Fact]
    public async Task AdminCommand_ByStoredAdmin_WithoutPlatformFlag_TakesEffect()
    {
        var (store, bus, logs, _, queue, _, commands) = await TestHarness.CreateAsync();
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u2", Nickname = "小明", IsAdmin = true,
        });
        await store.SaveUserAsync(new Erbai.Contracts.Storage.User
        {
            Platform = "douyin", RoomId = "1", UserId = "u3", Nickname = "小红",
        });

        // 存储管理员（弹幕事件不带标志）设置另一位管理员 → 应生效
        Assert.True(await commands.HandleMessageAsync(
            Ctx("设置管理员@小红", nickname: "小明", userId: "u2")));

        var target = await store.GetUserAsync("douyin", "1", "u3");
        Assert.NotNull(target);
        Assert.True(target.IsAdmin);
    }
}
