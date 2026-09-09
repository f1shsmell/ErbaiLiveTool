namespace Erbai.Modules.SongRequest.Search;

using System.Text.Json.Serialization;

/// <summary>LX Music music/play 的音质条目（128k/320k/flac）。</summary>
public sealed record SongType
{
    /// <summary>音质档位；LX 客户端 qualityFilter 仅认 128k/320k/flac/flac24bit，其余整条丢弃。</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("size")]
    public string Size { get; init; } = "";

    /// <summary>酷狗音质专属：该音质的独立 hash。</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; init; } = "";
}

/// <summary>
/// 单个搜索候选（平移旧 LX Music music/play 载荷字段：
/// source/name/singer/songmid/img/albumId/interval(mm:ss)/albumName/types/hash）。
/// 由三源搜索产出，经 System.Text.Json 序列化进 song_requests.search_result_json；
/// 播放状态机消费 candidates 做换源。属性名即 LX 线格式（JsonPropertyName
/// 小写驼峰）——序列化产物直接是 music/play 载荷，NativeData 消费方按小写键读取。
/// </summary>
public sealed record SongSearchResult
{
    /// <summary>音源："kugou" | "netease" | "qqmusic"（lxmusic 内部源 "idle" 归一为 kw 属播放器侧）。</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("singer")]
    public string Singer { get; init; } = "";

    /// <summary>酷狗 hash / 网易云 id / QQ songmid 等稳定 ID（对应 LX music/play 的 songmid）。</summary>
    [JsonPropertyName("songmid")]
    public required string SongMid { get; init; }

    [JsonPropertyName("img")]
    public string Img { get; init; } = "";

    [JsonPropertyName("albumId")]
    public string AlbumId { get; init; } = "";

    /// <summary>时长（mm:ss 字符串，与 LX 载荷一致）。</summary>
    [JsonPropertyName("interval")]
    public string Interval { get; init; } = "";

    [JsonPropertyName("albumName")]
    public string AlbumName { get; init; } = "";

    /// <summary>音质表（酷狗带 hash；网易云固定三档；QQ 按 size 字段）。</summary>
    [JsonPropertyName("types")]
    public IReadOnlyList<SongType> Types { get; init; } = [];

    /// <summary>酷狗 128k hash（与 SongMid 相同）。</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; init; } = "";

    /// <summary>QQ 音乐必传字段（strMediaMid，songmid 之外的播放凭证）。</summary>
    [JsonPropertyName("strMediaMid")]
    public string StrMediaMid { get; init; } = "";

    /// <summary>QQ 音乐数字歌曲 id（songid，原生插队 /playbysongid 必需）；非 QQ 源为 0。</summary>
    [JsonPropertyName("songId")]
    public long SongId { get; init; }

    /// <summary>QQ 音乐 songtype（原生插队必需）；非 QQ 源为 0。</summary>
    [JsonPropertyName("songType")]
    public int SongType { get; init; }
}
