using System.Text.Json;
using Erbai.Contracts.Requests;
using Erbai.Modules.SongRequest.Search;
using Erbai.Contracts.Storage;

namespace Erbai.Modules.SongRequest.Import;

/// <summary>
/// 网易云歌单导入客户端（2026-10 实测定型）：
/// - 歌单详情走 <c>/api/v6/playlist/detail</c>（POST form id/n，匿名可用，全量
///   tracks 带新格式字段 ar/dt/al；旧 /api/playlist/detail 已不再返回 tracks）；
/// - 用户公开歌单列表走 <c>/api/user/playlist/</c>（GET uid，匿名可用，more 分页）。
///   注意：匿名「昵称搜用户」（search type=1002 与 weapi 全系）已被风控——搜用户
///   名不可用，UI 侧改为让用户粘贴用户主页链接/UID。
/// </summary>
public sealed class NeteasePlaylistClient : IPlaylistImportClient
{
    private const string BaseReferer = "https://music.163.com/";

    private static readonly IReadOnlyDictionary<string, string> RefererHeader =
        new Dictionary<string, string> { ["Referer"] = BaseReferer };

    private readonly SearchHttpClient _http;

    public NeteasePlaylistClient(SearchHttpClient http) => _http = http;

    public PlaylistSource Platform => PlaylistSource.Netease;

    public async Task<ImportedPlaylist> FetchAsync(PlaylistShareLink link, CancellationToken ct)
    {
        var playlistId = link.PlaylistId;
        if (playlistId is null)
        {
            // 163cn.tv 短链：跟随重定向取真实歌单 id；解析后可能是用户主页——
            // 重定向链中带 user/home 的直接报可读错误
            playlistId = await ShortLinkResolver.ResolveAsync(link.Url, ShareLinkParser.ExtractNeteasePlaylistId, ct: ct)
                ?? throw new PlaylistImportException("无法从网易云短链接解析出歌单 id（链接可能已失效）");
            if (playlistId.Length == 0)
            {
                throw new PlaylistImportException("无法从网易云短链接解析出歌单 id（链接可能已失效）");
            }
        }

        return await FetchByIdAsync(playlistId, ct);
    }

    /// <summary>按歌单 id 拉取详情（UI 的用户歌单列表选中后也走这里）。</summary>
    public async Task<ImportedPlaylist> FetchByIdAsync(string playlistId, CancellationToken ct = default)
    {
        var body = await _http.PostFormAsync(
            "https://music.163.com/api/v6/playlist/detail",
            new Dictionary<string, string> { ["id"] = playlistId, ["n"] = "1000" },
            RefererHeader,
            ct);
        if (body is null)
        {
            throw new PlaylistImportException("网易云歌单接口无响应（可能被风控或歌单不存在，稍后再试）");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new PlaylistImportException("网易云歌单接口返回了无法解析的内容（可能被风控）");
        }

        using (doc)
        {
            var playlist = doc.RootElement.TryGetProperty("playlist", out var pl) ? pl : default;
            if (playlist.ValueKind != JsonValueKind.Object || playlist.TryGetProperty("name", out var nameEl) == false)
            {
                throw new PlaylistImportException("网易云歌单不存在或未公开（私密歌单无法导入）");
            }

            var name = nameEl.GetString()?.Trim() ?? "";
            if (name.Length == 0)
            {
                name = $"网易云歌单 {playlistId}";
            }

            var songs = new List<ImportedSong>();
            if (playlist.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var track in tracks.EnumerateArray())
                {
                    var song = ParseTrack(track);
                    if (song is not null)
                    {
                        songs.Add(song);
                    }
                }
            }

            if (songs.Count == 0)
            {
                throw new PlaylistImportException("网易云歌单接口未返回任何曲目（可能被风控，稍后再试）");
            }

            return new ImportedPlaylist(PlaylistSource.Netease, name, playlistId, songs);
        }
    }

    /// <summary>
    /// 拉取某用户（主页链接/UID）的全部公开歌单（创建 + 收藏），供 UI 列表选择。
    /// </summary>
    public async Task<IReadOnlyList<UserPlaylistSummary>> ListUserPlaylistsAsync(string userId, CancellationToken ct = default)
    {
        var summaries = new List<UserPlaylistSummary>();
        var offset = 0;
        const int pageSize = 100;
        while (offset < 2000) // 防御上限：40 页足够任何正常用户
        {
            var body = await _http.GetJsonAsync(
                "https://music.163.com/api/user/playlist/",
                new Dictionary<string, string> { ["uid"] = userId, ["limit"] = pageSize.ToString(), ["offset"] = offset.ToString() },
                RefererHeader,
                ct);
            if (body is null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("playlist", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var item in array.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "";
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var count = item.TryGetProperty("trackCount", out var cntEl) ? cntEl.GetInt32() : 0;
                if (id.Length > 0 && name.Length > 0)
                {
                    summaries.Add(new UserPlaylistSummary(id, name, count));
                }
            }

            var more = doc.RootElement.TryGetProperty("more", out var moreEl) && moreEl.ValueKind == JsonValueKind.True;
            if (!more || array.GetArrayLength() == 0)
            {
                break;
            }

            offset += pageSize;
        }

        return summaries;
    }

    /// <summary>v6 track（新格式）：name / id / ar[]（歌手）/ al（专辑）/ dt（时长 ms）。</summary>
    private static ImportedSong? ParseTrack(JsonElement track)
    {
        var name = track.TryGetProperty("name", out var nameEl) ? nameEl.GetString()?.Trim() ?? "" : "";
        if (name.Length == 0)
        {
            return null;
        }

        var id = track.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "";
        var singer = string.Join("/",
            track.TryGetProperty("ar", out var ar) && ar.ValueKind == JsonValueKind.Array
                ? ar.EnumerateArray().Select(a => a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Where(s => s.Length > 0)
                : []);
        var albumName = track.TryGetProperty("al", out var al) && al.ValueKind == JsonValueKind.Object
            ? al.TryGetProperty("name", out var an) ? an.GetString() ?? "" : ""
            : "";
        var durationMs = track.TryGetProperty("dt", out var dt) && dt.TryGetInt64(out var ms) ? ms : 0;

        var candidate = new SongSearchResult
        {
            Source = "netease",
            Name = name,
            Singer = singer,
            SongMid = id,
            Interval = ImportFormat.FormatInterval((int)(durationMs / 1000)),
            AlbumName = albumName,
            Types =
            [
                new SongType { Type = "128k", Size = "" },
                new SongType { Type = "320k", Size = "" },
                new SongType { Type = "flac", Size = "" },
            ],
            Hash = "",
        };
        return new ImportedSong(name, singer, candidate);
    }
}

/// <summary>用户公开歌单摘要（用户主页拉取流程的列表项）。</summary>
public sealed record UserPlaylistSummary(string Id, string Name, int TrackCount);
