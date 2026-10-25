using System.Net.WebSockets;
using System.Threading.Channels;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Plugins;

namespace Erbai.Core.Hosting;

/// <summary>
/// 直播平台插件生命周期基类（2026-10 精简轮）：事件通道（256 有界丢旧）、
/// 启停/IsRunning/Dispose 的锁与令牌管理——两个平台插件逐字节相同的脚手架。
/// <b>只收编生命周期，不收编监听循环</b>：B站（初始化链/认证语义/指数退避/心跳
/// 看门狗）与抖音（Grabber 自愈）是不同状态机，强行模板化会用 6 个钩子换耦合。
/// 子类在 StartAsync 里校验参数后调 <see cref="StartLoop"/>。
/// </summary>
public abstract class LivePluginBase : ILivePlatformPlugin
{
    private readonly object _sync = new();
    private Channel<LiveEvent> _events;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    protected LivePluginBase()
    {
        _events = CreateEventChannel();
    }

    public abstract string Key { get; }

    public abstract string DisplayName { get; }

    public abstract Task StartAsync(AppConfig config, CancellationToken ct);

    public abstract LiveCapabilities Capabilities { get; }

    public ChannelReader<LiveEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.Reader;
            }
        }
    }

    /// <summary>监听循环写入事件（StartLoop 重建通道后指向新通道；子类收帧时调用）。</summary>
    protected ChannelWriter<LiveEvent> EventWriter => _events.Writer;

    /// <summary>监听循环是否在跑（UI 启停按钮状态用）。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    /// <summary>重建事件通道并启动监听循环（子类 StartAsync 校验参数后调用）。</summary>
    protected void StartLoop(Func<CancellationToken, Task> loop)
    {
        lock (_sync)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _events = CreateEventChannel();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => loop(_cts.Token), CancellationToken.None);
        }
    }

    /// <summary>标准停止：取消循环 → 等退出 → 完结事件通道（幂等）。</summary>
    public virtual async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        Channel<LiveEvent>? events;
        lock (_sync)
        {
            loop = _loop;
            cts = _cts;
            events = _events;
            _loop = null;
            _cts = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        events.Writer.TryComplete();
        cts.Dispose();
    }

    public virtual ValueTask DisposeAsync() => new(StopAsync());

    private static Channel<LiveEvent> CreateEventChannel() =>
        Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    /// <summary>可取消延迟（重连间隔；取消视为正常退出）。</summary>
    protected static async Task DelayOrCancel(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
