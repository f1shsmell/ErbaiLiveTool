using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Erbai.Connector.QQMusic;

/// <summary>QQ音乐窗口状态（标题 "歌名 - 歌手" 最后分隔符切分）。</summary>
public sealed record QqPlaybackState(bool IsRunning, string? Title, string? Artist, long? WindowHandle, string? WindowTitle);

/// <summary>
/// QQ音乐 native 面（机制 docs/04 §1.5.3）：窗口枚举 + 标题解析 +
/// 单实例命令（QQMusic.exe /playcontrol 'next'|'prev'|'pause'|'play'）。
/// </summary>
public static class QqNativeController
{
    /// <summary>主窗口：可见 + 标题可解析曲目优先 + "QQ音乐" 标题 + 标题长度。</summary>
    public static nint? FindMainWindow()
    {
        nint? best = null;
        var bestScore = -1;
        foreach (var window in InspectWindows())
        {
            if (!window.IsVisible)
            {
                continue;
            }

            var score = 0;
            if (ParseTitle(window.Title) is not null)
            {
                score += 4;
            }

            if (window.Title.Equals("QQ音乐", StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            score += Math.Min(window.Title.Length, 100);
            if (score > bestScore)
            {
                bestScore = score;
                best = window.Handle;
            }
        }

        return best;
    }

    public static QqPlaybackState ReadPlaybackState()
    {
        var window = FindMainWindow();
        if (window is null)
        {
            return new QqPlaybackState(false, null, null, null, null);
        }

        var title = ReadWindowText(window.Value);
        var parsed = ParseTitle(title);
        return new QqPlaybackState(true, parsed?.Title, parsed?.Artist, window.Value.ToInt64(), title);
    }

    /// <summary>窗口标题解析："歌名 - 歌手"（最后分隔符，歌名可含分隔符）。</summary>
    public static (string Title, string Artist)? ParseTitle(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)
            || windowTitle.Equals("QQ音乐", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        const string separator = " - ";
        var index = windowTitle.LastIndexOf(separator, StringComparison.Ordinal);
        if (index <= 0)
        {
            return null;
        }

        var title = windowTitle[..index].Trim();
        var artist = windowTitle[(index + separator.Length)..].Trim();
        return string.IsNullOrWhiteSpace(title) ? null : (title, artist);
    }

    /// <summary>单实例命令（/playcontrol）。返回 (是否发送, 消息)。</summary>
    public static (bool Sent, string Message) SendPlayControl(string executablePath, string argument)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("/playcontrol");
        startInfo.ArgumentList.Add($"'{argument}'");
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "QQMusic.exe 单实例命令进程未启动。");
            }

            if (!process.WaitForExit(4000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return (false, "QQMusic.exe 单实例命令进程未按时退出。");
            }

            return (process.ExitCode == 0, process.ExitCode == 0 ? "QQ 音乐已接收单实例命令" : $"QQ 音乐单实例命令退出码 {process.ExitCode}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, $"启动 QQMusic.exe 失败：{ex.Message}");
        }
    }

    /// <summary>常见安装目录定位 QQMusic.exe（未运行时的兜底）。</summary>
    public static string? FindExecutablePath()
    {
        foreach (var process in Process.GetProcessesByName("QQMusic"))
        {
            try
            {
                if (process.MainModule?.FileName is { Length: > 0 } path && File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
            }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "QQMusic", "QQMusic.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "QQMusic", "QQMusic.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<QqWindowInfo> InspectWindows()
    {
        var windows = new List<QqWindowInfo>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0)
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!process.ProcessName.Equals("QQMusic", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                windows.Add(new QqWindowInfo(handle, (int)processId, ReadWindowText(handle), IsWindowVisible(handle)));
            }
            catch (ArgumentException)
            {
                // 枚举期间进程退出
            }

            return true;
        }, nint.Zero);
        return windows;
    }

    public sealed record QqWindowInfo(nint Handle, int ProcessId, string Title, bool IsVisible);

    private static string ReadWindowText(nint handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private delegate bool EnumWindowsCallback(nint handle, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
}
