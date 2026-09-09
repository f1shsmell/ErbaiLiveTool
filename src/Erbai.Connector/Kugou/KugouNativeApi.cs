using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Erbai.Connector.Kugou;

/// <summary>酷狗播放状态（窗口标题 + KuGou.ini [PlaybackState] 合并）。</summary>
public sealed record KugouPlaybackState(
    string Source,
    string WindowTitle,
    string RawTitle,
    string Artist,
    string Title,
    int SongItem,
    int SongList,
    int SongTable,
    long LastPositionMilliseconds);

/// <summary>命令投递结果。</summary>
public sealed record KugouCommandResult(
    string Action,
    string Method,
    bool Sent,
    long? WindowHandle,
    int? ProcessId,
    string? Error);

/// <summary>酷狗可投递命令（对齐上游枚举语义）。</summary>
public enum KugouAppCommand
{
    NextTrack = 11,
    PreviousTrack = 12,
    Stop = 13,
    PlayPause = 14,
    Play = 46,
    Pause = 47,
}

/// <summary>
/// 酷狗 native 控制面（机制 docs/04 §1.5.2，代码表达沿用上游）：
/// 窗口枚举（KuGou 进程可见主窗口）、KuGou.ini [PlaybackState] 读取、
/// Local\KuGouDataExchange 共享内存（偏移 0x0e 读 IPC 接收窗口）、
/// WM_COPYDATA 插歌（data=20 或本地文件 data=1）、WM_APPCOMMAND 控制。
/// 点击路径带前台保护：无法将酷狗置前台时拒绝点击（防误点其他窗口）。
/// </summary>
public static class KugouNativeApi
{
    private const string DataExchangeMappingName = @"Local\KuGouDataExchange";
    private const int DataExchangeWindowOffset = 0x0e;
    private const uint WmCopyData = 0x004a;
    private const uint WmAppCommand = 0x0319;
    private const uint FileMapRead = 0x0004;
    private const uint SendTimeoutAbortIfHung = 0x0002;
    private const int CopyDataData = 1; // 本地文件
    private const int InsertPayloadData = 20; // 插歌 payload

    private static readonly string KugouIniPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KuGou8",
        "KuGou.ini");

    public static string IniPath => KugouIniPath;

    /// <summary>酷狗主窗口（KuGou 进程、可见、面积最大者优先）。</summary>
    public static (nint Handle, int ProcessId)? FindMainWindow()
    {
        var processIds = Process.GetProcessesByName("KuGou").Select(p => p.Id).ToHashSet();
        if (processIds.Count == 0)
        {
            return null;
        }

        (nint Handle, int ProcessId) best = default;
        var bestArea = 0L;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var pid);
            if (!processIds.Contains((int)pid) || !IsWindowVisible(handle))
            {
                return true;
            }

            GetWindowRect(handle, out var rect);
            var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                best = (handle, (int)pid);
                bestArea = area;
            }

            return true;
        }, nint.Zero);
        return best.Handle == nint.Zero ? null : best;
    }

    /// <summary>共享内存公布的 IPC 接收窗口（偏移 0x0e 的窗口句柄 + 归属进程）。</summary>
    public static (nint Handle, int ProcessId)? InspectIpcEndpoint()
    {
        var mapping = OpenFileMapping(FileMapRead, false, DataExchangeMappingName);
        if (mapping == nint.Zero)
        {
            return null;
        }

        var view = nint.Zero;
        try
        {
            view = MapViewOfFile(mapping, FileMapRead, 0, 0, nuint.Zero);
            if (view == nint.Zero)
            {
                return null;
            }

            var rawHandle = unchecked((uint)Marshal.ReadInt32(view, DataExchangeWindowOffset));
            if (rawHandle == 0)
            {
                return null;
            }

            var handle = (nint)rawHandle;
            var processId = GetWindowProcessId(handle);
            return processId == 0 ? null : (handle, processId);
        }
        finally
        {
            if (view != nint.Zero)
            {
                _ = UnmapViewOfFile(view);
            }

            _ = CloseHandle(mapping);
        }
    }

    /// <summary>
    /// 播放状态：窗口标题优先（酷狗 ticker 格式解析），回退 KuGou.ini
    /// [PlaybackState].LastPlayingTitleName；位置/SongItem 等来自 ini。
    /// </summary>
    public static KugouPlaybackState ReadPlaybackState()
    {
        var target = FindMainWindow();
        var windowTitle = target is null ? string.Empty : ReadWindowTitle(target.Value.Handle);
        var iniTitle = ReadIniString("PlaybackState", "LastPlayingTitleName").Trim();
        var liveTitle = ExtractTitleFromTicker(windowTitle);
        var rawTitle = string.IsNullOrWhiteSpace(liveTitle) ? iniTitle : liveTitle;
        var (artist, title) = ParseArtistAndTitle(rawTitle);
        return new KugouPlaybackState(
            string.IsNullOrWhiteSpace(liveTitle) ? "KuGou.ini" : "WindowTitle",
            windowTitle.Trim(),
            rawTitle,
            artist,
            title,
            ReadIniInt("PlaybackState", "LastPlayingSongItem"),
            ReadIniInt("PlaybackState", "LastPlayingSongList"),
            ReadIniInt("PlaybackState", "LastPlayingSongTable"),
            ReadIniLong("PlaybackState", "LastPlayingSongPos"));
    }

    /// <summary>
    /// 投递控制命令。策略（收敛版）：WM_APPCOMMAND（不抢前台）→ 失败时
    /// 前台保护点击（ShowWindow + 置前台，失败即拒绝）。返回是否已发送。
    /// </summary>
    public static KugouCommandResult SendCommand(KugouAppCommand command)
    {
        var target = FindMainWindow();
        if (target is null)
        {
            return new KugouCommandResult(command.ToString(), "none", false, null, null, "没有找到可见的酷狗主窗口");
        }

        // WM_APPCOMMAND 优先：不抢前台、不误点
        var appCommandId = command switch
        {
            KugouAppCommand.NextTrack => 0x0b,
            KugouAppCommand.PreviousTrack => 0x0c,
            KugouAppCommand.PlayPause or KugouAppCommand.Play or KugouAppCommand.Pause => 0x0e,
            KugouAppCommand.Stop => 0x0d,
            _ => 0,
        };
        if (appCommandId != 0)
        {
            var lParam = (nint)(appCommandId << 16);
            var delivered = SendMessageTimeout(
                target.Value.Handle,
                WmAppCommand,
                target.Value.Handle,
                lParam,
                SendTimeoutAbortIfHung,
                1500,
                out _);
            if (delivered != nint.Zero)
            {
                return new KugouCommandResult(command.ToString(), "WM_APPCOMMAND", true, target.Value.Handle, target.Value.ProcessId, null);
            }
        }

        // 前台保护点击兜底（部分酷狗版本不接受 APPCOMMAND）
        if (!TryBringToForeground(target.Value.Handle))
        {
            return new KugouCommandResult(
                command.ToString(),
                "TargetedPhysicalClick",
                false,
                target.Value.Handle,
                target.Value.ProcessId,
                "Windows 拒绝将酷狗置于前台；为避免误点其他窗口，已取消点击");
        }

        var clientSize = GetClientSize(target.Value.Handle);
        var point = command switch
        {
            KugouAppCommand.NextTrack => (X: clientSize.Width / 2 + 51, Y: clientSize.Height - 37),
            KugouAppCommand.PreviousTrack => (X: clientSize.Width / 2 - 55, Y: clientSize.Height - 37),
            KugouAppCommand.PlayPause or KugouAppCommand.Play or KugouAppCommand.Pause
                => (X: clientSize.Width / 2, Y: clientSize.Height - 37),
            _ => ((int X, int Y)?)null,
        };
        if (point is null)
        {
            return new KugouCommandResult(command.ToString(), "WM_APPCOMMAND", true, target.Value.Handle, target.Value.ProcessId, null);
        }

        Thread.Sleep(150);
        if (!TryClickClientPoint(target.Value.Handle, point.Value.X, point.Value.Y, out var clickError))
        {
            return new KugouCommandResult(command.ToString(), "TargetedPhysicalClick", false, target.Value.Handle, target.Value.ProcessId, clickError);
        }

        return new KugouCommandResult(command.ToString(), "TargetedPhysicalClick", true, target.Value.Handle, target.Value.ProcessId, null);
    }

    /// <summary>
    /// WM_COPYDATA 插歌（data=20）：payload 为酷狗文件对象 JSON 信封
    /// （BuildInsertNextPayload 产出），发送窗口 "KugouControlPocSender"。
    /// 投递窗口来自共享内存 IPC 端点（TaskListener 类由上层校验）。
    /// </summary>
    public static KugouCommandResult SendInsertNext(nint targetHandle, string payload)
    {
        var senderWindow = CreateWindowEx(
            0,
            "STATIC",
            "KugouControlPocSender",
            0, 0, 0, 0, 0,
            new nint(-3), // HWND_MESSAGE
            nint.Zero,
            nint.Zero,
            nint.Zero);
        var dataPointer = Marshal.StringToHGlobalUni(payload);
        var copyData = new CopyDataStruct
        {
            Data = InsertPayloadData,
            ByteCount = checked((uint)Encoding.Unicode.GetByteCount(payload)),
            DataPointer = dataPointer,
        };
        var structPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CopyDataStruct>());
        try
        {
            Marshal.StructureToPtr(copyData, structPointer, false);
            var delivered = SendMessageTimeout(
                targetHandle,
                WmCopyData,
                senderWindow,
                structPointer,
                SendTimeoutAbortIfHung,
                1500,
                out _);
            return delivered == nint.Zero
                ? new KugouCommandResult("InsertNext", "WM_COPYDATA/dwData=20", false, targetHandle, null, "酷狗没有接受插歌消息")
                : new KugouCommandResult("InsertNext", "WM_COPYDATA/dwData=20", true, targetHandle, null, null);
        }
        finally
        {
            if (structPointer != nint.Zero)
            {
                Marshal.FreeHGlobal(structPointer);
            }

            if (dataPointer != nint.Zero)
            {
                Marshal.FreeHGlobal(dataPointer);
            }

            if (senderWindow != nint.Zero)
            {
                _ = DestroyWindow(senderWindow);
            }
        }
    }

    /// <summary>窗口标题 → 曲名/歌手（"标题 - 歌手 - 酷狗音乐" / 包裹 ticker 格式）。</summary>
    public static (string Artist, string Title) ParseArtistAndTitle(string rawTitle)
    {
        var value = (rawTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return ("", "");
        }

        // "歌曲 - 歌手" 常见形态；兼容完整窗口标题（末段为 "酷狗音乐" 时丢弃）
        var parts = value.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var artist = parts[^1].Trim();
            if (artist.Equals("酷狗音乐", StringComparison.OrdinalIgnoreCase))
            {
                // 完整窗口标题形态：去掉 ticker 后缀后重切
                var trimmed = value[..^" - 酷狗音乐".Length].Trim();
                parts = trimmed.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    return ("", trimmed);
                }

                artist = parts[^1].Trim();
            }

            var title = string.Join(" - ", parts[..^1]).Trim();
            return (artist, title);
        }

        return ("", value);
    }

    /// <summary>酷狗窗口标题 ticker 解析："X - 酷狗音乐" / "X - 酷狗音乐 Y" / "酷狗音乐 X -"。</summary>
    public static string ExtractTitleFromTicker(string windowTitle)
    {
        const string suffix = " - 酷狗音乐";
        const string separator = " - 酷狗音乐 ";
        const string wrappedPrefix = "酷狗音乐 ";
        var value = (windowTitle ?? string.Empty).Trim();
        if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return value[..^suffix.Length].Trim();
        }

        var separatorIndex = value.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (separatorIndex >= 0)
        {
            var before = value[..separatorIndex];
            var after = value[(separatorIndex + separator.Length)..];
            return $"{after}{before}".Trim();
        }

        if (value.StartsWith(wrappedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var unwrapped = value[wrappedPrefix.Length..];
            return unwrapped.EndsWith(" -", StringComparison.Ordinal)
                ? unwrapped[..^2].Trim()
                : unwrapped.Trim();
        }

        return string.Empty;
    }

    private static string ReadWindowTitle(nint handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool TryBringToForeground(nint handle)
    {
        _ = ShowWindow(handle, 9); // SW_RESTORE
        return SetForegroundWindow(handle);
    }

    private static bool TryClickClientPoint(nint handle, int x, int y, out string? error)
    {
        _ = GetWindowRect(handle, out var rect);
        var screenX = rect.Left + x;
        var screenY = rect.Top + y;
        _ = SetCursorPos(screenX, screenY);
        Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, 0); // LEFTDOWN
        Thread.Sleep(40);
        mouse_event(0x0004, 0, 0, 0, 0); // LEFTUP
        error = null;
        return true;
    }

    private static (int Width, int Height) GetClientSize(nint handle)
    {
        _ = GetClientRect(handle, out var rect);
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static int GetWindowProcessId(nint handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return (int)processId;
    }

    private static string ReadIniString(string section, string key)
    {
        var builder = new StringBuilder(1024);
        _ = GetPrivateProfileString(section, key, string.Empty, builder, builder.Capacity, KugouIniPath);
        return builder.ToString();
    }

    private static int ReadIniInt(string section, string key) =>
        int.TryParse(ReadIniString(section, key), out var value) ? value : 0;

    private static long ReadIniLong(string section, string key) =>
        long.TryParse(ReadIniString(section, key), out var value) ? value : 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nint Data;
        public uint ByteCount;
        public nint DataPointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    private delegate bool EnumWindowsProc(nint handle, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint handle, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extraInfo);

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeout(nint handle, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenFileMapping(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll")]
    private static extern nint MapViewOfFile(nint mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytesToMap);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(nint baseAddress);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileStringW", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileString(string section, string key, string defaultValue, StringBuilder result, int size, string filePath);
}
