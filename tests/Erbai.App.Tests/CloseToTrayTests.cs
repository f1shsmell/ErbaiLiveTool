using Erbai.App;

namespace Erbai.App.Tests;

/// <summary>
/// 托盘常驻关闭分流测试（2026-09）：点 X 是否隐藏到托盘后台运行的纯决策逻辑。
/// 关键不变量：系统关机/注销（isSystemShutdown）一律不得隐藏，否则阻止系统关机流程。
/// </summary>
public class CloseToTrayTests
{
    [Theory]
    // 用户点 X（非系统关机）
    [InlineData(false, true, true)]   // 开启托盘 → 隐藏到托盘后台运行
    [InlineData(false, false, false)] // 关闭托盘 → 完整退出（与旧行为一致）
    // 系统关机/注销（AppExit）
    [InlineData(true, true, false)]   // 即使开启托盘也绝不隐藏，必须放行关机
    [InlineData(true, false, false)]  // 关闭托盘 + 系统关机 → 退出
    public void ShouldHideOnClose_MatchesDecisionTable(bool isSystemShutdown, bool minimizeToTrayOnClose, bool expected)
        => Assert.Equal(expected, CloseToTray.ShouldHideOnClose(isSystemShutdown, minimizeToTrayOnClose));
}
