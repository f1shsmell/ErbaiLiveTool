using System.Runtime.InteropServices;

namespace Erbai.OverlayWpf;

/// <summary>
/// WPF 悬浮窗 Win32 扩展样式辅助（bililive_dm SourceInitialized 同款：TOOLWINDOW + 可选
/// WS_EX_TRANSPARENT 点击穿透；穿透热切换后 SetWindowPos FRAMECHANGED 刷新命中测试）。
/// </summary>
internal static class WpfWin32
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>窗口句柄就绪后调用一次：TOOLWINDOW（不进 Alt-Tab）+ 初始穿透。</summary>
    public static void Configure(IntPtr hwnd, bool clickThrough)
    {
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        var next = ex | WS_EX_TOOLWINDOW;
        if (clickThrough)
        {
            next |= WS_EX_TRANSPARENT;
        }

        if (next != ex)
        {
            _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, next);
            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    /// <summary>点击穿透热切换：增删 WS_EX_TRANSPARENT + SWP_FRAMECHANGED 刷新命中测试。</summary>
    public static void ApplyClickThrough(IntPtr hwnd, bool flag)
    {
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        var next = flag ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (next == ex)
        {
            return;
        }

        _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, next);
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 取窗口<b>屏幕像素</b>矩形（GetWindowRect）。位置持久化统一用屏幕像素：
    /// 点歌/排队是 WinForms（Location 即像素），WPF 的 Left/Top 是 DIP——
    /// 若两边各存各的单位，DPI 缩放（125%/150%）下复位位置会整体偏移。
    /// </summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out RECT rect)
    {
        rect = default;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
    }

    /// <summary>
    /// 按<b>屏幕像素</b>定位窗口左上角（不改尺寸/Z 序/不抢焦点）。
    /// WPF 窗必须在句柄就绪（SourceInitialized）后调用，否则 Left/Top 会被 WPF
    /// 布局按 DIP 重算覆盖。
    /// </summary>
    public static void MoveToPixels(IntPtr hwnd, int left, int top)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
