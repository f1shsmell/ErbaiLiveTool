using System.Net.Http.Json;
using System.Text.Json;

namespace Erbai.Connector.QQMusic;

/// <summary>QQ 音乐目录搜索曲目（songId/songMid/songType 供原生插队）。</summary>
public sealed record QqCatalogSong(
    long SongId,
    string SongMid,
    int SongType,
    string Title,
    string Artist,
    string Album,
    string AlbumMid,
    int DurationSeconds,
    bool IsPlayable)
{
    public string StableIdentity => $"{SongId}:{SongMid}:{SongType}";
}

/// <summary>
/// QQ 音乐目录搜索（机制 docs/04 §1.5.3）：u.y.qq.com musicu.fcg
/// DoSearchForQQMusicDesktop 优先，空结果回退 c.y.qq.com client_search_cp。
/// 结果含 songId/songType（原生插队所需）。
/// </summary>
public sealed class QqCatalogClient : IDisposable
{
    private static readonly Uri SearchEndpoint = new("https://u.y.qq.com/cgi-bin/musicu.fcg");
    private const string LegacySearchEndpoint = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";

    private readonly HttpClient _http;

    public QqCatalogClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.Referrer = new Uri("https://y.qq.com/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 QQMusicControlPoc/1.0");
    }

    public async Task<IReadOnlyList<QqCatalogSong>> SearchAsync(string query, int count = 12, CancellationToken ct = default)
    {
        var payload = new
        {
            comm = new { ct = 24, cv = 0 },
            search = new
            {
                method = "DoSearchForQQMusicDesktop",
                module = "music.search.SearchCgiService",
                param = new
                {
                    grp = 1,
                    num_per_page = Math.Clamp(count, 1, 30),
                    page_num = 1,
                    query = query.Trim(),
                    search_type = 0,
                },
            },
        };
        try
        {
            using var response = await _http.PostAsJsonAsync(SearchEndpoint, payload, ct);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsStringAsync(ct);
            var songs = ParseSearchResponse(raw);
            if (songs.Count > 0)
            {
                return songs;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // 回退 legacy
        }

        return await SearchLegacyAsync(query, count, ct);
    }

    private static IReadOnlyList<QqCatalogSong> ParseSearchResponse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (!TryGetProperty(doc.RootElement, out var list, "search", "data", "body", "song", "list")
            || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var songs = new List<QqCatalogSong>();
        foreach (var item in list.EnumerateArray())
        {
            if (TryParseSong(item, out var song))
            {
                songs.Add(song);
            }
        }

        return songs;
    }

    private async Task<IReadOnlyList<QqCatalogSong>> SearchLegacyAsync(string query, int count, CancellationToken ct)
    {
        try
        {
            var uri = $"{LegacySearchEndpoint}?w={Uri.EscapeDataString(query)}&format=json&n={Math.Clamp(count, 1, 30)}&p=1";
            using var response = await _http.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            if (!TryGetProperty(doc.RootElement, out var list, "data", "song", "list")
                || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var songs = new List<QqCatalogSong>();
            foreach (var item in list.EnumerateArray())
            {
                if (TryParseLegacySong(item, out var song))
                {
                    songs.Add(song);
                }
            }

            return songs;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return [];
        }
    }

    private static bool TryParseSong(JsonElement item, out QqCatalogSong song)
    {
        song = null!;
        if (!item.TryGetProperty("songid", out var idElement)
            || !idElement.TryGetInt64(out var songId)
            || songId <= 0)
        {
            return false;
        }

        var title = GetString(item, "songname");
        var artist = GetString(item, "singer");
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var songMid = GetString(item, "songmid") ?? string.Empty;
        var album = GetString(item, "albumname") ?? string.Empty;
        var albumMid = GetString(item, "albummid") ?? string.Empty;
        var songType = item.TryGetProperty("songtype", out var type) && type.TryGetInt32(out var t) ? t : 0;
        var duration = item.TryGetProperty("interval", out var interval) && interval.TryGetInt32(out var d) ? d : 0;
        var isPlayable = !item.TryGetProperty("isonly", out var only) || only.GetInt32() == 0;
        song = new QqCatalogSong(songId, songMid, songType, title, artist ?? "", album, albumMid, duration, isPlayable);
        return true;
    }

    private static bool TryParseLegacySong(JsonElement item, out QqCatalogSong song)
    {
        song = null!;
        if (!item.TryGetProperty("songid", out var idElement)
            || !idElement.TryGetInt64(out var songId)
            || songId <= 0)
        {
            return false;
        }

        var title = GetString(item, "songname");
        var artist = GetString(item, "singer");
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var songMid = GetString(item, "songmid") ?? string.Empty;
        var album = GetString(item, "albumname") ?? string.Empty;
        var albumMid = GetString(item, "albummid") ?? string.Empty;
        var songType = item.TryGetProperty("songtype", out var type) && type.TryGetInt32(out var t) ? t : 0;
        song = new QqCatalogSong(songId, songMid, songType, title, artist ?? "", album, albumMid, 0, true);
        return true;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] path)
    {
        value = default;
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose() => _http.Dispose();
}
