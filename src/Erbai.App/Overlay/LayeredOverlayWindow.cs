using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Erbai.App.Overlay;

/// <summary>
/// 自绘透明悬浮窗（预案 B 产品化，2026-08-29）：UpdateLayeredWindow + GDI+ 位图 per-pixel alpha。
/// 背景：本机 DWM 对「分层窗口 + DComp 内容」的 alpha 混合把底层当白色（Spike A 复现：
/// 半透明蓝混出 (120,120,248) 浅蓝、透明像素纯白；Spike D 实测 ULW 位图透明透出下层
/// Crimson），故弃用 Spike A 的 WebView2 合成模式配方，改为位图提交——alpha 由
/// Format32bppPArgb 位图直接呈现，不经 DComp 合成，任何环境都可靠。
/// 窗口：WS_EX_LAYERED|TRANSPARENT(条件)|TOOLWINDOW|NOACTIVATE；定时重绘 + ULW 提交；
/// 置顶/穿透热切换、非穿透态拖动（复用原 CompositionOverlayWindow 的体验）。
/// </summary>
internal sealed class LayeredOverlayWindow : Form
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int ULW_ALPHA = 0x2;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    private readonly Action<Graphics, int, int> _paint;
    private readonly Func<Graphics, Size, Size>? _autoSizeProvider;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly bool _initialClickThrough;
    private Bitmap? _backBuffer;

    /// <summary>窗线程创建完毕（OnHandleCreated 置位；供 Runtime 等待句柄就绪）。</summary>
    public readonly ManualResetEventSlim HandleReady = new(false);

    /// <summary>渲染异常（记录日志，窗口继续空转）。</summary>
    public event Action<string>? PaintFailed;

    /// <summary>初始化失败（构造期异常已由 Runtime 捕获，此事件用于 OnLoad 期失败）。</summary>
    public event Action<string>? InitFailed;

    /// <param name="paint">每帧绘制回调（窗线程）：透明底自绘 + 文字阴影，勿画不透明背景。</param>
    /// <param name="intervalMs">重绘间隔：弹幕动画 33ms；点歌/排队低频 500ms。</param>
    /// <param name="autoSizeProvider">
    /// 宽高自适应测量回调（窗线程，2026-09）：每帧用测量 Graphics + 当前窗口尺寸算目标尺寸
    /// → 右下角锚定 resize。必须在窗线程执行：渲染器测量依赖 Paint 同一套字体，跨线程会与
    /// EnsureFonts 并发 Dispose/重建字体（GDI+ 非线程安全），且跨线程读 Form.Size 非法。
    /// null = 不自适应（固定宽高）。
    /// </param>
    public LayeredOverlayWindow(int width, int height, bool topmost, bool clickThrough,
        Point? location, Action<Graphics, int, int> paint, int intervalMs = 33,
        Func<Graphics, Size, Size>? autoSizeProvider = null)
    {
        _paint = paint;
        _autoSizeProvider = autoSizeProvider;
        _initialClickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = topmost;

        var area = Screen.PrimaryScreen!.WorkingArea;
        Size = new Size(width, height);
        Location = location ?? new Point(area.Right - width - 40, area.Bottom - height - 80);
        TrackAnchor();

        _timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(intervalMs, 16, 5000) };
        _timer.Tick += (_, _) => RenderTick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        HandleReady.Set();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (_initialClickThrough)
            {
                cp.ExStyle |= WS_EX_TRANSPARENT;
            }

            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            RenderTick(); // 首帧（含失败即 InitFailed）
            _timer.Start();
        }
        catch (Exception ex)
        {
            InitFailed?.Invoke($"自绘悬浮窗首帧失败：{ex.Message}");
            BeginInvoke(Close);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _backBuffer?.Dispose();
        _backBuffer = null;
        base.OnFormClosed(e);
    }

    // ---- 拖动（决策 #17 悬浮窗体验）：仅非穿透态可拖（穿透时 WS_EX_TRANSPARENT 收不到鼠标）----

    private bool _dragging;
    private Point _dragStartScreen;
    private Point _windowStart;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStartScreen = PointToScreen(e.Location);
            _windowStart = Location;
            Capture = true;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            var cur = Cursor.Position;
            Location = new Point(
                _windowStart.X + cur.X - _dragStartScreen.X,
                _windowStart.Y + cur.Y - _dragStartScreen.Y);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            TrackAnchor(); // 拖动结束刷新右下角缓存（关闭时采集持久化）
        }

        base.OnMouseUp(e);
    }

    /// <summary>绘制一帧并提交（位图复用，避免每帧分配）。宽高自适应时在绘制前按内容 resize。</summary>
    private void RenderTick()
    {
        try
        {
            if (_autoSizeProvider is not null)
            {
                ApplyAutoSize();
            }

            _backBuffer ??= new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(_backBuffer);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);
            _paint(g, Size.Width, Size.Height);
            Submit(_backBuffer);
        }
        catch (Exception ex)
        {
            PaintFailed?.Invoke($"自绘悬浮窗渲染失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 宽高自适应（2026-09）：用一张离屏 1×1 位图的 Graphics 让渲染器实测内容宽高，
    /// 与当前尺寸不同才 resize（右下角锚定，向左上生长）。
    /// 测量 Graphics 只用于 MeasureString，不影响提交用的 backBuffer。
    /// 上限受屏幕工作区约束，防止字号放大到 5× 时窗口超出屏幕。
    /// </summary>
    private void ApplyAutoSize()
    {
        using var probe = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var pg = Graphics.FromImage(probe);
        pg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        var target = _autoSizeProvider!(pg, Size);
        var area = Screen.FromControl(this).WorkingArea;
        var width = Math.Clamp(target.Width, 40, Math.Max(40, area.Width - 40));
        var height = Math.Clamp(target.Height, 40, Math.Max(40, area.Height - 40));
        ResizeTo(width, height);
    }

    private void Submit(Bitmap bmp)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0)); // PArgb → premultiplied alpha
        var old = SelectObject(memDc, hBitmap);
        var dst = new POINT { X = Location.X, Y = Location.Y };
        var size = new SIZE { W = bmp.Width, H = bmp.Height };
        var src = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        _ = UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        SelectObject(memDc, old);
        DeleteObject(hBitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    /// <summary>置顶热切换（Form.TopMost → SetWindowPos(HWND_TOPMOST)）。</summary>
    public void ApplyTopmost(bool flag)
    {
        if (IsHandleCreated && !IsDisposed)
        {
            TopMost = flag;
        }
    }

    /// <summary>
    /// 调整窗口尺寸（宽高自适应，2026-09）：<b>右下角锚定</b>——宽/高变化时向左上生长
    /// （右边界与底边不动），位图缓存尺寸失效后下一帧重建。
    /// 原实现只锚底边（顶边上移、左边界不动），宽度也自适应后会向右溢出屏幕；
    /// 且右下角锚定与位置持久化语义一致（存 AnchorRight/AnchorBottom，见 OverlayWindowItemConfig）。
    /// </summary>
    public void ResizeTo(int width, int height)
    {
        if (Size.Width == width && Size.Height == height)
        {
            return;
        }

        var right = Location.X + Size.Width;
        var bottom = Location.Y + Size.Height;
        Size = new Size(width, height);
        Location = new Point(right - width, bottom - height);
        _backBuffer?.Dispose();
        _backBuffer = null;
        TrackAnchor();
    }

    /// <summary>
    /// 当前窗口<b>右下角</b>屏幕坐标（位置持久化采集用）。值在窗线程内随构造/拖动/尺寸变化
    /// 维护成缓存字段，故任意线程可安全读取（不触碰 Control.Location 跨线程路径）。
    /// </summary>
    public (int Right, int Bottom) AnchorBottomRight => (_anchorRight, _anchorBottom);

    private volatile int _anchorRight;
    private volatile int _anchorBottom;

    /// <summary>刷新右下角缓存（须在窗线程调用：读 Location/Size）。</summary>
    private void TrackAnchor()
    {
        _anchorRight = Location.X + Size.Width;
        _anchorBottom = Location.Y + Size.Height;
    }

    /// <summary>点击穿透热切换：增删 WS_EX_TRANSPARENT + SWP_FRAMECHANGED 刷新命中测试。</summary>
    public void ApplyClickThrough(bool flag)
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        var ex = GetWindowLong(Handle, GWL_EXSTYLE);
        var next = flag ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (next != ex)
        {
            _ = SetWindowLongPtr(Handle, GWL_EXSTYLE, next);
            _ = SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int W, H; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
