using System.Drawing;
using System.Drawing.Imaging;
using Erbai.App.Overlay;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;

namespace Erbai.App.Tests;

public class OverlayStyleTests
{
    private static SongRequest Song(string name) => new()
    {
        Platform = "bilibili",
        UserId = "u1",
        SongName = name,
        Status = RequestStatus.Queued,
    };

    private static Bitmap Paint(QueueOverlayRenderer renderer, int w = 300, int h = 150)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            renderer.Paint(g, w, h);
        }

        return bmp;
    }

    [Fact]
    public void ApplyStyle_AccentColor_ChangesAccentBarColor()
    {
        var renderer = new QueueOverlayRenderer();
        renderer.ApplyStyle(new OverlayWindowStyleConfig { AccentColor = "#FF0000", BackgroundOpacity = 0 });
        renderer.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("测试"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });

        using var bmp = Paint(renderer);
        // 强调条区域 (12, y+2, 4, 44*scale)：采样其中一点应为红色系
        var px = bmp.GetPixel(13, 20);
        Assert.True(px.R > 200 && px.G < 80 && px.B < 80, $"强调条应为红色系，实际 R={px.R} G={px.G} B={px.B}");
    }

    [Fact]
    public void ApplyStyle_Opacity_HalvesTextAlpha()
    {
        var full = new QueueOverlayRenderer();
        full.ApplyStyle(new OverlayWindowStyleConfig { BackgroundOpacity = 0 });
        full.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("测试"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });

        var half = new QueueOverlayRenderer();
        half.ApplyStyle(new OverlayWindowStyleConfig { Opacity = 0.5, BackgroundOpacity = 0 });
        half.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("测试"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });

        using var bmpFull = Paint(full);
        using var bmpHalf = Paint(half);
        var fullMax = MaxAlphaInTitle(bmpFull);
        var halfMax = MaxAlphaInTitle(bmpHalf);
        Assert.True(halfMax < fullMax - 50,
            $"opacity=0.5 应显著更透明：fullMax={fullMax} halfMax={halfMax}");
    }

    /// <summary>歌名文字区（y≈30–70）最大 alpha。</summary>
    private static int MaxAlphaInTitle(Bitmap bmp)
    {
        var maxAlpha = 0;
        for (var y = 30; y < 70; y++)
        {
            for (var x = 20; x < 200; x++)
            {
                maxAlpha = Math.Max(maxAlpha, bmp.GetPixel(x, y).A);
            }
        }

        return maxAlpha;
    }

    [Fact]
    public void ApplyStyle_FontScale_EnlargesText()
    {
        var small = new QueueOverlayRenderer();
        small.ApplyStyle(new OverlayWindowStyleConfig { BackgroundOpacity = 0 });
        small.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("测试"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });

        var large = new QueueOverlayRenderer();
        large.ApplyStyle(new OverlayWindowStyleConfig { FontScale = 1.6, BackgroundOpacity = 0 });
        large.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("测试"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });

        using var bmpSmall = Paint(small);
        using var bmpLarge = Paint(large);
        // 大字号渲染出的文字应占据更宽的像素范围（歌名行）
        var smallWidth = ContentWidth(bmpSmall);
        var largeWidth = ContentWidth(bmpLarge);
        Assert.True(largeWidth > smallWidth * 1.3, $"1.6× 字号应显著更宽：small={smallWidth} large={largeWidth}");
    }

    /// <summary>统计歌名行（y≈46）最左到最右的内容像素跨度。</summary>
    private static int ContentWidth(Bitmap bmp)
    {
        var left = int.MaxValue;
        var right = -1;
        for (var y = 40; y < 60; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).A > 40)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }

        return right - left;
    }

    [Fact]
    public void ApplyStyle_BackgroundOpacity_DrawsPanel()
    {
        // 有背景：面板区域出现半透明深蓝黑（玻璃面板效果）
        var withBg = new QueueOverlayRenderer();
        withBg.ApplyStyle(new OverlayWindowStyleConfig { BackgroundOpacity = 0.5 });
        withBg.Update(new QueueSnapshot { Items = [], DisplayLimit = 5 });
        using var bmp = Paint(withBg);
        var px = bmp.GetPixel(12, 12); // 面板左上角（圆角内）
        Assert.True(px.A > 0 && px.A < 255, $"半透明底 alpha 应在 (0,255)，实际 {px.A}");
        Assert.True(px.B > px.R, $"面板应为深蓝黑系（B>R），实际 {px}");

        // 无背景：同一位置全透明
        var noBg = new QueueOverlayRenderer();
        noBg.ApplyStyle(new OverlayWindowStyleConfig { BackgroundOpacity = 0 });
        noBg.Update(new QueueSnapshot { Items = [], DisplayLimit = 5 });
        using var bmp2 = Paint(noBg);
        Assert.Equal(0, bmp2.GetPixel(12, 12).A);
    }
}
