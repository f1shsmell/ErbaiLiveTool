namespace Erbai.Contracts.Storage;

/// <summary>歌单播放模式（空闲轮播取数顺序）：顺序轮转 / 随机（洗牌一轮内不重复）。</summary>
public enum PlaylistPlayMode
{
    Sequential,
    Shuffle,
}

/// <summary>歌单来源：本机创建 / 平台歌单导入。</summary>
public enum PlaylistSource
{
    Local,
    Netease,
    Qqmusic,
    Kugou,
}

/// <summary>
/// 歌单元数据（playlists 表行）。is_idle 为全局唯一的"空闲播放源"标记
/// （partial unique index 兜底）：点歌队列空时 Worker 轮播该歌单的曲目，
/// 替代旧独立空闲歌单。song_count 为列表查询的联表计数（详情查询同样带出）。
/// </summary>
public sealed record Playlist
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    public PlaylistSource Source { get; init; } = PlaylistSource.Local;

    /// <summary>平台侧歌单 id（导入来源；更新已导入歌单时按 (source, source_id) 对账）。</summary>
    public string SourceId { get; init; } = "";

    public bool IsIdle { get; init; }

    public PlaylistPlayMode PlayMode { get; init; } = PlaylistPlayMode.Sequential;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public int SongCount { get; init; }
}
