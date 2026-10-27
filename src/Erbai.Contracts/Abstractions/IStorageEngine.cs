using Erbai.Contracts.QueueUp;
using Erbai.Contracts.Requests;
using Erbai.Contracts.Storage;

namespace Erbai.Contracts.Abstractions;

/// <summary>
/// 存储引擎契约（阶段 1 面）：SQLite 引擎 + song_requests 域方法。
/// 并发契约：所有访问经专用写线程串行化；写失败自动回滚；
/// 序号在 BEGIN IMMEDIATE 事务内原子分配；状态更新为 CAS（expected_statuses
/// + 流转表校验，不符返回 null）；每次状态变化写 queue_events 审计流。
/// </summary>
public interface IStorageEngine : IAsyncDisposable
{
    string DatabasePath { get; }

    Task OpenAsync(CancellationToken ct = default);

    Task CloseAsync();

    /// <summary>SQLite backup API 一致性快照（含 WAL）；目标不得等于数据库路径。</summary>
    Task<string> BackupToAsync(string destination, CancellationToken ct = default);

    /// <summary>插入请求，序列号在事务内以 MAX(sequence)+1 原子分配。</summary>
    Task<SongRequest> InsertRequestAsync(SongRequest request, CancellationToken ct = default);

    /// <summary>
    /// CAS 更新：old 状态不在 expectedStatuses 或流转非法时返回 null 且不写入；
    /// 状态实际变化时追加 queue_events(status_changed) 审计。
    /// </summary>
    Task<SongRequest?> UpdateRequestAsync(
        long requestId,
        RequestStatus? status = null,
        string? failureReason = null,
        string? searchResultJson = null,
        string? canonicalSongKey = null,
        IReadOnlySet<RequestStatus>? expectedStatuses = null,
        CancellationToken ct = default);

    Task<SongRequest?> GetRequestAsync(long requestId, CancellationToken ct = default);

    /// <summary>热路径定向查询：只取 status 列。</summary>
    Task<RequestStatus?> GetRequestStatusAsync(long requestId, CancellationToken ct = default);

    /// <summary>SQL 聚合统计，不扫全表。</summary>
    Task<IReadOnlyDictionary<RequestStatus, int>> CountRequestsByStatusAsync(CancellationToken ct = default);

    /// <summary>按 sequence 升序列出（可按状态集合过滤）。</summary>
    Task<IReadOnlyList<SongRequest>> ListRequestsAsync(IReadOnlySet<RequestStatus>? statuses = null, CancellationToken ct = default);

    /// <summary>SQL 分页查询历史（不整表载入内存）。</summary>
    Task<(IReadOnlyList<SongRequest> Rows, int Total)> ListRequestsPageAsync(
        string? platform = null,
        RequestStatus? status = null,
        bool descending = true,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default);

    /// <summary>规范化歌键是否已存在于活动状态（queued/searching/ready/dispatched）。</summary>
    Task<bool> CanonicalExistsAsync(string canonicalKey, CancellationToken ct = default);

    /// <summary>
    /// 窗口内提交计数（点歌频率限制）：该 (platform, room_id, user_id) 自 since 起
    /// 状态非 rejected 的请求行数。platform/userId 为空时恒返回 0。
    /// </summary>
    Task<int> CountRecentRequestsAsync(
        string platform, string roomId, string userId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// 历史数据保留清理（2026-10 长稳轮）：删除 created_at 早于 cutoff 的
    /// queue_events 审计行与终态 song_requests（活动请求永不清理；仍被事件引用的
    /// 请求跳过以保外键）。返回 (删除事件数, 删除请求数)。
    /// </summary>
    Task<(int EventsDeleted, int RequestsDeleted)> PruneHistoryAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    // ---- users 域----

    /// <summary>
    /// UPSERT：昵称覆盖、等级 COALESCE 不降级、特权按传入值显式覆盖、
    /// request_count 仅 increment 时 +1（last_request_at 同步更新）。
    /// </summary>
    Task SaveUserAsync(User user, bool incrementRequestCount = false, CancellationToken ct = default);

    Task<User?> GetUserAsync(string platform, string roomId, string userId, CancellationToken ct = default);

    /// <summary>
    /// 跨房间特权查询：同一
    /// (platform, user_id) 在多个 room_id 下可能有记录——弹幕事件的 RoomId 可能缺失
    /// （落库为 ''）、直播间号也可能变化——任一房间是管理员/主播即视为有特权。
    /// 与 <see cref="ListUsersAsync"/> 的跨房间 MAX 语义一致；无记录返回 (false, false)。
    /// 供命令权限判定使用：按 room_id 精确匹配会漏掉上述记录（2026-09-18 实测）。
    /// </summary>
    Task<(bool IsAdmin, bool IsAnchor)> GetUserPrivilegeAsync(string platform, string userId, CancellationToken ct = default);

    /// <summary>
    /// 按 platform/room_id 过滤 + 昵称子串匹配（LIKE，转义 %/_）；跨房间按
    /// (platform, user_id) 去重取最新，admin/anchor 取分组 MAX（任一房间是
    /// 管理员即显示管理员），管理员置顶再按昵称排序。
    /// </summary>
    Task<IReadOnlyList<User>> ListUsersAsync(
        string? platform = null,
        string? roomId = null,
        string? nickname = null,
        int limit = 200,
        CancellationToken ct = default);

    /// <summary>只写 is_admin，不动 is_anchor；用户不存在返回 null。</summary>
    Task<User?> SetUserAdminAsync(string platform, string roomId, string userId, bool admin, CancellationToken ct = default);

    /// <summary>删除所有非管理员用户行，返回删除行数（管理员特权留存）。</summary>
    Task<int> DeleteNonAdminUsersAsync(CancellationToken ct = default);

    // ---- banned_users 域（幂等 UPSERT / 定向查询）----

    /// <summary>拉黑（幂等 UPSERT：重复拉黑覆盖昵称/原因/操作人，不报错）。</summary>
    Task<BannedUser?> BanUserAsync(
        string platform,
        string roomId,
        string userId,
        string nickname = "",
        string reason = "",
        string bannedBy = "",
        CancellationToken ct = default);

    /// <summary>解除拉黑；实际删除行返回 true。</summary>
    Task<bool> UnbanUserAsync(string platform, string roomId, string userId, CancellationToken ct = default);

    Task<bool> IsUserBannedAsync(string platform, string roomId, string userId, CancellationToken ct = default);

    Task<IReadOnlyList<BannedUser>> ListBannedUsersAsync(
        string? platform = null,
        string? roomId = null,
        string? nickname = null,
        int limit = 200,
        CancellationToken ct = default);

    // ---- banned_songs 域（规则：精确歌名 + "*关键词" 子串）----

    Task<IReadOnlyList<string>> ListBannedSongsAsync(CancellationToken ct = default);

    /// <summary>整体替换歌曲黑名单（去空行、保序）。</summary>
    Task ReplaceBannedSongsAsync(IReadOnlyList<string> rules, CancellationToken ct = default);

    // ---- playlists 域 ----

    /// <summary>全部歌单（空闲播放源置顶，其余按创建序），带联表曲目计数。</summary>
    Task<IReadOnlyList<Playlist>> ListPlaylistsAsync(CancellationToken ct = default);

    Task<Playlist?> GetPlaylistAsync(long playlistId, CancellationToken ct = default);

    /// <summary>当前空闲播放源歌单（is_idle 标记）；未标记返回 null。</summary>
    Task<Playlist?> GetIdlePlaylistAsync(CancellationToken ct = default);

    /// <summary>创建歌单（默认本机创建、顺序播放；名称去空白后不得为空；source/sourceId 供导入来源对账）。</summary>
    Task<Playlist> CreatePlaylistAsync(
        string name,
        PlaylistSource source = PlaylistSource.Local,
        string sourceId = "",
        CancellationToken ct = default);

    /// <summary>按导入来源 (source, sourceId) 对账查找歌单（重复导入同一平台歌单时覆盖更新）。</summary>
    Task<Playlist?> FindImportedPlaylistAsync(PlaylistSource source, string sourceId, CancellationToken ct = default);

    /// <summary>清空歌单全部曲目（重导入路径；返回是否存在）。</summary>
    Task<bool> ClearPlaylistSongsAsync(long playlistId, CancellationToken ct = default);

    /// <summary>重命名（名称去空白后不得为空）；歌单不存在返回 null。</summary>
    Task<Playlist?> RenamePlaylistAsync(long playlistId, string name, CancellationToken ct = default);

    /// <summary>删除歌单及其全部曲目（单事务）；歌单不存在返回 false。</summary>
    Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken ct = default);

    /// <summary>
    /// 标记/取消空闲播放源（全局唯一：标记时自动取消旧标记，取消传 null）；
    /// playlistId 非空但不存在时返回 null（取消标记的回滚语义由事务保证）。
    /// </summary>
    Task<Playlist?> SetIdlePlaylistAsync(long? playlistId, CancellationToken ct = default);

    /// <summary>设置播放模式（顺序/随机）；歌单不存在返回 null。</summary>
    Task<Playlist?> SetPlayModeAsync(long playlistId, PlaylistPlayMode mode, CancellationToken ct = default);

    /// <summary>按 position 顺序读出歌单曲目。</summary>
    Task<IReadOnlyList<PlaylistSong>> ListPlaylistSongsAsync(long playlistId, CancellationToken ct = default);

    /// <summary>追加单曲（position = 末尾 +1；空歌名抛 ArgumentException）。</summary>
    Task<PlaylistSong> AddPlaylistSongAsync(
        long playlistId, string name, string singer = "", string? searchJson = null, CancellationToken ct = default);

    /// <summary>批量追加（导入路径；单事务；空歌名条目剔除；返回实际插入数）。</summary>
    Task<int> AddPlaylistSongsAsync(
        long playlistId, IReadOnlyList<(string Name, string Singer, string? SearchJson)> songs, CancellationToken ct = default);

    /// <summary>删除单曲；不存在返回 false。</summary>
    Task<bool> RemovePlaylistSongAsync(long songId, CancellationToken ct = default);

    /// <summary>上移/下移单曲（offset -1/+1；越界为无操作；不存在为无操作）。</summary>
    Task MovePlaylistSongAsync(long playlistId, long songId, int offset, CancellationToken ct = default);

    // ---- queueup_entries 域----

    /// <summary>插入排队条目（created_at 升序 FIFO）。</summary>
    Task<QueueUpEntry> InsertQueueUpEntryAsync(QueueUpEntry entry, CancellationToken ct = default);

    /// <summary>置条目终态（completed/cancelled）；条目不存在返回 null。</summary>
    Task<QueueUpEntry?> UpdateQueueUpEntryStatusAsync(long entryId, QueueUpStatus status, CancellationToken ct = default);

    /// <summary>更新排队内容（重复「排队」= 替换自己的内容，用户已确认）；条目不存在返回 null。</summary>
    Task<QueueUpEntry?> UpdateQueueUpEntryContentAsync(long entryId, string content, CancellationToken ct = default);

    /// <summary>列出条目（默认 queued 活动条目；created_at 升序）。</summary>
    Task<IReadOnlyList<QueueUpEntry>> ListQueueUpEntriesAsync(QueueUpStatus? status = null, CancellationToken ct = default);

    /// <summary>SQL 分页查询排队历史（终态 completed/cancelled；不整表载入内存）。</summary>
    Task<(IReadOnlyList<QueueUpEntry> Rows, int Total)> ListQueueUpHistoryPageAsync(
        QueueUpStatus? status = null,
        bool descending = true,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default);
}
