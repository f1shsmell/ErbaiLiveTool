namespace Erbai.App.Ux;

/// <summary>
/// 主窗口位置钳制（2026-10 长稳轮）：恢复记忆位置时保证窗口至少有一部分
/// （标题栏可拖拽区）落在某块屏幕内——拔掉外接显示器/改分辨率后，落在
/// 已不存在区域的位置会把窗口"弄丢"（任务栏有图标但看不见也拖不回来）。
/// 纯函数便于单测；调用方传入各屏幕的工作区。
/// </summary>
internal static class WindowPlacement
{
    /// <summary>窗口在任一屏幕可见区内必须保留的最小宽度（像素）。</summary>
    public const int MinVisibleWidth = 120;

    /// <summary>窗口在任一屏幕可见区内必须保留的最小高度（像素，盖过标题栏）。</summary>
    public const int MinVisibleHeight = 60;

    public readonly record struct ScreenRect(int Left, int Top, int Width, int Height);

    /// <summary>
    /// 把窗口位置钳制到最近的可见屏幕：完全在所有屏幕外时回到第一块屏幕的
    /// 左上角；部分越界时平移到至少保留 MinVisible 区域。
    /// 无屏幕信息（空列表）时原样返回。
    /// </summary>
    public static (int X, int Y) Clamp(int x, int y, int width, int height, IReadOnlyList<ScreenRect> screens)
    {
        if (screens.Count == 0)
        {
            return (x, y);
        }

        // 与任一屏幕有足够交集即可
        foreach (var s in screens)
        {
            var visibleX = Math.Min(x + width, s.Left + s.Width) - Math.Max(x, s.Left);
            var visibleY = Math.Min(y + height, s.Top + s.Height) - Math.Max(y, s.Top);
            if (visibleX >= MinVisibleWidth && visibleY >= MinVisibleHeight)
            {
                return (x, y); // 已有足够可见区域
            }
        }

        // 完全不可见：钳到与主屏（第一块）工作区的可见关系——优先平移到屏内左上
        var primary = screens[0];
        var clampedX = Math.Clamp(x, primary.Left, Math.Max(primary.Left, primary.Left + primary.Width - MinVisibleWidth));
        var clampedY = Math.Clamp(y, primary.Top, Math.Max(primary.Top, primary.Top + primary.Height - MinVisibleHeight));
        return (clampedX, clampedY);
    }
}
