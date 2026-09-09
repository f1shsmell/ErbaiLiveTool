using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;

namespace Erbai.OverlayWpf;

/// <summary>
/// bililive_dm（B站弹幕姬）同款侧边栏弹幕悬浮窗（WPF，2026-09 起参考 PCL ModAnimation 重做动画层）。
/// 视觉对齐弹幕姬 DanmakuTextControl：半透明深灰底条（#7B303030 系）+ 黄色用户名 + 白色内容；
/// 时间线 = 高度拉伸 → 文字淡入 → 停留 → 淡出后移除（Store.MainOverlayEffect1-4 默认值）。
///
/// 动画引擎（2026-09 重做，参考 PCL Modules/Base/ModAnimation.vb）：弃用"每行一个 Storyboard"，
/// 改为 <b>单 DispatcherTimer 帧循环 + 行状态机 + 元素对象池</b>——
///   1. 单 33ms 时钟推进全部活跃行（PCL AniRunning/AniGroups 同思路），按 TickCount 计算
///      每行四段时间（Expand/TextIn/Hold/Fade）进度并代码插值，不再为每行分配动画对象，
///      高弹幕量时无 Storyboard/时钟对象 GC 压力，行为可精确控制（暂停/加速/整体淡出）。
///   2. Border/TextBlock 走对象池：行退场回收清空重置，新行优先复用，降低高频新建开销。
///   3. 视觉时序语义与弹幕姬参数化完全一致（线性拉伸/线性淡入/停留/线性淡出），
///      设置页 Effect* 参数兼容不变。
/// 窗口运行在独立 STA 线程（WpfDanmakuOverlayRuntime 管理，Dispatcher.Run 消息循环），
/// code-only（无 .xaml）：WinUI 3 主程序同进程共存，规避双 XAML 编译冲突。
/// </summary>
internal sealed class WpfDanmakuWindow : Window
{
    // bililive_dm Store 默认动画参数（MainOverlayEffect1-4 / MainOverlayFontsize）；
    // 运行时值来自 OverlayWindowStyleConfig.Effect*（设置页可调，见 docs/02 #17 修订）。
    private const double BaseFontSize = 18.667;
    private const string DefaultFontFamily = "Microsoft YaHei";

    /// <summary>帧间隔：33ms ≈ 30fps（弹幕姬与旧 GDI+ 弹幕方案同帧率；行动画平滑度足够）。</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly StackPanel _root;
    private readonly List<ActiveLine> _lines = new(); // 活跃行（PCL AniGroups 等价：按加入序推进）
    private readonly Stack<Border> _borderPool = new(); // Border 对象池（回收重置，行内 TextBlock 同步池化）
    private readonly Stack<TextBlock> _textPool = new();
    private readonly DispatcherTimer _ticker;
    private readonly object _styleGate = new();
    private OverlayWindowStyleConfig _style = new();
    private bool _clickThrough;
    private bool _dragging;
    private Point _dragStart;
    private double _dragWinLeft;
    private double _dragWinTop;

    /// <summary>待应用的右下角屏幕像素锚点（句柄就绪后定位；拖动/高度伸缩时刷新）。</summary>
    private (int Right, int Bottom)? _pendingAnchor;

    // 右下角屏幕像素缓存：在窗线程内随定位/拖动/伸缩维护，供关闭时跨线程采集（
    // 与 LayeredOverlayWindow.AnchorBottomRight 同构——不经 WindowInteropHelper 跨线程读 WPF 对象）
    private volatile int _anchorRight;
    private volatile int _anchorBottom;
    private volatile bool _anchorReady;

    /// <summary>高度伸缩的下限（低于此值等同无弹幕时的最小可视高度）。</summary>
    private const double MinAutoHeight = 60;

    /// <summary>高度伸缩的上限：行数很多时不无限撑高遮挡直播画面（超出由 MaxLines 截断）。</summary>
    private const double MaxAutoHeight = 720;

    /// <summary>
    /// 当前窗口<b>右下角</b>屏幕像素坐标（位置持久化采集用）；句柄未就绪返回 null。
    /// 值取自 Win32 GetWindowRect 的窗线程缓存，与点歌/排队（WinForms Location）同一
    /// 坐标系，故 DPI 缩放（125%/150%）下复位不偏移，且任意线程可安全读取。
    /// </summary>
    public (int Right, int Bottom)? AnchorBottomRight =>
        _anchorReady ? (_anchorRight, _anchorBottom) : null;

    /// <summary>刷新右下角像素缓存（须在窗线程调用）。</summary>
    private void TrackAnchor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!WpfWin32.TryGetWindowRect(hwnd, out var r))
        {
            return;
        }

        _anchorRight = r.Right;
        _anchorBottom = r.Bottom;
        _anchorReady = true;
    }

    /// <param name="anchorBottomRight">
    /// 窗口<b>右下角</b>屏幕像素坐标（2026-09 位置持久化）：句柄就绪后按像素精确定位，
    /// 高度伸缩时保持右下角不动（行从底部堆叠，底部锚定才不跳动）。
    /// 单位统一用屏幕像素而非 WPF 的 Left/Top（DIP）——与点歌/排队的 WinForms Location
    /// 同一坐标系，避免 DPI 缩放（125%/150%）下复位位置整体偏移。
    /// </param>
    public WpfDanmakuWindow(int width, int height, bool topmost, bool clickThrough,
        (int Right, int Bottom)? anchorBottomRight = null)
    {
        _clickThrough = clickThrough;
        Width = width;
        Height = height;
        Topmost = topmost;
        _pendingAnchor = anchorBottomRight;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;

        // 弹幕行从底部堆叠（新行把旧行向上顶，弹幕姬 MainOverlay 同款布局）
        _root = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 8, 10),
        };
        Content = _root;

        // 单帧循环（PCL 动画引擎核心）：有活跃行时运行，空转时停止省电。
        _ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = FrameInterval,
        };
        _ticker.Tick += OnTick;

        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) => Topmost = true; // 失焦强置顶（弹幕姬 overlay_Deactivated 同款）
        Closed += (_, _) =>
        {
            _ticker.Stop();
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        };
        MouseLeftButtonDown += OnMouseLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftUp;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WpfWin32.Configure(hwnd, _clickThrough);
        // 句柄就绪后按右下角锚点精确定位（像素坐标系；WPF 的 Left/Top 是 DIP，
        // 直接赋值会在 DPI 缩放下偏移，故统一走 Win32 SetWindowPos）
        ApplyAnchor();
    }

    /// <summary>按 <see cref="_pendingAnchor"/>（右下角屏幕像素）定位窗口左上角。</summary>
    private void ApplyAnchor()
    {
        if (_pendingAnchor is not { } anchor)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (!WpfWin32.TryGetWindowRect(hwnd, out var r))
        {
            return;
        }

        WpfWin32.MoveToPixels(hwnd, anchor.Right - (r.Right - r.Left), anchor.Bottom - (r.Bottom - r.Top));
        TrackAnchor();
    }

    /// <summary>
    /// 高度随活跃弹幕行数伸缩（2026-09 用户拍板：弹幕窗用固定默认宽 + 高度随行数）：
    /// 行从底部堆叠，故伸缩时保持<b>右下角不动</b>（向上生长），否则每来一条弹幕
    /// 整个窗口会向下跳动。宽度固定 DefaultWidth，不参与伸缩。
    /// </summary>
    private void UpdateAutoHeight()
    {
        if (_pendingAnchor is null)
        {
            return; // 无锚点（不应发生：runtime 总是提供位置）→ 保持固定高度
        }

        var sum = _root.Margin.Bottom; // 底部留白
        foreach (var line in _lines)
        {
            sum += line.Border.Height + line.Border.Margin.Top; // 实际（含拉伸动画中的）行高
        }

        var target = Math.Clamp(sum + 6, MinAutoHeight, MaxAutoHeight);
        if (Math.Abs(target - Height) < 1)
        {
            return;
        }

        Height = target;
        // 高度生效后再按像素回正右下角（布局异步，排到 Loaded 优先级执行）
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ApplyAnchor);
    }

    // ---- 数据入口（任意线程；feed 后台线程触发，marshal 到 WPF UI 线程）----

    /// <summary>推送直播事件（任意线程可调；弹幕/礼物/进场等按类型显示）。</summary>
    public void Push(LiveEvent evt)
    {
        var line = DanmakuLineBuilder.Build(evt);
        if (line is null)
        {
            return;
        }

        try
        {
            if (Dispatcher.CheckAccess())
            {
                AddLine(line.Value);
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // 此委托在 UI 线程稍后执行，其异常回不到外层 try/catch：必须就地吞掉，
                    // 否则成为 Dispatcher 未处理异常 → Dispatcher.Run 抛出 → 整个弹幕窗
                    // 线程退出（2026-09-03 日志实锤"启动后数秒弹幕窗消失"的放大因素）。
                    try
                    {
                        AddLine(line.Value);
                    }
                    catch
                    {
                        // 单帧渲染异常：丢弃该帧，窗口继续
                    }
                });
            }
        }
        catch
        {
            // 窗口已关闭/dispatcher 停机竞态：丢弃该帧
        }
    }

    // ---- 单行动画状态机（PCL AniData 等价：时间线起点 + 四段时长，由帧循环插值）----

    private sealed class ActiveLine
    {
        public required Border Border;
        public required TextBlock Text;
        public double TargetHeight; // 展开完成后的行高
        public long StartTick;      // 行开始时间（TickCount64，帧循环据此推进）
        public double ExpandMs;     // 高度拉伸段
        public double TextInMs;     // 文字淡入段
        public double HoldMs;       // 停留段
        public double FadeMs;       // 整体淡出段
    }

    // ---- 弹幕行构建 + 帧推进（参考 PCL ModAnimation：一切动画由单时钟逐帧插值）----

    private void AddLine(DanmakuLineModel line)
    {
        var style = CurrentStyle;
        var maxLines = Math.Clamp(style.MaxLines, 5, 200);
        if (_root.Children.Count >= maxLines)
        {
            // 超限：移除最旧行（先停帧推进——从 _lines 摘除即可）
            RemoveLineAt(0);
        }

        // 动画时间线（bililive_dm Store.MainOverlayEffect1-4，设置页可调）：
        //   0 → expand 高度拉伸；expand → expand+textIn 文字淡入；
        //   +hold 停留；最后 fade 内淡出
        var expand = Math.Clamp(style.EffectExpand, 0.05, 5.0);
        var textInAt = expand + Math.Clamp(style.EffectTextIn, 0.05, 5.0);
        var holdAt = textInAt + Math.Clamp(style.EffectHold, 0.0, 60.0);
        var fadeAt = holdAt + Math.Clamp(style.EffectFade, 0.1, 10.0);

        var fontSize = BaseFontSize * Math.Clamp(style.FontScale, 0.6, 5.0);
        var bgAlpha = (byte)Math.Round(Math.Clamp(style.BackgroundOpacity, 0.0, 0.95) * 255);
        var fontFamily = new FontFamily(
            string.IsNullOrWhiteSpace(style.FontFamily) ? DefaultFontFamily : style.FontFamily);
        var accent = TryParseColor(style.AccentColor) ?? Color.FromRgb(0x7F, 0xD0, 0xFF);
        var textBrush = new SolidColorBrush(TryParseColor(style.TextColor) ?? Colors.White);

        var text = RentText(fontFamily, fontSize);
        text.TextWrapping = TextWrapping.Wrap;
        text.Foreground = textBrush;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Inlines.Add(new Run(line.Who + "：")
        {
            FontWeight = FontWeights.Bold,
            Foreground = ResolveWhoColor(line),
        });
        text.Inlines.Add(new Run(line.Text) { Foreground = textBrush });
        text.Effect = BuildShadow(style);

        // 行容器：可选 Lv 徽章（抖音粉丝团等级）+ 文本，水平排列
        var linePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 7, 12, 7),
        };
        var badgeW = 0.0;
        if (line.FanLevel is { } lv && lv > 0)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(52, accent.R, accent.G, accent.B)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    FontFamily = fontFamily,
                    FontSize = fontSize * 0.72,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(accent),
                    Text = $"Lv.{lv}",
                },
            };
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            badgeW = badge.DesiredSize.Width + 6;
            linePanel.Children.Add(badge);
        }

        linePanel.Children.Add(text);

        // 测量行高（行宽受限 → 文本换行后高度自适应）
        var availWidth = Math.Max(20, Width - 16 - 24);
        text.MaxWidth = Math.Max(20, availWidth - badgeW);
        linePanel.Measure(new Size(availWidth, double.PositiveInfinity));
        var lineHeight = linePanel.DesiredSize.Height + 2;

        var border = RentBorder(bgAlpha);
        border.Child = linePanel;
        border.Height = 0; // 从 0 展开
        border.Opacity = 1;
        text.Opacity = 0; // 展开期间文字保持不可见，展开后淡入
        _root.Children.Add(border);

        _lines.Add(new ActiveLine
        {
            Border = border,
            Text = text,
            TargetHeight = lineHeight,
            StartTick = Environment.TickCount64,
            ExpandMs = expand * 1000,
            TextInMs = (textInAt - expand) * 1000,
            HoldMs = (holdAt - textInAt) * 1000,
            FadeMs = (fadeAt - holdAt) * 1000,
        });

        if (!_ticker.IsEnabled)
        {
            _ticker.Start();
        }

        UpdateAutoHeight(); // 新增行 → 高度伸缩（右下角锚定向上生长）
    }

    /// <summary>帧循环：推进所有活跃行到当前时刻（PCL ModAnimation 单时钟逐帧插值）。</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        if (_lines.Count == 0)
        {
            _ticker.Stop();
            return;
        }

        var now = Environment.TickCount64;
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var line = _lines[i];
            var elapsed = now - line.StartTick; // ms

            // 1) 高度拉伸 0 → 目标高（线性；文字保持 0）
            if (elapsed < line.ExpandMs)
            {
                line.Border.Height = line.TargetHeight * elapsed / line.ExpandMs;
                line.Text.Opacity = 0;
                continue;
            }

            line.Border.Height = line.TargetHeight;
            var textElapsed = elapsed - line.ExpandMs;

            // 2) 文字淡入 0 → 1
            if (textElapsed < line.TextInMs)
            {
                line.Text.Opacity = textElapsed / line.TextInMs;
                continue;
            }

            line.Text.Opacity = 1;
            var fadeElapsed = textElapsed - line.TextInMs;

            // 3) 停留段：原样保持
            if (fadeElapsed < line.HoldMs)
            {
                continue;
            }

            // 4) 整体淡出 1 → 0 后移除回收
            var fadeIn = fadeElapsed - line.HoldMs;
            if (fadeIn < line.FadeMs)
            {
                line.Border.Opacity = 1 - fadeIn / line.FadeMs;
                continue;
            }

            RemoveLineAt(i);
        }

        // 拉伸动画期间行高逐帧变化 → 高度同步（RemoveLineAt 已各自刷新，此处兜住纯拉伸帧）
        UpdateAutoHeight();
    }

    /// <summary>摘除并回收第 index 行（对象池 + 视觉树同步清理）。</summary>
    private void RemoveLineAt(int index)
    {
        var line = _lines[index];
        _lines.RemoveAt(index);
        _root.Children.Remove(line.Border);
        Recycle(line);
        UpdateAutoHeight(); // 行数减少 → 高度收缩（右下角保持不动）
    }

    private Border RentBorder(byte bgAlpha)
    {
        Border border;
        if (_borderPool.Count > 0)
        {
            border = _borderPool.Pop();
        }
        else
        {
            border = new Border();
        }

        border.Background = new SolidColorBrush(Color.FromArgb(bgAlpha, 0x30, 0x30, 0x30));
        border.CornerRadius = new CornerRadius(6);
        border.Margin = new Thickness(0, 4, 0, 0);
        return border;
    }

    private TextBlock RentText(FontFamily fontFamily, double fontSize)
    {
        TextBlock text;
        if (_textPool.Count > 0)
        {
            text = _textPool.Pop();
            // 双保险：若 text 仍有残留父容器（Recycle 以外的遗漏路径），先摘除再复用
            if (text.Parent is Panel stalePanel)
            {
                stalePanel.Children.Remove(text);
            }
        }
        else
        {
            text = new TextBlock();
        }

        text.FontFamily = fontFamily;
        text.FontSize = fontSize;
        return text;
    }

    private void Recycle(ActiveLine line)
    {
        // 重置行容器（高度/透明度/内容摘除），留池复用。
        // 注意必须先摘空 linePanel 的 Children 再丢弃它：text 仍挂在 linePanel 上，
        // 若直接 Border.Child = null，text 的 Parent 会残留旧 linePanel——重新挂到新行时抛
        // "指定的元素已经是另一个元素的逻辑子元素"，且该异常发生在 Dispatcher 委托内
        // （外层 catch 抓不到），会杀穿 Dispatcher.Run 使整个弹幕窗线程退出（2026-09-03
        // 用户日志实锤：启动后数秒弹幕窗消失 + "启动失败"误导日志）。
        if (line.Border.Child is Panel panel)
        {
            panel.Children.Clear(); // text 与 Lv 徽章一并摘除 → text.Parent 清空，可安全复用
        }

        line.Border.Child = null;
        line.Border.Height = 0;
        line.Border.Opacity = 1;
        _borderPool.Push(line.Border);

        // 重置文本（清 Inlines/透明度），留池复用；Effect 由下次 Rent 重设
        line.Text.Inlines.Clear();
        line.Text.Opacity = 1;
        line.Text.Effect = null;
        _textPool.Push(line.Text);
    }

    /// <summary>用户名颜色：warn 红（bililive_dm warn=true 红名）＞ 管理员青 ＞ 主播橙 ＞ 默认黄。</summary>
    private static Brush ResolveWhoColor(DanmakuLineModel line)
    {
        if (line.Warn)
        {
            return Brushes.Red;
        }

        if (line.IsAdmin)
        {
            return new SolidColorBrush(Color.FromRgb(0x6A, 0xDC, 0xFF));
        }

        if (line.IsAnchor)
        {
            return new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x6B));
        }

        return Brushes.Yellow;
    }

    /// <summary>文字阴影（样式参数映射：发光→模糊半径，描边→阴影深度，强调色→阴影色）。</summary>
    private static DropShadowEffect BuildShadow(OverlayWindowStyleConfig style)
    {
        var glow = Math.Clamp(style.TextGlow, 0.0, 1.0);
        var outline = Math.Clamp(style.TextOutline, 0.0, 1.0);
        return new DropShadowEffect
        {
            Color = TryParseColor(style.AccentColor) ?? Colors.Black,
            BlurRadius = 3 + glow * 18,
            ShadowDepth = 1 + outline * 2,
            Opacity = 0.85,
            Direction = 270,
        };
    }

    private static Color? TryParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var raw = hex.Trim().TrimStart('#');
        if (raw.Length != 6 || !int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return null;
        }

        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // ---- 样式 / 置顶 / 穿透（runtime 经 Dispatcher marshal 调用）----

    public void ApplyStyle(OverlayWindowStyleConfig style)
    {
        lock (_styleGate)
        {
            _style = style;
        }

        Opacity = Math.Clamp(style.Opacity, 0.5, 1.0);
    }

    private OverlayWindowStyleConfig CurrentStyle
    {
        get
        {
            lock (_styleGate)
            {
                return _style;
            }
        }
    }

    public void ApplyTopmost(bool flag) => Topmost = flag;

    public void ApplyClickThrough(bool flag)
    {
        _clickThrough = flag;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return; // 句柄未就绪（SourceInitialized 前）：初始值已由构造参数传递
        }

        WpfWin32.ApplyClickThrough(hwnd, flag);
    }

    // ---- 拖动（仅非穿透态；穿透时 WS_EX_TRANSPARENT 收不到鼠标，无需处理）----

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragWinLeft = Left;
        _dragWinTop = Top;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var p = e.GetPosition(this);
        Left = _dragWinLeft + p.X - _dragStart.X;
        Top = _dragWinTop + p.Y - _dragStart.Y;
        TrackAnchor(); // 拖动中持续刷新右下角缓存（关闭时采集的就是最后位置）
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        // 拖动结束：把目标锚点更新为当前位置，否则下一次高度伸缩会按旧锚点把窗口拉回去
        if (AnchorBottomRight is { } moved)
        {
            _pendingAnchor = moved;
        }
    }
}