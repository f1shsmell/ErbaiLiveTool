using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Player.Connectors;

/// <summary>响应快照 JSON → PlayerSnapshot（camelCase 线格式 + nextSource/rawStatus）。</summary>
internal static class SnapshotParser
{
    public static PlayerSnapshot Parse(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            return new PlayerSnapshot
            {
                Connected = false,
                NextObservation = NextObservation.Unknown,
            };
        }

        var connected = obj.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True;
        var version = GetString(obj, "version");
        var current = GetTrack(obj, "current");
        var next = GetTrack(obj, "next");
        var nextSource = ConnectorProtocol.ParseNextSource(GetString(obj, "nextSource"));
        var rawStatus = GetString(obj, "rawStatus");
        double? progress = obj.TryGetProperty("progressSeconds", out var p) && p.TryGetDouble(out var pv)
            ? pv
            : null;

        return new PlayerSnapshot
        {
            Connected = connected,
            Version = version,
            Current = current,
            Next = next,
            NextObservation = nextSource,
            RawStatus = rawStatus,
            ProgressSeconds = progress,
        };
    }

    internal static PlayerTrack? DeserializeTrack(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PlayerTrack
        {
            Platform = GetString(element, "platform") ?? "",
            Id = GetString(element, "id") ?? "",
            Title = GetString(element, "title") ?? "",
            Artist = GetString(element, "artist") ?? "",
            Album = GetString(element, "album") ?? "",
            DurationSeconds = element.TryGetProperty("durationSeconds", out var d) && d.TryGetInt32(out var dv)
                ? dv
                : null,
            CoverUrl = GetString(element, "coverUrl") ?? "",
            NativeData = GetString(element, "nativeData"),
        };
    }

    private static PlayerTrack? GetTrack(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PlayerTrack
        {
            Platform = GetString(value, "platform") ?? "",
            Id = GetString(value, "id") ?? "",
            Title = GetString(value, "title") ?? "",
            Artist = GetString(value, "artist") ?? "",
            Album = GetString(value, "album") ?? "",
            DurationSeconds = value.TryGetProperty("durationSeconds", out var d) && d.TryGetInt32(out var dv)
                ? dv
                : null,
            CoverUrl = GetString(value, "coverUrl") ?? "",
            NativeData = GetString(value, "nativeData"),
        };
    }

    internal static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
