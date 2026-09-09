using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Queue;
using Erbai.Contracts.QueueUp;

namespace Erbai.App.Overlay;

/// <summary>
/// 自绘渲染器基类：窗线程 Paint 时调用 <see cref="Paint"/>；数据更新（任意线程）经
/// Update* 进入，内部锁保护。透明底：不画任何容器背景，仅文字/内容元素 + 阴影。
/// 样式参数（强调色/字号缩放/描边/发光/不透明度）经 <see cref="ApplyStyle"/> 注入，
/// 设置页热切换实时生效（2026-08-29 样式参数化，参考 AwooMusicBot/bilipdj/blivechat）。
/// </summary>
internal abstract class OverlayRenderer
{
    private readonly object _styleGate = new();
    private OverlayWindowStyleConfig _style = new();

    public abstract void Paint(Graphics g, int width, int height);

    /// <summary>当前快照内容高度（宽高自适应窗口用；非自适渲染器返回 0）。</summary>
    public virtual int ComputeContentHeight(int width) => 0;

    /// <summary>
    /// 当前快照的内容宽度（宽高自适应窗口用，2026-09）：按最长一行实测文字宽 + 边距，
    /// 使窗口宽度贴合内容。须在窗线程调用并传入 Graphics——测量依赖 Paint 同一套字体，
    /// 跨线程调用会与 Paint 的 EnsureFonts 并发重建/Dispose 字体（GDI+ 非线程安全）。
    /// 非自适渲染器返回 0。
    /// </summary>
    public virtual int ComputeContentWidth(Graphics g) => 0;

    /// <summary>注入样式（任意线程；Paint 时读取，线程安全）。</summary>
    public void ApplyStyle(OverlayWindowStyleConfig style)
    {
        lock (_styleGate)
        {
            _style = style;
        }
    }

    /// <summary>解析强调色（#RRGGBB；空 = 类型默认）。</summary>
    protected Color ResolveAccent(Color fallback)
    {
        var raw = Style.AccentColor;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        try
        {
            return ColorTranslator.FromHtml(raw);
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>解析正文文字颜色（#RRGGBB；空 = 默认白）。次级文字由调用方乘 alpha。</summary>
    protected Color ResolveText(Color fallback)
    {
        var raw = Style.TextColor;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        try
        {
            return ColorTranslator.FromHtml(raw);
        }
        catch
        {
            return fallback;
        }
    }

    protected OverlayWindowStyleConfig Style
    {
        get
        {
            lock (_styleGate)
            {
                return _style;
            }
        }
    }

    /// <summary>字号缩放系数（Paint 内字体重建用）。范围 0.6–5.0（2026-09 用户放宽，原 0.6–1.6）。</summary>
    protected float FontScale => (float)Math.Clamp(Style.FontScale, 0.6, 5.0);

    /// <summary>
    /// 配置字体名 → GDI+ 可用字体族（空/无效回落默认微软雅黑）。
    /// 点歌/排队原先字体名硬编码，2026-09 起消费 Style.FontFamily 与弹幕一致。
    /// <see cref="Font"/> 构造对不存在的字体名会静默回落系统默认（视觉突变却无从排查），
    /// 故这里先用 <see cref="FontFamily"/> 探测：字体未安装时抛 ArgumentException → 回落默认。
    /// </summary>
    protected string FontFamilyName
    {
        get
        {
            var raw = Style.FontFamily;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return DefaultFontFamily;
            }

            try
            {
                using var probe = new FontFamily(raw.Trim());
                return probe.Name;
            }
            catch (ArgumentException)
            {
                return DefaultFontFamily; // 字体未安装：回落默认
            }
        }
    }

    /// <summary>悬浮窗默认字体（与弹幕窗 WpfDanmakuWindow.DefaultFontFamily 保持一致）。</summary>
    protected const string DefaultFontFamily = "Microsoft YaHei";

    // ---- 宽高自适应的边距与上下限（点歌/排队共用，2026-09）----

    /// <summary>内容右侧留白：背景面板内边距 8px + 余量，避免文字贴边。</summary>
    protected const int AutoSizeRightPadding = 16;

    /// <summary>自适应宽度下限：过窄会挤成多行/省略号，保底可读。</summary>
    protected const int AutoSizeMinWidth = 200;

    /// <summary>
    /// 自适应宽度上限：超长歌名/昵称不应把悬浮窗撑到半屏（遮挡直播画面）。
    /// 超过则由 Paint 的 Ellipsize 截断。
    /// </summary>
    protected const int AutoSizeMaxWidth = 560;

    /// <summary>实测内容宽 → 夹到自适应上下限（宽高自适应共用）。</summary>
    protected static int ClampAutoSizeWidth(double measured) =>
        (int)Math.Clamp(Math.Ceiling(measured), AutoSizeMinWidth, AutoSizeMaxWidth);

    /// <summary>内容不透明度 alpha 系数（0.5–1 → 128–255）。</summary>
    protected float OpacityAlpha => (float)(Math.Clamp(Style.Opacity, 0.5, 1.0) * 255);

    protected float OutlineStrength => (float)Math.Clamp(Style.TextOutline, 0.0, 1.0);

    protected float GlowStrength => (float)Math.Clamp(Style.TextGlow, 0.0, 1.0);

    /// <summary>背景面板不透明度（0–0.95；0 = 无底全透明）。</summary>
    protected float BackgroundAlpha => (float)Math.Clamp(Style.BackgroundOpacity, 0.0, 0.95) * 255f;

    /// <summary>画圆角半透明背景面板（深蓝黑，bilipdj moren.css / AwooMusicBot 玻璃面板参考；
    /// 留 8px 边距 + 淡白边框）。所有渲染器 Paint 开头调用，内容绘制在其上。</summary>
    protected void DrawBackground(Graphics g, int width, int height)
    {
        var a = BackgroundAlpha;
        if (a <= 1f)
        {
            return;
        }

        using var path = RoundedRectPath(new Rectangle(8, 8, width - 16, height - 16), 12);
        using var fill = new SolidBrush(Color.FromArgb((int)a, 10, 16, 32));
        g.FillPath(fill, path);
        using var border = new Pen(Color.FromArgb((int)(a * 0.14f), 255, 255, 255), 1f);
        g.DrawPath(border, path);
    }

    private static GraphicsPath RoundedRectPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>按 alpha 系数压暗颜色（不透明度参数统一入口）。</summary>
    protected Color WithOpacity(Color c) => Color.FromArgb((int)(c.A * OpacityAlpha / 255f), c);

    /// <summary>替换 alpha 通道保留 RGB（次级文字用配置文字色 + 固定透明度）。</summary>
    protected static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>GDI+ 自绘工具：文字阴影（透明底上白字可读性）+ 可选描边/发光（样式参数化）。</summary>
internal static class OverlayDrawing
{
    /// <summary>画带黑色投影的文字（透明底自绘的统一文字渲染入口）。
    /// <paramref name="outline"/> 0–1 黑色描边强度（8 方向偏移模拟）；
    /// <paramref name="glow"/> 0–1 强调色光晕强度（多半径同心偏移模拟，bilipdj 霓虹风）；
    /// <paramref name="opacity"/> 0–1 整体不透明度（前景/阴影/描边/发光统一乘，样式参数化）。</summary>
    public static void Text(Graphics g, string text, Font font, Color color, float x, float y,
        float shadowAlpha = 200f, float shadowOffset = 1f, float outline = 0f, Color glowColor = default,
        float glow = 0f, float opacity = 1f)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var op = Math.Clamp(opacity, 0f, 1f);
        if (op <= 0.01f)
        {
            return;
        }

        // 全部文字绘制走 GraphicsPath + FillPath：DrawString 会忽略 Brush 的 alpha
        // （文本实心区恒 alpha=255，半透明阴影/不透明度参数失效——实测复现），
        // FillPath 尊重 alpha（2026-08-29 样式参数化修复）。

        if (glow > 0.01f)
        {
            // 光晕：强调色多半径同心偏移（GDI+ 无 blur，近似法）
            var gc = Color.FromArgb((int)(46 * glow * op), glowColor.R, glowColor.G, glowColor.B);
            using var glowBrush = new SolidBrush(gc);
            for (var r = 2; r <= 6; r += 2)
            {
                FillText(g, text, font, glowBrush, x - r, y);
                FillText(g, text, font, glowBrush, x + r, y);
                FillText(g, text, font, glowBrush, x, y - r);
                FillText(g, text, font, glowBrush, x, y + r);
            }
        }

        if (outline > 0.01f)
        {
            // 描边：黑色 8 方向偏移（alpha 随强度）
            var ob = Color.FromArgb((int)((70 + 150 * outline) * op), 0, 0, 0);
            using var outlineBrush = new SolidBrush(ob);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx != 0 || dy != 0)
                    {
                        FillText(g, text, font, outlineBrush, x + dx, y + dy);
                    }
                }
            }
        }

        if (shadowAlpha > 0)
        {
            using var shadow = new SolidBrush(Color.FromArgb((int)(shadowAlpha * op), 0, 0, 0));
            FillText(g, text, font, shadow, x + shadowOffset, y + shadowOffset);
        }

        using var fg = new SolidBrush(Color.FromArgb((int)(color.A * op), color));
        FillText(g, text, font, fg, x, y);
    }

    /// <summary>路径填充绘制文本（尊重 Brush alpha；与 DrawString 有 ~1px 定位差，可接受）。</summary>
    private static void FillText(Graphics g, string text, Font font, Brush brush, float x, float y)
    {
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size,
            new PointF(x, y), StringFormat.GenericDefault);
        g.FillPath(brush, path);
    }

    /// <summary>字符串截断到像素宽（加省略号）。</summary>
    public static string Ellipsize(Graphics g, string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || g.MeasureString(text, font).Width <= maxWidth)
        {
            return text ?? "";
        }

        const string dot = "…";
        var w = g.MeasureString(dot, font).Width;
        var budget = maxWidth - w;
        var chars = new List<char>(text.Length + 1);
        foreach (var ch in text)
        {
            if (g.MeasureString(new string(chars.Append(ch).ToArray()), font).Width > budget)
            {
                break;
            }

            chars.Add(ch);
        }

        return new string(chars.ToArray()) + dot;
    }
}

/// <summary>点歌悬浮窗渲染器（/overlay/queue 视觉翻译：透明底 + 白字阴影 + 强调色条；
/// 样式参数化：强调色/字号缩放/描边/发光/不透明度，2026-08-29）。</summary>
internal sealed class QueueOverlayRenderer : OverlayRenderer
{
    private readonly object _gate = new();
    private QueueSnapshot _snapshot = new();
    private Font _labelFont = new("Microsoft YaHei", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font _titleFont = new("Microsoft YaHei", 23f, FontStyle.Bold, GraphicsUnit.Pixel);
    private Font _singerFont = new("Microsoft YaHei", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font _itemFont = new("Microsoft YaHei", 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font _posFont = new("Microsoft YaHei", 13.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private Color _accent = Color.FromArgb(255, 123, 84);
    private Color _cyan = Color.FromArgb(127, 208, 255);
    private float _cachedScale = -1f;
    private string _cachedFamily = "";

    public void Update(QueueSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
        }
    }

    /// <summary>字号/字体随样式重建（仅二者变化时，避免每帧创建字体）。
    /// 缓存键必须同时含字体名：只改字体不改字号时若只比 scale 会漏重建（2026-09 字体可配）。</summary>
    private void EnsureFonts(float scale, string family)
    {
        if (Math.Abs(_cachedScale - scale) < 0.01f &&
            string.Equals(_cachedFamily, family, StringComparison.Ordinal))
        {
            return;
        }

        _cachedScale = scale;
        _cachedFamily = family;
        _labelFont.Dispose(); _titleFont.Dispose(); _singerFont.Dispose();
        _itemFont.Dispose(); _posFont.Dispose();
        _labelFont = new Font(family, 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _titleFont = new Font(family, 23f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _singerFont = new Font(family, 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _itemFont = new Font(family, 13.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _posFont = new Font(family, 13.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    public override void Paint(Graphics g, int width, int height)
    {
        QueueSnapshot s;
        lock (_gate)
        {
            s = _snapshot;
        }

        var scale = FontScale;
        EnsureFonts(scale, FontFamilyName);
        _accent = ResolveAccent(Color.FromArgb(255, 123, 84));
        var text = ResolveText(Color.White);
        var outline = OutlineStrength;
        var glow = GlowStrength;
        var op = OpacityAlpha / 255f;
        DrawBackground(g, width, height);

        var items = s.Items ?? [];
        var current = items.FirstOrDefault(i => i.IsCurrent);
        var y = 12f;

        if (current is not null)
        {
            // 正在播放：左侧强调色条 + 标签 + 歌名 + 歌手
            using var bar = new SolidBrush(WithOpacity(_accent));
            g.FillRectangle(bar, 12, y + 2, 4, 44 * scale);
            OverlayDrawing.Text(g, "♪ 正在播放", _labelFont, Alpha(text, 200), 28, y,
                outline: outline, glowColor: _accent, glow: glow, opacity: op);
            y += 20 * scale;
            var title = OverlayDrawing.Ellipsize(g, current.Request.SongName ?? "", _titleFont, width - 40);
            OverlayDrawing.Text(g, title, _titleFont, text, 26, y,
                shadowAlpha: 230f, outline: outline, glowColor: _accent, glow: glow, opacity: op);
            y += 34 * scale;
            var singer = current.Request.Singer ?? "";
            if (singer.Length > 0)
            {
                OverlayDrawing.Text(g, singer, _singerFont, Alpha(text, 230), 28, y,
                    outline: outline, glowColor: _accent, glow: glow, opacity: op);
                y += 24 * scale;
            }
            else
            {
                y += 18 * scale;
            }
        }
        else if (s.Player is { SongName.Length: > 0 })
        {
            // 点歌队列无正在播放项 → 可能正在播"空闲歌单"：复用快照 Player 字段
            // 回落显示（overlay 数据源原只在 is_current 项上画正在播放）。
            using var bar = new SolidBrush(WithOpacity(_accent));
            g.FillRectangle(bar, 12, y + 2, 4, 44 * scale);
            OverlayDrawing.Text(g, "♪ 正在播放", _labelFont, Alpha(text, 200), 28, y,
                outline: outline, glowColor: _accent, glow: glow, opacity: op);
            y += 20 * scale;
            var title = OverlayDrawing.Ellipsize(g, s.Player.SongName, _titleFont, width - 40);
            OverlayDrawing.Text(g, title, _titleFont, text, 26, y,
                shadowAlpha: 230f, outline: outline, glowColor: _accent, glow: glow, opacity: op);
            y += 34 * scale;
            if (s.Player.Singer is { Length: > 0 })
            {
                OverlayDrawing.Text(g, s.Player.Singer, _singerFont, Alpha(text, 230), 28, y,
                    outline: outline, glowColor: _accent, glow: glow, opacity: op);
                y += 24 * scale;
            }
            else
            {
                y += 18 * scale;
            }
        }

        // 点歌队列（显示前 N 条，跳过正在播放的）
        var displayLimit = s.DisplayLimit ?? 5;
        var queueItems = items.Where(i => !i.IsCurrent).Take(Math.Max(0, displayLimit)).ToList();
        if (queueItems.Count > 0)
        {
            if (current is null)
            {
                OverlayDrawing.Text(g, "点歌队列", _labelFont, Alpha(text, 200), 12, y,
                    outline: outline, glowColor: _accent, glow: glow, opacity: op);
                y += 20 * scale;
            }

            foreach (var item in queueItems)
            {
                if (y > height - 18)
                {
                    break; // 底部截断（原 HTML 的渐隐蒙版以行数截断代替）
                }

                var pos = $"{item.Position}.";
                var pw = g.MeasureString(pos, _posFont).Width;
                OverlayDrawing.Text(g, pos, _posFont, _cyan, 16, y, outline: outline, opacity: op);
                var song = $"{item.Request.SongName}{Suffix(item.Request.Singer)}";
                var songText = OverlayDrawing.Ellipsize(g, song, _itemFont, width - 16 - pw - 10);
                OverlayDrawing.Text(g, songText, _itemFont, text, 16 + pw + 8, y,
                    outline: outline, glowColor: _accent, glow: glow, opacity: op);
                y += 24 * scale;
            }
        }
    }

    private static string Suffix(string? singer) =>
        string.IsNullOrEmpty(singer) ? "" : $" - {singer}";

    /// <summary>当前快照的内容高度（AutoHeight 自适应窗口用；行高/间距与 Paint 一致）。</summary>
    public override int ComputeContentHeight(int width)
    {
        QueueSnapshot s;
        lock (_gate)
        {
            s = _snapshot;
        }

        var scale = FontScale;
        var items = s.Items ?? [];
        var current = items.FirstOrDefault(i => i.IsCurrent);
        var displayLimit = s.DisplayLimit ?? 5;
        var queueCount = Math.Min(items.Count(i => !i.IsCurrent), Math.Max(0, displayLimit));
        var y = 12.0;
        if (current is not null)
        {
            y += 20 * scale; // 正在播放 label
            y += 34 * scale; // 歌名
            var singer = current.Request.Singer ?? "";
            y += singer.Length > 0 ? 24 * scale : 18 * scale;
        }
        else if (s.Player is { SongName.Length: > 0 })
        {
            // 空闲歌单在播：与 Paint 的回落一致计入高度（AutoHeight 自适应）
            y += 20 * scale;
            y += 34 * scale;
            y += s.Player.Singer is { Length: > 0 } ? 24 * scale : 18 * scale;
        }

        if (queueCount > 0 && current is null)
        {
            y += 20 * scale; // 点歌队列 label
        }

        y += queueCount * 24 * scale; // 每行
        return Math.Max(40, (int)Math.Ceiling(y + 8));
    }

    /// <summary>
    /// 当前快照的内容宽度（宽高自适应，2026-09）：逐行按 Paint 的实际 x 起点 + 实测文字宽，
    /// 取最宽者 + 右侧留白，再夹到 [AutoSizeMinWidth, AutoSizeMaxWidth]。
    /// 超上限时由 Paint 的 Ellipsize 截断（长歌名不把窗口撑到遮挡直播画面）。
    /// 须窗线程调用（与 EnsureFonts 共字体）。
    /// </summary>
    public override int ComputeContentWidth(Graphics g)
    {
        QueueSnapshot s;
        lock (_gate)
        {
            s = _snapshot;
        }

        var scale = FontScale;
        EnsureFonts(scale, FontFamilyName);

        var widest = 0f;
        var items = s.Items ?? [];
        var current = items.FirstOrDefault(i => i.IsCurrent);

        // 正在播放区（与 Paint 的 x 起点一致：label/歌手 28、歌名 26）
        if (current is not null)
        {
            widest = Math.Max(widest, 28 + g.MeasureString("♪ 正在播放", _labelFont).Width);
            widest = Math.Max(widest,
                26 + g.MeasureString(current.Request.SongName ?? "", _titleFont).Width);
            if ((current.Request.Singer ?? "").Length > 0)
            {
                widest = Math.Max(widest, 28 + g.MeasureString(current.Request.Singer!, _singerFont).Width);
            }
        }
        else if (s.Player is { SongName.Length: > 0 })
        {
            // 空闲歌单在播的回落显示（与 Paint 同构）
            widest = Math.Max(widest, 28 + g.MeasureString("♪ 正在播放", _labelFont).Width);
            widest = Math.Max(widest, 26 + g.MeasureString(s.Player.SongName, _titleFont).Width);
            if (s.Player.Singer is { Length: > 0 })
            {
                widest = Math.Max(widest, 28 + g.MeasureString(s.Player.Singer, _singerFont).Width);
            }
        }

        var displayLimit = s.DisplayLimit ?? 5;
        var queueItems = items.Where(i => !i.IsCurrent).Take(Math.Max(0, displayLimit)).ToList();
        if (queueItems.Count > 0)
        {
            if (current is null)
            {
                widest = Math.Max(widest, 12 + g.MeasureString("点歌队列", _labelFont).Width);
            }

            foreach (var item in queueItems)
            {
                // Paint 布局：序号在 x=16，歌名在 16 + 序号宽 + 8
                var pw = g.MeasureString($"{item.Position}.", _posFont).Width;
                var song = $"{item.Request.SongName}{Suffix(item.Request.Singer)}";
                widest = Math.Max(widest, 16 + pw + 8 + g.MeasureString(song, _itemFont).Width);
            }
        }

        return ClampAutoSizeWidth(widest + AutoSizeRightPadding);
    }

    public void DisposeFonts()
    {
        _labelFont.Dispose(); _titleFont.Dispose(); _singerFont.Dispose();
        _itemFont.Dispose(); _posFont.Dispose();
    }
}

/// <summary>排队看板悬浮窗渲染器（/overlay/queueup 视觉翻译：序号徽章 + 昵称 + 内容 + 礼物角标；
/// 样式参数化：徽章渐变随强调色、字号缩放、描边/发光、不透明度）。</summary>
internal sealed class QueueUpOverlayRenderer : OverlayRenderer
{
    private readonly object _gate = new();
    private QueueUpSnapshot _snapshot = new();
    private Font _labelFont = new("Microsoft YaHei", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font _itemFont = new("Microsoft YaHei", 14.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font _nickFont = new("Microsoft YaHei", 14.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private Font _badgeFont = new("Microsoft YaHei", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
    private Font _tagFont = new("Microsoft YaHei", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
    private float _cachedScale = -1f;
    private string _cachedFamily = "";

    public void Update(QueueUpSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
        }
    }

    /// <summary>字号/字体随样式重建；缓存键同时含字体名（只改字体不改字号也要重建，2026-09 字体可配）。</summary>
    private void EnsureFonts(float scale, string family)
    {
        if (Math.Abs(_cachedScale - scale) < 0.01f &&
            string.Equals(_cachedFamily, family, StringComparison.Ordinal))
        {
            return;
        }

        _cachedScale = scale;
        _cachedFamily = family;
        _labelFont.Dispose(); _itemFont.Dispose(); _nickFont.Dispose();
        _badgeFont.Dispose(); _tagFont.Dispose();
        _labelFont = new Font(family, 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _itemFont = new Font(family, 14.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _nickFont = new Font(family, 14.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _badgeFont = new Font(family, 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _tagFont = new Font(family, 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    public override void Paint(Graphics g, int width, int height)
    {
        QueueUpSnapshot s;
        lock (_gate)
        {
            s = _snapshot;
        }

        var scale = FontScale;
        EnsureFonts(scale, FontFamilyName);
        var accent = ResolveAccent(Color.FromArgb(255, 154, 60));
        var text = ResolveText(Color.White);
        var outline = OutlineStrength;
        var glow = GlowStrength;
        var op = OpacityAlpha / 255f;
        DrawBackground(g, width, height);

        var y = 12f;
        OverlayDrawing.Text(g, "排队队列", _labelFont, Alpha(text, 200), 16, y,
            outline: outline, glowColor: accent, glow: glow, opacity: op);
        y += 24 * scale;

        var rowH = 28 * scale;
        var badgeSize = 24 * scale;
        var maxRows = Math.Max(1, (int)((height - y - 8) / rowH));
        foreach (var item in (s.Items ?? []).Take(maxRows))
        {
            // 序号徽章（强调色渐变圆）
            using var badgeBrush = new LinearGradientBrush(
                new RectangleF(16, y, badgeSize, badgeSize),
                Lighten(accent, 0.55f), accent, 90f);
            g.FillEllipse(badgeBrush, 16, y, badgeSize, badgeSize);
            var posText = item.Position.ToString();
            var posSize = g.MeasureString(posText, _badgeFont);
            OverlayDrawing.Text(g, posText, _badgeFont, Color.FromArgb(36, 28, 0),
                16 + (badgeSize - posSize.Width) / 2, y + (badgeSize - posSize.Height) / 2, shadowAlpha: 0);

            var x = 16 + badgeSize + 8;
            var nick = item.Entry.Nickname ?? "";
            var nickText = OverlayDrawing.Ellipsize(g, nick, _nickFont, width * 0.35f);
            OverlayDrawing.Text(g, nickText, _nickFont, text, x, y + 3, outline: outline, opacity: op);

            x += g.MeasureString(nickText, _nickFont).Width + 10;
            var content = item.Entry.Content ?? "";
            if (content.Length > 0)
            {
                var contentText = OverlayDrawing.Ellipsize(g, content, _itemFont, width - x - 8);
                OverlayDrawing.Text(g, contentText, _itemFont, Alpha(text, 235), x, y + 3,
                    outline: outline, glowColor: accent, glow: glow, opacity: op);
                x += g.MeasureString(contentText, _itemFont).Width + 8;
            }

            if (item.Entry.Source == QueueUpSource.Gift)
            {
                // 礼物角标（内容级小标签，保留半透明底）
                var tag = "礼物";
                var tagSize = g.MeasureString(tag, _tagFont);
                using var tagBg = new SolidBrush(Color.FromArgb(36, 255, 120, 80));
                g.FillRectangle(tagBg, x, y + 3, tagSize.Width + 14, tagSize.Height + 2);
                using var tagFg = new SolidBrush(Color.FromArgb(255, 154, 108));
                g.DrawString(tag, _tagFont, tagFg, x + 7, y + 3);
            }

            y += rowH;
        }
    }

    /// <summary>向白色方向提亮（渐变高光端）。</summary>
    private static Color Lighten(Color c, float t) => Color.FromArgb(c.A,
        (int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t));

    /// <summary>当前快照的内容高度（AutoHeight 自适应窗口用；行高/间距与 Paint 一致）。</summary>
    public override int ComputeContentHeight(int width)
    {
        QueueUpSnapshot s;
        lock (_gate)
        {
            s = _snapshot;
        }

        var scale = FontScale;
        var rowH = 28 * scale;
        var rows = (s.Items ?? []).Count();
        var y = 12.0 + 24 * scale; // 标题
        y += rows * rowH;
        return Math.Max(40, (int)Math.Ceiling(y + 8));
    }

    /// <summary>
    /// 当前快照的内容宽度（宽高自适应，2026-09）：按 Paint 的 x 累加同构实测每行
    /// （徽章 + 8 + 昵称 + 10 + 内容 + 8 + 礼物角标），取最宽行 + 右侧留白，夹到上下限。
    /// 须窗线程调用（与 EnsureFonts 共字体）。
    /// </summary>
    public override int ComputeContentWidth(Graphics g)
    {
        QueueUpSnapshot s;
        lock (_gate)
        {
            s = _snapshot;
        }

        var scale = FontScale;
        EnsureFonts(scale, FontFamilyName);

        var badgeSize = 24 * scale;
        var widest = 16 + g.MeasureString("排队队列", _labelFont).Width;
        foreach (var item in s.Items ?? [])
        {
            var x = 16 + badgeSize + 8;
            x += g.MeasureString(item.Entry.Nickname ?? "", _nickFont).Width + 10;
            var content = item.Entry.Content ?? "";
            if (content.Length > 0)
            {
                x += g.MeasureString(content, _itemFont).Width + 8;
            }

            if (item.Entry.Source == QueueUpSource.Gift)
            {
                x += g.MeasureString("礼物", _tagFont).Width + 14;
            }

            widest = Math.Max(widest, x);
        }

        return ClampAutoSizeWidth(widest + AutoSizeRightPadding);
    }

    public void DisposeFonts()
    {
        _labelFont.Dispose(); _itemFont.Dispose(); _nickFont.Dispose();
        _badgeFont.Dispose(); _tagFont.Dispose();
    }
}

