using System.Text.Json;
using Erbai.Contracts.Requests;
using Erbai.Modules.SongRequest.Search;
using Erbai.Contracts.Storage;

namespace Erbai.Modules.SongRequest.Import;

/// <summary>
/// QQ 音乐歌单导入客户端（2026-10 实测定型）。桌面 UA + c.y.qq.com 老路径已被
/// "check privacy error" 拦截，musicu.fcg 新模块（PlaylistSonglist 等）匿名一律
/// 500003 要登录；<b>移动端 qzone-music 变体 + 手机 UA 匿名可取全量</b>（实测
/// 1283 首单次全返，无 30 首截断）。
/// </summary>
public sealed class QqPlaylistClient : IPlaylistImportClient
{
    /// <summary>移动端端点要求手机 UA（桌面 UA 同 URL 被 privacy check 拒绝）。</summary>
    public const string MobileUserAgent =
        "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36" +
        " (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36";

    private const string Endpoint = "http://i.y.qq.com/qzone-music/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg";

    private static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string> { ["Referer"] = "http://y.qq.com/" };

    private readonly SearchHttpClient _http;

    public QqPlaylistClient(SearchHttpClient http) => _http = http;

    public PlaylistSource Platform => PlaylistSource.Qqmusic;

    public async Task<ImportedPlaylist> FetchAsync(PlaylistShareLink link, CancellationToken ct)
    {
        var playlistId = link.PlaylistId;
        if (playlistId is null)
        {
            // c6.y.qq.com 短链：跟随重定向取 disstid
            playlistId = await ShortLinkResolver.ResolveAsync(link.Url, ShareLinkParser.ExtractQqPlaylistId, ct: ct)
                ?? throw new PlaylistImportException("无法从 QQ 音乐短链接解析出歌单 id（链接可能已失效）");
            if (playlistId.Length == 0)
            {
                throw new PlaylistImportException("无法从 QQ 音乐短链接解析出歌单 id（链接可能已失效）");
            }
        }

        return await FetchByIdAsync(playlistId, ct);
    }

    /// <summary>按 disstid 拉取歌单（全量单次返回）。</summary>
    public async Task<ImportedPlaylist> FetchByIdAsync(string playlistId, CancellationToken ct = default)
    {
        var body = await _http.GetJsonAsync(
            Endpoint,
            new Dictionary<string, string>
            {
                ["type"] = "1",
                ["json"] = "1",
                ["utf8"] = "1",
                ["onlysong"] = "0",
                ["disstid"] = playlistId,
                ["format"] = "json",
            },
            Headers,
            ct);
        if (body is null)
        {
            throw new PlaylistImportException("QQ 音乐歌单接口无响应（稍后再试）");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new PlaylistImportException("QQ 音乐歌单接口返回了无法解析的内容");
        }

        using (doc)
        {
            var cd = doc.RootElement.TryGetProperty("cdlist", out var cds) && cds.ValueKind == JsonValueKind.Array && cds.GetArrayLength() > 0
                ? cds[0]
                : default;
            if (cd.ValueKind != JsonValueKind.Object)
            {
                throw new PlaylistImportException("QQ 音乐歌单不存在、未公开或接口被风控（稍后再试）");
            }

            var name = cd.TryGetProperty("dissname", out var nameEl) ? nameEl.GetString()?.Trim() ?? "" : "";
            if (name.Length == 0)
            {
                name = $"QQ 音乐歌单 {playlistId}";
            }

            var songs = new List<ImportedSong>();
            if (cd.TryGetProperty("songlist", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var song in list.EnumerateArray())
                {
                    var parsed = ParseSong(song);
                    if (parsed is not null)
                    {
                        songs.Add(parsed);
                    }
                }
            }

            if (songs.Count == 0)
            {
                throw new PlaylistImportException("QQ 音乐歌单接口未返回任何曲目（歌单可能为空或已设为私密）");
            }

            return new ImportedPlaylist(PlaylistSource.Qqmusic, name, playlistId, songs);
        }
    }

    /// <summary>
    /// 与 SearchProviders 的 QQ 候选同构：songmid/songid/songtype/albummid/sizes，
    /// QQ 原生插队（/playbysongid）依赖 songid/songtype。
    /// </summary>
    private static ImportedSong? ParseSong(JsonElement first)
    {
        var name = Text(Find(first, "songname"));
        if (name.Length == 0)
        {
            return null;
        }

        var singer = string.Join("/",
            Find(first, "singer") is { ValueKind: JsonValueKind.Array } singers
                ? singers.EnumerateArray().Select(a => Text(Find(a, "name"))).Where(s => s.Length > 0)
                : []);
        var songMid = Text(Find(first, "songmid"));
        var albumMid = Text(Find(first, "albummid"));
        var albumName = Text(Find(first, "albumname"));
        var interval = Text(Find(first, "interval"), "0");
        _ = int.TryParse(interval, out var intervalSeconds);
        var size128 = Text(Find(first, "size128"), "0");
        var size320 = Text(Find(first, "size320"), "0");
        var sizeFlac = Text(Find(first, "sizeflac"), "0");

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

        long.TryParse(Text(Find(first, "songid"), "0"), out var songId);
        int.TryParse(Text(Find(first, "type"), "0"), out var songType);

        var candidate = new SongSearchResult
        {
            Source = "qqmusic",
            Name = name,
            Singer = singer,
            SongMid = songMid,
            SongId = songId,
            SongType = songType,
            Img = albumMid.Length > 0
                ? $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg"
                : "",
            AlbumId = Text(Find(first, "albumid")),
            Interval = ImportFormat.FormatInterval(intervalSeconds),
            AlbumName = albumName,
            Types = types,
            Hash = "",
            // 歌单行无 file.media_mid：strMediaMid 回落 songmid（lx 载荷 QQ 源的常见等值）
            StrMediaMid = songMid,
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
