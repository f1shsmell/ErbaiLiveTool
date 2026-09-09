using System.Text.Json;
using Erbai.Contracts.Live;
using Erbai.Core.Storage;
using Erbai.Live.Douyin.Messages;
using Erbai.Live.Douyin.Services;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Live.Douyin.Tests;

/// <summary>DouyinUserSync：粉丝团等级 LRU 补查 + 显式标志合并持久化（docs/04 §3.2 业务侧）。</summary>
public class DouyinUserSyncTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"douyin-usersync-{Guid.NewGuid():N}.db");
    private SqliteStorageEngine _store = null!;
    private UserService _users = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteStorageEngine(_dbPath);
        await _store.OpenAsync();
        _users = new UserService(_store);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private static LiveEvent Danmaku(long userId, string roomId = "1", int? fanLevel = null,
        bool? isAdmin = null, bool? isAnchor = null, string? rawUser = null)
    {
        var raw = rawUser ?? "{}";
        var rawJson = JsonSerializer.Serialize(new
        {
            Type = 1,
            Data = new { Content = "点歌", WebRoomId = roomId, User = JsonDocument.Parse(raw).RootElement },
        });
        return new LiveEvent
        {
            Platform = "douyin",
            RoomId = roomId,
            Kind = LiveEventKind.Danmaku,
            UserId = userId,
            Nickname = "观众",
            Text = "点歌",
            IsAdmin = isAdmin,
            IsAnchor = isAnchor,
            FanLevel = fanLevel,
            Timestamp = DateTimeOffset.UtcNow,
            RawJson = rawJson,
        };
    }

    private static LiveEvent Subscribe(long userId, int fanLevel, string roomId = "1") => new()
    {
        Platform = "douyin",
        RoomId = roomId,
        Kind = LiveEventKind.Subscribe,
        UserId = userId,
        Nickname = "粉丝",
        FanLevel = fanLevel,
        Timestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task 粉丝团事件持久化等级()
    {
        await DouyinUserSync.HandleAsync(_users, Subscribe(1001, 9));
        var stored = await _store.GetUserAsync("douyin", "1", "1001");
        Assert.NotNull(stored);
        Assert.Equal(9, stored!.FanLevel);
    }

    [Fact]
    public async Task 弹幕缺等级从LRU补查()
    {
        // 先粉丝团事件（进 LRU + 持久化等级 5）
        await DouyinUserSync.HandleAsync(_users, Subscribe(2002, 5));
        // 再普通弹幕（无等级）→ 从 LRU 补 5
        await DouyinUserSync.HandleAsync(_users, Danmaku(2002, fanLevel: null));
        var stored = await _store.GetUserAsync("douyin", "1", "2002");
        Assert.NotNull(stored);
        Assert.Equal(5, stored!.FanLevel);
    }

    [Fact]
    public async Task 弹幕自带等级直接持久化()
    {
        await DouyinUserSync.HandleAsync(_users, Danmaku(3003, fanLevel: 7));
        var stored = await _store.GetUserAsync("douyin", "1", "3003");
        Assert.NotNull(stored);
        Assert.Equal(7, stored!.FanLevel);
    }

    [Fact]
    public async Task 无显式标志不得降级已持久化管理员()
    {
        // 先以管理员身份保存（显式 IsAdmin=true）
        await DouyinUserSync.HandleAsync(_users, Danmaku(4004, isAdmin: true));
        Assert.True((await _store.GetUserAsync("douyin", "1", "4004"))!.IsAdmin);

        // 普通弹幕（IsAdmin=false，无任何身份键）→ 不降级
        await DouyinUserSync.HandleAsync(_users, Danmaku(4004, isAdmin: false, rawUser: """{"Nickname":"观众"}"""));
        var stored = await _store.GetUserAsync("douyin", "1", "4004");
        Assert.NotNull(stored);
        Assert.True(stored!.IsAdmin);
    }

    [Fact]
    public async Task 显式假标志不得覆盖持久化管理员()
    {
        // 先持久化管理员
        await DouyinUserSync.HandleAsync(_users, Danmaku(5005, isAdmin: true));

        // 带显式否定标志（IsModerator:"0" 是"显式"但值为假）→ 不覆盖
        await DouyinUserSync.HandleAsync(_users, Danmaku(5005, isAdmin: false, rawUser: """{"IsModerator":"0"}"""));
        Assert.True((await _store.GetUserAsync("douyin", "1", "5005"))!.IsAdmin);
    }

    [Fact]
    public async Task 主播role不升格管理员()
    {
        await DouyinUserSync.HandleAsync(_users, Danmaku(6006, isAnchor: true, rawUser: """{"role":"主播"}"""));
        var stored = await _store.GetUserAsync("douyin", "1", "6006");
        Assert.NotNull(stored);
        Assert.True(stored!.IsAnchor);
        Assert.False(stored.IsAdmin);
    }

    [Fact]
    public async Task 未识别用户跳过持久化()
    {
        await DouyinUserSync.HandleAsync(_users, Danmaku(0, rawUser: """{"SecUid":"abc"}"""));
        var stored = await _store.GetUserAsync("douyin", "1", "0");
        Assert.Null(stored); // userId=0 无持久化键，不落库也不抛异常
    }

    [Fact]
    public async Task Data顶层身份键消息持久化特权()
    {
        // #9：显式标志扫描面必须与解析器一致（Data 顶层 + Data.User 两层）。
        // 顶层 IsModerator=true（无 User 键）→ 解析器识别 admin → 持久化 admin。
        var rawJson = JsonSerializer.Serialize(new
        {
            Type = 1,
            Data = new
            {
                Content = "点歌",
                WebRoomId = "1",
                IsModerator = true, // 顶层身份键（部分 Grabber 变体形态）
                User = new { Nickname = "观众", Id = "7007" },
            },
        });
        await DouyinUserSync.HandleAsync(_users, new LiveEvent
        {
            Platform = "douyin",
            RoomId = "1",
            Kind = LiveEventKind.Danmaku,
            UserId = 7007,
            Nickname = "观众",
            Text = "点歌",
            IsAdmin = true,
            Timestamp = DateTimeOffset.UtcNow,
            RawJson = rawJson,
        });

        var stored = await _store.GetUserAsync("douyin", "1", "7007");
        Assert.NotNull(stored);
        Assert.True(stored!.IsAdmin);
    }
}
