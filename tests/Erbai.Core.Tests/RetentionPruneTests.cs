using Erbai.Contracts.Requests;
using Erbai.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Erbai.Core.Tests;

/// <summary>
/// 历史数据保留清理（2026-10 长稳轮）：超期 queue_events 与终态 song_requests
/// 被删；活动请求与窗口内数据保留；仍被事件引用的请求跳过（外键安全）。
/// 背景：长期直播下 queue_events（每请求 ~3-5 行）与终态请求无限增长，
/// 拖慢历史页/备份。审计事件的 created_at 是事件发生时刻（公共 API 不回填），
/// 超期事件用原生 SQL 构造。
/// </summary>
public class RetentionPruneTests
{
    private static string TempDb() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "test.db");

    private static SongRequest NewRequest(string songName, RequestStatus status, DateTimeOffset createdAt) =>
        new()
        {
            Platform = "douyin",
            RoomId = "1",
            UserId = "u1",
            Nickname = "观众",
            SongName = songName,
            Singer = "歌手",
            Status = status,
            CreatedAt = createdAt,
        };

    private static void BackdateEvents(string dbPath, long requestId, string createdAtIso)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE queue_events SET created_at = $iso WHERE request_id = $id";
        command.Parameters.AddWithValue("$iso", createdAtIso);
        command.Parameters.AddWithValue("$id", requestId);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Prune_删除超期终态与事件_保留活动与窗口内()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var now = DateTimeOffset.UtcNow;
        var old = now - TimeSpan.FromDays(30);
        var recent = now - TimeSpan.FromDays(2);

        // 超期终态（应删）：状态机合法路径 dispatched→completed 产生审计事件
        var oldCompleted = await engine.InsertRequestAsync(NewRequest("老歌-完成", RequestStatus.Dispatched, old));
        Assert.NotNull(oldCompleted);
        Assert.NotNull(await engine.UpdateRequestAsync(oldCompleted.RequestId!.Value, status: RequestStatus.Completed));
        BackdateEvents(path, oldCompleted.RequestId!.Value,
            old.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'")); // 事件一并超期

        // 超期拒绝（终态、无事件，应删）
        _ = await engine.InsertRequestAsync(NewRequest("老歌-拒绝", RequestStatus.Rejected, old));
        // 超期但活动（永不删）
        _ = await engine.InsertRequestAsync(NewRequest("老歌-排队中", RequestStatus.Queued, old));
        // 窗口内终态（保留）
        _ = await engine.InsertRequestAsync(NewRequest("新歌-完成", RequestStatus.Completed, recent));

        var (eventsDeleted, requestsDeleted) = await engine.PruneHistoryAsync(now - TimeSpan.FromDays(14));

        Assert.True(eventsDeleted >= 1, $"应删除超期审计事件，实际 {eventsDeleted}");
        Assert.Equal(2, requestsDeleted); // 老-完成（事件已删后放行）+ 老-拒绝

        var remaining = await engine.ListRequestsAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, r => r.SongName == "老歌-排队中");
        Assert.Contains(remaining, r => r.SongName == "新歌-完成");
    }

    [Fact]
    public async Task Prune_无匹配时返回零()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var (eventsDeleted, requestsDeleted) = await engine.PruneHistoryAsync(DateTimeOffset.UtcNow);
        Assert.Equal(0, eventsDeleted);
        Assert.Equal(0, requestsDeleted);
    }

    [Fact]
    public async Task Prune_事件仍在窗口内的超期终态请求跳过()
    {
        // 请求 20 天前创建、今天才流转终态（长生命周期/主播补录场景）：其审计事件
        // 在窗口内 → NOT EXISTS 拦下请求删除，外键不破；事件也不删（未超期）
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var now = DateTimeOffset.UtcNow;
        var request = await engine.InsertRequestAsync(
            NewRequest("跨窗歌", RequestStatus.Dispatched, now - TimeSpan.FromDays(20)));
        Assert.NotNull(request);
        Assert.NotNull(await engine.UpdateRequestAsync(request.RequestId!.Value, status: RequestStatus.Completed));

        var (eventsDeleted, requestsDeleted) = await engine.PruneHistoryAsync(now - TimeSpan.FromDays(14));

        Assert.Equal(0, eventsDeleted); // 事件在窗口内不删
        Assert.Equal(0, requestsDeleted); // 请求被事件引用 → 跳过
        Assert.Single(await engine.ListRequestsAsync());
    }
}
