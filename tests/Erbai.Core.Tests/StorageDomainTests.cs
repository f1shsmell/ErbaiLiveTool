using Erbai.Contracts.Storage;
using Erbai.Core.Storage;

namespace Erbai.Core.Tests;

/// <summary>
/// 存储域补齐（阶段 2 里程碑 1）：users / banned_users / banned_songs /
/// idle_playlist 语义 + SqliteExecutor 关闭竞态修复。
/// 行为对齐（test_save_user_writes_privileges_*、
/// test_list_users_filters_*、test_set_user_admin_*）与 docs/03 §2–§3。
/// </summary>
public class StorageDomainTests
{
    private static string TempDb() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "test.db");

    private static async Task<SqliteStorageEngine> OpenEngineAsync()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();
        return engine;
    }

    private static User NewUser(
        string platform = "douyin",
        string roomId = "1",
        string userId = "u1",
        string nickname = "观众",
        bool isAdmin = false,
        bool isAnchor = false,
        int? fanLevel = null,
        int? medalLevel = null) =>
        new()
        {
            Platform = platform,
            RoomId = roomId,
            UserId = userId,
            Nickname = nickname,
            IsAdmin = isAdmin,
            IsAnchor = isAnchor,
            FanLevel = fanLevel,
            MedalLevel = medalLevel,
        };

    [Fact]
    public async Task SaveUser_UpsertOverwritesNickname_KeepsLevelsOnNull()
    {
        await using var engine = await OpenEngineAsync();
        await engine.SaveUserAsync(NewUser(nickname: "旧名", fanLevel: 5, medalLevel: 3));

        // 昵称覆盖；等级传入 NULL 保持旧值（COALESCE 不降级）
        await engine.SaveUserAsync(NewUser(nickname: "新名"));
        var user = await engine.GetUserAsync("douyin", "1", "u1");

        Assert.NotNull(user);
        Assert.Equal("新名", user.Nickname);
        Assert.Equal(5, user.FanLevel);
        Assert.Equal(3, user.MedalLevel);
        Assert.False(user.IsAdmin);
    }

    [Fact]
    public async Task SaveUser_PrivilegesExplicitlyOverwritten_ForRevocation()
    {
        await using var engine = await OpenEngineAsync();
        await engine.SaveUserAsync(NewUser(isAdmin: true));
        // 显式传 false 必须能撤销（特权可撤销语义，旧 R5）
        await engine.SaveUserAsync(NewUser(isAdmin: false));

        var user = await engine.GetUserAsync("douyin", "1", "u1");
        Assert.NotNull(user);
        Assert.False(user.IsAdmin);
    }

    [Fact]
    public async Task SaveUser_RequestCountOnlyIncrementsOnRequest()
    {
        await using var engine = await OpenEngineAsync();
        await engine.SaveUserAsync(NewUser(), incrementRequestCount: true);
        await engine.SaveUserAsync(NewUser()); // 普通消息不加
        await engine.SaveUserAsync(NewUser(), incrementRequestCount: true);

        var user = await engine.GetUserAsync("douyin", "1", "u1");
        Assert.NotNull(user);
        Assert.Equal(2, user.RequestCount);
        Assert.NotNull(user.LastRequestAt);
    }

    [Fact]
    public async Task ListUsers_CrossRoomDedup_TakesLatest_AdminMaxAcrossRooms_AdminFirst()
    {
        await using var engine = await OpenEngineAsync();
        // 同一用户两个房间：room1 管理员、room2 普通；room2 更新更晚
        await engine.SaveUserAsync(NewUser(roomId: "1", nickname: "甲", isAdmin: true));
        await engine.SaveUserAsync(NewUser(roomId: "2", nickname: "乙"));
        await engine.SaveUserAsync(NewUser(roomId: "3", userId: "u2", nickname: "路人"));

        var users = await engine.ListUsersAsync();

        Assert.Equal(2, users.Count);
        // 管理员置顶；跨房间 admin 取 MAX
        var first = users[0];
        Assert.Equal("u1", first.UserId);
        Assert.True(first.IsAdmin);
        Assert.Equal("乙", first.Nickname); // 最新一条昵称
        Assert.Equal("u2", users[1].UserId);
    }

    [Fact]
    public async Task ListUsers_NicknameSubstringMatch_EscapesWildcards()
    {
        await using var engine = await OpenEngineAsync();
        await engine.SaveUserAsync(NewUser(nickname: "小明"));
        await engine.SaveUserAsync(NewUser(userId: "u2", nickname: "小100%"));

        var hit = await engine.ListUsersAsync(nickname: "明");
        Assert.Single(hit);

        // % 按字面匹配：搜 "100%" 只命中 u2，不因通配符放大
        var literal = await engine.ListUsersAsync(nickname: "100%");
        Assert.Single(literal);
        Assert.Equal("u2", literal[0].UserId);
    }

    [Fact]
    public async Task SetUserAdmin_WritesOnlyAdmin_LeavesAnchorUntouched()
    {
        await using var engine = await OpenEngineAsync();
        await engine.SaveUserAsync(NewUser(isAnchor: true));
        await engine.SetUserAdminAsync("douyin", "1", "u1", admin: true);

        var user = await engine.GetUserAsync("douyin", "1", "u1");
        Assert.NotNull(user);
        Assert.True(user.IsAdmin);
        Assert.True(user.IsAnchor); // anchor 不受影响

        // 撤销 admin 不得连带清掉 anchor
        await engine.SetUserAdminAsync("douyin", "1", "u1", admin: false);
        user = await engine.GetUserAsync("douyin", "1", "u1");
        Assert.NotNull(user);
        Assert.False(user.IsAdmin);
        Assert.True(user.IsAnchor);
    }

    [Fact]
    public async Task SetUserAdmin_MissingUser_ReturnsNull()
    {
        await using var engine = await OpenEngineAsync();
        var result = await engine.SetUserAdminAsync("douyin", "1", "nobody", admin: true);
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteNonAdminUsers_KeepsOnlyAdmins()
    {
        await using var engine = await OpenEngineAsync();
        await engine.SaveUserAsync(NewUser(userId: "a", isAdmin: true));
        await engine.SaveUserAsync(NewUser(userId: "b"));
        await engine.SaveUserAsync(NewUser(userId: "c"));

        var deleted = await engine.DeleteNonAdminUsersAsync();

        Assert.Equal(2, deleted);
        var users = await engine.ListUsersAsync();
        Assert.Single(users);
        Assert.Equal("a", users[0].UserId);
    }

    [Fact]
    public async Task BanUser_IdempotentUpsert_OverwritesReasonWithoutDuplicate()
    {
        await using var engine = await OpenEngineAsync();
        var first = await engine.BanUserAsync("douyin", "1", "u1", nickname: "甲", reason: "刷屏", bannedBy: "主播");
        Assert.NotNull(first);

        var second = await engine.BanUserAsync("douyin", "1", "u1", nickname: "甲", reason: "广告", bannedBy: "主播");
        Assert.NotNull(second);
        Assert.Equal("广告", second.Reason);

        var banned = await engine.ListBannedUsersAsync();
        Assert.Single(banned); // 幂等：不重复行
    }

    [Fact]
    public async Task UnbanUser_ReturnsTrueWhenDeleted_FalseWhenAbsent()
    {
        await using var engine = await OpenEngineAsync();
        await engine.BanUserAsync("douyin", "1", "u1", nickname: "甲");

        Assert.True(await engine.UnbanUserAsync("douyin", "1", "u1"));
        Assert.False(await engine.UnbanUserAsync("douyin", "1", "u1")); // 已不在
        Assert.False(await engine.IsUserBannedAsync("douyin", "1", "u1"));
    }

    [Fact]
    public async Task IsUserBanned_ScopedByPlatformAndRoom()
    {
        await using var engine = await OpenEngineAsync();
        await engine.BanUserAsync("douyin", "1", "u1", nickname: "甲");

        Assert.True(await engine.IsUserBannedAsync("douyin", "1", "u1"));
        Assert.False(await engine.IsUserBannedAsync("douyin", "2", "u1")); // 其他房间
        Assert.False(await engine.IsUserBannedAsync("bilibili", "1", "u1")); // 其他平台
    }

    [Fact]
    public async Task ListBannedUsers_FiltersByNickname_NewestFirst()
    {
        await using var engine = await OpenEngineAsync();
        await engine.BanUserAsync("douyin", "1", "u1", nickname: "小明");
        await engine.BanUserAsync("douyin", "1", "u2", nickname: "小红");

        var hit = await engine.ListBannedUsersAsync(nickname: "明");
        Assert.Single(hit);
        Assert.Equal("u1", hit[0].UserId);
    }

    [Fact]
    public async Task ReplaceBannedSongs_ReplacesWholeList_DropsBlank()
    {
        await using var engine = await OpenEngineAsync();
        await engine.ReplaceBannedSongsAsync(new[] { "晴天", "*小苹果", "  " });

        var rules = await engine.ListBannedSongsAsync();
        Assert.Equal(2, rules.Count);
        Assert.Contains("晴天", rules);
        Assert.Contains("*小苹果", rules);

        // 整体替换：旧的消失
        await engine.ReplaceBannedSongsAsync(new[] { "海阔天空" });
        rules = await engine.ListBannedSongsAsync();
        Assert.Single(rules);
        Assert.Equal("海阔天空", rules[0]);
    }

    [Fact]
    public async Task SaveIdleSongs_ReplacesKeepsOrder_DropsEmptyNames()
    {
        await using var engine = await OpenEngineAsync();
        await engine.SaveIdleSongsAsync(new[]
        {
            new IdleSong { Name = "晴天", Singer = "周杰伦" },
            new IdleSong { Name = "海阔天空", Singer = "Beyond" },
        });

        var songs = await engine.LoadIdleSongsAsync();
        Assert.Equal(2, songs.Count);
        Assert.Equal("晴天", songs[0].Name);
        Assert.Equal("Beyond", songs[1].Singer);

        // 整体替换 + 空歌名剔除 + 保序
        await engine.SaveIdleSongsAsync(new[]
        {
            new IdleSong { Name = "  ", Singer = "x" },
            new IdleSong { Name = "光辉岁月", Singer = "" },
        });
        songs = await engine.LoadIdleSongsAsync();
        Assert.Single(songs);
        Assert.Equal("光辉岁月", songs[0].Name);
    }

    [Fact]
    public async Task ExecuteAfterClose_ThrowsInsteadOfHanging()
    {
        // 阶段 1 遗留竞态：通道完成后 TryWrite 失败 → TCS 永不完成，无 ct
        // 调用方挂死。修复后应快速抛 InvalidOperationException。
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        var executor = new SqliteExecutor(connection);
        await executor.StopAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(_ => 1, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("closed", ex.Message);
    }

    [Fact]
    public async Task ExecuteAfterEngineClose_FailsFast()
    {
        var engine = await OpenEngineAsync();
        await engine.CloseAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.GetUserAsync("douyin", "1", "u1"));
        Assert.Contains("not open", ex.Message);
    }

    // ---- queueup_entries 域（阶段 5）----

    private static Erbai.Contracts.QueueUp.QueueUpEntry NewQueueUpEntry(
        string userId = "u1",
        string nickname = "观众甲",
        string content = "上麦唱一首",
        string? createdAt = null) =>
        new()
        {
            UserId = userId,
            Nickname = nickname,
            Content = content,
            Source = Erbai.Contracts.QueueUp.QueueUpSource.Danmaku,
            Status = Erbai.Contracts.QueueUp.QueueUpStatus.Queued,
            CreatedAt = createdAt is null
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.Parse(createdAt, System.Globalization.CultureInfo.InvariantCulture),
        };

    [Fact]
    public async Task QueueUp_InsertAssignsId_ListFifoByCreatedAt()
    {
        await using var engine = await OpenEngineAsync();
        var a = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(userId: "u1", createdAt: "2026-08-25T10:00:00+00:00"));
        var b = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(userId: "u2", createdAt: "2026-08-25T09:00:00+00:00"));

        Assert.True(a.Id > 0);
        Assert.True(b.Id > a.Id);

        var entries = await engine.ListQueueUpEntriesAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal("u2", entries[0].UserId); // created_at 升序 FIFO（u2 更早）
        Assert.Equal("u1", entries[1].UserId);
        Assert.Equal(Erbai.Contracts.QueueUp.QueueUpSource.Danmaku, entries[0].Source);
        Assert.Equal(Erbai.Contracts.QueueUp.QueueUpStatus.Queued, entries[0].Status);
    }

    [Fact]
    public async Task QueueUp_DefaultCreatedAt_FilledByStorage()
    {
        await using var engine = await OpenEngineAsync();
        var entry = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(createdAt: null));

        Assert.NotEqual(default, entry.CreatedAt);
    }

    [Fact]
    public async Task QueueUp_UpdateStatus_CompletesOnlyQueued_AndKeepsHistory()
    {
        await using var engine = await OpenEngineAsync();
        var entry = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry());

        var completed = await engine.UpdateQueueUpEntryStatusAsync(
            entry.Id, Erbai.Contracts.QueueUp.QueueUpStatus.Completed);
        Assert.NotNull(completed);
        Assert.Equal(Erbai.Contracts.QueueUp.QueueUpStatus.Completed, completed.Status);

        // 幂等：已完成条目不能再次流转（防重复出队）
        Assert.Null(await engine.UpdateQueueUpEntryStatusAsync(
            entry.Id, Erbai.Contracts.QueueUp.QueueUpStatus.Cancelled));

        // 默认列表只取 queued；显式按状态可取历史
        Assert.Empty(await engine.ListQueueUpEntriesAsync());
        Assert.Single(await engine.ListQueueUpEntriesAsync(Erbai.Contracts.QueueUp.QueueUpStatus.Completed));
    }

    [Fact]
    public async Task QueueUp_UpdateContent_ReplacesOnlyQueued()
    {
        await using var engine = await OpenEngineAsync();
        var entry = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(content: "旧内容"));

        var updated = await engine.UpdateQueueUpEntryContentAsync(entry.Id, "新内容");
        Assert.NotNull(updated);
        Assert.Equal("新内容", updated.Content);

        // 已出队条目内容不可改
        await engine.UpdateQueueUpEntryStatusAsync(entry.Id, Erbai.Contracts.QueueUp.QueueUpStatus.Completed);
        Assert.Null(await engine.UpdateQueueUpEntryContentAsync(entry.Id, "再改"));

        // 不存在的条目返回 null
        Assert.Null(await engine.UpdateQueueUpEntryContentAsync(99999, "x"));
        Assert.Null(await engine.UpdateQueueUpEntryStatusAsync(99999, Erbai.Contracts.QueueUp.QueueUpStatus.Cancelled));
    }

    [Fact]
    public async Task QueueUp_GiftEntry_PersistsSource()
    {
        await using var engine = await OpenEngineAsync();
        var entry = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry() with
        {
            Source = Erbai.Contracts.QueueUp.QueueUpSource.Gift,
            Content = "",
        });

        var loaded = Assert.Single(await engine.ListQueueUpEntriesAsync());
        Assert.Equal(Erbai.Contracts.QueueUp.QueueUpSource.Gift, loaded.Source);
        Assert.Equal("", loaded.Content);
    }

    // ---- queueup 历史分页查询 ----

    [Fact]
    public async Task QueueUp_HistoryPage_DefaultsToTerminalStates_DescendingPaginated()
    {
        await using var engine = await OpenEngineAsync();
        var e1 = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(userId: "u1", createdAt: "2026-08-25T10:00:00+00:00"));
        var e2 = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(userId: "u2", createdAt: "2026-08-25T11:00:00+00:00"));
        var e3 = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(userId: "u3", createdAt: "2026-08-25T12:00:00+00:00"));
        await engine.UpdateQueueUpEntryStatusAsync(e1.Id, Erbai.Contracts.QueueUp.QueueUpStatus.Completed);
        await engine.UpdateQueueUpEntryStatusAsync(e2.Id, Erbai.Contracts.QueueUp.QueueUpStatus.Cancelled);
        await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(userId: "u4", createdAt: "2026-08-25T13:00:00+00:00")); // 仍 queued，不入历史

        // 默认：全部终态，倒序（最新的 e2 在前）
        var (rows, total) = await engine.ListQueueUpHistoryPageAsync();
        Assert.Equal(2, total);
        Assert.Equal(2, rows.Count);
        Assert.Equal("u2", rows[0].UserId);
        Assert.Equal(Erbai.Contracts.QueueUp.QueueUpStatus.Cancelled, rows[0].Status);
        Assert.Equal("u1", rows[1].UserId);

        // 分页：limit=1 offset=1 取第二条；总数不受 limit 影响
        var (page2, total2) = await engine.ListQueueUpHistoryPageAsync(limit: 1, offset: 1);
        Assert.Equal(2, total2);
        Assert.Single(page2);
        Assert.Equal("u1", page2[0].UserId);

        // offset 超界返回空行但总数保留
        var (empty, total3) = await engine.ListQueueUpHistoryPageAsync(offset: 99);
        Assert.Empty(empty);
        Assert.Equal(2, total3);

        // ascending = true 时最早在前
        var (asc, _) = await engine.ListQueueUpHistoryPageAsync(descending: false);
        Assert.Equal("u1", asc[0].UserId);
    }

    [Fact]
    public async Task QueueUp_HistoryPage_SingleTerminalStateFilter()
    {
        await using var engine = await OpenEngineAsync();
        var e1 = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(userId: "u1"));
        var e2 = await engine.InsertQueueUpEntryAsync(NewQueueUpEntry(userId: "u2"));
        await engine.UpdateQueueUpEntryStatusAsync(e1.Id, Erbai.Contracts.QueueUp.QueueUpStatus.Completed);
        await engine.UpdateQueueUpEntryStatusAsync(e2.Id, Erbai.Contracts.QueueUp.QueueUpStatus.Cancelled);

        var (completed, completedTotal) = await engine.ListQueueUpHistoryPageAsync(
            status: Erbai.Contracts.QueueUp.QueueUpStatus.Completed);
        Assert.Equal(1, completedTotal);
        Assert.Equal("u1", Assert.Single(completed).UserId);

        var (cancelled, cancelledTotal) = await engine.ListQueueUpHistoryPageAsync(
            status: Erbai.Contracts.QueueUp.QueueUpStatus.Cancelled);
        Assert.Equal(1, cancelledTotal);
        Assert.Equal("u2", Assert.Single(cancelled).UserId);
    }

    [Fact]
    public async Task QueueUp_HistoryPage_RejectsNonTerminalStatus()
    {
        await using var engine = await OpenEngineAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await engine.ListQueueUpHistoryPageAsync(
                status: Erbai.Contracts.QueueUp.QueueUpStatus.Queued));
    }
}
