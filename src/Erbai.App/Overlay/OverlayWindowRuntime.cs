using System.Drawing;
using System.Windows.Forms;
using Erbai.Contracts.Queue;
using Erbai.Contracts.QueueUp;
using Erbai.OverlayWpf;

namespace Erbai.App.Overlay;

/// <summary>
/// 点歌/排队悬浮窗运行时（决策 #17 预案 B，2026-08-29）：独立 STA 线程 + WinForms 消息循环
/// 跑 <see cref="LayeredOverlayWindow"/>（UpdateLayeredWindow 位图透明，替代 Spike A 的
/// WebView2 合成模式——本机 DWM 对「分层窗口+DComp」的 alpha 混合把底层当白，Spike D 实锤）。
/// 数据直连 OverlayServer：点歌/排队 HTTP 轮询；渲染 GDI+ 自绘。
/// 弹幕悬浮窗已于 2026-09 迁移到 <see cref="WpfDanmakuOverlayRuntime"/>（bililive_dm 同款 WPF 方案）。
/// Form 必须在窗线程内创建（线程归属 = 创建线程）；控制操作（置顶/穿透/关闭）经
/// BeginInvoke marshal 到窗线程，线程安全。
/// </summary>
public sealed class OverlayWindowRuntime : IOverlayWindowHandle
{
    private readonly LayeredOverlayWindow _form;
    private readonly OverlayFeed _feed;
    private readonly OverlayRenderer _renderer;
    private readonly Thread _thread;
    private int _closed;

    /// <param name="anchorBottomRight">
    /// 窗口<b>右下角</b>屏幕坐标（2026-09 位置持久化）；null = 首次启动按默认位置摆放。
    /// 存右下角而非左上角：宽高全自适应时窗口向左上生长，右下角锚定才不会随内容增减漂移。
    /// </param>
    public OverlayWindowRuntime(
        OverlayWindowKind kind,
        string baseUrl,
        bool topmost,
        bool clickThrough,
        (int Right, int Bottom)? anchorBottomRight = null,
        Action<string>? onInitFailed = null,
        Action<string>? onStage = null)
    {
        // 数据源与渲染器线程无关，先建（feed 后台轮询/收流）
        (_feed, _renderer) = CreateFeedAndRenderer(kind, baseUrl);

        // 宽高全自适应（2026-09）：尺寸由窗线程每帧按内容实测，故初始尺寸只是首帧前的占位，
        // 首帧 RenderTick 即调整到位。自适应测量必须在窗线程（渲染器与 Paint 共字体，
        // 跨线程测量会与 EnsureFonts 并发 Dispose/重建字体）。
        const int initialWidth = 267;
        const int initialHeight = 80;
        var location = anchorBottomRight is { } anchor
            ? new Point(anchor.Right - initialWidth, anchor.Bottom - initialHeight)
            : (Point?)null;

        var created = new TaskCompletionSource<LayeredOverlayWindow>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            LayeredOverlayWindow form;
            try
            {
                var renderer = _renderer;
                form = new LayeredOverlayWindow(
                    initialWidth, initialHeight, topmost, clickThrough, location,
                    paint: (g, w, h) => renderer.Paint(g, w, h),
                    intervalMs: 500,
                    // 宽度按内容实测（与当前宽度无关），高度按该宽度下的行数实测
                    autoSizeProvider: (g, _) =>
                    {
                        var width = renderer.ComputeContentWidth(g);
                        return new Size(width, renderer.ComputeContentHeight(width));
                    });
            }
            catch (Exception ex)
            {
                created.TrySetException(ex);
                return;
            }

            created.TrySetResult(form);
            Application.Run(form);
        })
        {
            Name = "OverlayWindow",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        try
        {
            _form = created.Task.Result; // 阻塞最多 5s（窗线程构造，正常毫秒级）
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            _feed.Dispose();
            throw new InvalidOperationException($"悬浮窗构造失败：{ex.InnerException.Message}", ex.InnerException);
        }

        // 构造期就订阅，避免 OnLoad 早期失败时事件已触发但无人接收（静默消失）
        if (onInitFailed is not null)
        {
            _form.InitFailed += onInitFailed;
        }

        if (onStage is not null)
        {
            _form.PaintFailed += onStage;
        }

        // 等窗线程 OnHandleCreated 置位（句柄归属窗线程；非窗线程直接读 Handle 会跨线程
        // CreateHandle 导致句柄归属错误）；带超时防止静默失败。
        if (!_form.HandleReady.Wait(TimeSpan.FromSeconds(5)))
        {
            _feed.Dispose();
            throw new TimeoutException("悬浮窗口句柄未在 5s 内就绪");
        }
    }

    private static (OverlayFeed Feed, OverlayRenderer Renderer) CreateFeedAndRenderer(
        OverlayWindowKind kind, string baseUrl)
    {
        switch (kind)
        {
            case OverlayWindowKind.SongQueue:
            {
                var feed = new PollingOverlayFeed(baseUrl, pollQueue: true, pollQueueUp: false);
                var renderer = new QueueOverlayRenderer();
                feed.QueueUpdated += renderer.Update;
                return (feed, renderer);
            }

            case OverlayWindowKind.QueueUp:
            {
                var feed = new PollingOverlayFeed(baseUrl, pollQueue: false, pollQueueUp: true);
                var renderer = new QueueUpOverlayRenderer();
                feed.QueueUpUpdated += renderer.Update;
                return (feed, renderer);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind,
                    "弹幕悬浮窗已迁移到 WPF 方案（Erbai.OverlayWpf.WpfDanmakuOverlayRuntime）");
        }
    }

    /// <summary>应用样式参数（强调色/字号/描边/发光/不透明度；渲染器内部线程安全，
    /// 任意线程可调，设置页热切换实时生效——2026-08-29 样式参数化）。
    /// 字号/字体变化会改变内容宽高，由窗线程下一帧自适应测量接管（无需在此显式 resize：
    /// 跨线程读 Form.Size/调 ResizeTo 会触发非法跨线程访问，2026-09 改为窗线程内每帧测量）。</summary>
    public void ApplyStyle(Erbai.Contracts.Configuration.OverlayWindowStyleConfig style) =>
        _renderer.ApplyStyle(style);

    /// <summary>
    /// 尺寸热切换（2026-09 改语义为 no-op）：宽高已全自适应——窗线程每帧按内容实测尺寸，
    /// 配置里的 Width/Height/AutoHeight 均已弃用（见 OverlayWindowItemConfig）。
    /// 保留空实现以满足 <see cref="IOverlayWindowHandle"/> 契约与既有调用点。
    /// </summary>
    public void ApplySize(int width, int height, bool autoHeight)
    {
    }

    /// <summary>当前窗口右下角屏幕坐标（关闭时采集持久化）；窗口未就绪返回 null。
    /// 值由窗线程维护成 volatile 缓存字段，任意线程可安全读取。</summary>
    public (int Right, int Bottom)? AnchorBottomRight =>
        _closed != 0 || !_form.IsHandleCreated || _form.IsDisposed
            ? null
            : _form.AnchorBottomRight;

    public void SetTopmost(bool flag) => InvokeForm(() => _form.ApplyTopmost(flag));

    public void SetClickThrough(bool flag) => InvokeForm(() => _form.ApplyClickThrough(flag));

    private void InvokeForm(Action action)
    {
        if (_closed != 0 || !_form.IsHandleCreated || _form.IsDisposed)
        {
            return;
        }

        try
        {
            _form.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // 窗口已销毁竞态：忽略（关闭路径会兜底）
        }
    }

    /// <summary>关闭窗口并结束其线程（幂等）。
    /// 注意不能走 InvokeForm：Close 已置 _closed 标志，InvokeForm 首行 `_closed != 0` 会把
    /// 关闭动作自己短路（关不掉 bug，2026-08-28 修复）。</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        if (!_form.IsHandleCreated || _form.IsDisposed)
        {
            _feed.Dispose();
            return;
        }

        try
        {
            _form.BeginInvoke(() =>
            {
                try
                {
                    _form.Close();
                }
                catch
                {
                    // 关闭竞态：忽略（窗口最终会随线程/进程清理）
                }
                finally
                {
                    _feed.Dispose();
                }
            });
        }
        catch (InvalidOperationException)
        {
            // 窗口已销毁竞态：忽略
            _feed.Dispose();
        }
    }

    public void Dispose() => Close();
}
