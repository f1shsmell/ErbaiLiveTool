using System.Text.Encodings.Web;
using System.Text.Json;

namespace Erbai.Modules.SongRequest.Search;

/// <summary>
/// 单个搜索源适配器（数据驱动 spec：URL/参数/解析）。
/// 只做 HTTP + 解析；韧性（熔断/缓存）由 SearchCoordinator 统一包裹。
/// 端点：酷狗 mobilecdn.kugou.com/api/v3/search/song（hash 作 songmid + types 带
/// 128k/320k/flac hash）；网易云 music.163.com/api/search/get/web（id 作 songmid，
/// 毫秒转秒，多歌手 "/" 连接）；QQ c.y.qq.com/.../client_search_cp（songmid +
/// strMediaMid 必传，封面拼 y.gtimg.cn）。docs/03 §5。
/// </summary>
public abstract class SearchProvider
{
    public abstract string Key { get; }

    public abstract string DisplayName { get; }

    /// <summary>搜索前 <paramref name="limit"/> 首候选（已解析、含完整载荷；供原唱优先重排）。</summary>
    public abstract Task<IReadOnlyList<SongSearchResult>> SearchTopAsync(
        string keyword, bool preferHot, int limit, CancellationToken ct);

    /// <summary>搜索第一首候选；无结果返回 null（保留兼容旧调用/测试）。</summary>
    public async Task<SongSearchResult?> SearchFirstAsync(string keyword, bool preferHot, CancellationToken ct)
    {
        var top = await SearchTopAsync(keyword, preferHot, 1, ct);
        return top.FirstOrDefault();
    }

    protected static string FormatInterval(int seconds)
    {
        if (seconds <= 0)
        {
            return "";
        }

        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    protected static JsonElement? Find(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    protected static string Text(JsonElement? element, string fallback = "") =>
        element is { ValueKind: JsonValueKind.String or JsonValueKind.Number }
            ? element.Value.ToString()
            : fallback;
}

/// <summary>酷狗搜索源（docs/03 §5：hash 作 songmid，types 带三档 hash）。</summary>
public sealed class KugouSearchProvider : SearchProvider
{
    private const string Url = "http://mobilecdn.kugou.com/api/v3/search/song";
    private readonly SearchHttpClient _client;

    public KugouSearchProvider(SearchHttpClient client) => _client = client;

    public override string Key => "kugou";

    public override string DisplayName => "酷狗";

    public override async Task<IReadOnlyList<SongSearchResult>> SearchTopAsync(
        string keyword, bool preferHot, int limit, CancellationToken ct)
    {
        var body = await _client.GetJsonAsync(Url, new Dictionary<string, string>
        {
            ["format"] = "json",
            ["keyword"] = keyword,
            ["page"] = "1",
            ["pagesize"] = "5",
            ["showtype"] = "1",
        }, new Dictionary<string, string>(), ct);
        if (body is null)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("info", out var info) ||
            info.ValueKind != JsonValueKind.Array ||
            info.GetArrayLength() == 0)
        {
            return [];
        }

        var result = new List<SongSearchResult>();
        foreach (var item in info.EnumerateArray().Take(limit))
        {
            var parsed = ParseSong(item);
            if (parsed is not null)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static SongSearchResult? ParseSong(JsonElement first)
    {
        var songName = Text(Find(first, "songname"));
        var singer = Text(Find(first, "singername"));
        var hash = Text(Find(first, "hash"));
        var hash320 = Text(Find(first, "320hash"));
        var hashFlac = Text(Find(first, "sqhash"));
        var duration = Text(Find(first, "duration"), "0");
        int.TryParse(duration, out var durationSeconds);
        var img = Text(Find(first, "imgUrl")) != "" ? Text(Find(first, "imgUrl")) : Text(Find(first, "img"));

        var types = new List<SongType>();
        if (hash.Length > 0)
        {
            types.Add(new SongType { Type = "128k", Size = "", Hash = hash });
        }

        if (hash320.Length > 0)
        {
            types.Add(new SongType { Type = "320k", Size = "", Hash = hash320 });
        }

        if (hashFlac.Length > 0)
        {
            types.Add(new SongType { Type = "flac", Size = "", Hash = hashFlac });
        }

        if (types.Count == 0)
        {
            types.Add(new SongType { Type = "128k", Size = "", Hash = hash });
        }

        return new SongSearchResult
        {
            Source = "kugou",
            Name = songName,
            Singer = singer,
            SongMid = hash, // kg 用 hash 作 songmid
            Img = img,
            AlbumId = Text(Find(first, "album_id")),
            Interval = FormatInterval(durationSeconds),
            AlbumName = Text(Find(first, "album_name")),
            Types = types,
            Hash = hash,
        };
    }
}

/// <summary>网易云搜索源（docs/03 §5：id 作 songmid，毫秒转秒，多歌手 "/" 连接）。</summary>
public sealed class NeteaseSearchProvider : SearchProvider
{
    private const string WebUrl = "https://music.163.com/api/search/get/web";
    private const string CloudUrl = "https://music.163.com/api/cloudsearch/pc";
    private readonly SearchHttpClient _client;

    public NeteaseSearchProvider(SearchHttpClient client) => _client = client;

    public override string Key => "netease";

    public override string DisplayName => "网易云";

    public override async Task<IReadOnlyList<SongSearchResult>> SearchTopAsync(
        string keyword, bool preferHot, int limit, CancellationToken ct)
    {
        // 双端点并发，成功即用：原单端点（search/get/web）慢/超时时整单被拖到上限
        // （docs/00 修复记录 #19）；任一成功即返回，全失败返回空
        var tasks = new[]
        {
            SearchEndpointAsync(WebUrl, keyword, preferHot, ct),
            SearchEndpointAsync(CloudUrl, keyword, preferHot, ct),
        };
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var first = results.FirstOrDefault(r => r is { Count: > 0 });
        return first is null ? [] : first.Take(limit).ToList();
    }

    private async Task<IReadOnlyList<SongSearchResult>?> SearchEndpointAsync(string url, string keyword, bool preferHot, CancellationToken ct)
    {
        try
        {
            var body = await _client.PostFormAsync(url, new Dictionary<string, string>
            {
                ["s"] = keyword,
                ["type"] = "1",
                ["offset"] = "0",
                ["limit"] = "5",
            }, new Dictionary<string, string>
            {
                ["Referer"] = "https://music.163.com/",
                ["Content-Type"] = "application/x-www-form-urlencoded",
            }, ct);
            if (body is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("code", out var code) ||
                code.GetInt32() != 200 ||
                !doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("songs", out var songs) ||
                songs.ValueKind != JsonValueKind.Array ||
                songs.GetArrayLength() == 0)
            {
                return null;
            }

            // prefer_hot：仅当全部结果都带 popularity 才按热度降序取最热版本
            //（脏值按 0），部分带/都不带 → 保持源返回顺序
            var parsed = new List<(SongSearchResult Song, bool HasPopularity, int Popularity)>();
            foreach (var item in songs.EnumerateArray())
            {
                var song = ParseSong(item);
                if (song is null)
                {
                    continue;
                }

                var hasPopularity = item.TryGetProperty("popularity", out var pop);
                parsed.Add((song, hasPopularity, hasPopularity ? SearchCoordinator.Popularity(pop) : 0));
            }

            if (parsed.Count == 0)
            {
                return null;
            }

            if (preferHot && parsed.All(p => p.HasPopularity))
            {
                parsed.Sort((a, b) => b.Popularity.CompareTo(a.Popularity));
            }

            return parsed.Select(p => p.Song).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null; // 端点失败由另一端点兜底（整体超时仍由编排层单源上限控制）
        }
    }

    private static SongSearchResult? ParseSong(JsonElement first)
    {
        var singer = string.Join("/", first.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? artists.EnumerateArray().Select(a => Text(Find(a, "name"))).Where(s => s.Length > 0)
            : []);
        var album = Find(first, "album");
        var albumName = album is { } a ? Text(Find(a, "name")) : "";
        var albumId = album is { } b ? Text(Find(b, "id")) : "";
        var songId = Text(Find(first, "id"));
        var durationMs = Text(Find(first, "duration"), "0");
        long.TryParse(durationMs, out var durationMillis);
        var img = album is { } c ? Text(Find(c, "picUrl")) : "";

        return new SongSearchResult
        {
            Source = "netease",
            Name = Text(Find(first, "name")),
            Singer = singer,
            SongMid = songId, // wy 用歌曲 id 作 songmid
            Img = img,
            AlbumId = albumId,
            Interval = FormatInterval((int)(durationMillis / 1000)),
            AlbumName = albumName,
            Types =
            [
                new SongType { Type = "128k", Size = "" },
                new SongType { Type = "320k", Size = "" },
                new SongType { Type = "flac", Size = "" },
            ],
            Hash = "",
        };
    }
}

/// <summary>QQ 音乐搜索源（docs/03 §5：mid 作 songmid，media_mid 作 strMediaMid，
/// 封面拼 y.gtimg.cn；2026-09 迁移 musicu.fcg——client_search_cp 已下线/风控 500）。</summary>
public sealed class QqMusicSearchProvider : SearchProvider
{
    private const string Url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string SmartboxUrl = "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg";
    private const string DetailUrl = "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg";
    private static readonly IReadOnlyDictionary<string, string> QqHeaders =
        new Dictionary<string, string> { ["Referer"] = "https://y.qq.com/" };
    private readonly SearchHttpClient _client;

    /// <summary>rpc 序列化选项：QQ 服务器不认 \uXXXX 转义(实测中文变 \u 后搜不到结果)，
    /// 必须输出原始 UTF-8 中文。UnsafeRelaxedJsonEscaping 仍转义 " 和 \，JSON 结构安全。</summary>
    private static readonly JsonSerializerOptions RpcJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public QqMusicSearchProvider(SearchHttpClient client) => _client = client;

    public override string Key => "qqmusic";

    public override string DisplayName => "QQ音乐";

    public override async Task<IReadOnlyList<SongSearchResult>> SearchTopAsync(
        string keyword, bool preferHot, int limit, CancellationToken ct)
    {
        // 主通道 musicu.fcg POST(编码双坑已修,2026-09)。该搜索接口间歇性风控
        // (HTTP 500 / 2001+sum:0 空结果,用户实测 23:14 仍无候选),空结果或异常时
        // 回退 smartbox_new.fcg(轻量建议接口,稳定)+ fcg_play_single_song.fcg
        // 按 songmid 补详情——保证单源播放器(QQ 客户端)尽量拿到可播候选。
        try
        {
            var primary = await SearchViaMusicuAsync(keyword, limit, ct);
            if (primary.Count > 0)
            {
                return primary;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 主通道异常(网络/解析)不致命：走回退通道兜底
        }

        return await SearchViaSmartboxAsync(keyword, limit, ct);
    }

    /// <summary>主通道：musicu.fcg 统一搜索接口(POST body 携带 JSON-RPC，
    /// DoSearchForQQMusicDesktop 现役方法,须 grp:1 否则空列表)；返回 req_0.data.body.song.list。
    /// 编码双坑(2026-09 实测,docs/00)：① JsonSerializer 默认把中文转义成 \uXXXX,
    /// QQ 服务器不认 → 搜不到；必须 UnsafeRelaxedJsonEscaping 输出原始 UTF-8 中文。
    /// ② query 里 data 参数经 new Uri 规范化会被改写编码(%22 变裸引号、中文原样),
    /// 酷狗/网易云宽容、QQ 严格 → 必须 POST body 发送。</summary>
    private async Task<IReadOnlyList<SongSearchResult>> SearchViaMusicuAsync(
        string keyword, int limit, CancellationToken ct)
    {
        var rpc = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["req_0"] = new Dictionary<string, object>
            {
                ["module"] = "music.search.SearchCgiService",
                ["method"] = "DoSearchForQQMusicDesktop",
                ["param"] = new Dictionary<string, object>
                {
                    ["grp"] = 1,
                    ["num_per_page"] = Math.Max(limit, 5),
                    ["page_num"] = 1,
                    ["query"] = keyword,
                    ["search_type"] = 0,
                },
            },
        }, RpcJsonOptions);
        var body = await _client.PostJsonAsync(Url, rpc, QqHeaders, ct);
        if (body is null)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("req_0", out var req) ||
            !req.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("body", out var bodyNode) ||
            !bodyNode.TryGetProperty("song", out var song) ||
            !song.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() == 0)
        {
            return [];
        }

        var result = new List<SongSearchResult>();
        foreach (var item in list.EnumerateArray().Take(limit))
        {
            var parsed = ParseSong(item);
            if (parsed is not null)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    /// <summary>
    /// 回退通道：smartbox_new.fcg 搜索建议(返回 id=songid / mid=songmid / name / singer,
    /// 无 songtype/interval/album/file) → 逐条 fcg_play_single_song.fcg 按 songmid 补详情
    /// (字段结构与 musicu 一致,直接复用 ParseSong)。详情失败保留 smartbox 基础字段
    /// (songtype 默认 0——QQ 绝大多数歌曲为单曲 type=0)。
    /// </summary>
    private async Task<IReadOnlyList<SongSearchResult>> SearchViaSmartboxAsync(
        string keyword, int limit, CancellationToken ct)
    {
        var body = await _client.GetJsonAsync(SmartboxUrl, new Dictionary<string, string>
        {
            ["key"] = keyword,
            ["format"] = "json",
            ["utf8"] = "1",
        }, QqHeaders, ct);
        if (body is null)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("song", out var song) ||
            !song.TryGetProperty("itemlist", out var list) ||
            list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() == 0)
        {
            return [];
        }

        var result = new List<SongSearchResult>();
        foreach (var item in list.EnumerateArray().Take(limit))
        {
            var songMid = Text(Find(item, "mid"));
            if (songMid.Length == 0)
            {
                continue;
            }

            var detail = await TryFetchDetailAsync(songMid, ct);
            if (detail is not null)
            {
                result.Add(detail);
                continue;
            }

            long.TryParse(Text(Find(item, "id"), "0"), out var songId);
            result.Add(new SongSearchResult
            {
                Source = "qqmusic",
                Name = Text(Find(item, "name")),
                Singer = Text(Find(item, "singer")),
                SongMid = songMid,
                SongId = songId,
                SongType = 0, // 单曲默认；详情接口不可用时无法精确鉴定
                Types = [new SongType { Type = "128k", Size = "" }],
            });
        }

        return result;
    }

    /// <summary>按 songmid 拉详情(songtype/interval/album/file/media_mid/isonly)，
    /// 失败返回 null(调用方保留 smartbox 基础字段)。songmid 为 ASCII,无编码坑。</summary>
    private async Task<SongSearchResult?> TryFetchDetailAsync(string songMid, CancellationToken ct)
    {
        try
        {
            var body = await _client.GetJsonAsync(DetailUrl, new Dictionary<string, string>
            {
                ["songmid"] = songMid,
                ["format"] = "json",
                ["utf8"] = "1",
            }, QqHeaders, ct);
            if (body is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                return null;
            }

            // 详情条目字段与 musicu.fcg song 条目一致(mid/id/type/interval/album/file/singer)
            return ParseSong(data[0]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // 详情补全失败不致命
        }
    }

    private static SongSearchResult? ParseSong(JsonElement first)
    {
        var singer = string.Join("/", first.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array
            ? singers.EnumerateArray().Select(s => Text(Find(s, "name"))).Where(s => s.Length > 0)
            : []);
        // 新接口字段（musicu.fcg）：mid 作 songmid、name 作歌名、id 作 songid、
        // type 作 songtype、album 对象、file 对象（size_* / media_mid）
        var songMid = Text(Find(first, "mid"));
        if (songMid.Length == 0)
        {
            return null; // 缺 mid 的条目不是有效歌曲（跳过）
        }

        var interval = Text(Find(first, "interval"), "0");
        int.TryParse(interval, out var intervalSeconds);
        var album = Find(first, "album");
        var albumMid = album is { } a ? Text(Find(a, "mid")) : "";
        var albumName = album is { } b ? Text(Find(b, "name")) : "";
        var albumId = album is { } c ? Text(Find(c, "id")) : "";
        var img = albumMid.Length > 0
            ? $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg"
            : "";
        var file = Find(first, "file");
        var size128 = file is { } d ? Text(Find(d, "size_128mp3"), "0") : "0";
        var size320 = file is { } e ? Text(Find(e, "size_320mp3"), "0") : "0";
        var sizeFlac = file is { } f ? Text(Find(f, "size_flac"), "0") : "0";
        var mediaMid = file is { } g ? Text(Find(g, "media_mid")) : "";
        // songid/songtype：QQ 原生插队（/playbysongid id_0=={songid}&&songtype_0=={songtype}）必需，
        // 否则 QqMusicConnector.ParsePayload 拿不到载荷、每个候选都被拒（docs/00 用户实测"QQ 音乐无法点歌"）
        long.TryParse(Text(Find(first, "id"), "0"), out var songId);
        int.TryParse(Text(Find(first, "type"), "0"), out var songType);
        var types = new List<SongType>();
        if (long.TryParse(size128, out var s128) && s128 > 0)
        {
            types.Add(new SongType { Type = "128k", Size = size128 });
        }

        if (long.TryParse(size320, out var s320) && s320 > 0)
        {
            types.Add(new SongType { Type = "320k", Size = size320 });
        }

        if (long.TryParse(sizeFlac, out var sFlac) && sFlac > 0)
        {
            types.Add(new SongType { Type = "flac", Size = sizeFlac });
        }

        if (types.Count == 0)
        {
            types.Add(new SongType { Type = "128k", Size = "" });
        }

        return new SongSearchResult
        {
            Source = "qqmusic",
            Name = Text(Find(first, "name")),
            Singer = singer,
            SongMid = songMid,
            SongId = songId,
            SongType = songType,
            Img = img,
            AlbumId = albumId,
            Interval = FormatInterval(intervalSeconds),
            AlbumName = albumName,
            Types = types,
            Hash = "",
            StrMediaMid = mediaMid,
        };
    }
}
