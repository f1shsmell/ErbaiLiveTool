using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connector.Netease;

/// <summary>
/// 网易云搜索客户端（机制参考上游，代码表达沿用上游）：多端点并发序尝试
/// （api/search/get/web → api/search/get → api/cloudsearch/pc），POST form，
/// Referrer + 国内绕过头；响应分类（Results/Empty/Retryable）；id=xxx 或
/// 纯数字走 song/detail 精确解析。
/// </summary>
public sealed class NeteaseSearchClient
{
    private static readonly string[] Endpoints =
    [
        "https://music.163.com/api/search/get/web",
        "https://music.163.com/api/search/get",
        "https://music.163.com/api/cloudsearch/pc",
    ];

    private readonly HttpClient _http;

    public NeteaseSearchClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>关键词搜索（多端点并发，任一成功即返回；5s/端点、6s 总预算）。</summary>
    public async Task<IReadOnlyList<PlayerTrack>> SearchByKeywordAsync(string query, CancellationToken ct)
    {
        // 原实现三端点串行（5s/端点）→ 最坏 10s+、总预算 14s；改并发后
        // 实际耗时 = 最快成功端点（docs/00 修复记录 #19）
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(6));
        var attempts = Endpoints.Select(endpoint => TrySearchEndpointAsync(endpoint, query, overall.Token)).ToList();
        var results = await Task.WhenAll(attempts).ConfigureAwait(false);
        return results.FirstOrDefault(tracks => tracks.Count > 0) ?? [];
    }

    private async Task<IReadOnlyList<PlayerTrack>> TrySearchEndpointAsync(
        string endpoint, string query, CancellationToken overallToken)
    {
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            return await SearchByEndpointAsync(endpoint, query, attempt.Token);
        }
        catch (OperationCanceledException) when (!overallToken.IsCancellationRequested)
        {
            return []; // 该端点超时：并发下由其它端点兜底
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return []; // 端点失败：并发下由其它端点兜底
        }
    }

    private async Task<IReadOnlyList<PlayerTrack>> SearchByEndpointAsync(string endpoint, string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = query,
                ["offset"] = "0",
                ["limit"] = "20",
                ["type"] = "1",
            }),
        };
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.TryAddWithoutValidation("X-Real-IP", "118.88.88.88");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "118.88.88.88");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array)
        {
            return []; // 不可解析 = 空（多端点兜底）
        }

        var tracks = new List<PlayerTrack>();
        foreach (var song in songs.EnumerateArray())
        {
            var id = GetText(song, "id");
            var title = GetText(song, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            tracks.Add(new PlayerTrack
            {
                Platform = "netease",
                Id = id,
                Title = title,
                Artist = GetArtists(song),
                Album = GetAlbum(song),
                CoverUrl = GetCover(song),
                NativeData = song.GetRawText(),
            });
        }

        return tracks;
    }

    /// <summary>song/detail 精确解析（id=xxx / 纯数字路径）。</summary>
    public async Task<PlayerTrack?> TryResolveSongIdAsync(string songId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://music.163.com/api/v3/song/detail?c={Uri.EscapeDataString($"[{{\"id\":{songId}}}]")}");
            request.Headers.Referrer = new Uri("https://music.163.com/");
            request.Headers.TryAddWithoutValidation("X-Real-IP", "118.88.88.88");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "118.88.88.88");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("songs", out var songs)
                || songs.ValueKind != JsonValueKind.Array
                || songs.GetArrayLength() == 0)
            {
                return null;
            }

            var song = songs[0];
            var id = GetText(song, "id");
            var title = GetText(song, "name");
            if (!string.Equals(id, songId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return new PlayerTrack
            {
                Platform = "netease",
                Id = id,
                Title = title,
                Artist = GetArtists(song),
                Album = GetAlbum(song),
                CoverUrl = GetCover(song),
                NativeData = song.GetRawText(),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private static string GetArtists(JsonElement song)
    {
        JsonElement artists;
        if ((!song.TryGetProperty("artists", out artists) || artists.ValueKind != JsonValueKind.Array)
            && (!song.TryGetProperty("ar", out artists) || artists.ValueKind != JsonValueKind.Array))
        {
            return string.Empty;
        }

        return string.Join("/", artists.EnumerateArray()
            .Select(artist => GetText(artist, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string GetAlbum(JsonElement song)
    {
        JsonElement album;
        return ((song.TryGetProperty("album", out album) && album.ValueKind == JsonValueKind.Object)
                || (song.TryGetProperty("al", out album) && album.ValueKind == JsonValueKind.Object))
            ? GetText(album, "name")
            : string.Empty;
    }

    private static string GetCover(JsonElement song)
    {
        if (song.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            var cover = GetText(album, "picUrl");
            if (!string.IsNullOrWhiteSpace(cover))
            {
                return cover;
            }
        }

        return GetText(song, "coverUrl");
    }

    private static string GetText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;
}
