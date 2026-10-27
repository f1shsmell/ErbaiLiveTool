namespace Erbai.Contracts.Storage;

/// <summary>
/// 歌单曲目（playlist_songs 表行，按 position 顺序）。与旧 IdleSong 相同的
/// "歌名+歌手"文本模型——播放时实时搜索；search_json 为导入时落库的搜索候选
/// 快照（SongSearchResult JSON 数组），播放优先命中存量候选、缺失回退实时搜索。
/// </summary>
public sealed record PlaylistSong
{
    public required long Id { get; init; }

    public required long PlaylistId { get; init; }

    public required string Name { get; init; }

    public string Singer { get; init; } = "";

    public int Position { get; init; }

    public string? SearchJson { get; init; }
}
