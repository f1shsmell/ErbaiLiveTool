using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Plugins;
using Erbai.Core.Hosting;
using Erbai.Live.Douyin.Messages;

namespace Erbai.Live.Douyin;

/// <summary>
/// 抖音弹幕插件（阶段 4，docs/01 §3.3 + 04 §3）：连 Grabber 的本地 WS（默认 ws://127.0.0.1:8888），
/// <c>{Type, Data}</c> 报文经 <see cref="DouyinMessageParser"/> 归一为 <see cref="LiveEvent"/>。
/// - WS 连接工厂可注入（对齐 BilibiliLivePlugin 的 wsUriFactory 先例；测试用假 WS 服务器）；
/// - 断线固定延迟重连（默认 5s，旧 run_douyin_client 语义）；
/// - <b>Grabber 死亡自愈（2026-10 长稳轮）</b>：连续 <see cref="ReviveMinFailures"/> 次连接失败
///   （连不上本地 WS 端口）≈ 抓包器进程已死，经 <see cref="Reviver"/> 回调让宿主重拉
///   Grabber（组合根接 DouyinGrabberHost.StartAsync：端口活着即幂等 adopt，死了即重新拉起）；
///   两次拉起之间至少间隔 <see cref="ReviveThrottle"/>（防拉起风暴）。
/// - 循环内任何异常不得逃逸（worker 不变量）。
/// </summary>
public sealed class DouyinLivePlugin : LivePluginBase
{
    private static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>触发自愈前需要的连续连接失败次数（每次失败间隔一个重连延迟）。</summary>
    internal const int ReviveMinFailures = 3;

    /// <summary>两次自愈拉起之间的最小间隔（拉起含 30×0.25s 端口探测，防风暴）。</summary>
    internal static readonly TimeSpan ReviveThrottle = TimeSpan.FromSeconds(30);

    private readonly ILogBus? _logs;
    private readonly TimeSpan _reconnectDelay;
    private readonly Func<Uri, ClientWebSocket> _wsFactory;
    private volatile IReadOnlySet<string> _allowedRoomIds;
    // ── Grabber 自愈状态（仅监听循环线程读写，无需加锁） ──
    private Func<CancellationToken, Task>? _reviver;
    private TimeSpan _reviveThrottle = ReviveThrottle;
    private int _consecutiveConnectFailures;
    private DateTimeOffset _lastReviveAt = DateTimeOffset.MinValue;

    public DouyinLivePlugin(
        ILogBus? logs = null,
        TimeSpan? reconnectDelay = null,
        Func<Uri, ClientWebSocket>? wsFactory = null,
        IReadOnlySet<string>? allowedRoomIds = null,
        Func<CancellationToken, Task>? reviver = null,
        TimeSpan? reviveThrottle = null)
    {
        _logs = logs;
        _reconnectDelay = reconnectDelay ?? DefaultReconnectDelay;
        _wsFactory = wsFactory ?? (_ => new ClientWebSocket());
        _allowedRoomIds = allowedRoomIds ?? new HashSet<string>(StringComparer.Ordinal);
        _reviver = reviver;
        if (reviveThrottle is { } throttle)
        {
            _reviveThrottle = throttle;
        }
    }

    /// <summary>
    /// 运行时注入/更新自愈回调（组合根在装配完 GrabberHost 后调用；返回 false 的回调
    /// 会被忽略）。回调异常被循环隔离——自愈失败只是下一次失败窗口再试。
    /// </summary>
    public void SetReviver(Func<CancellationToken, Task>? reviver) => _reviver = reviver;

    /// <summary>运行时更新房间白名单（重启平台前由组合根按最新配置调用；原子替换引用）。</summary>
    public void UpdateRoomFilter(IEnumerable<string> roomIds)
    {
        _allowedRoomIds = roomIds.ToHashSet(StringComparer.Ordinal);
    }

    public override string Key => "douyin";

    public override string DisplayName => "抖音弹幕";

    public override LiveCapabilities Capabilities =>
        LiveCapabilities.Danmaku | LiveCapabilities.Gift | LiveCapabilities.Enter |
        LiveCapabilities.Follow | LiveCapabilities.Share | LiveCapabilities.Like |
        LiveCapabilities.LiveState;

    public override Task StartAsync(AppConfig config, CancellationToken ct)
    {
        var wsUrl = string.IsNullOrWhiteSpace(config.DouyinWsUrl)
            ? new Uri("ws://127.0.0.1:8888")
            : new Uri(config.DouyinWsUrl);
        StartLoop(token => RunLoopAsync(wsUrl, token));
        return Task.CompletedTask;
    }

    // ── 主循环：连接 → 收报文解析 → 断开重连 ────────────────────────────────

    private async Task RunLoopAsync(Uri wsUrl, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(wsUrl, ct);
                _consecutiveConnectFailures = 0; // 连接过（哪怕已断开）：抓包器活着
                _logs?.Warning($"[抖音] 连接断开，{_reconnectDelay.TotalSeconds:0.#}s 后重连");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logs?.Warning($"[抖音] 连接失败（{ex.Message}），{_reconnectDelay.TotalSeconds:0.#}s 后重连");
                await MaybeReviveGrabberAsync(ct);
            }
            catch (IOException ex)
            {
                _logs?.Warning($"[抖音] 连接中断（{ex.Message}），{_reconnectDelay.TotalSeconds:0.#}s 后重连");
            }
            catch (Exception ex)
            {
                _logs?.Error($"[抖音] 监听循环异常（已隔离，重连）：{ex.Message}", ex.ToString());
            }

            await DelayOrCancel(_reconnectDelay, ct);
        }
    }

    /// <summary>
    /// 连接失败时的 Grabber 自愈：连续失败达阈值且距上次拉起超过节流窗，回调宿主重拉。
    /// 回调异常/未注入都不影响重连循环（下一次失败窗口再试）。
    /// </summary>
    private async Task MaybeReviveGrabberAsync(CancellationToken ct)
    {
        var reviver = _reviver;
        if (reviver is null)
        {
            return;
        }

        _consecutiveConnectFailures++;
        if (_consecutiveConnectFailures < ReviveMinFailures)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastReviveAt < _reviveThrottle)
        {
            return;
        }

        _lastReviveAt = DateTimeOffset.UtcNow;
        try
        {
            _logs?.Warning($"[抖音] 本地弹幕端口连续 {_consecutiveConnectFailures} 次连不上，尝试重新拉起抓包器");
            await reviver(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logs?.Warning($"[抖音] 拉起抓包器失败（稍后再试）：{ex.Message}");
        }
        finally
        {
            // 计数复位：失败-拉起-等下一窗口，而不是每次失败都触发判定
            _consecutiveConnectFailures = 0;
        }
    }

    private async Task ConnectAndListenAsync(Uri wsUrl, CancellationToken ct)
    {
        using var ws = _wsFactory(wsUrl);
        await ws.ConnectAsync(wsUrl, ct);
        _logs?.Information($"[抖音] 已连接 {wsUrl}，等待直播弹幕");

        var buffer = new byte[1 << 16];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(ms.ToArray());
            var evt = DouyinMessageParser.Parse(
                text,
                _allowedRoomIds,
                error => _logs?.Warning($"[抖音] {error}"));
            if (evt is not null)
            {
                EventWriter.TryWrite(evt);
            }
        }
    }

}
