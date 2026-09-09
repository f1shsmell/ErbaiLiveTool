using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connector;

/// <summary>
/// 平台连接器后端（决策 #15：机制参考旧实现，代码表达沿用上游）。
/// 宿主按请求 player 字段路由；同一时间只激活一个平台（PlayerManager
/// 唯一激活语义）。操作全部返回 JsonElement(响应 result 的原生载荷)。
/// </summary>
public interface IConnectorBackend
{
    /// <summary>连接器 id："dummy" | "lxmusic" | "netease" | "kugou" | "qqmusic" | "folia"。</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>加性能力（如 "snapshot-events-v1"）。</summary>
    string[] ProtocolCapabilities { get; }

    /// <summary>建连/激活（首帧快照）；失败抛异常（客户端记 start_error 继续运行）。</summary>
    Task<JsonElement> ActivateAsync(CancellationToken ct);

    Task DeactivateAsync();

    /// <summary>轮询快照（宿主侧超时 probe=6s）。</summary>
    Task<JsonElement> ProbeAsync(CancellationToken ct);

    /// <summary>原生搜索，返回裸数组（不支持返回 []）。</summary>
    Task<JsonElement> SearchAsync(string query, CancellationToken ct);

    /// <summary>执行命令（命令名已由宿主解析为 PlayerCommand）。</summary>
    Task<JsonElement> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct);

    /// <summary>快照事件流（可选能力；null = 无事件源，客户端回退轮询）。</summary>
    IAsyncEnumerable<JsonElement>? WatchSnapshotsAsync(CancellationToken ct);
}

/// <summary>快照序列化辅助（camelCase + nextSource 线格式，v1.1）。</summary>
public static class SnapshotJson
{
    public static JsonElement Serialize(PlayerSnapshot snapshot)
    {
        var current = SerializeTrack(snapshot.Current);
        var next = SerializeTrack(snapshot.Next);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("connected", snapshot.Connected);
            writer.WriteString("version", snapshot.Version);
            if (current is { } currentTrack)
            {
                writer.WritePropertyName("current");
                currentTrack.WriteTo(writer);
            }
            else
            {
                writer.WriteNull("current");
            }

            if (next is { } nextTrack)
            {
                writer.WritePropertyName("next");
                nextTrack.WriteTo(writer);
            }
            else
            {
                writer.WriteNull("next");
            }

            writer.WriteString("nextSource", ConnectorProtocol.NextSource(snapshot.NextObservation));
            if (!string.IsNullOrEmpty(snapshot.RawStatus))
            {
                writer.WriteString("rawStatus", snapshot.RawStatus);
            }

            if (snapshot.ProgressSeconds is not null)
            {
                writer.WriteNumber("progressSeconds", snapshot.ProgressSeconds.Value);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static JsonElement? SerializeTrack(PlayerTrack? track)
    {
        if (track is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("platform", track.Platform);
            writer.WriteString("id", track.Id);
            writer.WriteString("title", track.Title);
            writer.WriteString("artist", track.Artist);
            writer.WriteString("album", track.Album);
            writer.WriteNumber("durationSeconds", track.DurationSeconds ?? 0);
            writer.WriteString("coverUrl", track.CoverUrl);
            if (!string.IsNullOrEmpty(track.NativeData))
            {
                writer.WriteString("nativeData", track.NativeData);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static PlayerTrack? DeserializeTrack(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }

        return new PlayerTrack
        {
            Platform = GetString(obj, "platform") ?? "",
            Id = GetString(obj, "id") ?? "",
            Title = GetString(obj, "title") ?? "",
            Artist = GetString(obj, "artist") ?? "",
            Album = GetString(obj, "album") ?? "",
            DurationSeconds = obj.TryGetProperty("durationSeconds", out var d) && d.TryGetInt32(out var dv)
                ? dv
                : null,
            CoverUrl = GetString(obj, "coverUrl") ?? "",
            NativeData = GetString(obj, "nativeData"),
        };
    }

    public static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
