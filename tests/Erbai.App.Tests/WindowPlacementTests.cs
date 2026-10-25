using Erbai.App.Ux;

namespace Erbai.App.Tests;

/// <summary>
/// 主窗口位置钳制（2026-10 长稳轮）：记忆位置恢复时保证窗口在任一屏幕上
/// 至少保留可拖拽的可见区域——拔掉外接显示器/改分辨率后窗口不会"消失"。
/// </summary>
public class WindowPlacementTests
{
    private static readonly WindowPlacement.ScreenRect Primary =
        new(0, 0, 1920, 1080);

    private static readonly WindowPlacement.ScreenRect Secondary =
        new(1920, 0, 1920, 1080);

    [Fact]
    public void 屏幕内位置_原样保留()
    {
        var (x, y) = WindowPlacement.Clamp(100, 80, 1120, 720, [Primary, Secondary]);
        Assert.Equal(100, x);
        Assert.Equal(80, y);
    }

    [Fact]
    public void 副屏位置_显示器仍在时保留()
    {
        var (x, y) = WindowPlacement.Clamp(2200, 100, 1120, 720, [Primary, Secondary]);
        Assert.Equal(2200, x);
        Assert.Equal(100, y);
    }

    [Fact]
    public void 副屏位置_副屏已拔_钳回主屏()
    {
        // 只剩主屏：x=2200 完全在屏幕外
        var (x, y) = WindowPlacement.Clamp(2200, 100, 1120, 720, [Primary]);
        Assert.True(x >= 0 && x < 1920, $"钳制后 x={x} 应在主屏内");
        Assert.Equal(100, y);
    }

    [Fact]
    public void 部分越界_保留足够可见区域()
    {
        // 窗口左缘在屏幕左外 -600：仍有 520px 在屏内（>120）→ 允许保留
        var (keepX, _) = WindowPlacement.Clamp(-600, 100, 1120, 720, [Primary]);
        Assert.Equal(-600, keepX);

        // 左缘 -1100：只剩 20px 可见（<120）→ 钳制
        var (clampedX, _) = WindowPlacement.Clamp(-1100, 100, 1120, 720, [Primary]);
        Assert.True(clampedX >= 0, $"可见区域不足时应钳回屏内，实际 {clampedX}");
    }

    [Fact]
    public void 负坐标偏移屏_同样钳制()
    {
        var leftScreen = new WindowPlacement.ScreenRect(-1920, 0, 1920, 1080);
        // 目标位置在左侧屏外更远处（-4000）：完全不可见 → 钳到左屏可见处
        var (x, _) = WindowPlacement.Clamp(-4000, 100, 1120, 720, [leftScreen]);
        Assert.True(x >= -1920 && x <= -800, $"应钳回左屏，实际 {x}");
    }

    [Fact]
    public void 无屏幕信息_原样返回()
    {
        var (x, y) = WindowPlacement.Clamp(500, 500, 1120, 720, []);
        Assert.Equal(500, x);
        Assert.Equal(500, y);
    }
}
