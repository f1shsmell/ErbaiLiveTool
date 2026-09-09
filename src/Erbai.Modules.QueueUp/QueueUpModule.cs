using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Plugins;
using Erbai.Contracts.QueueUp;
using Erbai.Core.Hosting;

namespace Erbai.Modules.QueueUp;

/// <summary>
/// 排队队列模块（阶段 5，docs/01 §3.7 最小版）：订阅 LiveEvent——
/// Danmaku 解析「排队/取消排队/完成」命令，Gift 走礼物插队规则引擎
/// （insert_at 自动插队 / grant_eligibility 授予资格）。
/// 状态经 EventBus queueup.* 事件 → overlay 排队看板频道 + WinUI 排队队列页。
/// 生命周期自持（StartAsync 创建 cts，DisposeAsync 取消循环），启停互不影响。
/// </summary>
public sealed class QueueUpModule : IFeatureModule, IQueueUpModule
{
    private CancellationTokenSource? _cts;
    private readonly Func<Erbai.Contracts.Configuration.AppConfig> _config;
    private Subscription<LiveEvent>? _subscription;
    private Task? _loopTask;
    private bool _started;

    public QueueUpModule(IStorageEngine storage, IEventBus bus, ILogBus logs, Func<Erbai.Contracts.Configuration.AppConfig> config)
    {
        Storage = storage;
        Bus = bus;
        Logs = logs;
        _config = config;
        Service = new QueueUpService(storage, bus, logs, config);
    }

    public string Key => "queueup";

    public string DisplayName => "排队";

    public IStorageEngine Storage { get; }

    public IEventBus Bus { get; }

    public ILogBus Logs { get; }

    /// <summary>队列核心（UI 页面经此操作：完成队首/取消指定条目/读快照）。</summary>
    public QueueUpService Service { get; }

    /// <summary>宿主协作面（内置插件迁移后主程序经 Contracts 接口访问）。</summary>
    Erbai.Contracts.QueueUp.IQueueUpService IQueueUpModule.Service => Service;

    public async Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        // 先启动核心（存储恢复可能抛，如库不可用）——成功后才置 _started。
        // 若先置位再 await，启动异常后 ModuleHost 标 StartFailed，但重试
        // StartModuleAsync 会撞 _started 早退：状态显示 Started 实际无订阅循环。
        await Service.StartAsync(ct);
        _started = true;
        // 独立于传入 ct 的订阅循环：DisposeAsync 自持取消，宿主 Stop 不依赖 Start 的 token 存活；
        // cts 每次 Start 重建（模块可 Dispose 后重启，docs/00 退出标准「插件启停互不影响」）
        _cts = new CancellationTokenSource();
        _subscription = context.EventBus.Subscribe<LiveEvent>(capacity: 256);
        _loopTask = ModuleLoops.RunConsumerLoopAsync(
            _subscription,
            HandleLiveEventAsync,
            context.Logs,
            "queueup.live",
            _cts.Token);
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

        _subscription?.Dispose();
        _subscription = null;
        _loopTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task HandleLiveEventAsync(LiveEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case LiveEventKind.Danmaku:
                await HandleDanmakuAsync(evt, ct);
                break;
            case LiveEventKind.Gift:
                await HandleGiftAsync(evt, ct);
                break;
        }
    }

    private async Task HandleDanmakuAsync(LiveEvent evt, CancellationToken ct)
    {
        var command = QueueUpCommandParser.Parse(evt.Text ?? "");
        if (command is null)
        {
            return;
        }

        var userId = CompositeUserId(evt);
        switch (command.Kind)
        {
            case QueueUpCommandKind.Enqueue:
            {
                var result = await Service.EnqueueAsync(userId, evt.Nickname, command.Content, ct: ct);
                if (!result.Accepted && result.Reason is not null && result.Reason != "disabled")
                {
                    Logs.Log(LogLevel.Information,
                        $"[排队] {evt.Nickname} 排队被拒（{result.Reason}）");
                }

                break;
            }

            case QueueUpCommandKind.Cancel:
            {
                var result = await Service.CancelByUserAsync(userId, ct);
                if (result.Accepted)
                {
                    Logs.Log(LogLevel.Information, $"[排队] {evt.Nickname} 取消了排队");
                }

                break;
            }

            case QueueUpCommandKind.Complete:
            {
                // 「完成」为管理命令：仅 admin/anchor 可执行；普通观众静默忽略
                if (evt.IsAdmin != true && evt.IsAnchor != true)
                {
                    return;
                }

                var result = await Service.CompleteNextAsync(ct);
                if (result.Accepted)
                {
                    Logs.Log(LogLevel.Information,
                        $"[排队] {evt.Nickname} 完成队首：{result.Entry?.Nickname}（{result.Entry?.Content}）");
                }

                break;
            }
        }
    }

    private async Task HandleGiftAsync(LiveEvent evt, CancellationToken ct)
    {
        var settings = _config();
        if (!settings.QueueUp.Enabled)
        {
            return;
        }

        var rule = GiftRuleEngine.Match(settings.QueueUp.Rules, evt);
        if (rule is null)
        {
            return;
        }

        var userId = CompositeUserId(evt);
        if (rule.Action == "grant_eligibility")
        {
            if (Service.GrantEligibility(userId))
            {
                Logs.Log(LogLevel.Information,
                    $"[排队] {evt.Nickname} 送礼命中规则「授予入队资格」（{rule.MatchValue}）");
            }

            return;
        }

        // insert_at：自动插队到第 N 位（source=gift，内容留空；队满仍拒绝）
        var result = await Service.EnqueueAsync(
            userId, evt.Nickname, "", QueueUpSource.Gift, rule.Position, ct);
        if (result.Accepted)
        {
            Logs.Log(LogLevel.Information,
                $"[排队] 礼物插队：{evt.Nickname}（{evt.GiftName} ×{evt.GiftCount}）→ 第 {rule.Position} 位");
        }
        else if (result.Reason == "queue_full")
        {
            Logs.Log(LogLevel.Information, $"[排队] 礼物插队被拒（队满）：{evt.Nickname}");
        }
    }

    /// <summary>跨平台用户标识复合键（B站/抖音同 id 不互相覆盖/取消）。</summary>
    private static string CompositeUserId(LiveEvent evt) => $"{evt.Platform}:{evt.RoomId}:{evt.UserId}";
}
