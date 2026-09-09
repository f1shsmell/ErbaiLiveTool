using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Erbai.Contracts.Players;

namespace Erbai.Connector.Kugou;

/// <summary>
/// 酷狗 native 控制面接口（可测性缝：自动化测试注入 fake，绝不点击真实桌面）。
/// InspectIpcEndpoint 返回**已通过校验**的 IPC 端点（窗口类 TaskListener +
/// 归属 KuGou 进程），null = 不可用——校验职责在 native 实现内。
/// </summary>
public interface IKugouNativeControl
{
    (nint Handle, int ProcessId)? FindMainWindow();

    (nint Handle, int ProcessId)? InspectIpcEndpoint();

    KugouPlaybackState ReadPlaybackState();

    KugouCommandResult SendCommand(KugouAppCommand command);

    KugouCommandResult SendInsertNext(nint targetHandle, string payload);
}

/// <summary>真实现：包装 KugouNativeApi + IPC 端点三重校验（共享内存/窗口类/进程名）。</summary>
public sealed class KugouNativeControl : IKugouNativeControl
{
    public (nint Handle, int ProcessId)? FindMainWindow() => KugouNativeApi.FindMainWindow();

    public (nint Handle, int ProcessId)? InspectIpcEndpoint()
    {
        var endpoint = KugouNativeApi.InspectIpcEndpoint();
        if (endpoint is null)
        {
            return null;
        }

        var className = ReadWindowClass(endpoint.Value.Handle);
        if (!className.Equals("TaskListener", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(endpoint.Value.ProcessId);
            return process.ProcessName.Equals("KuGou", StringComparison.OrdinalIgnoreCase)
                ? endpoint
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public KugouPlaybackState ReadPlaybackState() => KugouNativeApi.ReadPlaybackState();

    public KugouCommandResult SendCommand(KugouAppCommand command) => KugouNativeApi.SendCommand(command);

    public KugouCommandResult SendInsertNext(nint targetHandle, string payload) =>
        KugouNativeApi.SendInsertNext(targetHandle, payload);

    /// <summary>IPC 端点窗口类（酷狗 TaskListener 校验）。</summary>
    public static string ReadWindowClass(nint handle)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, StringBuilder text, int maxCount);
}

/// <summary>IPC 端点（TaskListener + KuGou 进程双重校验后使用）。</summary>
public readonly record struct KugouIpcEndpoint(nint Handle, int ProcessId);

/// <summary>pending next 簿记（同一时间一个待插入目标）。</summary>
public sealed record PendingKugouNext(PlayerTrack Target, DateTimeOffset CreatedAt);

/// <summary>插入 payload 构建结果。</summary>
public sealed record InsertPayload(string Json, string Hash);

/// <summary>
/// 酷狗插歌 payload 构建（机制 docs/04 §1.5.2）：搜索结果 JSON →
/// 酷狗文件对象信封 {Source, SourceFile, ..., Files:[file], Count:"1", ...}，
/// WM_COPYDATA data=20 投递。hash 大写归一。
/// </summary>
public static class KugouInsertPayload
{
    public static InsertPayload? Build(string rawSongJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawSongJson);
            var song = doc.RootElement;
            var songName = GetText(song, "songname");
            var singerName = GetText(song, "singername");
            var filename = GetText(song, "filename");
            if (string.IsNullOrWhiteSpace(filename))
            {
                filename = string.IsNullOrWhiteSpace(singerName) ? songName : $"{singerName} - {songName}";
            }

            var duration = GetLong(song, "timelength");
            if (duration <= 0)
            {
                duration = GetLong(song, "duration") * 1000;
            }

            var hash = GetText(song, "hash").ToUpperInvariant();
            var file = new Dictionary<string, object?>
            {
                ["filename"] = filename,
                ["hash"] = hash,
                ["size"] = GetText(song, "filesize", "0"),
                ["duration"] = duration.ToString(),
                ["bitrate"] = GetText(song, "bitrate", "0"),
                ["isfilehead"] = "0",
                ["mvhash"] = GetText(song, "mvhash"),
                ["mvtrack"] = "0",
                ["mvstate"] = "0",
                ["ismvfilehead"] = "0",
                ["isvip"] = GetText(song, "isvip", "0"),
                ["privilege"] = GetText(song, "privilege", "0"),
                ["album_id"] = GetText(song, "album_id"),
                ["scid"] = "0",
                ["mixsongid"] = GetText(song, "mixsongid", "0"),
                ["special_id"] = GetText(song, "specialid", "0"),
                ["encrypt"] = "-1",
                ["songname"] = songName,
                ["singerinfo"] = Array.Empty<object>(),
                ["album_name"] = GetText(song, "album_name"),
                ["quality"] = "0",
                ["vip_icon"] = "0",
                ["songdescription"] = string.Empty,
            };
            var envelope = new Dictionary<string, object?>
            {
                ["Source"] = "UnifiedPlayerControlPoc",
                ["SourceFile"] = string.Empty,
                ["SourcePath"] = string.Empty,
                ["ChargePath"] = string.Empty,
                ["ClassName"] = string.Empty,
                ["Files"] = new[] { file },
                ["Count"] = "1",
                ["ListId"] = string.Empty,
                ["DownloadPath"] = string.Empty,
            };
            return new InsertPayload(System.Text.Json.JsonSerializer.Serialize(envelope), hash);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string GetText(System.Text.Json.JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static long GetLong(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? result
            : 0;
}
