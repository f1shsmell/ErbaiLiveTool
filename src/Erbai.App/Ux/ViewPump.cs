using System.Threading.Channels;
using Erbai.Contracts.Abstractions;
using Microsoft.UI.Dispatching;

namespace Erbai.App.Ux;

/// <summary>
/// 页面事件泵（2026-10 精简轮）：订阅总线 → 后台消费循环 → UI 线程回调，
/// Dispose 即取消+退订。原为 QueuePage/QueueUpPage/OverviewPage 各自复制的
/// "Subscribe + Task.Run + ReadAllAsync + TryEnqueue + Unloaded 清理"脚手架。
/// </summary>
public sealed class ViewPump<T> : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable _subscription;
    private readonly ChannelReader<T> _reader;
    private int _disposed;

    public ViewPump(IEventBus bus, DispatcherQueue dispatcher, int capacity, Action<T> onUi)
    {
        var sub = bus.Subscribe<T>(capacity);
        _subscription = sub;
        _reader = sub.Reader;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in _reader.ReadAllAsync(ct))
                {
                    dispatcher.TryEnqueue(() => onUi(item));
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _subscription.Dispose();
        _cts.Dispose();
    }
}
