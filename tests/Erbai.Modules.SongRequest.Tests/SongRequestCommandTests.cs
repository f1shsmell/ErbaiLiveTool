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
}
