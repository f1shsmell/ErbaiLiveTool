using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connector.Folia;

/// <summary>Folia Stage WS 事件（仅 STATUS / TRACK_CHANGED 会解析出曲目）。</summary>
public sealed record FoliaStageEvent(string Name, PlayerTrack? Current, PlayerTrack? Next);

/// <summary>
/// Folia Stage 事件 JSON 解析（纯函数，docs/04 §1.5.1）：载荷多形态
/// （track 直挂 / data.track / current，next 同形态），字段大小写不敏感；
/// 未知事件忽略。曲目缺标题时兜底为 "歌曲 {id}"。
/// </summary>
public static class FoliaStageJson
{
    public static FoliaStageEvent? ParseEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseEvent(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static FoliaStageEvent? ParseEvent(JsonElement root)
    {
        var name = GetString(root, "event") ?? GetString(root, "type") ?? string.Empty;
        if (!name.Equals("STATUS", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("TRACK_CHANGED", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var track = ParseTrack(root);
        var next = ParseNextTrack(root);
        return new FoliaStageEvent(name.ToUpperInvariant(), track, next);
    }

    public static PlayerTrack? ParseNextTrack(JsonElement payload)
    {
        if (TryGetProperty(payload, "next", out var next))
        {
            return ParseTrack(next);
        }

        if (TryGetProperty(payload, "data", out var data) && TryGetProperty(data, "next", out next))
        {
            return ParseTrack(next);
        }

        return null;
    }

    /// <summary>多形态 track 定位：track / current / data.track；缺 id 返回 null。</summary>
    public static PlayerTrack? ParseTrack(JsonElement payload)
    {
        var track = payload;
        if (TryGetProperty(payload, "track", out var direct))
        {
            track = direct;
        }
        else if (TryGetProperty(payload, "current", out var current))
        {
            track = current;
        }
        else if (TryGetProperty(payload, "data", out var data) && TryGetProperty(data, "track", out var dataTrack))
        {
            track = dataTrack;
        }

        if (track.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetScalarString(track, "id") ?? GetScalarString(track, "songId") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var title = GetString(track, "title") ?? GetString(track, "name") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"歌曲 {id}";
        }

        return new PlayerTrack
        {
            Platform = "folia",
            Id = id,
            Title = title,
            Artist = ParseArtists(track),
            Album = ParseAlbum(track),
            CoverUrl = ParseCover(track),
        };
    }

    /// <summary>搜索响应 songs 定位：songs / data.songs / result.songs。</summary>
    public static JsonElement? FindSongs(JsonElement root)
    {
        if (TryGetProperty(root, "songs", out var songs) && songs.ValueKind == JsonValueKind.Array)
        {
            return songs;
        }

        if (TryGetProperty(root, "data", out var data)
            && TryGetProperty(data, "songs", out songs)
            && songs.ValueKind == JsonValueKind.Array)
        {
            return songs;
        }

        if (TryGetProperty(root, "result", out var result)
            && TryGetProperty(result, "songs", out songs)
            && songs.ValueKind == JsonValueKind.Array)
        {
            return songs;
        }

        return null;
    }

    public static string ParseArtists(JsonElement track)
    {
        if (TryGetProperty(track, "artists", out var artists) || TryGetProperty(track, "ar", out artists))
        {
            if (artists.ValueKind == JsonValueKind.Array)
            {
                return string.Join(
                    "/",
                    artists.EnumerateArray()
                        .Select(artist => artist.ValueKind == JsonValueKind.String
                            ? artist.GetString()
                            : GetString(artist, "name"))
                        .Where(name => !string.IsNullOrWhiteSpace(name)));
            }
        }

        if (TryGetProperty(track, "artist", out var artistValue))
        {
            return artistValue.ValueKind == JsonValueKind.String
                ? artistValue.GetString() ?? string.Empty
                : GetString(artistValue, "name") ?? string.Empty;
        }

        return string.Empty;
    }

    public static string ParseAlbum(JsonElement track)
    {
        if (!TryGetProperty(track, "album", out var album) && !TryGetProperty(track, "al", out album))
        {
            return string.Empty;
        }

        return album.ValueKind == JsonValueKind.String
            ? album.GetString() ?? string.Empty
            : GetString(album, "name") ?? string.Empty;
    }

    public static string ParseCover(JsonElement track)
    {
        if (TryGetProperty(track, "album", out var album) || TryGetProperty(track, "al", out album))
        {
            if (album.ValueKind == JsonValueKind.Object)
            {
                var albumCover = GetString(album, "picUrl") ?? GetString(album, "coverUrl");
                if (!string.IsNullOrWhiteSpace(albumCover))
                {
                    return albumCover;
                }
            }
        }

        return GetString(track, "coverUrl")
            ?? GetString(track, "cover")
            ?? GetString(track, "picUrl")
            ?? string.Empty;
    }

    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string? GetScalarString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
