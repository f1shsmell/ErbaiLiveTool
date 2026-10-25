using System.Threading.Channels;
using Erbai.Contracts.Abstractions;

namespace Erbai.Core.Events;

/// <summary>
/// 进程内事件总线：有界通道广播，慢消费者丢旧保新（不阻塞发布方）。
/// 订阅表用锁保护（Publish 可来自任意线程：弹幕线程/worker 线程/UI 线程）。
/// 丢弃可观测（2026-10 长稳轮）：通道将满时的写入意味着最旧事件被挤掉——
/// 计数并经 <see cref="DropObserved"/> 节流上报（默认 30s/订阅一次），
/// 供组合根接日志：弹幕洪泛下"页面不动了/点歌没反应"有了可定位的信号。
/// </summary>
public sealed class EventBus : IEventBus
{
    /// <summary>同一订阅两次丢弃告警的最小间隔（防告警本身刷屏）。</summary>
    private static readonly TimeSpan DropReportThrottle = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly Dictionary<Type, List<ISubscriptionEntry>> _subscribers = new();

    /// <summary>订阅队列溢出丢弃时上报（事件类型名 + 累计丢弃数）；节流，线程安全。</summary>
    public event Action<string, long>? DropObserved;

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

        var entry = new SubscriptionEntry<T>(channel.Writer, channel.Reader, capacity, OnDrop);
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

    private void OnDrop(ISubscriptionEntry entry)
    {
        var handler = DropObserved;
        if (handler is null)
        {
            return;
        }

        // 条目自身节流（上报后清零计数）：告警频率有界
        var total = entry.TakeReportedDrops();
        if (total > 0)
        {
            try
            {
                handler(entry.TypeName, total);
            }
            catch
            {
                // 上报失败不影响总线（观察者异常隔离）
            }
        }
    }

    private interface ISubscriptionEntry
    {
        string TypeName { get; }

        void TryWrite(object? value);

        /// <summary>取出自上次上报以来的丢弃计数并清零（无丢弃返回 0）。</summary>
        long TakeReportedDrops();
    }

    private sealed class SubscriptionEntry<T> : ISubscriptionEntry
    {
        private readonly ChannelWriter<T> _writer;
        private readonly ChannelReader<T> _reader;
        private readonly int _capacity;
        private readonly Action<ISubscriptionEntry> _onDrop;
        private long _dropped;
        private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

        public SubscriptionEntry(
            ChannelWriter<T> writer,
            ChannelReader<T> reader,
            int capacity,
            Action<ISubscriptionEntry> onDrop)
        {
            _writer = writer;
            _reader = reader;
            _capacity = capacity;
            _onDrop = onDrop;
        }

        public string TypeName => typeof(T).Name;

        public void TryWrite(object? value)
        {
            if (value is not T typed)
            {
                return;
            }

            // DropOldest 满通道写入必挤掉最旧一条：写前计数 = 容量即丢弃。
            // 并发写手可能重复计一次（两者都看到满）——遥测近似值可接受，零锁开销。
            var willDrop = _reader.CanCount && _reader.Count >= _capacity;
            _writer.TryWrite(typed);
            if (willDrop)
            {
                Interlocked.Increment(ref _dropped);
                _onDrop(this);
            }
        }

        public long TakeReportedDrops()
        {
            var now = DateTimeOffset.UtcNow;
            var last = _lastReport;
            if (last != DateTimeOffset.MinValue && now - last < DropReportThrottle)
            {
                return 0; // 节流窗内：计数留到下一次上报
            }

            var dropped = Interlocked.Read(ref _dropped);
            if (dropped <= 0)
            {
                return 0;
            }

            // CompareExchange 保证并发上报只有一个取走计数（取走即清零）
            if (Interlocked.CompareExchange(ref _dropped, 0, dropped) != dropped)
            {
                return 0;
            }

            _lastReport = now;
            return dropped;
        }
    }
}
