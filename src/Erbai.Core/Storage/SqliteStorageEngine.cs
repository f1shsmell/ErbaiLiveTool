using System.Globalization;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.QueueUp;
using Erbai.Contracts.Requests;
using Erbai.Contracts.Storage;
using Microsoft.Data.Sqlite;

namespace Erbai.Core.Storage;

/// <summary>
/// SQLite 存储引擎：WAL、专用线程串行化、BEGIN IMMEDIATE 内
/// MAX(sequence)+1 原子分配、CAS 更新（expected_statuses + 流转表校验）、
/// queue_events 状态审计流、schema_meta 版本化迁移框架、SQLite backup API
/// 一致性备份。v2：独立空闲歌单收编为多歌单（playlists/playlist_songs，
/// 指定其一为空闲播放源）。
/// </summary>
public sealed class SqliteStorageEngine : IStorageEngine
{
    public const int SchemaVersion = 2;

    /// <summary>
    /// 版本化迁移语句（未来版本在此追加）。迁移在大建表块之后执行——新表结构
    /// 由大建表块负责（IF NOT EXISTS 对新旧库都幂等），迁移只做数据收编。
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string[]> Migrations = new Dictionary<int, string[]>
    {
        // v1→v2：独立空闲歌单 → 多歌单（指定其一为空闲播放源）。存量
        // idle_playlist_songs 收编进默认歌单"我的空闲歌单"并自动标记 is_idle，
        // 老用户升级后空闲轮播行为不变；空表也建歌单（保留"曾配置过"的状态）。
        [2] =
        [
            """
            INSERT INTO playlists(name, source, source_id, is_idle, play_mode, created_at, updated_at)
            VALUES ('我的空闲歌单', 'local', '', 1, 'sequential',
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            """
            INSERT INTO playlist_songs(playlist_id, name, singer, position)
            SELECT (SELECT id FROM playlists WHERE is_idle = 1), name, singer, position
            FROM idle_playlist_songs ORDER BY position, id;
            """,
            "DROP TABLE idle_playlist_songs;",
        ],
    };

    private readonly string _path;
    private SqliteExecutor? _executor;
    private bool _opened;

    public SqliteStorageEngine(string databasePath)
    {
        _path = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => _path;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        if (_opened)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        await connection.OpenAsync(ct);
        try
        {
            await ExecuteCommandAsync(connection, "PRAGMA journal_mode=WAL", ct);
            await ExecuteCommandAsync(connection, "PRAGMA foreign_keys=ON", ct);
            await CreateSchemaAsync(connection, ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        _executor = new SqliteExecutor(connection);
        _opened = true;
    }

    public Task CloseAsync()
    {
        var executor = _executor;
        _executor = null;
        _opened = false;
        return executor?.StopAsync() ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    // ---- 备份（SQLite backup API 一致性快照，含 WAL）----

    public Task<string> BackupToAsync(string destination, CancellationToken ct = default)
    {
        var target = Path.GetFullPath(destination);
        if (string.Equals(target, _path, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("backup destination must differ from database path", nameof(destination));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return ExecuteAsync(connection =>
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = target,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            backup.Open();
            connection.BackupDatabase(backup);
            return target;
        }, ct);
    }

    // ---- song_requests 域 ----

    public Task<SongRequest> InsertRequestAsync(SongRequest request, CancellationToken ct = default) =>
        ExecuteAsync(connection => InsertRequestSync(connection, request), ct);

    public Task<SongRequest?> UpdateRequestAsync(
        long requestId,
        RequestStatus? status = null,
        string? failureReason = null,
        string? searchResultJson = null,
        string? canonicalSongKey = null,
        IReadOnlySet<RequestStatus>? expectedStatuses = null,
        CancellationToken ct = default) =>
        ExecuteAsync(
            connection => UpdateRequestSync(
                connection, requestId, status, failureReason, searchResultJson, canonicalSongKey, expectedStatuses),
            ct);

    public Task<SongRequest?> GetRequestAsync(long requestId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM song_requests WHERE id = $id";
            command.Parameters.AddWithValue("$id", requestId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRequest(reader) : null;
        }, ct);

    public Task<RequestStatus?> GetRequestStatusAsync(long requestId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM song_requests WHERE id = $id";
            command.Parameters.AddWithValue("$id", requestId);
            var value = command.ExecuteScalar();
            return value is null ? (RequestStatus?)null : Enum.Parse<RequestStatus>((string)value, ignoreCase: true);
        }, ct);

    public Task<IReadOnlyDictionary<RequestStatus, int>> CountRequestsByStatusAsync(CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status, COUNT(*) AS cnt FROM song_requests GROUP BY status";
            var result = new Dictionary<RequestStatus, int>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[Enum.Parse<RequestStatus>(reader.GetString(0), ignoreCase: true)] = reader.GetInt32(1);
            }

            return (IReadOnlyDictionary<RequestStatus, int>)result;
        }, ct);

    public Task<IReadOnlyList<SongRequest>> ListRequestsAsync(
        IReadOnlySet<RequestStatus>? statuses = null, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var list = new List<SongRequest>();
            using var command = connection.CreateCommand();
            if (statuses is { Count: > 0 })
            {
                var marks = string.Join(",", statuses.Select((_, i) => $"$s{i}"));
                command.CommandText = $"SELECT * FROM song_requests WHERE status IN ({marks}) ORDER BY sequence";
                var index = 0;
                foreach (var status in statuses)
                {
                    command.Parameters.AddWithValue($"$s{index++}", StatusToDb(status));
                }
            }
            else
            {
                command.CommandText = "SELECT * FROM song_requests ORDER BY sequence";
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadRequest(reader));
            }

            return (IReadOnlyList<SongRequest>)list;
        }, ct);

    public Task<(IReadOnlyList<SongRequest> Rows, int Total)> ListRequestsPageAsync(
        string? platform = null,
        RequestStatus? status = null,
        bool descending = true,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var where = new List<string>();
            var parameters = new List<(string Name, object Value)>();
            if (!string.IsNullOrEmpty(platform))
            {
                where.Add("platform = $platform");
                parameters.Add(("$platform", platform!));
            }

            if (status is not null)
            {
                where.Add("status = $status");
                parameters.Add(("$status", StatusToDb(status.Value)));
            }

            var clause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";
            var direction = descending ? "DESC" : "ASC";

            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = $"SELECT COUNT(*) AS c FROM song_requests {clause}";
            AddParameters(countCommand, parameters);
            var total = Convert.ToInt32(countCommand.ExecuteScalar());

            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT * FROM song_requests {clause} ORDER BY sequence {direction} LIMIT $limit OFFSET $offset";
            AddParameters(command, parameters);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);

            var rows = new List<SongRequest>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadRequest(reader));
            }

            return ((IReadOnlyList<SongRequest>)rows, total);
        }, ct);

    public Task<bool> CanonicalExistsAsync(string canonicalKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(canonicalKey))
        {
            return Task.FromResult(false);
        }

        return ExecuteAsync(connection =>
        {
            var active = RequestStatuses.Active.ToList();
            var marks = string.Join(",", active.Select((_, i) => $"$s{i}"));
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT 1 FROM song_requests WHERE canonical_song_key = $key AND status IN ({marks}) LIMIT 1";
            command.Parameters.AddWithValue("$key", canonicalKey);
            var index = 0;
            foreach (var status in active)
            {
                command.Parameters.AddWithValue($"$s{index++}", StatusToDb(status));
            }

            return command.ExecuteScalar() is not null;
        }, ct);
    }

    /// <summary>
    /// 窗口内提交计数（点歌频率限制， 检查序）：该
    /// (platform, room_id, user_id) 自 since 起状态非 rejected 的请求行数。
    /// 计入 failed（搜索也消耗资源），排除 rejected（被拒提交不重复惩罚）。
    /// created_at 为固定宽 ISO-8601 文本，字典序即时间序。
    /// </summary>
    public Task<int> CountRecentRequestsAsync(
        string platform, string roomId, string userId, DateTimeOffset since, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(userId))
        {
            return Task.FromResult(0);
        }

        return ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM song_requests
                WHERE platform = $platform AND room_id = $room AND user_id = $user
                  AND status != 'rejected' AND created_at >= $since
                """;
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$room", roomId ?? "");
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$since", FormatIso(since));
            return Convert.ToInt32(command.ExecuteScalar());
        }, ct);
    }

    /// <summary>
    /// 历史数据保留清理（2026-10 长稳轮）：单事务内先删超期 queue_events，再删
    /// 超期终态 song_requests——仍被未删事件引用的请求跳过（queue_events 对
    /// song_requests 有外键，直接删请求会违反约束）。活动请求（含 queued/
    /// searching/dispatched）永不清理。
    /// </summary>
    public Task<(int EventsDeleted, int RequestsDeleted)> PruneHistoryAsync(
        DateTimeOffset cutoff, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var cutoffIso = FormatIso(cutoff);
                var terminal = RequestStatuses.Terminal.ToList();
                var marks = string.Join(",", terminal.Select((_, i) => $"$t{i}"));

                int eventsDeleted;
                using (var eventsCommand = connection.CreateCommand())
                {
                    eventsCommand.CommandText = "DELETE FROM queue_events WHERE created_at < $cutoff";
                    eventsCommand.Parameters.AddWithValue("$cutoff", cutoffIso);
                    eventsDeleted = eventsCommand.ExecuteNonQuery();
                }

                int requestsDeleted;
                using (var requestsCommand = connection.CreateCommand())
                {
                    requestsCommand.CommandText = $"""
                        DELETE FROM song_requests
                        WHERE created_at < $cutoff
                          AND status IN ({marks})
                          AND NOT EXISTS (SELECT 1 FROM queue_events WHERE queue_events.request_id = song_requests.id)
                        """;
                    requestsCommand.Parameters.AddWithValue("$cutoff", cutoffIso);
                    var index = 0;
                    foreach (var status in terminal)
                    {
                        requestsCommand.Parameters.AddWithValue($"$t{index++}", StatusToDb(status));
                    }

                    requestsDeleted = requestsCommand.ExecuteNonQuery();
                }

                transaction.Commit();
                return (eventsDeleted, requestsDeleted);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, ct);

    // ---- 内部实现 ----

    private Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> action, CancellationToken ct)
    {
        var executor = _executor ?? throw new InvalidOperationException("storage engine is not open");
        return executor.ExecuteAsync(action, ct);
    }

    private static async Task ExecuteCommandAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS song_requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sequence INTEGER NOT NULL,
                platform TEXT NOT NULL,
                room_id TEXT NOT NULL DEFAULT '',
                user_id TEXT NOT NULL DEFAULT '',
                nickname TEXT NOT NULL DEFAULT '',
                is_admin INTEGER NOT NULL DEFAULT 0,
                is_anchor INTEGER NOT NULL DEFAULT 0,
                fan_level INTEGER,
                medal_level INTEGER,
                song_name TEXT NOT NULL,
                singer TEXT NOT NULL DEFAULT '',
                canonical_song_key TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL,
                failure_reason TEXT NOT NULL DEFAULT '',
                search_result_json TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_requests_sequence ON song_requests(sequence);
            CREATE INDEX IF NOT EXISTS idx_requests_status ON song_requests(status);
            CREATE INDEX IF NOT EXISTS idx_requests_user_status
                ON song_requests(platform, room_id, user_id, status);
            CREATE TABLE IF NOT EXISTS users (
                platform TEXT NOT NULL,
                room_id TEXT NOT NULL DEFAULT '',
                user_id TEXT NOT NULL,
                nickname TEXT NOT NULL DEFAULT '',
                fan_level INTEGER,
                medal_level INTEGER,
                is_admin INTEGER NOT NULL DEFAULT 0,
                is_anchor INTEGER NOT NULL DEFAULT 0,
                request_count INTEGER NOT NULL DEFAULT 0,
                last_request_at TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(platform, room_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS queue_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                request_id INTEGER,
                event_type TEXT NOT NULL,
                old_status TEXT,
                new_status TEXT,
                payload_json TEXT,
                created_at TEXT NOT NULL,
                FOREIGN KEY(request_id) REFERENCES song_requests(id)
            );
            CREATE TABLE IF NOT EXISTS banned_users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                platform TEXT NOT NULL,
                room_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                nickname TEXT NOT NULL,
                reason TEXT NOT NULL DEFAULT '',
                banned_by TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                UNIQUE (platform, room_id, user_id)
            );
            CREATE INDEX IF NOT EXISTS idx_banned_users_user ON banned_users(user_id);
            CREATE TABLE IF NOT EXISTS banned_songs (
                rule TEXT PRIMARY KEY
            );
            CREATE TABLE IF NOT EXISTS playlists (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'local',
                source_id TEXT NOT NULL DEFAULT '',
                is_idle INTEGER NOT NULL DEFAULT 0,
                play_mode TEXT NOT NULL DEFAULT 'sequential',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            -- 空闲播放源全局唯一（partial unique index；SetIdle 先清后标兜底）
            CREATE UNIQUE INDEX IF NOT EXISTS idx_playlists_idle
                ON playlists(is_idle) WHERE is_idle = 1;
            CREATE TABLE IF NOT EXISTS playlist_songs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                playlist_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                singer TEXT NOT NULL DEFAULT '',
                position INTEGER NOT NULL,
                search_json TEXT,
                FOREIGN KEY(playlist_id) REFERENCES playlists(id)
            );
            CREATE INDEX IF NOT EXISTS idx_playlist_songs ON playlist_songs(playlist_id, position);
            CREATE TABLE IF NOT EXISTS queueup_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id TEXT NOT NULL,
                nickname TEXT NOT NULL DEFAULT '',
                content TEXT NOT NULL DEFAULT '',
                source TEXT NOT NULL DEFAULT 'danmaku',
                status TEXT NOT NULL DEFAULT 'queued',
                created_at TEXT NOT NULL
            );
            -- queueup 状态过滤（启动恢复/历史页按 status 查询；2026-10 长稳轮补建，
            -- IF NOT EXISTS 对既有库下次打开自动补齐，无需迁移）
            CREATE INDEX IF NOT EXISTS idx_queueup_status ON queueup_entries(status);
            """;
        await command.ExecuteNonQueryAsync(ct);

        // schema_meta 版本化迁移框架（干净库 version=1；未来版本在此追加语句）
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT value FROM schema_meta WHERE key = 'schema_version'";
        var versionValue = await versionCommand.ExecuteScalarAsync(ct);
        var version = versionValue is null ? 1 : int.Parse((string)versionValue, CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"database schema version {version} is newer than supported {SchemaVersion}");
        }

        if (versionValue is null)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText =
                "INSERT INTO schema_meta(key, value) VALUES ('schema_version', $v)";
            insertCommand.Parameters.AddWithValue("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture));
            await insertCommand.ExecuteNonQueryAsync(ct);
        }
        else
        {
            while (version < SchemaVersion)
            {
                version += 1;
                if (Migrations.TryGetValue(version, out var statements))
                {
                    foreach (var statement in statements)
                    {
                        await using var migrateCommand = connection.CreateCommand();
                        migrateCommand.CommandText = statement;
                        await migrateCommand.ExecuteNonQueryAsync(ct);
                    }
                }

                await using var bumpVersionCommand = connection.CreateCommand();
                bumpVersionCommand.CommandText =
                    "UPDATE schema_meta SET value = $v WHERE key = 'schema_version'";
                bumpVersionCommand.Parameters.AddWithValue("$v", version.ToString(CultureInfo.InvariantCulture));
                await bumpVersionCommand.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static SongRequest InsertRequestSync(SqliteConnection connection, SongRequest request)
    {
        if (string.IsNullOrEmpty(request.Platform))
        {
            throw new ArgumentException("platform is required", nameof(request));
        }

        using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
        long requestId;
        try
        {
            long sequence;
            if (request.Sequence is null)
            {
                using var sequenceCommand = connection.CreateCommand();
                sequenceCommand.CommandText =
                    "SELECT COALESCE(MAX(sequence), 0) + 1 AS value FROM song_requests";
                sequence = Convert.ToInt64(sequenceCommand.ExecuteScalar());
            }
            else
            {
                sequence = request.Sequence.Value;
            }

            var now = UtcNowIso();
            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = """
                INSERT INTO song_requests(
                    sequence, platform, room_id, user_id, nickname,
                    is_admin, is_anchor, fan_level, medal_level, song_name, singer,
                    canonical_song_key, status, failure_reason,
                    search_result_json, created_at, updated_at
                ) VALUES (
                    $sequence, $platform, $roomId, $userId, $nickname,
                    $isAdmin, $isAnchor, $fanLevel, $medalLevel, $songName, $singer,
                    $canonicalKey, $status, $failureReason,
                    $searchResultJson, $createdAt, $updatedAt
                );
                """;
            insertCommand.Parameters.AddWithValue("$sequence", sequence);
            insertCommand.Parameters.AddWithValue("$platform", request.Platform);
            insertCommand.Parameters.AddWithValue("$roomId", request.RoomId);
            insertCommand.Parameters.AddWithValue("$userId", request.UserId);
            insertCommand.Parameters.AddWithValue("$nickname", request.Nickname);
            insertCommand.Parameters.AddWithValue("$isAdmin", request.IsAdmin ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$isAnchor", request.IsAnchor ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$fanLevel", (object?)request.FanLevel ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$medalLevel", (object?)request.MedalLevel ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$songName", request.SongName);
            insertCommand.Parameters.AddWithValue("$singer", request.Singer);
            insertCommand.Parameters.AddWithValue("$canonicalKey", request.CanonicalSongKey);
            insertCommand.Parameters.AddWithValue("$status", StatusToDb(request.Status));
            insertCommand.Parameters.AddWithValue("$failureReason", request.FailureReason);
            insertCommand.Parameters.AddWithValue("$searchResultJson", (object?)request.SearchResultJson ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$createdAt",
                request.CreatedAt == default ? now : FormatIso(request.CreatedAt));
            insertCommand.Parameters.AddWithValue("$updatedAt", now);

            insertCommand.ExecuteNonQuery();
            using (var lastIdCommand = connection.CreateCommand())
            {
                lastIdCommand.CommandText = "SELECT last_insert_rowid()";
                requestId = Convert.ToInt64(lastIdCommand.ExecuteScalar());
            }

            transaction.Commit();

            return request with
            {
                RequestId = requestId,
                Sequence = sequence,
                CreatedAt = request.CreatedAt == default ? ParseIso(now) : request.CreatedAt,
                UpdatedAt = ParseIso(now),
            };
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (SqliteException)
            {
            }

            throw;
        }
    }

    private static SongRequest? UpdateRequestSync(
        SqliteConnection connection,
        long requestId,
        RequestStatus? status,
        string? failureReason,
        string? searchResultJson,
        string? canonicalSongKey,
        IReadOnlySet<RequestStatus>? expectedStatuses)
    {
        RequestStatus oldStatus;
        using (var statusCommand = connection.CreateCommand())
        {
            statusCommand.CommandText = "SELECT status FROM song_requests WHERE id = $id";
            statusCommand.Parameters.AddWithValue("$id", requestId);
            var value = statusCommand.ExecuteScalar();
            if (value is null)
            {
                return null; // 记录不存在
            }

            oldStatus = Enum.Parse<RequestStatus>((string)value, ignoreCase: true);
        }

        if (expectedStatuses is not null && !expectedStatuses.Contains(oldStatus))
        {
            return null; // CAS 失败
        }

        if (status is not null && !RequestStateMachine.CanTransition(oldStatus, status.Value))
        {
            return null; // 流转表校验失败
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var updates = new List<string>();
            var now = UtcNowIso();
            if (status is not null)
            {
                updates.Add("status = $status");
            }

            if (failureReason is not null)
            {
                updates.Add("failure_reason = $failureReason");
            }

            if (searchResultJson is not null)
            {
                updates.Add("search_result_json = $searchResultJson");
            }

            if (canonicalSongKey is not null)
            {
                updates.Add("canonical_song_key = $canonicalSongKey");
            }

            updates.Add("updated_at = $updatedAt");
            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText =
                $"UPDATE song_requests SET {string.Join(", ", updates)} WHERE id = $id";
            if (status is not null)
            {
                updateCommand.Parameters.AddWithValue("$status", StatusToDb(status.Value));
            }

            if (failureReason is not null)
            {
                updateCommand.Parameters.AddWithValue("$failureReason", failureReason);
            }

            if (searchResultJson is not null)
            {
                updateCommand.Parameters.AddWithValue("$searchResultJson", searchResultJson);
            }

            if (canonicalSongKey is not null)
            {
                updateCommand.Parameters.AddWithValue("$canonicalSongKey", canonicalSongKey);
            }

            updateCommand.Parameters.AddWithValue("$updatedAt", now);
            updateCommand.Parameters.AddWithValue("$id", requestId);
            updateCommand.ExecuteNonQuery();

            if (status is not null && status.Value != oldStatus)
            {
                using var auditCommand = connection.CreateCommand();
                auditCommand.CommandText = """
                    INSERT INTO queue_events(request_id, event_type, old_status, new_status, payload_json, created_at)
                    VALUES ($requestId, 'status_changed', $oldStatus, $newStatus, NULL, $createdAt)
                    """;
                auditCommand.Parameters.AddWithValue("$requestId", requestId);
                auditCommand.Parameters.AddWithValue("$oldStatus", StatusToDb(oldStatus));
                auditCommand.Parameters.AddWithValue("$newStatus", StatusToDb(status.Value));
                auditCommand.Parameters.AddWithValue("$createdAt", now);
                auditCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (SqliteException)
            {
            }

            throw;
        }

        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = "SELECT * FROM song_requests WHERE id = $id";
        selectCommand.Parameters.AddWithValue("$id", requestId);
        using var reader = selectCommand.ExecuteReader();
        return reader.Read() ? ReadRequest(reader) : null;
    }

    // ---- users 域 ----

    public Task SaveUserAsync(User user, bool incrementRequestCount = false, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            SaveUserSync(connection, user, incrementRequestCount);
            return true;
        }, ct);

    public Task<User?> GetUserAsync(string platform, string roomId, string userId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            if (string.IsNullOrEmpty(userId))
            {
                return null;
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT * FROM users WHERE platform = $platform AND room_id = $roomId AND user_id = $userId";
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$roomId", roomId);
            command.Parameters.AddWithValue("$userId", userId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadUser(reader) : null;
        }, ct);

    /// <summary>
    /// 跨房间特权 MAX：同一 (platform, user_id) 的全部 room_id
    /// 行取 MAX(is_admin)/MAX(is_anchor)。不按 room_id 过滤的原因见接口注释——
    /// 按 room_id 精确匹配会漏掉 RoomId 缺失（''）或直播间号变化的记录。
    /// </summary>
    public Task<(bool IsAdmin, bool IsAnchor)> GetUserPrivilegeAsync(
        string platform, string userId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            if (string.IsNullOrEmpty(userId))
            {
                return (false, false);
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COALESCE(MAX(is_admin), 0), COALESCE(MAX(is_anchor), 0) " +
                "FROM users WHERE platform = $platform AND user_id = $userId";
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$userId", userId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return (false, false);
            }

            return (reader.GetInt64(0) != 0, reader.GetInt64(1) != 0);
        }, ct);

    public Task<IReadOnlyList<User>> ListUsersAsync(
        string? platform = null,
        string? roomId = null,
        string? nickname = null,
        int limit = 200,
        CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var where = BuildUserFilter(platform, roomId, nickname);
            // 同一用户（platform + user_id）跨房间多条：按 user_id 去重只保留
            // 最新一条（昵称/等级取最新）；admin/anchor 取分组 MAX（任一房间
            // 是管理员即显示管理员——MAX 必须在取 rn=1 之前计算，WHERE 过滤
            // 在其后，否则 MAX 退化为单行值；管理员置顶再按昵称。
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT platform, room_id, user_id, nickname, fan_level, medal_level,
                       max_admin AS is_admin, max_anchor AS is_anchor,
                       request_count, last_request_at, updated_at
                FROM (
                    SELECT u.*,
                           MAX(u.is_admin) OVER (PARTITION BY u.platform, u.user_id) AS max_admin,
                           MAX(u.is_anchor) OVER (PARTITION BY u.platform, u.user_id) AS max_anchor,
                           ROW_NUMBER() OVER (
                               PARTITION BY u.platform, u.user_id ORDER BY u.updated_at DESC
                           ) AS rn
                    FROM users u
                    {where}
                )
                WHERE rn = 1 ORDER BY is_admin DESC, nickname LIMIT $limit
                """.Replace("{where}", where.Clause);
            AddParameters(command, where.Parameters);
            command.Parameters.AddWithValue("$limit", Math.Max(limit, 1));
            var list = new List<User>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadUser(reader));
            }

            return (IReadOnlyList<User>)list;
        }, ct);

    public Task<User?> SetUserAdminAsync(string platform, string roomId, string userId, bool admin, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            if (string.IsNullOrEmpty(userId))
            {
                return null;
            }

            // 只写 is_admin；is_anchor 不动（撤销 admin 不得连带清掉主播角色）
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE users SET is_admin = $admin, updated_at = $updatedAt
                WHERE platform = $platform AND room_id = $roomId AND user_id = $userId
                """;
            command.Parameters.AddWithValue("$admin", admin ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", UtcNowIso());
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$roomId", roomId);
            command.Parameters.AddWithValue("$userId", userId);
            if (command.ExecuteNonQuery() == 0)
            {
                return null;
            }

            using var selectCommand = connection.CreateCommand();
            selectCommand.CommandText =
                "SELECT * FROM users WHERE platform = $platform AND room_id = $roomId AND user_id = $userId";
            selectCommand.Parameters.AddWithValue("$platform", platform);
            selectCommand.Parameters.AddWithValue("$roomId", roomId);
            selectCommand.Parameters.AddWithValue("$userId", userId);
            using var reader = selectCommand.ExecuteReader();
            return reader.Read() ? ReadUser(reader) : null;
        }, ct);

    public Task<int> DeleteNonAdminUsersAsync(CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM users WHERE is_admin = 0";
            return command.ExecuteNonQuery();
        }, ct);

    // ---- banned_users 域 ----

    public Task<BannedUser?> BanUserAsync(
        string platform,
        string roomId,
        string userId,
        string nickname = "",
        string reason = "",
        string bannedBy = "",
        CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            if (string.IsNullOrEmpty(userId))
            {
                return null;
            }

            var now = UtcNowIso();
            using (var command = connection.CreateCommand())
            {
                // 幂等 UPSERT：重复拉黑覆盖昵称/原因/操作人，不报错不重复行
                command.CommandText = """
                    INSERT INTO banned_users(platform, room_id, user_id, nickname, reason, banned_by, created_at)
                    VALUES ($platform, $roomId, $userId, $nickname, $reason, $bannedBy, $createdAt)
                    ON CONFLICT(platform, room_id, user_id) DO UPDATE SET
                        nickname = excluded.nickname,
                        reason = excluded.reason,
                        banned_by = excluded.banned_by
                    """;
                command.Parameters.AddWithValue("$platform", platform);
                command.Parameters.AddWithValue("$roomId", roomId);
                command.Parameters.AddWithValue("$userId", userId);
                command.Parameters.AddWithValue("$nickname", nickname);
                command.Parameters.AddWithValue("$reason", reason);
                command.Parameters.AddWithValue("$bannedBy", bannedBy);
                command.Parameters.AddWithValue("$createdAt", now);
                command.ExecuteNonQuery();
            }

            using var selectCommand = connection.CreateCommand();
            selectCommand.CommandText =
                "SELECT * FROM banned_users WHERE platform = $platform AND room_id = $roomId AND user_id = $userId";
            selectCommand.Parameters.AddWithValue("$platform", platform);
            selectCommand.Parameters.AddWithValue("$roomId", roomId);
            selectCommand.Parameters.AddWithValue("$userId", userId);
            using var reader = selectCommand.ExecuteReader();
            return reader.Read() ? ReadBannedUser(reader) : null;
        }, ct);

    public Task<bool> UnbanUserAsync(string platform, string roomId, string userId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            if (string.IsNullOrEmpty(userId))
            {
                return false;
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM banned_users WHERE platform = $platform AND room_id = $roomId AND user_id = $userId";
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$roomId", roomId);
            command.Parameters.AddWithValue("$userId", userId);
            return command.ExecuteNonQuery() > 0;
        }, ct);

    public Task<bool> IsUserBannedAsync(string platform, string roomId, string userId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            if (string.IsNullOrEmpty(userId))
            {
                return false;
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM banned_users WHERE platform = $platform AND room_id = $roomId AND user_id = $userId LIMIT 1";
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$roomId", roomId);
            command.Parameters.AddWithValue("$userId", userId);
            return command.ExecuteScalar() is not null;
        }, ct);

    public Task<IReadOnlyList<BannedUser>> ListBannedUsersAsync(
        string? platform = null,
        string? roomId = null,
        string? nickname = null,
        int limit = 200,
        CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var where = BuildUserFilter(platform, roomId, nickname);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT * FROM banned_users {where.Clause} ORDER BY created_at DESC LIMIT $limit";
            AddParameters(command, where.Parameters);
            command.Parameters.AddWithValue("$limit", Math.Max(limit, 1));
            var list = new List<BannedUser>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadBannedUser(reader));
            }

            return (IReadOnlyList<BannedUser>)list;
        }, ct);

    // ---- banned_songs 域 ----

    public Task<IReadOnlyList<string>> ListBannedSongsAsync(CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var list = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rule FROM banned_songs ORDER BY rule";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            return (IReadOnlyList<string>)list;
        }, ct);

    public Task ReplaceBannedSongsAsync(IReadOnlyList<string> rules, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var cleaned = rules
                .Select(r => r?.Trim() ?? "")
                .Where(r => r.Length > 0)
                .ToList();
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                using (var deleteCommand = connection.CreateCommand())
                {
                    deleteCommand.CommandText = "DELETE FROM banned_songs";
                    deleteCommand.ExecuteNonQuery();
                }

                foreach (var rule in cleaned)
                {
                    using var insertCommand = connection.CreateCommand();
                    insertCommand.CommandText = "INSERT OR IGNORE INTO banned_songs(rule) VALUES ($rule)";
                    insertCommand.Parameters.AddWithValue("$rule", rule);
                    insertCommand.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (SqliteException)
                {
                }

                throw;
            }

            return true;
        }, ct);

    // ---- playlists 域 ----

    /// <summary>歌单行 SELECT 骨架（song_count 联表计数；WHERE/ORDER 由各查询追加）。</summary>
    private const string PlaylistSelect = """
        SELECT p.id, p.name, p.source, p.source_id, p.is_idle, p.play_mode,
               p.created_at, p.updated_at,
               (SELECT COUNT(*) FROM playlist_songs s WHERE s.playlist_id = p.id) AS song_count
        FROM playlists p
        """;

    public Task<IReadOnlyList<Playlist>> ListPlaylistsAsync(CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var list = new List<Playlist>();
            using var command = connection.CreateCommand();
            command.CommandText = $"{PlaylistSelect} ORDER BY p.is_idle DESC, p.id ASC";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadPlaylist(reader));
            }

            return (IReadOnlyList<Playlist>)list;
        }, ct);

    public Task<Playlist?> GetPlaylistAsync(long playlistId, CancellationToken ct = default) =>
        ExecuteAsync(connection => ReadPlaylistById(connection, playlistId), ct);

    public Task<Playlist?> GetIdlePlaylistAsync(CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"{PlaylistSelect} WHERE p.is_idle = 1";
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPlaylist(reader) : null;
        }, ct);

    public Task<Playlist> CreatePlaylistAsync(
        string name,
        PlaylistSource source = PlaylistSource.Local,
        string sourceId = "",
        CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var trimmed = name?.Trim() ?? throw new ArgumentException("playlist name is required", nameof(name));
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("playlist name is required", nameof(name));
            }

            var now = UtcNowIso();
            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = """
                INSERT INTO playlists(name, source, source_id, is_idle, play_mode, created_at, updated_at)
                VALUES ($name, $source, $sourceId, 0, 'sequential', $now, $now)
                """;
            insertCommand.Parameters.AddWithValue("$name", trimmed);
            insertCommand.Parameters.AddWithValue("$source", PlaylistSourceToDb(source));
            insertCommand.Parameters.AddWithValue("$sourceId", sourceId ?? "");
            insertCommand.Parameters.AddWithValue("$now", now);
            insertCommand.ExecuteNonQuery();

            using var lastIdCommand = connection.CreateCommand();
            lastIdCommand.CommandText = "SELECT last_insert_rowid()";
            var id = Convert.ToInt64(lastIdCommand.ExecuteScalar());
            return ReadPlaylistById(connection, id)!;
        }, ct);

    public Task<Playlist?> FindImportedPlaylistAsync(PlaylistSource source, string sourceId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            if (source == PlaylistSource.Local || string.IsNullOrEmpty(sourceId))
            {
                return null; // 本机歌单无来源对账语义
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"{PlaylistSelect} WHERE p.source = $source AND p.source_id = $sourceId LIMIT 1";
            command.Parameters.AddWithValue("$source", PlaylistSourceToDb(source));
            command.Parameters.AddWithValue("$sourceId", sourceId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPlaylist(reader) : null;
        }, ct);

    public Task<bool> ClearPlaylistSongsAsync(long playlistId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using (var checkCommand = connection.CreateCommand())
            {
                checkCommand.CommandText = "SELECT 1 FROM playlists WHERE id = $id";
                checkCommand.Parameters.AddWithValue("$id", playlistId);
                if (checkCommand.ExecuteScalar() is null)
                {
                    return false;
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM playlist_songs WHERE playlist_id = $id";
            command.Parameters.AddWithValue("$id", playlistId);
            command.ExecuteNonQuery();
            TouchPlaylist(connection, playlistId);
            return true;
        }, ct);

    public Task<Playlist?> RenamePlaylistAsync(long playlistId, string name, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var trimmed = name?.Trim() ?? "";
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("playlist name is required", nameof(name));
            }

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE playlists SET name = $name, updated_at = $now WHERE id = $id";
            command.Parameters.AddWithValue("$name", trimmed);
            command.Parameters.AddWithValue("$now", UtcNowIso());
            command.Parameters.AddWithValue("$id", playlistId);
            return command.ExecuteNonQuery() == 0 ? null : ReadPlaylistById(connection, playlistId);
        }, ct);

    public Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                using (var songsCommand = connection.CreateCommand())
                {
                    songsCommand.CommandText = "DELETE FROM playlist_songs WHERE playlist_id = $id";
                    songsCommand.Parameters.AddWithValue("$id", playlistId);
                    songsCommand.ExecuteNonQuery();
                }

                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM playlists WHERE id = $id";
                command.Parameters.AddWithValue("$id", playlistId);
                var deleted = command.ExecuteNonQuery() > 0;
                transaction.Commit();
                return deleted;
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (SqliteException)
                {
                }

                throw;
            }
        }, ct);

    public Task<Playlist?> SetIdlePlaylistAsync(long? playlistId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                // 先清后标（顺序不能反：partial unique index 禁止两行同时 is_idle=1）；
                // 目标不存在时回滚——旧标记保持原状
                using (var clearCommand = connection.CreateCommand())
                {
                    clearCommand.CommandText = "UPDATE playlists SET is_idle = 0 WHERE is_idle = 1";
                    clearCommand.ExecuteNonQuery();
                }

                if (playlistId is { } id)
                {
                    using var markCommand = connection.CreateCommand();
                    markCommand.CommandText =
                        "UPDATE playlists SET is_idle = 1, updated_at = $now WHERE id = $id";
                    markCommand.Parameters.AddWithValue("$now", UtcNowIso());
                    markCommand.Parameters.AddWithValue("$id", id);
                    if (markCommand.ExecuteNonQuery() == 0)
                    {
                        transaction.Rollback();
                        return null;
                    }
                }

                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (SqliteException)
                {
                }

                throw;
            }

            return playlistId is null ? null : ReadPlaylistById(connection, playlistId.Value);
        }, ct);

    public Task<Playlist?> SetPlayModeAsync(long playlistId, PlaylistPlayMode mode, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE playlists SET play_mode = $mode, updated_at = $now WHERE id = $id";
            command.Parameters.AddWithValue("$mode", PlaylistPlayModeToDb(mode));
            command.Parameters.AddWithValue("$now", UtcNowIso());
            command.Parameters.AddWithValue("$id", playlistId);
            return command.ExecuteNonQuery() == 0 ? null : ReadPlaylistById(connection, playlistId);
        }, ct);

    public Task<IReadOnlyList<PlaylistSong>> ListPlaylistSongsAsync(long playlistId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var list = new List<PlaylistSong>();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT * FROM playlist_songs WHERE playlist_id = $id ORDER BY position, id";
            command.Parameters.AddWithValue("$id", playlistId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadPlaylistSong(reader));
            }

            return (IReadOnlyList<PlaylistSong>)list;
        }, ct);

    public Task<PlaylistSong> AddPlaylistSongAsync(
        long playlistId, string name, string singer = "", string? searchJson = null, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var trimmedName = name?.Trim() ?? throw new ArgumentException("song name is required", nameof(name));
            if (trimmedName.Length == 0)
            {
                throw new ArgumentException("song name is required", nameof(name));
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var songId = InsertPlaylistSong(connection, playlistId, trimmedName, (singer ?? "").Trim(), searchJson);
                TouchPlaylist(connection, playlistId);
                transaction.Commit();
                return ReadPlaylistSongById(connection, songId)!;
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (SqliteException)
                {
                }

                throw;
            }
        }, ct);

    public Task<int> AddPlaylistSongsAsync(
        long playlistId, IReadOnlyList<(string Name, string Singer, string? SearchJson)> songs, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var inserted = 0;
                foreach (var song in songs)
                {
                    var name = song.Name?.Trim() ?? "";
                    if (name.Length == 0)
                    {
                        continue; // 空歌名剔除（对齐旧 SaveIdleSongs 语义）
                    }

                    InsertPlaylistSong(connection, playlistId, name, (song.Singer ?? "").Trim(), song.SearchJson);
                    inserted++;
                }

                if (inserted > 0)
                {
                    TouchPlaylist(connection, playlistId);
                }

                transaction.Commit();
                return inserted;
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (SqliteException)
                {
                }

                throw;
            }
        }, ct);

    public Task<bool> RemovePlaylistSongAsync(long songId, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var song = ReadPlaylistSongById(connection, songId);
            if (song is null)
            {
                return false;
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM playlist_songs WHERE id = $id";
                command.Parameters.AddWithValue("$id", songId);
                command.ExecuteNonQuery();
                TouchPlaylist(connection, song.PlaylistId);
                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (SqliteException)
                {
                }

                throw;
            }

            return true;
        }, ct);

    public Task MovePlaylistSongAsync(long playlistId, long songId, int offset, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            if (offset is not (-1 or 1))
            {
                return true;
            }

            // 全量读序（id + position）后与相邻行交换 position——不能在单条
            // UPDATE 的 CASE 子查询里互换：SQLite 逐行求值时子查询会读到
            // 同语句已更新的行，交换退化为 no-op
            var ordered = new List<(long Id, long Position)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT id, position FROM playlist_songs WHERE playlist_id = $id ORDER BY position, id";
                command.Parameters.AddWithValue("$id", playlistId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ordered.Add((reader.GetInt64(0), reader.GetInt64(1)));
                }
            }

            var index = ordered.FindIndex(row => row.Id == songId);
            var target = index + offset;
            if (index < 0 || target < 0 || target >= ordered.Count)
            {
                return true; // 越界无操作
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                SetPlaylistSongPosition(connection, songId, ordered[target].Position);
                SetPlaylistSongPosition(connection, ordered[target].Id, ordered[index].Position);
                TouchPlaylist(connection, playlistId);
                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (SqliteException)
                {
                }

                throw;
            }

            return true;
        }, ct);

    private static void SetPlaylistSongPosition(SqliteConnection connection, long songId, long position)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playlist_songs SET position = $position WHERE id = $id";
        command.Parameters.AddWithValue("$position", position);
        command.Parameters.AddWithValue("$id", songId);
        command.ExecuteNonQuery();
    }

    private static long InsertPlaylistSong(
        SqliteConnection connection, long playlistId, string name, string singer, string? searchJson)
    {
        using var positionCommand = connection.CreateCommand();
        positionCommand.CommandText =
            "SELECT COALESCE(MAX(position) + 1, 0) FROM playlist_songs WHERE playlist_id = $id";
        positionCommand.Parameters.AddWithValue("$id", playlistId);
        var position = Convert.ToInt32(positionCommand.ExecuteScalar());

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO playlist_songs(playlist_id, name, singer, position, search_json)
            VALUES ($playlistId, $name, $singer, $position, $searchJson)
            """;
        insertCommand.Parameters.AddWithValue("$playlistId", playlistId);
        insertCommand.Parameters.AddWithValue("$name", name);
        insertCommand.Parameters.AddWithValue("$singer", singer);
        insertCommand.Parameters.AddWithValue("$position", position);
        insertCommand.Parameters.AddWithValue("$searchJson", (object?)searchJson ?? DBNull.Value);
        insertCommand.ExecuteNonQuery();

        using var lastIdCommand = connection.CreateCommand();
        lastIdCommand.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(lastIdCommand.ExecuteScalar());
    }

    /// <summary>歌单内容变更时刷新 updated_at（列表对账依据）。</summary>
    private static void TouchPlaylist(SqliteConnection connection, long playlistId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playlists SET updated_at = $now WHERE id = $id";
        command.Parameters.AddWithValue("$now", UtcNowIso());
        command.Parameters.AddWithValue("$id", playlistId);
        command.ExecuteNonQuery();
    }

    private static Playlist? ReadPlaylistById(SqliteConnection connection, long playlistId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"{PlaylistSelect} WHERE p.id = $id";
        command.Parameters.AddWithValue("$id", playlistId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPlaylist(reader) : null;
    }

    private static PlaylistSong? ReadPlaylistSongById(SqliteConnection connection, long songId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM playlist_songs WHERE id = $id";
        command.Parameters.AddWithValue("$id", songId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPlaylistSong(reader) : null;
    }

    private static Playlist ReadPlaylist(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Source = PlaylistSourceFromDb(reader.GetString(reader.GetOrdinal("source"))),
        SourceId = reader.GetString(reader.GetOrdinal("source_id")),
        IsIdle = reader.GetInt64(reader.GetOrdinal("is_idle")) != 0,
        PlayMode = PlaylistPlayModeFromDb(reader.GetString(reader.GetOrdinal("play_mode"))),
        CreatedAt = ParseIso(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = ParseIso(reader.GetString(reader.GetOrdinal("updated_at"))),
        SongCount = (int)reader.GetInt64(reader.GetOrdinal("song_count")),
    };

    private static PlaylistSong ReadPlaylistSong(SqliteDataReader reader)
    {
        var searchJson = reader.IsDBNull(reader.GetOrdinal("search_json"))
            ? null
            : reader.GetString(reader.GetOrdinal("search_json"));
        return new PlaylistSong
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            PlaylistId = reader.GetInt64(reader.GetOrdinal("playlist_id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Singer = reader.GetString(reader.GetOrdinal("singer")),
            Position = (int)reader.GetInt64(reader.GetOrdinal("position")),
            SearchJson = searchJson,
        };
    }

    private static string PlaylistPlayModeToDb(PlaylistPlayMode mode) => mode switch
    {
        PlaylistPlayMode.Sequential => "sequential",
        PlaylistPlayMode.Shuffle => "shuffle",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>未知播放模式值容错回默认（读路径容忍，避免库值漂移炸开整页）。</summary>
    private static PlaylistPlayMode PlaylistPlayModeFromDb(string value) => value switch
    {
        "shuffle" => PlaylistPlayMode.Shuffle,
        _ => PlaylistPlayMode.Sequential,
    };

    private static string PlaylistSourceToDb(PlaylistSource source) => source switch
    {
        PlaylistSource.Local => "local",
        PlaylistSource.Netease => "netease",
        PlaylistSource.Qqmusic => "qqmusic",
        PlaylistSource.Kugou => "kugou",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static PlaylistSource PlaylistSourceFromDb(string value) => value switch
    {
        "netease" => PlaylistSource.Netease,
        "qqmusic" => PlaylistSource.Qqmusic,
        "kugou" => PlaylistSource.Kugou,
        _ => PlaylistSource.Local,
    };

    // ---- queueup_entries 域（阶段 5 排队模块）----

    public Task<QueueUpEntry> InsertQueueUpEntryAsync(QueueUpEntry entry, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            var now = UtcNowIso();
            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = """
                INSERT INTO queueup_entries(user_id, nickname, content, source, status, created_at)
                VALUES ($userId, $nickname, $content, $source, $status, $createdAt)
                """;
            insertCommand.Parameters.AddWithValue("$userId", entry.UserId);
            insertCommand.Parameters.AddWithValue("$nickname", entry.Nickname);
            insertCommand.Parameters.AddWithValue("$content", entry.Content);
            insertCommand.Parameters.AddWithValue("$source", QueueUpSourceToDb(entry.Source));
            insertCommand.Parameters.AddWithValue("$status", QueueUpStatusToDb(entry.Status));
            insertCommand.Parameters.AddWithValue("$createdAt",
                entry.CreatedAt == default ? now : FormatIso(entry.CreatedAt));
            insertCommand.ExecuteNonQuery();

            using var lastIdCommand = connection.CreateCommand();
            lastIdCommand.CommandText = "SELECT last_insert_rowid()";
            var entryId = Convert.ToInt64(lastIdCommand.ExecuteScalar());
            return entry with
            {
                Id = entryId,
                CreatedAt = entry.CreatedAt == default ? ParseIso(now) : entry.CreatedAt,
            };
        }, ct);

    public Task<QueueUpEntry?> UpdateQueueUpEntryStatusAsync(
        long entryId, QueueUpStatus status, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE queueup_entries SET status = $status WHERE id = $id AND status = 'queued'";
            command.Parameters.AddWithValue("$status", QueueUpStatusToDb(status));
            command.Parameters.AddWithValue("$id", entryId);
            if (command.ExecuteNonQuery() == 0)
            {
                return (QueueUpEntry?)null; // 不存在或已非 queued（幂等防重复出队）
            }

            return ReadQueueUpEntrySync(connection, entryId);
        }, ct);

    public Task<QueueUpEntry?> UpdateQueueUpEntryContentAsync(
        long entryId, string content, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE queueup_entries SET content = $content WHERE id = $id AND status = 'queued'";
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$id", entryId);
            if (command.ExecuteNonQuery() == 0)
            {
                return (QueueUpEntry?)null;
            }

            return ReadQueueUpEntrySync(connection, entryId);
        }, ct);

    public Task<IReadOnlyList<QueueUpEntry>> ListQueueUpEntriesAsync(
        QueueUpStatus? status = null, CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            // null = 只取 queued 活动条目（模块消费语义）；历史查询显式传终态
            using var command = connection.CreateCommand();
            command.CommandText = status is null or QueueUpStatus.Queued
                ? "SELECT * FROM queueup_entries WHERE status = 'queued' ORDER BY created_at ASC, id ASC"
                : "SELECT * FROM queueup_entries WHERE status = $status ORDER BY created_at ASC, id ASC";
            if (status is not null && status != QueueUpStatus.Queued)
            {
                command.Parameters.AddWithValue("$status", QueueUpStatusToDb(status.Value));
            }

            var list = new List<QueueUpEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadQueueUpEntry(reader));
            }

            return (IReadOnlyList<QueueUpEntry>)list;
        }, ct);

    public Task<(IReadOnlyList<QueueUpEntry> Rows, int Total)> ListQueueUpHistoryPageAsync(
        QueueUpStatus? status = null,
        bool descending = true,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default) =>
        ExecuteAsync(connection =>
        {
            // 终态历史查询：status 为 null 时取 completed/cancelled 全部终态；
            // 显式传终态（completed/cancelled）时单态过滤。
            var where = new List<string>();
            var parameters = new List<(string Name, object Value)>();
            if (status is null)
            {
                where.Add("status IN ('completed','cancelled')");
            }
            else
            {
                if (status != QueueUpStatus.Completed && status != QueueUpStatus.Cancelled)
                {
                    throw new ArgumentOutOfRangeException(nameof(status),
                        "排队历史只允许终态（completed/cancelled）或 null（全部终态）");
                }

                where.Add("status = $status");
                parameters.Add(("$status", QueueUpStatusToDb(status.Value)));
            }

            var clause = $"WHERE {string.Join(" AND ", where)}";
            var direction = descending ? "DESC" : "ASC";

            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = $"SELECT COUNT(*) AS c FROM queueup_entries {clause}";
            AddParameters(countCommand, parameters);
            var total = Convert.ToInt32(countCommand.ExecuteScalar());

            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT * FROM queueup_entries {clause} ORDER BY created_at {direction}, id {direction} LIMIT $limit OFFSET $offset";
            AddParameters(command, parameters);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);

            var rows = new List<QueueUpEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadQueueUpEntry(reader));
            }

            return ((IReadOnlyList<QueueUpEntry>)rows, total);
        }, ct);

    private static QueueUpEntry? ReadQueueUpEntrySync(SqliteConnection connection, long entryId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM queueup_entries WHERE id = $id";
        command.Parameters.AddWithValue("$id", entryId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadQueueUpEntry(reader) : null;
    }

    private static QueueUpEntry ReadQueueUpEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        UserId = reader.GetString(reader.GetOrdinal("user_id")),
        Nickname = reader.GetString(reader.GetOrdinal("nickname")),
        Content = reader.GetString(reader.GetOrdinal("content")),
        Source = QueueUpSourceFromDb(reader.GetString(reader.GetOrdinal("source"))),
        Status = QueueUpStatusFromDb(reader.GetString(reader.GetOrdinal("status"))),
        CreatedAt = ParseIso(reader.GetString(reader.GetOrdinal("created_at"))),
    };

    private static string QueueUpSourceToDb(QueueUpSource source) => source switch
    {
        QueueUpSource.Danmaku => "danmaku",
        QueueUpSource.Gift => "gift",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static QueueUpSource QueueUpSourceFromDb(string value) => value switch
    {
        "danmaku" => QueueUpSource.Danmaku,
        "gift" => QueueUpSource.Gift,
        _ => throw new InvalidOperationException($"unknown queueup source: {value}"),
    };

    private static string QueueUpStatusToDb(QueueUpStatus status) => status switch
    {
        QueueUpStatus.Queued => "queued",
        QueueUpStatus.Completed => "completed",
        QueueUpStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static QueueUpStatus QueueUpStatusFromDb(string value) => value switch
    {
        "queued" => QueueUpStatus.Queued,
        "completed" => QueueUpStatus.Completed,
        "cancelled" => QueueUpStatus.Cancelled,
        _ => throw new InvalidOperationException($"unknown queueup status: {value}"),
    };

    private static SongRequest ReadRequest(SqliteDataReader reader)
    {
        long? fanLevel = reader.IsDBNull(reader.GetOrdinal("fan_level"))
            ? null
            : reader.GetInt64(reader.GetOrdinal("fan_level"));
        long? medalLevel = reader.IsDBNull(reader.GetOrdinal("medal_level"))
            ? null
            : reader.GetInt64(reader.GetOrdinal("medal_level"));
        var searchResult = reader.IsDBNull(reader.GetOrdinal("search_result_json"))
            ? null
            : reader.GetString(reader.GetOrdinal("search_result_json"));
        return new SongRequest
        {
            RequestId = reader.GetInt64(reader.GetOrdinal("id")),
            Sequence = reader.GetInt64(reader.GetOrdinal("sequence")),
            Platform = reader.GetString(reader.GetOrdinal("platform")),
            RoomId = reader.GetString(reader.GetOrdinal("room_id")),
            UserId = reader.GetString(reader.GetOrdinal("user_id")),
            Nickname = reader.GetString(reader.GetOrdinal("nickname")),
            IsAdmin = reader.GetInt64(reader.GetOrdinal("is_admin")) != 0,
            IsAnchor = reader.GetInt64(reader.GetOrdinal("is_anchor")) != 0,
            FanLevel = fanLevel is null ? null : (int)fanLevel,
            MedalLevel = medalLevel is null ? null : (int)medalLevel,
            SongName = reader.GetString(reader.GetOrdinal("song_name")),
            Singer = reader.GetString(reader.GetOrdinal("singer")),
            CanonicalSongKey = reader.GetString(reader.GetOrdinal("canonical_song_key")),
            Status = Enum.Parse<RequestStatus>(reader.GetString(reader.GetOrdinal("status")), ignoreCase: true),
            FailureReason = reader.GetString(reader.GetOrdinal("failure_reason")),
            SearchResultJson = searchResult,
            CreatedAt = ParseIso(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = ParseIso(reader.GetString(reader.GetOrdinal("updated_at"))),
        };
    }

    private static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static void SaveUserSync(SqliteConnection connection, User user, bool incrementRequestCount)
    {
        if (string.IsNullOrEmpty(user.UserId))
        {
            return;
        }

        var now = UtcNowIso();
        using var command = connection.CreateCommand();
        // UPSERT 语义：
        // 昵称覆盖；等级 COALESCE 不降级（传入 NULL 保持旧值）；特权显式覆盖
        // （is_admin/is_anchor 直接写传入值，可撤销）；request_count 仅
        // increment 时 +1 并同步 last_request_at。
        command.CommandText = """
            INSERT INTO users(
                platform, room_id, user_id, nickname, fan_level,
                medal_level, is_admin, is_anchor, request_count,
                last_request_at, updated_at
            ) VALUES (
                $platform, $roomId, $userId, $nickname, $fanLevel,
                $medalLevel, $isAdmin, $isAnchor, $requestCount,
                $lastRequestAt, $updatedAt
            )
            ON CONFLICT(platform, room_id, user_id) DO UPDATE SET
                nickname = excluded.nickname,
                fan_level = COALESCE(excluded.fan_level, users.fan_level),
                medal_level = COALESCE(excluded.medal_level, users.medal_level),
                is_admin = excluded.is_admin,
                is_anchor = excluded.is_anchor,
                request_count = users.request_count + $requestCountDelta,
                last_request_at = CASE WHEN $requestCountDelta = 1 THEN excluded.last_request_at ELSE users.last_request_at END,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$platform", user.Platform);
        command.Parameters.AddWithValue("$roomId", user.RoomId);
        command.Parameters.AddWithValue("$userId", user.UserId);
        command.Parameters.AddWithValue("$nickname", user.Nickname);
        command.Parameters.AddWithValue("$fanLevel", (object?)user.FanLevel ?? DBNull.Value);
        command.Parameters.AddWithValue("$medalLevel", (object?)user.MedalLevel ?? DBNull.Value);
        command.Parameters.AddWithValue("$isAdmin", user.IsAdmin ? 1 : 0);
        command.Parameters.AddWithValue("$isAnchor", user.IsAnchor ? 1 : 0);
        command.Parameters.AddWithValue("$requestCount", incrementRequestCount ? 1 : 0);
        command.Parameters.AddWithValue("$lastRequestAt", (object?)(incrementRequestCount ? now : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$requestCountDelta", incrementRequestCount ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", now);
        command.ExecuteNonQuery();
    }

    private static User ReadUser(SqliteDataReader reader)
    {
        long? fanLevel = reader.IsDBNull(reader.GetOrdinal("fan_level"))
            ? null
            : reader.GetInt64(reader.GetOrdinal("fan_level"));
        long? medalLevel = reader.IsDBNull(reader.GetOrdinal("medal_level"))
            ? null
            : reader.GetInt64(reader.GetOrdinal("medal_level"));
        DateTimeOffset? lastRequestAt = reader.IsDBNull(reader.GetOrdinal("last_request_at"))
            ? null
            : ParseIso(reader.GetString(reader.GetOrdinal("last_request_at")));
        return new User
        {
            Platform = reader.GetString(reader.GetOrdinal("platform")),
            RoomId = reader.GetString(reader.GetOrdinal("room_id")),
            UserId = reader.GetString(reader.GetOrdinal("user_id")),
            Nickname = reader.GetString(reader.GetOrdinal("nickname")),
            IsAdmin = reader.GetInt64(reader.GetOrdinal("is_admin")) != 0,
            IsAnchor = reader.GetInt64(reader.GetOrdinal("is_anchor")) != 0,
            FanLevel = fanLevel is null ? null : (int)fanLevel,
            MedalLevel = medalLevel is null ? null : (int)medalLevel,
            RequestCount = (int)reader.GetInt64(reader.GetOrdinal("request_count")),
            LastRequestAt = lastRequestAt,
            UpdatedAt = ParseIso(reader.GetString(reader.GetOrdinal("updated_at"))),
        };
    }

    private static BannedUser ReadBannedUser(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        Platform = reader.GetString(reader.GetOrdinal("platform")),
        RoomId = reader.GetString(reader.GetOrdinal("room_id")),
        UserId = reader.GetString(reader.GetOrdinal("user_id")),
        Nickname = reader.GetString(reader.GetOrdinal("nickname")),
        Reason = reader.GetString(reader.GetOrdinal("reason")),
        BannedBy = reader.GetString(reader.GetOrdinal("banned_by")),
        CreatedAt = ParseIso(reader.GetString(reader.GetOrdinal("created_at"))),
    };

    private sealed record WhereClause(string Clause, List<(string Name, object Value)> Parameters);

    /// <summary>构造 users/banned_users 通用过滤子句（platform/room_id/nickname LIKE 转义）。</summary>
    private static WhereClause BuildUserFilter(string? platform, string? roomId, string? nickname)
    {
        var clauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        if (!string.IsNullOrEmpty(platform))
        {
            clauses.Add("platform = $platform");
            parameters.Add(("$platform", platform!));
        }

        if (!string.IsNullOrEmpty(roomId))
        {
            clauses.Add("room_id = $roomId");
            parameters.Add(("$roomId", roomId!));
        }

        if (!string.IsNullOrEmpty(nickname))
        {
            clauses.Add("nickname LIKE $nickname ESCAPE '\\'");
            parameters.Add(("$nickname", $"%{EscapeLike(nickname!)}%"));
        }

        return new WhereClause(clauses.Count > 0 ? $"WHERE {string.Join(" AND ", clauses)}" : "", parameters);
    }

    /// <summary>转义 LIKE 通配符，昵称搜索按字面匹配。</summary>
    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    internal static string StatusToDb(RequestStatus status) => status.ToString().ToLowerInvariant();

    internal static string UtcNowIso() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static string FormatIso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
