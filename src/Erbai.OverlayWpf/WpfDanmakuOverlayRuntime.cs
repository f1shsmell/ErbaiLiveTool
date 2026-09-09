using System.Windows.Threading;
using Erbai.Contracts.Configuration;

namespace Erbai.OverlayWpf;

/// <summary>
/// 弹幕悬浮窗运行时（bililive_dm 同款方案，2026-09）：独立 STA 线程 + WPF Dispatcher 跑
/// <see cref="WpfDanmakuWindow"/>（AllowsTransparency 透明窗 + Storyboard 弹幕行动画）；
/// 数据源 = OverlayServer WS 的 live.* 增量事件（<see cref="DanmakuOverlayFeed"/>，弹幕实时直发）。
/// 置顶/穿透/样式热切换 marshal 到窗线程，线程安全；幂等关闭。
/// </summary>
public sealed class WpfDanmakuOverlayRuntime : IOverlayWindowHandle
{
    /// <summary>
    /// 弹幕窗内置默认宽高（2026-09 用户拍板：弹幕窗不做宽高自适应）。
    /// 弹幕行靠 TextWrapping 按窗口宽度换行，"宽度随内容自适应"会与"内容靠宽度换行"
    /// 形成循环依赖（长弹幕把窗口撑到半屏遮挡画面），故用固定默认宽；
    /// 高度按活跃行数在窗内伸缩（行从底部堆叠，顶部留白由 AllowsTransparency 透出）。
    /// </summary>
    public const int DefaultWidth = 400;

    public const int DefaultHeight = 260;

    private readonly DanmakuOverlayFeed _feed;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private WpfDanmakuWindow? _window;
    private int _closed;

    /// <param name="baseUrl">OverlayServer 基址（http://127.0.0.1:{port}）。2026-09 起无 token。</param>
    /// <param name="anchorBottomRight">
    /// 窗口<b>右下角</b>屏幕坐标（2026-09 位置持久化）：由治理器提供——有持久化值用持久化值，
    /// 否则用按类型错开的默认位置。存右下角与点歌/排队一致：关闭时采集、下次启动复位到同一处。
    /// </param>
    /// <param name="onInitFailed">窗口线程启动失败回调（记日志，不拖垮主程序）。</param>
    public WpfDanmakuOverlayRuntime(
        string baseUrl,
        bool topmost,
        bool clickThrough,
        (int Right, int Bottom) anchorBottomRight,
        Action<string>? onInitFailed = null)
    {
        _feed = new DanmakuOverlayFeed(baseUrl);

        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            try
            {
                // 宽高取内置默认；位置由右下角锚点（屏幕像素）在句柄就绪后 Win32 定位——
                // 不在这里设 Left/Top：那是 DIP，赋屏幕像素值会在 DPI 缩放下偏移
                var win = new WpfDanmakuWindow(
                    DefaultWidth, DefaultHeight, topmost, clickThrough, anchorBottomRight);
                lock (_gate)
                {
                    _window = win;
                }

                _feed.LiveEventReceived += win.Push; // 事件任意线程触发，Push 内 marshal
                win.Show();
                ready.Set();
                Dispatcher.Run(); // 窗口关闭（Closed）时 InvokeShutdown 退出
            }
            catch (Exception ex)
            {
                ready.Set();
                _feed.Dispose(); // 启动失败：停掉 WS 重连循环，避免泄漏后台线程
                onInitFailed?.Invoke($"弹幕悬浮窗（WPF）启动失败：{ex.Message}");
            }
        })
        {
            Name = "WpfDanmakuOverlay",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _feed.Dispose();
            throw new TimeoutException("弹幕悬浮窗（WPF）线程未在 5s 内就绪");
        }
    }

    public void SetTopmost(bool flag) => Invoke(win => win.ApplyTopmost(flag));

    public void SetClickThrough(bool flag) => Invoke(win => win.ApplyClickThrough(flag));

    public void ApplyStyle(OverlayWindowStyleConfig style) => Invoke(win => win.ApplyStyle(style));

    /// <summary>
    /// 尺寸热切换（2026-09 改语义为 no-op）：弹幕窗宽高不取配置——宽度固定 DefaultWidth
    /// （弹幕行靠 TextWrapping 按窗宽换行，宽度随内容自适应会与换行形成循环依赖），
    /// 高度随活跃行数在窗内伸缩。配置里的 Width/Height 已弃用（见 OverlayWindowItemConfig）。
    /// </summary>
    public void ApplySize(int width, int height, bool autoHeight)
    {
    }

    /// <summary>当前窗口右下角屏幕像素坐标（关闭时采集持久化）；窗口未就绪返回 null。</summary>
    public (int Right, int Bottom)? AnchorBottomRight
    {
        get
        {
            WpfDanmakuWindow? win;
            lock (_gate)
            {
                win = _window;
            }

            return _closed != 0 || win is null ? null : win.AnchorBottomRight;
        }
    }

    private void Invoke(Action<WpfDanmakuWindow> action)
    {
        WpfDanmakuWindow? win;
        lock (_gate)
        {
            win = _window;
        }

        if (_closed != 0 || win is null)
        {
            return;
        }

        try
        {
            if (win.Dispatcher.CheckAccess())
            {
                action(win);
            }
            else
            {
                win.Dispatcher.BeginInvoke(action, win);
            }
        }
        catch
        {
            // 窗口已销毁竞态：忽略（关闭路径兜底）
        }
    }

    /// <summary>关闭窗口并结束其线程（幂等）。
    /// 注意不能走 <see cref="Invoke"/>：Close 已置 _closed 标志，Invoke 首行 `_closed != 0`
    /// 会把关闭动作自己短路（关不掉 bug，与 OverlayWindowRuntime.cs 2026-08-28 修复同类）——
    /// 直接取窗口句柄 BeginInvoke 关闭。</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _feed.Dispose();
        WpfDanmakuWindow? win;
        lock (_gate)
        {
            win = _window;
        }

        if (win is null)
        {
            return;
        }

        try
        {
            if (win.Dispatcher.CheckAccess())
            {
                win.Close();
            }
            else
            {
                win.Dispatcher.BeginInvoke(() => win.Close());
            }
        }
        catch
        {
            // 窗口已销毁竞态：忽略
        }
    }

    public void Dispose() => Close();
}
