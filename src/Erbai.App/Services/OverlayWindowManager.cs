using System.Drawing;
using System.Windows.Forms;
using Erbai.App.Overlay;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Logging;
using Erbai.OverlayWpf;
using Erbai.Web;

namespace Erbai.App.Services;

/// <summary>
/// 悬浮窗治理器（决策 #17，2026-09 修订）：每类悬浮窗一个独立实例，各自开关（Enabled）、
/// 置顶（Topmost）、点击穿透（ClickThrough）——三者均可运行时热切换。
/// 点歌/排队 = WinForms 自绘（<see cref="OverlayWindowRuntime"/>）；弹幕 = WPF
/// bililive_dm 同款（<see cref="WpfDanmakuOverlayRuntime"/>），经 <see cref="IOverlayWindowHandle"/>
/// 统一治理。页面复用 OverlayServer 的独立端点（/overlay/queue、/overlay/queueup、/overlay/danmaku）。
/// overlay 未启动时不创建任何悬浮窗（构造函数容忍 null，空转）。
/// </summary>
public sealed class OverlayWindowManager : IAsyncDisposable
{
    private readonly OverlayServer? _overlay;
    private readonly ILogBus _logs;
    private readonly IConfigStore? _config;
    private readonly Dictionary<OverlayWindowKind, IOverlayWindowHandle> _runtimes = new();

    /// <summary>
    /// 各悬浮窗最近一次已知的右下角屏幕像素坐标（2026-09 位置持久化）：
    /// 窗口关闭前采集入表，退出时统一写回配置。停用/重开单个悬浮窗也不丢位置。
    /// </summary>
    private readonly Dictionary<OverlayWindowKind, (int Right, int Bottom)> _lastAnchors = new();

    private readonly object _gate = new();

    /// <param name="config">
    /// 配置存储（位置持久化回写用）；null = 只复位不写回（测试/无配置场景）。
    /// </param>
    public OverlayWindowManager(OverlayServer? overlay, ILogBus logs, IConfigStore? config = null)
    {
        _overlay = overlay;
        _logs = logs;
        _config = config;
    }

    /// <summary>当前打开的悬浮窗种类（诊断/调试）。</summary>
    public IReadOnlyCollection<OverlayWindowKind> ActiveKinds
    {
        get
        {
            lock (_gate)
            {
                return _runtimes.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// 按配置全量应用（启动装配与设置页热切换共用）：启用 → 创建/保持并同步置顶穿透；
    /// 停用 → 关闭对应实例。任何单个窗口失败只记日志，不拖垮其他窗口与主程序。
    /// </summary>
    public void ApplyConfig(AppConfig cfg)
    {
        if (_overlay is null)
        {
            return; // overlay 未启动，无页面可载
        }

        foreach (var (kind, item) in ItemsOf(cfg))
        {
            UpdateWindow(kind, item);
        }
    }

    private static IEnumerable<(OverlayWindowKind Kind, OverlayWindowItemConfig Item)> ItemsOf(AppConfig cfg)
    {
        yield return (OverlayWindowKind.SongQueue, cfg.OverlayWindows.SongQueue);
        yield return (OverlayWindowKind.QueueUp, cfg.OverlayWindows.QueueUp);
        yield return (OverlayWindowKind.Danmaku, cfg.OverlayWindows.Danmaku);
    }

    private void UpdateWindow(OverlayWindowKind kind, OverlayWindowItemConfig item)
    {
        lock (_gate)
        {
            if (_runtimes.TryGetValue(kind, out var runtime))
            {
                if (!item.Enabled)
                {
                    _runtimes.Remove(kind);
                    CaptureAnchor(kind, runtime); // 停用前记住位置（重新启用时复位到同一处）
                    runtime.Close();
                    return;
                }

                runtime.SetTopmost(item.Topmost);
                runtime.SetClickThrough(item.ClickThrough);
                runtime.ApplyStyle(item.Style); // 样式参数热切换（强调色/字号/描边/发光/不透明度）
                return;
            }

            if (!item.Enabled)
            {
                return;
            }
        }

        // 创建移出锁外：Runtime 构造会阻塞等待窗线程句柄（正常毫秒级，异常可达 5s 超时），
        // 不应持有 _gate（会卡住其他窗口与后续热切换）。
        try
        {
            var anchor = ResolveAnchor(kind, item);
            IOverlayWindowHandle created = kind == OverlayWindowKind.Danmaku
                ? CreateDanmakuWpf(item, anchor)
                : CreateWinFormsRuntime(kind, item, anchor);
            created.ApplyStyle(item.Style); // 初始样式参数

            lock (_gate)
            {
                // 二次检查：等待期间若同 kind 已被创建（并发），关掉本次实例
                if (_runtimes.ContainsKey(kind))
                {
                    created.Close();
                    return;
                }

                _runtimes[kind] = created;
            }

            _logs.Information($"悬浮窗[{kind}] 已启动（弹幕=WPF bililive_dm 同款 / 点歌排队=自绘），数据源=http://127.0.0.1:{_overlay!.BoundPort}");
        }
        catch (Exception ex)
        {
            _logs.Log(LogLevel.Warning, $"悬浮窗[{kind}] 启动失败（继续运行）：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析窗口右下角锚点（2026-09 位置持久化）：优先本次会话已记住的位置（用户刚拖动过/
    /// 刚停用又重开），其次配置里的持久化值（上次退出时写入），都没有则按类型默认摆放。
    /// 宽高已全自适应，故只存右下角——窗口向左上生长，右下角锚定才不会随内容增减漂移。
    /// </summary>
    private (int Right, int Bottom) ResolveAnchor(OverlayWindowKind kind, OverlayWindowItemConfig item)
    {
        lock (_gate)
        {
            if (_lastAnchors.TryGetValue(kind, out var remembered))
            {
                return remembered;
            }
        }

        if (item.AnchorRight is { } right && item.AnchorBottom is { } bottom)
        {
            return (right, bottom);
        }

        return DefaultAnchor(kind);
    }

    /// <summary>采集窗口当前右下角（关窗前调用；窗口未就绪则保留原值）。</summary>
    private void CaptureAnchor(OverlayWindowKind kind, IOverlayWindowHandle runtime)
    {
        if (runtime.AnchorBottomRight is not { } anchor)
        {
            return;
        }

        lock (_gate)
        {
            _lastAnchors[kind] = anchor;
        }
    }

    /// <summary>
    /// 按类型的默认右下角（屏幕右下错开，避免三窗完全重叠）：与原 OffsetLocation 的
    /// 视觉位置一致——原实现左上角 = area.Right - width - 40 - index*36，其右下角即
    /// area.Right - 40 - index*36，与窗口宽度无关，故宽高自适应后默认位置不变。
    /// </summary>
    private static (int Right, int Bottom) DefaultAnchor(OverlayWindowKind kind)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var index = kind switch
        {
            OverlayWindowKind.SongQueue => 0,
            OverlayWindowKind.QueueUp => 1,
            _ => 2,
        };
        return (area.Right - 40 - index * 36, area.Bottom - 80 - index * 24);
    }

    /// <summary>弹幕悬浮窗（bililive_dm 同款方案，2026-09）：WPF 透明窗 + 单帧循环弹幕行动画，
    /// 数据源 WS live.* 增量事件；宽高用内置默认（宽固定、高随活跃行数伸缩）。</summary>
    private IOverlayWindowHandle CreateDanmakuWpf(
        OverlayWindowItemConfig item, (int Right, int Bottom) anchor)
        => new WpfDanmakuOverlayRuntime(
            baseUrl: $"http://127.0.0.1:{_overlay!.BoundPort}",
            topmost: item.Topmost,
            clickThrough: item.ClickThrough,
            anchorBottomRight: anchor,
            onInitFailed: message => _logs.Log(LogLevel.Warning, message));

    /// <summary>点歌/排队悬浮窗（决策 #17 预案 B）：WinForms 分层窗 + GDI+ 自绘，数据源 HTTP 轮询。
    /// 宽高全自适应（窗线程每帧按内容实测，2026-09）。</summary>
    private IOverlayWindowHandle CreateWinFormsRuntime(
        OverlayWindowKind kind, OverlayWindowItemConfig item, (int Right, int Bottom) anchor)
        => new OverlayWindowRuntime(
            kind,
            baseUrl: $"http://127.0.0.1:{_overlay!.BoundPort}",
            topmost: item.Topmost,
            clickThrough: item.ClickThrough,
            anchorBottomRight: anchor,
            onInitFailed: message => _logs.Log(LogLevel.Warning, message),
            onStage: stage => _logs.Information($"悬浮窗[{kind}] {stage}"));

    /// <summary>
    /// 逐个关闭所有悬浮窗（幂等；AppServices.DisposeAsync 在 Overlay.DisposeAsync 前调用）。
    /// 关闭前采集各窗右下角并写回配置——下次启动复位到同一位置（2026-09 用户需求
    /// 「关闭程序时记住各自位置」）。写回失败只记日志，不阻塞退出。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Dictionary<OverlayWindowKind, (int Right, int Bottom)> anchors;
        lock (_gate)
        {
            foreach (var (kind, runtime) in _runtimes)
            {
                CaptureAnchor(kind, runtime); // 必须先采集再 Close（Close 后句柄已销毁）
                runtime.Close();
            }

            _runtimes.Clear();
            anchors = new Dictionary<OverlayWindowKind, (int Right, int Bottom)>(_lastAnchors);
        }

        await PersistAnchorsAsync(anchors);
    }

    /// <summary>
    /// 把各窗右下角写回配置的 AnchorRight/AnchorBottom（只改位置字段，其余原样保留）。
    /// 无变化时跳过写盘，避免每次退出都无谓落盘。
    /// </summary>
    private async Task PersistAnchorsAsync(Dictionary<OverlayWindowKind, (int Right, int Bottom)> anchors)
    {
        if (_config is null || anchors.Count == 0)
        {
            return;
        }

        try
        {
            var current = _config.Settings;
            var windows = current.OverlayWindows;
            var next = windows with
            {
                SongQueue = WithAnchor(windows.SongQueue, anchors, OverlayWindowKind.SongQueue),
                QueueUp = WithAnchor(windows.QueueUp, anchors, OverlayWindowKind.QueueUp),
                Danmaku = WithAnchor(windows.Danmaku, anchors, OverlayWindowKind.Danmaku),
            };

            if (Equals(next, windows))
            {
                return; // 位置没变（未拖动过/未采集到）：不写盘
            }

            await _config.PersistAsync(current with { OverlayWindows = next });
        }
        catch (Exception ex)
        {
            _logs.Log(LogLevel.Warning, $"悬浮窗位置写回配置失败（不影响退出）：{ex.Message}");
        }
    }

    /// <summary>
    /// 写回单个悬浮窗锚点。<b>没有采集到值时原样返回</b>——不能用 GetValueOrDefault：
    /// 缺省元组是 (0,0)，会把窗口位置错误重置到屏幕左上角（该窗本次未启用/未就绪时尤甚）。
    /// </summary>
    private static OverlayWindowItemConfig WithAnchor(
        OverlayWindowItemConfig item,
        Dictionary<OverlayWindowKind, (int Right, int Bottom)> anchors,
        OverlayWindowKind kind) =>
        anchors.TryGetValue(kind, out var anchor)
            ? item with { AnchorRight = anchor.Right, AnchorBottom = anchor.Bottom }
            : item;
}