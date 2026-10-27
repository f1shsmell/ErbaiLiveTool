using System.Text.Json;
using Erbai.Contracts.Requests;
using Erbai.Modules.SongRequest.Search;
using Erbai.Contracts.Storage;

namespace Erbai.Modules.SongRequest.Import;

/// <summary>
/// 酷狗歌单导入客户端（2026-10 实测定型）：mobilecdn v3 <c>special/info</c> 取
/// 歌单名 + <c>special/song</c> 按 pagesize=100 分页取曲（免签名，匿名可用；与
/// 现有酷狗搜索同域同族）。musicdl 走的 gatewayretry 端点带 MD5 签名，没有必要。
/// 曲目行只有 <c>filename</c>（"歌手 - 歌名"）没有独立歌名/歌手字段——按首个
/// " - " 拆分。短链（t1.kugou.com）跟随重定向解析 specialid。
/// </summary>
public sealed class KugouPlaylistClient : IPlaylistImportClient
{
    private const string Base = "http://mobilecdn.kugou.com/api/v3";

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    private readonly SearchHttpClient _http;

    public KugouPlaylistClient(SearchHttpClient http) => _http = http;

    public PlaylistSource Platform => PlaylistSource.Kugou;

    public async Task<ImportedPlaylist> FetchAsync(PlaylistShareLink link, CancellationToken ct)
    {
        var specialId = link.PlaylistId;
        if (specialId is null)
        {
            specialId = await ShortLinkResolver.ResolveAsync(link.Url, ShareLinkParser.ExtractKugouSpecialId, ct: ct)
                ?? throw new PlaylistImportException("无法从酷狗短链接解析出歌单 id（新版分享可能是 global_collection_id 格式，暂不支持）");
            if (specialId.Length == 0)
            {
                throw new PlaylistImportException("无法从酷狗短链接解析出歌单 id（链接可能已失效）");
            }
        }

        return await FetchByIdAsync(specialId, ct);
    }

    /// <summary>按 specialid 拉取歌单（info 取名 + song 分页取曲）。</summary>
    public async Task<ImportedPlaylist> FetchByIdAsync(string specialId, CancellationToken ct = default)
    {
        // 歌单名（失败不致命——回落占位名，曲目仍可导入）
        var name = $"酷狗歌单 {specialId}";
        var infoBody = await _http.GetJsonAsync(
            $"{Base}/special/info",
            new Dictionary<string, string> { ["specialid"] = specialId, ["format"] = "json" },
            EmptyHeaders,
            ct);
        if (infoBody is not null)
        {
            try
            {
                using var infoDoc = JsonDocument.Parse(infoBody);
                if (infoDoc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("specialname", out var sn) &&
                    sn.GetString()?.Trim() is { Length: > 0 } specialName)
                {
                    name = specialName;
                }
            }
            catch (JsonException)
            {
                // 名字拿不到就用占位，继续取曲目
            }
        }

        var songs = new List<ImportedSong>();
        const int pageSize = 100;
        var page = 1;
        while (page <= 100) // 防御上限：100 页 × 100 首
        {
            var body = await _http.GetJsonAsync(
                $"{Base}/special/song",
                new Dictionary<string, string>
                {
                    ["specialid"] = specialId,
                    ["page"] = page.ToString(),
                    ["pagesize"] = pageSize.ToString(),
                    ["format"] = "json",
                },
                EmptyHeaders,
                ct);
            if (body is null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
            if (data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Array ||
                info.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in info.EnumerateArray())
            {
                var song = ParseSong(item);
                if (song is not null)
                {
                    songs.Add(song);
                }
            }

            var total = data.TryGetProperty("total", out var totalEl) && totalEl.TryGetInt32(out var t) ? t : -1;
            // total 可用时以它为准翻页；缺失才退化"页未满即结束"启发式
            if (total >= 0 ? songs.Count >= total : info.GetArrayLength() < pageSize)
            {
                break;
            }

            page++;
        }

        if (songs.Count == 0)
        {
            throw new PlaylistImportException("酷狗歌单接口未返回任何曲目（歌单可能为空、已下架或 id 无效）");
        }

        return new ImportedPlaylist(PlaylistSource.Kugou, name, specialId, songs);
    }

    /// <summary>与 SearchProviders 的酷狗候选同构：hash 作 songmid、三档 hash 音质表。</summary>
    private static ImportedSong? ParseSong(JsonElement first)
    {
        // filename = "歌手 - 歌名"（按首个 " - " 拆；歌名含 " - " 时歌手取左侧不受影响）
        var filename = first.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(filename))
        {
            return null;
        }

        var separator = filename.IndexOf(" - ", StringComparison.Ordinal);
        var singer = separator > 0 ? filename[..separator].Trim() : "";
        var name = separator > 0 ? filename[(separator + 3)..].Trim() : filename.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var hash = Text(Find(first, "hash"));
        var hash320 = Text(Find(first, "320hash"));
        var hashFlac = Text(Find(first, "sqhash"));
        _ = int.TryParse(Text(Find(first, "duration"), "0"), out var durationSeconds);

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

        var candidate = new SongSearchResult
        {
            Source = "kugou",
            Name = name,
            Singer = singer,
            SongMid = hash,
            AlbumId = Text(Find(first, "album_id")),
            Interval = ImportFormat.FormatInterval(durationSeconds),
            AlbumName = Text(Find(first, "album_name")),
            Types = types,
            Hash = hash,
        };
        return new ImportedSong(name, singer, candidate);
    }

    private static JsonElement? Find(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : null;

    private static string Text(JsonElement? element, string fallback = "") =>
        element is { } e && (e.ValueKind == JsonValueKind.String || e.ValueKind == JsonValueKind.Number)
            ? e.ToString() ?? fallback
            : fallback;
}
