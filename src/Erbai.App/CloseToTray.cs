namespace Erbai.App;

/// <summary>
/// 托盘常驻关闭策略（2026-09）：点 X 是否隐藏到托盘而非退出。
/// 独立成纯函数便于单测——<see cref="ShouldHideOnClose"/> 不依赖 WinUI 类型，
/// isSystemShutdown 由宿主经 SystemEvents.SessionEnding（关机/注销）提供，
/// 测试工程无需引用 WindowsAppSDK。
/// </summary>
public static class CloseToTray
{
    /// <summary>
    /// 关闭窗口时是否隐藏到托盘后台运行。
    /// 系统关机/注销（isSystemShutdown）一律不隐藏：应用必须放行关闭，
    /// 否则会阻止系统关机流程（Win32 关闭协议下被强杀）。
    /// </summary>
    public static bool ShouldHideOnClose(bool isSystemShutdown, bool minimizeToTrayOnClose)
        => minimizeToTrayOnClose && !isSystemShutdown;
}
