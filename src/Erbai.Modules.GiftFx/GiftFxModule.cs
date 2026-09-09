using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Plugins;
using Erbai.Core.Hosting;

namespace Erbai.Modules.GiftFx;

/// <summary>
/// 礼物特效模块（阶段 5，docs/01 §3.8 框架通道）：GiftEvent → 串行化播放队列
/// （同一时间只播一个）→ OverlayHub WS `giftfx.play {template, nickname, gift,
/// count, durationSeconds}` → overlay 渲染层（演示横幅 CSS 动画 + 资产扩展点）。
/// 设置：开关（Enabled 热生效）/ 时长（随载荷下发）。
/// 串行队列有界 16 丢最旧（礼物狂刷时保最新，特效是瞬时的）。
/// </summary>
public sealed class GiftFxModule : IFeatureModule
{
    private readonly IEventBus _bus;
    private readonly ILogBus _logs;
    private readonly Func<Erbai.Contracts.Configuration.AppConfig> _config;
    private int _publishedCount;

    /// <summary>已成功推送到 overlay 的礼物数（诊断/测试可见性）。</summary>
    public int PublishedCount => Volatile.Read(ref _publishedCount);

    private IOverlayHub? _overlay;
    private CancellationTokenSource? _cts;
    private System.Threading.Channels.Channel<LiveEvent>? _pending;
    private Subscription<LiveEvent>? _subscription;
    private Task? _loopTask;
    private Task? _playTask;
    private bool _started;

    public GiftFxModule(IEventBus bus, ILogBus logs, Func<Erbai.Contracts.Configuration.AppConfig> config)
    {
        _bus = bus;
        _logs = logs;
        _config = config;
    }

    public string Key => "giftfx";

    public string DisplayName => "礼物特效";

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _overlay = context.Overlay;
        _cts = new CancellationTokenSource();
        // 串行播放队列：有界 16，满丢最旧（保最新礼物，慢消费者不阻塞发布方）
        _pending = System.Threading.Channels.Channel.CreateBounded<LiveEvent>(
            new System.Threading.Channels.BoundedChannelOptions(16)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        _subscription = context.EventBus.Subscribe<LiveEvent>(capacity: 128);
        _loopTask = ModuleLoops.RunConsumerLoopAsync(
            _subscription,
            OnLiveEventAsync,
            context.Logs,
            "giftfx.collect",
            _cts.Token);
        _playTask = Task.Run(() => PlayLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_playTask is not null)
        {
            try
            {
                await _playTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _subscription?.Dispose();
        _subscription = null;
        _loopTask = null;
        _playTask = null;
        _pending = null;
        _overlay = null;
        _cts?.Dispose();
        _cts = null;
    }

    private Task OnLiveEventAsync(LiveEvent evt, CancellationToken ct)
    {
        if (evt.Kind == LiveEventKind.Gift)
        {
            _pending?.Writer.TryWrite(evt);
        }

        return Task.CompletedTask;
    }
    /// <summary>串行播放循环：同一时间只播一个（逐条处理，前一条完成才取下一条）。</summary>
    private async Task PlayLoopAsync(CancellationToken ct)
    {
        var pending = _pending;
        if (pending is null)
        {
            return;
        }

        await foreach (var gift in pending.Reader.ReadAllAsync(ct))
        {
            // 整段循环体异常隔离（审计 T2-4）：任何非取消异常不得终止播放循环——
            // 与公理「模块后台循环异常不得逃逸」一致，否则 _config()/推送故障后
            // 礼物特效永久停摆且无重启路径。
            try
            {
                var settings = _config();
                if (!settings.GiftFx.Enabled)
                {
                    continue; // 开关关闭：丢弃（热生效）
                }

                var overlay = _overlay;
                if (overlay is null)
                {
                    continue; // overlay 未启动：无通道可推（不阻塞）
                }

                var payload = new
                {
                    template = "banner", // 演示模板；素材体系（Lottie/视频）后续版本
                    nickname = gift.Nickname,
                    gift = gift.GiftName ?? "",
                    count = gift.GiftCount,
                    durationSeconds = settings.GiftFx.DurationSeconds,
                };
                await overlay.PublishAsync("giftfx.play", payload, ct);
                Interlocked.Increment(ref _publishedCount);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logs.Log(LogLevel.Warning, $"[礼物特效] 播放循环异常（继续）: {ex.Message}");
            }
        }
    }
}
