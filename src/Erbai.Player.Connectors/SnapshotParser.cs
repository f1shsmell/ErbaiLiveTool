using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Player.Connectors;

/// <summary>响应快照 JSON → PlayerSnapshot（camelCase 线格式 + nextSource/rawStatus）。</summary>
internal static class SnapshotParser
{
    /// <param name="defaultPlatform">
    /// 连接器未在曲目里给出 platform 时的兜底平台键（= 连接器的 PlayerKey）。
    /// 上游 awoo 连接器的 search 结果与快照曲目都<b>不带</b> platform 字段，
    /// 不兜底会得到空串（PlayerTrack.Platform 是 required，空串会让下游
    /// 无法判断来源平台）。
    /// </param>
    public static PlayerSnapshot Parse(JsonElement? element, string? defaultPlatform = null)
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
        var current = GetTrack(obj, "current", defaultPlatform);
        var next = GetTrack(obj, "next", defaultPlatform);
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

    internal static PlayerTrack? DeserializeTrack(JsonElement element, string? defaultPlatform = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PlayerTrack
        {
            Platform = ResolvePlatform(element, defaultPlatform),
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

    /// <summary>曲目平台键：优先连接器给出的值，缺失/空串时用连接器 PlayerKey 兜底。</summary>
    private static string ResolvePlatform(JsonElement element, string? defaultPlatform)
    {
        var platform = GetString(element, "platform");
        return string.IsNullOrEmpty(platform) ? defaultPlatform ?? "" : platform;
    }

    private static PlayerTrack? GetTrack(JsonElement element, string property, string? defaultPlatform)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PlayerTrack
        {
            Platform = ResolvePlatform(value, defaultPlatform),
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
