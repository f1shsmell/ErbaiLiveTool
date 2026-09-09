using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Erbai.Connector.Netease;

/// <summary>网易云原生命令（七命令协议面内可用的 Next/PlayPause）。</summary>
public enum NeteaseNativeCommand
{
    Next,
    PlayPause,
}

public sealed record NeteaseIpcSendResult(bool Delivered, string Message);

/// <summary>
/// 网易云原生 IPC（机制 docs/04 §1.5.4，代码表达沿用上游）：
/// 窗口定位 = cloudmusic 进程 + OrpheusBrowserHost 类（排名：标题/尺寸/最小化）；
/// 原生命令 = GlobalFindAtom("next_local"/"play_pause_local") +
/// PostMessage(WM_HOTKEY, atom, (vk&lt;&lt;16)|MOD_CONTROL)——不产生真实键盘输入。
/// </summary>
public static class NeteaseNativeIpc
{
    private const uint WindowMessageHotkey = 0x0312;
    private const uint HotkeyModifierControl = 0x0002;
    private const ushort VirtualKeyRight = 0x27;
    private const ushort VirtualKeyP = 0x50;

    /// <summary>网易云主窗口端点（OrpheusBrowserHost 排名选主）。</summary>
    public static (nint Handle, int ProcessId)? FindEndpoint()
    {
        var processIds = Process.GetProcessesByName("cloudmusic")
            .Select(p => p.Id)
            .ToHashSet();
        if (processIds.Count == 0)
        {
            return null;
        }

        (nint Handle, int ProcessId) best = default;
        var bestRank = -1;
        long bestArea = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!processIds.Contains((int)pid) || !ReadWindowClass(window).Equals("OrpheusBrowserHost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            GetWindowRect(window, out var rect);
            var width = Math.Max(0, rect.Right - rect.Left);
            var height = Math.Max(0, rect.Bottom - rect.Top);
            var title = ReadWindowTitle(window);
            var rank = string.IsNullOrWhiteSpace(title) ? 0 : 4;
            if (!string.IsNullOrWhiteSpace(title)
                && !title.Equals("网易云音乐", StringComparison.OrdinalIgnoreCase)
                && !title.Equals("NetEase Cloud Music", StringComparison.OrdinalIgnoreCase))
            {
                rank += 4;
            }

            if (width >= 400 && height >= 300)
            {
                rank += 2;
            }

            if (IsIconic(window))
            {
                rank += 1;
            }

            if (rank > bestRank || (rank == bestRank && (long)width * height > bestArea))
            {
                best = (window, (int)pid);
                bestRank = rank;
                bestArea = (long)width * height;
            }

            return true;
        }, nint.Zero);
        return best.Handle == nint.Zero ? null : best;
    }

    /// <summary>原生命令（atom + WM_HOTKEY 投递，无键盘输入）。</summary>
    public static NeteaseIpcSendResult SendNativeCommand(NeteaseNativeCommand command)
    {
        var endpoint = FindEndpoint();
        if (endpoint is null)
        {
            return new NeteaseIpcSendResult(false, "没有发现网易云音乐主进程。");
        }

        var descriptor = command switch
        {
            NeteaseNativeCommand.Next => ("next_local", VirtualKeyRight),
            NeteaseNativeCommand.PlayPause => ("play_pause_local", VirtualKeyP),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var commandAtom = GlobalFindAtom(descriptor.Item1);
        if (commandAtom == 0)
        {
            return new NeteaseIpcSendResult(false, $"网易云未注册内部命令 {descriptor.Item1}，当前版本可能不支持。");
        }

        var lParam = (nint)(((uint)descriptor.Item2 << 16) | HotkeyModifierControl);
        if (!PostMessage(endpoint.Value.Handle, WindowMessageHotkey, (nint)commandAtom, lParam))
        {
            return new NeteaseIpcSendResult(false, $"网易云内部命令投递失败，Win32={Marshal.GetLastWin32Error()}。");
        }

        return new NeteaseIpcSendResult(true, $"已直接投递网易云内部命令 {descriptor.Item1}；未生成键盘输入。");
    }

    public static string? TryGetProcessVersion(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch
        {
            return null;
        }
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

    private static string ReadWindowClass(nint handle)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsCallback(nint handle, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out NeteaseRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort GlobalFindAtom(string name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NeteaseRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
