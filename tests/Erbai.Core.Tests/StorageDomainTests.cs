using Erbai.Contracts.Storage;
using Erbai.Core.Storage;

namespace Erbai.Core.Tests;

/// <summary>
/// 存储域补齐（阶段 2 里程碑 1）：users / banned_users / banned_songs /
/// playlists 语义（含 v1→v2 迁移） + SqliteExecutor 关闭竞态修复。
/// 行为对齐（test_save_user_writes_privileges_*、
/// test_list_users_filters_*、test_set_user_admin_*）与 –§3。
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

    /// <summary>
    /// 跨房间特权 MAX：同一 (platform, user_id) 多房间行取 MAX。
    /// 复刻实测形态——特权行在真实房间号下，另有一条 RoomId 缺失（''）的普通行。
    /// </summary>
    [Fact]
    public async Task GetUserPrivilege_TakesMaxAcrossRooms()
    {
        await using var engine = await OpenEngineAsync();
        await engine.SaveUserAsync(NewUser(roomId: "54380982833", userId: "u9", isAdmin: true, isAnchor: true));
        await engine.SaveUserAsync(NewUser(roomId: "", userId: "u9"));

        var (isAdmin, isAnchor) = await engine.GetUserPrivilegeAsync("douyin", "u9");

        Assert.True(isAdmin);
        Assert.True(isAnchor);
    }

    [Fact]
    public async Task GetUserPrivilege_NoRecordOrEmptyUserId_ReturnsFalse()
    {
        await using var engine = await OpenEngineAsync();
        Assert.Equal((false, false), await engine.GetUserPrivilegeAsync("douyin", "nobody"));
        Assert.Equal((false, false), await engine.GetUserPrivilegeAsync("douyin", ""));
    }

    [Fact]
    public async Task GetUserPrivilege_DoesNotLeakAcrossPlatforms()
    {
        await using var engine = await OpenEngineAsync();
        await engine.SaveUserAsync(NewUser(platform: "bilibili", userId: "u9", isAdmin: true));

        Assert.Equal((false, false), await engine.GetUserPrivilegeAsync("douyin", "u9"));
    }

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
    public async Task Playlists_CreateAddList_KeepsOrder_DropsEmptyNames()
    {
        await using var engine = await OpenEngineAsync();
        var playlist = await engine.CreatePlaylistAsync(" 我的歌单 ");
        Assert.Equal("我的歌单", playlist.Name);
        Assert.Equal(PlaylistSource.Local, playlist.Source);
        Assert.False(playlist.IsIdle);

        await engine.AddPlaylistSongAsync(playlist.Id, "晴天", "周杰伦");
        await engine.AddPlaylistSongAsync(playlist.Id, "海阔天空", "Beyond", searchJson: "[{\"source\":\"netease\"}]");
        // 批量路径空歌名剔除
        await engine.AddPlaylistSongsAsync(playlist.Id,
            new[] { ("  ", "x", (string?)null), ("光辉岁月", "", (string?)null) });

        var songs = await engine.ListPlaylistSongsAsync(playlist.Id);
        Assert.Equal(3, songs.Count);
        Assert.Equal("晴天", songs[0].Name);
        Assert.Equal("周杰伦", songs[0].Singer);
        Assert.Equal(0, songs[0].Position); // position 0 起始（沿用旧 idle_playlist_songs 约定）
        Assert.Equal("Beyond", songs[1].Singer);
        Assert.NotNull(songs[1].SearchJson); // 导入候选快照原样落库
        Assert.Equal("光辉岁月", songs[2].Name);

        var reloaded = Assert.Single(await engine.ListPlaylistsAsync());
        Assert.Equal(3, reloaded.SongCount);
    }

    [Fact]
    public async Task Playlists_AddSong_EmptyName_Throws()
    {
        await using var engine = await OpenEngineAsync();
        var playlist = await engine.CreatePlaylistAsync("空名");
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.AddPlaylistSongAsync(playlist.Id, "   "));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.CreatePlaylistAsync("  "));
    }

    [Fact]
    public async Task Playlists_SetIdle_IsExclusive_SwitchingAndClearing()
    {
        await using var engine = await OpenEngineAsync();
        var a = await engine.CreatePlaylistAsync("A");
        var b = await engine.CreatePlaylistAsync("B");

        var marked = await engine.SetIdlePlaylistAsync(a.Id);
        Assert.NotNull(marked);
        Assert.True(marked.IsIdle);
        Assert.Equal("A", (await engine.GetIdlePlaylistAsync())!.Name);

        // 标记 B 自动取消 A（唯一空闲播放源）
        await engine.SetIdlePlaylistAsync(b.Id);
        Assert.Equal("B", (await engine.GetIdlePlaylistAsync())!.Name);
        Assert.False((await engine.GetPlaylistAsync(a.Id))!.IsIdle);

        // 取消标记（null）→ 无空闲播放源
        await engine.SetIdlePlaylistAsync(null);
        Assert.Null(await engine.GetIdlePlaylistAsync());

        // 不存在的 id：返回 null 且旧标记保持
        await engine.SetIdlePlaylistAsync(a.Id);
        Assert.Null(await engine.SetIdlePlaylistAsync(99999));
        Assert.True((await engine.GetPlaylistAsync(a.Id))!.IsIdle);
    }

    [Fact]
    public async Task Playlists_DeletePlaylist_CascadesSongs_AndClearsIdleMark()
    {
        await using var engine = await OpenEngineAsync();
        var playlist = await engine.CreatePlaylistAsync("待删");
        await engine.AddPlaylistSongAsync(playlist.Id, "晴天");
        await engine.SetIdlePlaylistAsync(playlist.Id);

        Assert.True(await engine.DeletePlaylistAsync(playlist.Id));
        Assert.False(await engine.DeletePlaylistAsync(playlist.Id)); // 幂等
        Assert.Empty(await engine.ListPlaylistSongsAsync(playlist.Id));
        Assert.Empty(await engine.ListPlaylistsAsync());
        Assert.Null(await engine.GetIdlePlaylistAsync()); // 被删歌单是空闲源 → 标记随之消失
    }

    [Fact]
    public async Task Playlists_Rename_UpdatesName()
    {
        await using var engine = await OpenEngineAsync();
        var playlist = await engine.CreatePlaylistAsync("旧名");

        var renamed = await engine.RenamePlaylistAsync(playlist.Id, " 新名 ");
        Assert.NotNull(renamed);
        Assert.Equal("新名", renamed.Name);

        Assert.Null(await engine.RenamePlaylistAsync(99999, "随便"));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.RenamePlaylistAsync(playlist.Id, "  "));
    }

    [Fact]
    public async Task Playlists_MoveSong_SwapsNeighbors_Bounded()
    {
        await using var engine = await OpenEngineAsync();
        var playlist = await engine.CreatePlaylistAsync("排序");
        var a = await engine.AddPlaylistSongAsync(playlist.Id, "A");
        var b = await engine.AddPlaylistSongAsync(playlist.Id, "B");
        var c = await engine.AddPlaylistSongAsync(playlist.Id, "C");

        // 上移 B：B A C
        await engine.MovePlaylistSongAsync(playlist.Id, b.Id, -1);
        var songs = await engine.ListPlaylistSongsAsync(playlist.Id);
        Assert.Equal(["B", "A", "C"], songs.Select(s => s.Name).ToArray());

        // 上移首个 / 下移末个：越界无操作
        await engine.MovePlaylistSongAsync(playlist.Id, b.Id, -1);
        await engine.MovePlaylistSongAsync(playlist.Id, c.Id, +1);
        songs = await engine.ListPlaylistSongsAsync(playlist.Id);
        Assert.Equal(["B", "A", "C"], songs.Select(s => s.Name).ToArray());

        // 下移 B：A B C
        await engine.MovePlaylistSongAsync(playlist.Id, b.Id, +1);
        songs = await engine.ListPlaylistSongsAsync(playlist.Id);
        Assert.Equal(["A", "B", "C"], songs.Select(s => s.Name).ToArray());

        // 不存在的歌单/歌曲：无操作不抛
        await engine.MovePlaylistSongAsync(99999, b.Id, -1);
        await engine.MovePlaylistSongAsync(playlist.Id, 99999, -1);
    }

    [Fact]
    public async Task Playlists_SetPlayMode_Persists()
    {
        await using var engine = await OpenEngineAsync();
        var playlist = await engine.CreatePlaylistAsync("模式");

        var shuffled = await engine.SetPlayModeAsync(playlist.Id, PlaylistPlayMode.Shuffle);
        Assert.NotNull(shuffled);
        Assert.Equal(PlaylistPlayMode.Shuffle, shuffled.PlayMode);

        // 重开引擎后仍在（落库）
        await engine.CloseAsync();
        await engine.OpenAsync();
        var reloaded = await engine.GetPlaylistAsync(playlist.Id);
        Assert.Equal(PlaylistPlayMode.Shuffle, reloaded!.PlayMode);
    }

    [Fact]
    public async Task Playlists_ImportedSourceLookup_FindBySourceAndSourceId()
    {
        await using var engine = await OpenEngineAsync();
        await engine.CreatePlaylistAsync("本机", PlaylistSource.Local, "123");

        var imported = await engine.CreatePlaylistAsync("网易云单", PlaylistSource.Netease, "7530558459");
        Assert.Equal(PlaylistSource.Netease, imported.Source);
        Assert.Equal("7530558459", imported.SourceId);

        var found = await engine.FindImportedPlaylistAsync(PlaylistSource.Netease, "7530558459");
        Assert.NotNull(found);
        Assert.Equal(imported.Id, found.Id);

        // 本机歌单不参与来源对账；其他 source 同 id 不误命中
        Assert.Null(await engine.FindImportedPlaylistAsync(PlaylistSource.Local, "123"));
        Assert.Null(await engine.FindImportedPlaylistAsync(PlaylistSource.Qqmusic, "7530558459"));
        Assert.Null(await engine.FindImportedPlaylistAsync(PlaylistSource.Netease, ""));
    }

    [Fact]
    public async Task Playlists_ClearSongs_KeepsPlaylist()
    {
        await using var engine = await OpenEngineAsync();
        var playlist = await engine.CreatePlaylistAsync("清空");
        await engine.AddPlaylistSongAsync(playlist.Id, "晴天");

        Assert.True(await engine.ClearPlaylistSongsAsync(playlist.Id));
        Assert.Empty(await engine.ListPlaylistSongsAsync(playlist.Id));
        Assert.Single(await engine.ListPlaylistsAsync());
        Assert.False(await engine.ClearPlaylistSongsAsync(99999));
    }

    // ---- v1→v2 迁移：独立空闲歌单收编为指定歌单 ----

    /// <summary>手工构造 v1 库（旧 schema：idle_playlist_songs + 版本标记 1）。</summary>
    private static string CreateV1Database()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO schema_meta(key, value) VALUES ('schema_version', '1');
            CREATE TABLE idle_playlist_songs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                singer TEXT NOT NULL DEFAULT '',
                position INTEGER NOT NULL
            );
            INSERT INTO idle_playlist_songs(name, singer, position) VALUES ('晴天', '周杰伦', 0);
            INSERT INTO idle_playlist_songs(name, singer, position) VALUES ('海阔天空', 'Beyond', 1);
            INSERT INTO idle_playlist_songs(name, singer, position) VALUES ('光辉岁月', 'Beyond', 2);
            """;
        command.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public async Task Migration_V1ToV2_CarriesIdleSongsIntoDefaultMarkedPlaylist()
    {
        var path = CreateV1Database();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync(); // 触发 v1→v2 迁移

        // 旧空闲歌单收编为默认歌单，自动标记为空闲播放源，顺序保持
        var idle = await engine.GetIdlePlaylistAsync();
        Assert.NotNull(idle);
        Assert.Equal("我的空闲歌单", idle.Name);
        Assert.True(idle.IsIdle);

        var songs = await engine.ListPlaylistSongsAsync(idle.Id);
        Assert.Equal(3, songs.Count);
        Assert.Equal(["晴天", "海阔天空", "光辉岁月"], songs.Select(s => s.Name).ToArray());
        Assert.Equal([0, 1, 2], songs.Select(s => s.Position).ToArray());

        // 旧表已删除；版本号已升级
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='idle_playlist_songs'";
            Assert.Equal(0L, tables.ExecuteScalar());
            using var version = connection.CreateCommand();
            version.CommandText = "SELECT value FROM schema_meta WHERE key='schema_version'";
            Assert.Equal("2", version.ExecuteScalar());
        }
    }

    [Fact]
    public async Task Migration_V1ToV2_EmptyIdleTable_StillCreatesDefaultPlaylist()
    {
        var path = CreateV1Database();
        // 清空曲目，模拟"配置过但没加歌"的老用户
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM idle_playlist_songs";
            command.ExecuteNonQuery();
        }

        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var idle = await engine.GetIdlePlaylistAsync();
        Assert.NotNull(idle); // 空表也建默认歌单并标记（保留"曾配置过"的状态）
        Assert.Empty(await engine.ListPlaylistSongsAsync(idle.Id));
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
