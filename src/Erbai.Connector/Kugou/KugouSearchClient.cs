using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erbai.Contracts.Players;

namespace Erbai.Connector.Kugou;

/// <summary>
/// 酷狗 HTTP 搜索面（机制 docs/04 §1.5.2，代码表达沿用上游）：
/// 关键词 → mobilecdn.kugou.com/api/v3/search/song（固定参数 + 签名
/// signature=MD5(salt+排序参数串+salt)，salt 为固定常量）；
/// 永久分享链 → m.kugou.com/share/song.html?chain= HTML 内嵌 phpParam JSON；
/// 分享码 → 混合搜索 data.lists（type=recommend 优先，其次 isshareresult=1 的 song）。
/// 搜索结果 NativeData = 原始 JSON（插歌 payload 由 KugouConnector 构建）。
/// </summary>
public sealed class KugouSearchClient
{
    private const string SignatureSalt = "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA";
    private readonly HttpClient _http;

    public KugouSearchClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>关键词搜索（HTTPS 失败抛异常——保护搜索词，拒绝明文回退）。</summary>
    public async Task<IReadOnlyList<PlayerTrack>> SearchByKeywordAsync(string keyword, CancellationToken ct)
    {
        var queryString = BuildSignedQuery(keyword);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://mobilecdn.kugou.com{queryString}");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var results = new List<PlayerTrack>();
        if (!TryGetProperty(doc.RootElement, "data", out var data)
            || !TryGetProperty(data, "info", out var info)
            || info.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var song in info.EnumerateArray())
        {
            var hash = GetText(song, "hash").ToUpperInvariant();
            var audioId = GetLong(song, "audio_id");
            var title = GetText(song, "songname");
            var artist = GetText(song, "singername");
            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            results.Add(new PlayerTrack
            {
                Platform = "kugou",
                Id = audioId > 0 ? audioId.ToString(CultureInfo.InvariantCulture) : hash,
                Title = title,
                Artist = artist,
                Album = GetText(song, "album_name"),
                CoverUrl = GetCoverUrl(song),
                NativeData = song.GetRawText(),
            });
        }

        return results;
    }

    /// <summary>永久分享链解析（chain=xxx → HTML phpParam JSON → 单曲）。</summary>
    public async Task<IReadOnlyList<PlayerTrack>> ResolveShareChainAsync(string chain, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://m.kugou.com/share/song.html?chain={Uri.EscapeDataString(chain)}");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 Chrome/122.0 Mobile Safari/537.36");
            request.Headers.Referrer = new Uri("https://m.kugou.com/");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var match = Regex.Match(html, @"var\s+phpParam\s*=\s*(\{.*?\})\s*;", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return [];
            }

            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var song = doc.RootElement;
            if (TryGetProperty(song, "song_info", out var songInfo)
                && songInfo.ValueKind == JsonValueKind.Object
                && TryGetProperty(songInfo, "data", out var data)
                && data.ValueKind == JsonValueKind.Object)
            {
                song = data;
            }

            var track = CreateTrack(song);
            return track is null ? [] : [track];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return [];
        }
    }

    /// <summary>分享码解析：混合搜索 data.lists（recommend 优先，其次 isshareresult=1 song）。</summary>
    public async Task<IReadOnlyList<PlayerTrack>> ResolveShareCodeAsync(string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://mobilecdn.kugou.com{BuildSignedQuery(code)}");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!TryGetProperty(doc.RootElement, "data", out var data)
            || !TryGetProperty(data, "lists", out var groups)
            || groups.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // type=recommend 优先
        foreach (var group in groups.EnumerateArray())
        {
            if (!GetText(group, "type").Equals("recommend", StringComparison.OrdinalIgnoreCase)
                || !TryGetProperty(group, "lists", out var recommendations)
                || recommendations.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var tracks = recommendations.EnumerateArray()
                .Select(CreateTrack)
                .Where(t => t is not null)
                .Cast<PlayerTrack>()
                .ToArray();
            if (tracks.Length > 0)
            {
                return tracks;
            }
        }

        // 其次 isshareresult=1 的 song 组
        foreach (var group in groups.EnumerateArray())
        {
            if (!GetText(group, "type").Equals("song", StringComparison.OrdinalIgnoreCase)
                || GetLong(group, "isshareresult") != 1
                || !TryGetProperty(group, "lists", out var songs)
                || songs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var tracks = songs.EnumerateArray()
                .Select(CreateTrack)
                .Where(t => t is not null)
                .Cast<PlayerTrack>()
                .ToArray();
            if (tracks.Length > 0)
            {
                return tracks;
            }
        }

        return [];
    }

    /// <summary>签名搜索 URL 构造（固定参数表 + 排序签名，机制参考上游）。</summary>
    private static string BuildSignedQuery(string keyword)
    {
        const string dfid = "-";
        var now = DateTimeOffset.UtcNow;
        var milliseconds = now.ToUnixTimeMilliseconds();
        var clientTime = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mid = BigInteger.Parse($"0{Md5Hex(dfid)}", NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);
        var uuid = Md5Hex($"{dfid}{mid}");
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["ab_tag"] = "0",
            ["ability"] = "511",
            ["albumhide"] = "0",
            ["apiver"] = "22",
            ["area_code"] = "1",
            ["clientver"] = "20125",
            ["cursor"] = "0",
            ["is_gpay"] = "0",
            ["iscorrection"] = "1",
            ["keyword"] = keyword,
            ["nocollect"] = "0",
            ["osversion"] = "16.5",
            ["platform"] = "IOSFilter",
            ["recver"] = "2",
            ["req_ai"] = "1",
            ["requestid"] = $"{Md5Hex($"bdaa53d04e7475feb9024164a47032f9{milliseconds}")}_0",
            ["search_ability"] = "3",
            ["sec_aggre"] = "1",
            ["sec_aggre_bitmap"] = "0",
            ["style_type"] = "3",
            ["tag"] = "em",
            ["appid"] = "3116",
            ["dfid"] = dfid,
            ["mid"] = mid,
            ["uuid"] = uuid,
            ["userid"] = "0",
            ["clienttime"] = clientTime,
        };
        var signatureInput = string.Concat(parameters.Select(pair => $"{pair.Key}={pair.Value}"));
        parameters["signature"] = Md5Hex($"{SignatureSalt}{signatureInput}{SignatureSalt}");
        return "/api/v3/search/song?" + string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static PlayerTrack? CreateTrack(JsonElement song)
    {
        var hash = GetText(song, "hash").ToUpperInvariant();
        var audioId = GetLong(song, "audio_id");
        var title = GetText(song, "songname");
        var artist = GetText(song, "singername");
        if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(title))
        {
            // 分享页 phpParam 的字段形态与搜索不同
            hash = GetText(song, "FileHash").ToUpperInvariant();
            title = GetText(song, "songname");
            artist = GetText(song, "singername");
            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }
        }

        return new PlayerTrack
        {
            Platform = "kugou",
            Id = audioId > 0 ? audioId.ToString(CultureInfo.InvariantCulture) : hash,
            Title = title,
            Artist = artist,
            Album = GetText(song, "album_name"),
            CoverUrl = GetCoverUrl(song),
            NativeData = song.GetRawText(),
        };
    }

    private static string GetCoverUrl(JsonElement song)
    {
        var cover = GetText(song, "cover");
        if (!string.IsNullOrWhiteSpace(cover))
        {
            return cover;
        }

        var img = GetText(song, "img");
        return img.StartsWith("//", StringComparison.Ordinal) ? $"https:{img}" : img;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string GetText(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long GetLong(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static string Md5Hex(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
