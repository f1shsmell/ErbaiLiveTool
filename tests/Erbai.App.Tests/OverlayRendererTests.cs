using System.Drawing;
using System.Drawing.Imaging;
using Erbai.App.Overlay;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Queue;
using Erbai.Contracts.QueueUp;
using Erbai.Contracts.Requests;

namespace Erbai.App.Tests;

public class QueueOverlayRendererTests
{
    private static SongRequest Song(string name, string singer = "") => new()
    {
        Platform = "bilibili",
        UserId = "u1",
        SongName = name,
        Singer = singer,
        Status = RequestStatus.Queued,
    };

    private static QueueOverlayRenderer NoBackgroundRenderer() => new();

    [Fact]
    public void ComputeContentHeight_GrowsWithQueueRows()
    {
        var renderer = new QueueOverlayRenderer();
        renderer.Update(new QueueSnapshot
        {
            Items =
            [
                new QueueItem { Request = Song("正在播"), Position = 1, IsCurrent = true },
                new QueueItem { Request = Song("下一首"), Position = 2, IsCurrent = false },
                new QueueItem { Request = Song("再下一首"), Position = 3, IsCurrent = false },
            ],
            DisplayLimit = 5,
        });
        var hSmall = renderer.ComputeContentHeight(267);

        renderer.Update(new QueueSnapshot
        {
            Items = Enumerable.Range(1, 6).Select(i => new QueueItem
            {
                Request = Song($"歌{i}"),
                Position = i,
                IsCurrent = i == 1,
            }).ToList(),
            DisplayLimit = 5,
        });
        var hLarge = renderer.ComputeContentHeight(267);

        Assert.True(hLarge > hSmall, $"队列行增多高度应增长：hSmall={hSmall} hLarge={hLarge}");
        Assert.True(hSmall > 40 && hLarge > 60);
    }

    [Fact]
    public void ComputeContentHeight_RespectsDisplayLimit()
    {
        var renderer = new QueueOverlayRenderer();
        renderer.Update(new QueueSnapshot
        {
            Items = Enumerable.Range(1, 20).Select(i => new QueueItem
            {
                Request = Song($"歌{i}"),
                Position = i,
                IsCurrent = i == 1,
            }).ToList(),
            DisplayLimit = 3,
        });

        var h = renderer.ComputeContentHeight(267);
        Assert.True(h < 300, $"displayLimit=3 高度应受限（AutoHeight 上限保护）：h={h}");
    }
}

public class QueueOverlayRendererTransparentTests
{
    private static SongRequest Song(string name) => new()
    {
        Platform = "bilibili",
        UserId = "u1",
        SongName = name,
        Status = RequestStatus.Queued,
    };

    private static void PaintNoBackground(QueueOverlayRenderer renderer, Graphics g, int w, int h)
    {
        renderer.ApplyStyle(new OverlayWindowStyleConfig { BackgroundOpacity = 0 });
        renderer.Paint(g, w, h);
    }

    [Fact]
    public void Paint_TransparentBottom_LeavesEmptyAreaAlphaZero()
    {
        var renderer = new QueueOverlayRenderer();
        renderer.Update(new QueueSnapshot { Items = [], DisplayLimit = 5 });
        using var bmp = new Bitmap(300, 150, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            PaintNoBackground(renderer, g, 300, 150);
        }

        // 无内容时整个画布应保持透明（透明底）
        var px = bmp.GetPixel(150, 80);
        Assert.Equal(0, px.A);
    }

    [Fact]
    public void Paint_WithQueue_DrawsContentWithAlpha()
    {
        var renderer = new QueueOverlayRenderer();
        renderer.Update(new QueueSnapshot
        {
            Items =
            [
                new QueueItem { Request = Song("测试歌曲"), Position = 1, IsCurrent = true },
                new QueueItem { Request = Song("下一首"), Position = 2, IsCurrent = false },
            ],
            DisplayLimit = 5,
        });
        using var bmp = new Bitmap(300, 150, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            PaintNoBackground(renderer, g, 300, 150);
        }

        // 左上内容区（强调色条/标签/歌名）应绘出非透明像素
        var anyContent = false;
        for (var y = 0; y < 70 && !anyContent; y++)
        {
            for (var x = 0; x < 220; x++)
            {
                if (bmp.GetPixel(x, y).A > 0)
                {
                    anyContent = true;
                    break;
                }
            }
        }

        Assert.True(anyContent, "内容区应绘制出非透明像素（文字/强调条）");
    }
}

public class QueueUpOverlayRendererTests
{
    [Fact]
    public void Paint_BadgeAndRows_TransparentAround()
    {
        var renderer = new QueueUpOverlayRenderer();
        renderer.ApplyStyle(new OverlayWindowStyleConfig { BackgroundOpacity = 0 });
        renderer.Update(new QueueUpSnapshot
        {
            Items =
            [
                new QueueUpItem
                {
                    Position = 1,
                    Entry = new QueueUpEntry
                    {
                        UserId = "u1",
                        Nickname = "观众A",
                        Content = "点个歌",
                        CreatedAt = DateTimeOffset.UtcNow,
                    },
                },
            ],
        });
        using var bmp = new Bitmap(300, 150, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            renderer.Paint(g, 300, 150);
        }

        // 徽章行区域应有内容；右下角空白应透明
        Assert.True(bmp.GetPixel(20, 20).A > 0, "序号徽章应绘制");
        Assert.Equal(0, bmp.GetPixel(290, 140).A);
    }

    [Fact]
    public void ComputeContentHeight_GrowsWithRows()
    {
        var renderer = new QueueUpOverlayRenderer();
        renderer.Update(new QueueUpSnapshot { Items = [] });
        var hEmpty = renderer.ComputeContentHeight(267);

        renderer.Update(new QueueUpSnapshot
        {
            Items =
            [
                new QueueUpItem { Position = 1, Entry = new QueueUpEntry { UserId = "u1", Nickname = "A", Content = "x", CreatedAt = DateTimeOffset.UtcNow } },
                new QueueUpItem { Position = 2, Entry = new QueueUpEntry { UserId = "u2", Nickname = "B", Content = "y", CreatedAt = DateTimeOffset.UtcNow } },
            ],
        });
        var hFilled = renderer.ComputeContentHeight(267);

        Assert.True(hFilled > hEmpty, $"排队行增多高度应增长：hEmpty={hEmpty} hFilled={hFilled}");
        Assert.True(hEmpty >= 40);
    }
}

public class OverlayDrawingTests
{
    [Fact]
    public void Ellipsize_TruncatesLongText()
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using var font = new Font("Microsoft YaHei", 12f, FontStyle.Regular, GraphicsUnit.Pixel);

        var shortText = OverlayDrawing.Ellipsize(g, "短", font, 200f);
        Assert.Equal("短", shortText);

        var longText = OverlayDrawing.Ellipsize(g, new string('长', 500), font, 60f);
        Assert.EndsWith("…", longText);
        Assert.True(g.MeasureString(longText, font).Width <= 60f + 1f);
    }
}

/// <summary>
/// 2026-09 悬浮窗三项需求的渲染器行为测试：
/// ① 字号范围放宽到 0.6–5x（原 0.6–1.6）；② 点歌/排队消费 Style.FontFamily（原硬编码）；
/// ③ 宽高全自适应（ComputeContentWidth 实测内容宽 + 夹上下限）。
/// 均在窗线程语义下调用（传入测量 Graphics），与 Paint 共用同一套字体。
/// </summary>
public class OverlayAutoSizeAndFontTests
{
    private static SongRequest Song(string name, string singer = "") => new()
    {
        Platform = "bilibili",
        UserId = "u1",
        SongName = name,
        Singer = singer,
        Status = RequestStatus.Queued,
    };

    private static Graphics MeasureGraphics(Bitmap bmp) => Graphics.FromImage(bmp);

    [Fact]
    public void FontScale_AboveOldMax_EnlargesContentBeyond_1_6x()
    {
        // 旧实现 Clamp 到 1.6，FontScale=5 会被截断；放宽后 5x 应显著大于 1x
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = MeasureGraphics(bmp);

        var normal = new QueueOverlayRenderer();
        normal.ApplyStyle(new OverlayWindowStyleConfig { BackgroundOpacity = 0 });
        normal.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("测试"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });

        var huge = new QueueOverlayRenderer();
        huge.ApplyStyle(new OverlayWindowStyleConfig { FontScale = 5.0, BackgroundOpacity = 0 });
        huge.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("测试"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });

        var normalH = normal.ComputeContentHeight(400);
        var hugeH = huge.ComputeContentHeight(400);
        // 5x 字号内容高度应远超 1.6x 截断所能达到的（>2.5x 即证明未被截到 1.6）
        Assert.True(hugeH > normalH * 2.5, $"5× 字号内容应显著更高：normal={normalH} huge={hugeH}");
    }

    [Fact]
    public void ComputeContentWidth_GrowsWithLongerSongName()
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = MeasureGraphics(bmp);

        var renderer = new QueueOverlayRenderer();
        renderer.ApplyStyle(new OverlayWindowStyleConfig { BackgroundOpacity = 0 });
        renderer.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("短"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });
        var wShort = renderer.ComputeContentWidth(g);

        renderer.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("这是一个非常非常长的歌曲名字用来撑宽窗口"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });
        var wLong = renderer.ComputeContentWidth(g);

        Assert.True(wLong > wShort, $"歌名更长宽度应更大：short={wShort} long={wLong}");
    }

    [Fact]
    public void ComputeContentWidth_ClampedToMinMax()
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = MeasureGraphics(bmp);

        // 空快照 → 夹到下限（AutoSizeMinWidth=200）
        var empty = new QueueOverlayRenderer();
        empty.Update(new QueueSnapshot { Items = [], DisplayLimit = 5 });
        var wEmpty = empty.ComputeContentWidth(g);
        Assert.Equal(200, wEmpty);

        // 超长歌名 → 夹到上限（AutoSizeMaxWidth=560），不无限撑宽
        var huge = new QueueOverlayRenderer();
        huge.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song(new string('歌', 200)), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });
        var wHuge = huge.ComputeContentWidth(g);
        Assert.Equal(560, wHuge);
    }

    [Fact]
    public void QueueUp_ComputeContentWidth_GrowsWithContent()
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = MeasureGraphics(bmp);

        var renderer = new QueueUpOverlayRenderer();
        renderer.Update(new QueueUpSnapshot { Items = [] });
        var wEmpty = renderer.ComputeContentWidth(g);

        renderer.Update(new QueueUpSnapshot
        {
            Items =
            [
                new QueueUpItem { Position = 1, Entry = new QueueUpEntry { UserId = "u1", Nickname = "很长的昵称名字", Content = "一段很长的排队内容文字", CreatedAt = DateTimeOffset.UtcNow } },
            ],
        });
        var wFilled = renderer.ComputeContentWidth(g);

        Assert.True(wFilled > wEmpty, $"排队内容更长宽度应更大：empty={wEmpty} filled={wFilled}");
        Assert.True(wEmpty >= 200 && wFilled <= 560, $"宽度应在上下限内：empty={wEmpty} filled={wFilled}");
    }

    [Fact]
    public void FontFamily_InvalidName_FallsBackWithoutThrowing()
    {
        // 点歌/排队原字体硬编码；2026-09 起消费 Style.FontFamily。
        // 无效字体名必须优雅回落默认（Paint/测量都不抛），否则用户选错字体直接崩悬浮窗。
        using var bmp = new Bitmap(400, 200, PixelFormat.Format32bppPArgb);
        using var g = MeasureGraphics(bmp);

        var renderer = new QueueOverlayRenderer();
        renderer.ApplyStyle(new OverlayWindowStyleConfig
        {
            FontFamily = "这个字体一定不存在_xyz_123",
            BackgroundOpacity = 0,
        });
        renderer.Update(new QueueSnapshot
        {
            Items = [new QueueItem { Request = Song("测试", "歌手"), Position = 1, IsCurrent = true }],
            DisplayLimit = 5,
        });

        var ex = Record.Exception(() =>
        {
            g.Clear(Color.Transparent);
            renderer.Paint(g, 400, 200);
            _ = renderer.ComputeContentWidth(g);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void FontFamily_ValidName_ProducesDifferentWidthThanDefault()
    {
        // 有效字体确实被消费：不同字体实测宽度不同（证明未停留在硬编码默认）。
        // 内容必须足够长以超过自适应宽度下限（AutoSizeMinWidth=200）——
        // 否则两种字体都被夹到下限，宽度相同，测不出字体差异。
        static int WidthOf(string family)
        {
            var r = new QueueOverlayRenderer();
            r.ApplyStyle(new OverlayWindowStyleConfig { FontFamily = family, BackgroundOpacity = 0 });
            r.Update(new QueueSnapshot
            {
                Items = [new QueueItem { Request = Song("这是一首很长很长很长很长的测试歌曲名字", "某位歌手"), Position = 1, IsCurrent = true }],
                DisplayLimit = 5,
            });
            using var b = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
            using var gg = Graphics.FromImage(b);
            return r.ComputeContentWidth(gg);
        }

        var yahei = WidthOf("Microsoft YaHei");
        var simsun = WidthOf("SimSun");
        // 两者都应超过下限（证明内容足够长、未被夹紧）
        Assert.True(yahei > 200, $"雅黑宽度应超过下限：{yahei}");
        Assert.True(simsun > 200, $"宋体宽度应超过下限：{simsun}");
        // 宋体与雅黑同字号字宽不同（若字体未被消费，两者会相等）
        Assert.NotEqual(yahei, simsun);
    }
}
