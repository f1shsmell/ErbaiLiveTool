using Erbai.Contracts.Requests;
using Erbai.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Erbai.Core.Tests;

/// <summary>
/// 存储引擎（行为域：存储 CAS/序号/备份/写回滚）。
/// 并发契约见 docs/03 §3.2：序号在 BEGIN IMMEDIATE 内原子分配、CAS 更新、
/// queue_events 审计流、写失败自动回滚。
/// </summary>
public class StorageEngineTests
{
    private static string TempDb() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "test.db");

    private static SongRequest NewRequest(string songName = "测试歌曲", string? platform = "bilibili") =>
        new()
        {
            Platform = platform!,
            RoomId = "1017",
            UserId = "10001",
            Nickname = "观众",
            SongName = songName,
            Singer = "歌手",
            CanonicalSongKey = "ceshigequ-geshou",
            Status = RequestStatus.Queued,
        };

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static object? QueryScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public async Task Open_CreatesFile_WithSchemaVersion1()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        Assert.True(File.Exists(path));
        using var connection = OpenRaw(path);
        Assert.Equal("1", QueryScalar(connection, "SELECT value FROM schema_meta WHERE key = 'schema_version'"));

        // 全部 v1 表已建
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Contains("song_requests", tables);
        Assert.Contains("users", tables);
        Assert.Contains("queue_events", tables);
        Assert.Contains("banned_users", tables);
        Assert.Contains("banned_songs", tables);
        Assert.Contains("idle_playlist_songs", tables);
        Assert.Contains("queueup_entries", tables);
        Assert.Contains("schema_meta", tables);
    }

    [Fact]
    public async Task InsertRequest_AssignsSequencesAtomically_Incrementing()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var first = await engine.InsertRequestAsync(NewRequest("第一首"));
        var second = await engine.InsertRequestAsync(NewRequest("第二首"));
        var third = await engine.InsertRequestAsync(NewRequest("第三首"));

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(3, third.Sequence);
        Assert.NotNull(first.RequestId);
        Assert.True(second.RequestId > first.RequestId);
    }

    [Fact]
    public async Task InsertRequest_ExplicitSequence_IsRespected_AndNextAutoContinues()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var explicitOne = NewRequest() with { Sequence = 100 };
        await engine.InsertRequestAsync(explicitOne);
        var auto = await engine.InsertRequestAsync(NewRequest());

        Assert.Equal(100, explicitOne.Sequence);
        Assert.Equal(101, auto.Sequence);
    }

    [Fact]
    public async Task UpdateRequest_CasSuccess_ChangesStatusAndWritesAudit()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var inserted = await engine.InsertRequestAsync(NewRequest());
        var updated = await engine.UpdateRequestAsync(
            inserted.RequestId!.Value,
            status: RequestStatus.Searching,
            expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Queued });

        Assert.NotNull(updated);
        Assert.Equal(RequestStatus.Searching, updated!.Status);

        // 审计流
        using var connection = OpenRaw(path);
        Assert.Equal(1L, QueryScalar(connection, "SELECT COUNT(*) FROM queue_events"));
        Assert.Equal("status_changed",
            QueryScalar(connection, "SELECT event_type FROM queue_events WHERE request_id = " + inserted.RequestId));
    }

    [Fact]
    public async Task UpdateRequest_CasMismatch_ReturnsNull_LeavesState()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var inserted = await engine.InsertRequestAsync(NewRequest());
        var result = await engine.UpdateRequestAsync(
            inserted.RequestId!.Value,
            status: RequestStatus.Searching,
            expectedStatuses: new HashSet<RequestStatus> { RequestStatus.Received });

        Assert.Null(result);
        Assert.Equal(RequestStatus.Queued, await engine.GetRequestStatusAsync(inserted.RequestId!.Value));
        Assert.Equal(0L, QueryScalar(OpenRaw(path), "SELECT COUNT(*) FROM queue_events"));
    }

    [Fact]
    public async Task UpdateRequest_IllegalTransition_ReturnsNull()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var inserted = await engine.InsertRequestAsync(NewRequest());
        // queued -> completed 非法
        var result = await engine.UpdateRequestAsync(inserted.RequestId!.Value, status: RequestStatus.Completed);

        Assert.Null(result);
        Assert.Equal(RequestStatus.Queued, await engine.GetRequestStatusAsync(inserted.RequestId!.Value));
    }

    [Fact]
    public async Task UpdateRequest_SameStatus_IsAllowed()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var inserted = await engine.InsertRequestAsync(NewRequest());
        var result = await engine.UpdateRequestAsync(inserted.RequestId!.Value, status: RequestStatus.Queued);

        Assert.NotNull(result);
        // 同状态不产生审计事件
        Assert.Equal(0L, QueryScalar(OpenRaw(path), "SELECT COUNT(*) FROM queue_events"));
    }

    [Fact]
    public async Task UpdateRequest_MissingRow_ReturnsNull()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var result = await engine.UpdateRequestAsync(999999, status: RequestStatus.Searching);
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateRequest_PartialFields_AreWritten()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var inserted = await engine.InsertRequestAsync(NewRequest());
        var updated = await engine.UpdateRequestAsync(
            inserted.RequestId!.Value,
            failureReason: "song_not_found",
            searchResultJson: """{"candidates":[]}""");

        Assert.Equal("song_not_found", updated!.FailureReason);
        Assert.Equal("""{"candidates":[]}""", updated.SearchResultJson);
        Assert.Equal(RequestStatus.Queued, updated.Status); // 状态未动
    }

    [Fact]
    public async Task FailedWrite_RollsBack_ConnectionStaysUsable()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        // 参数校验失败触发写失败（platform 为 null → 事务内抛异常）
        var bad = NewRequest() with { Platform = null! };
        await Assert.ThrowsAsync<ArgumentException>(() => engine.InsertRequestAsync(bad));

        // 半截事务已回滚：后续操作不报 "cannot start a transaction within a transaction"
        var good = await engine.InsertRequestAsync(NewRequest());
        Assert.Equal(1, good.Sequence);
        Assert.Equal(1L, QueryScalar(OpenRaw(path), "SELECT COUNT(*) FROM song_requests"));
    }

    [Fact]
    public async Task BackupTo_CreatesConsistentSnapshot()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();
        await engine.InsertRequestAsync(NewRequest());
        await engine.InsertRequestAsync(NewRequest());

        var backupPath = Path.Combine(Path.GetDirectoryName(path)!, "backup.db");
        var result = await engine.BackupToAsync(backupPath);

        Assert.Equal(Path.GetFullPath(backupPath), result);
        Assert.True(File.Exists(backupPath));
        Assert.Equal(2L, QueryScalar(OpenRaw(backupPath), "SELECT COUNT(*) FROM song_requests"));
    }

    [Fact]
    public async Task BackupTo_SamePath_Throws()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => engine.BackupToAsync(path));
    }

    [Fact]
    public async Task Queries_HotPaths_Work()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var r1 = await engine.InsertRequestAsync(NewRequest("甲"));
        var r2 = await engine.InsertRequestAsync(NewRequest("乙"));
        var r3 = await engine.InsertRequestAsync(NewRequest("丙"));
        await engine.UpdateRequestAsync(r1.RequestId!.Value, status: RequestStatus.Skipped);
        await engine.UpdateRequestAsync(r3.RequestId!.Value, status: RequestStatus.Searching);

        // 定向状态查询
        Assert.Equal(RequestStatus.Skipped, await engine.GetRequestStatusAsync(r1.RequestId!.Value));
        Assert.Equal(RequestStatus.Searching, await engine.GetRequestStatusAsync(r3.RequestId!.Value));

        // 聚合
        var counts = await engine.CountRequestsByStatusAsync();
        Assert.Equal(1, counts[RequestStatus.Queued]);
        Assert.Equal(1, counts[RequestStatus.Skipped]);
        Assert.Equal(1, counts[RequestStatus.Searching]);

        // 状态过滤（r2 仍是 queued，属活动状态）
        var active = await engine.ListRequestsAsync(RequestStatuses.Active);
        Assert.Equal(new[] { r2.RequestId, r3.RequestId }, active.Select(r => r.RequestId).ToArray());

        // 分页 desc
        var (rows, total) = await engine.ListRequestsPageAsync(descending: true, limit: 2, offset: 0);
        Assert.Equal(3, total);
        Assert.Equal(2, rows.Count);
        Assert.Equal(r3.RequestId, rows[0].RequestId); // sequence 3 在前

        // 分页 + 平台过滤
        var (biliRows, biliTotal) = await engine.ListRequestsPageAsync(platform: "bilibili", limit: 10);
        Assert.Equal(3, biliTotal);
        Assert.Equal(3, biliRows.Count);
    }

    [Fact]
    public async Task CanonicalExists_OnlyChecksActiveStatuses()
    {
        var path = TempDb();
        await using var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();

        var request = await engine.InsertRequestAsync(NewRequest());
        Assert.True(await engine.CanonicalExistsAsync(request.CanonicalSongKey));

        // queued -> skipped 是合法流转；skipped 为终态，不再参与去重
        await engine.UpdateRequestAsync(request.RequestId!.Value, status: RequestStatus.Skipped);
        Assert.False(await engine.CanonicalExistsAsync(request.CanonicalSongKey));

        Assert.False(await engine.CanonicalExistsAsync(""));
    }

    [Fact]
    public async Task ClosedEngine_RejectsOperations()
    {
        var path = TempDb();
        var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();
        await engine.CloseAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.InsertRequestAsync(NewRequest()));
    }

    [Fact]
    public async Task NewerSchemaVersion_Throws()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // 构造一个 schema_version=99 的库
        using (var connection = OpenRaw(path))
        {
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
            create.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO schema_meta(key, value) VALUES ('schema_version', '99')";
            insert.ExecuteNonQuery();
        }

        await using var engine = new SqliteStorageEngine(path);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenAsync());
        Assert.Contains("newer than supported", ex.Message);
    }
}
