using System.Threading.Channels;
using Erbai.Contracts.Abstractions;

namespace Erbai.Core.Events;

/// <summary>
/// 进程内事件总线：有界通道广播，慢消费者丢旧保新（不阻塞发布方）。
/// 订阅表用锁保护（Publish 可来自任意线程：弹幕线程/worker 线程/UI 线程）。
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly object _sync = new();
    private readonly Dictionary<Type, List<ISubscriptionEntry>> _subscribers = new();

    public int SubscriberCount
    {
        get
        {
            lock (_sync)
            {
                return _subscribers.Values.Sum(list => list.Count);
            }
        }
    }

    public Subscription<T> Subscribe<T>(int capacity = 64)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        }

        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });

        var entry = new SubscriptionEntry<T>(channel.Writer);
        List<ISubscriptionEntry> list;
        lock (_sync)
        {
            if (!_subscribers.TryGetValue(typeof(T), out var existing))
            {
                existing = [];
                _subscribers[typeof(T)] = existing;
            }

            list = existing;
            list.Add(entry);
        }

        return new Subscription<T>(channel.Reader, () =>
        {
            lock (_sync)
            {
                list.Remove(entry);
                if (list.Count == 0)
                {
                    _subscribers.Remove(typeof(T));
                }
            }

            channel.Writer.TryComplete();
        });
    }

    public void Publish<T>(T @event)
    {
        List<ISubscriptionEntry>? targets = null;
        lock (_sync)
        {
            if (_subscribers.TryGetValue(typeof(T), out var list) && list.Count > 0)
            {
                targets = [.. list];
            }
        }

        if (targets is null)
        {
            return;
        }

        foreach (var target in targets)
        {
            target.TryWrite(@event);
        }
    }

    private interface ISubscriptionEntry
    {
        void TryWrite(object? value);
    }

    private sealed class SubscriptionEntry<T> : ISubscriptionEntry
    {
        private readonly ChannelWriter<T> _writer;

        public SubscriptionEntry(ChannelWriter<T> writer) => _writer = writer;

        public void TryWrite(object? value)
        {
            if (value is T typed)
            {
                _writer.TryWrite(typed);
            }
        }
    }
}
