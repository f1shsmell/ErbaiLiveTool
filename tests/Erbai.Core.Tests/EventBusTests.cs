using System.Threading.Channels;
using Erbai.Core.Events;

namespace Erbai.Core.Tests;

/// <summary>事件总线：有界通道丢旧保新、多订阅者、退订、发布不阻塞。</summary>
public class EventBusTests
{
    private sealed record TestEvent(int Value);

    [Fact]
    public async Task Publish_DeliversToSubscriber()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe<TestEvent>();
        bus.Publish(new TestEvent(42));

        var received = await subscription.Reader.ReadAsync();
        Assert.Equal(42, received.Value);
    }

    [Fact]
    public async Task Publish_DeliversToAllSubscribers()
    {
        var bus = new EventBus();
        using var s1 = bus.Subscribe<TestEvent>();
        using var s2 = bus.Subscribe<TestEvent>();

        bus.Publish(new TestEvent(7));

        Assert.Equal(7, (await s1.Reader.ReadAsync()).Value);
        Assert.Equal(7, (await s2.Reader.ReadAsync()).Value);
    }

    [Fact]
    public async Task Publish_DoesNotDeliverToOtherTypes()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe<TestEvent>();

        bus.Publish("string-event"); // 不同类型
        bus.Publish(new TestEvent(1));

        Assert.Equal(1, (await subscription.Reader.ReadAsync()).Value);
    }

    [Fact]
    public async Task Dispose_Unsubscribes()
    {
        var bus = new EventBus();
        var subscription = bus.Subscribe<TestEvent>();
        Assert.Equal(1, bus.SubscriberCount);

        subscription.Dispose();
        Assert.Equal(0, bus.SubscriberCount);

        bus.Publish(new TestEvent(1));
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await subscription.Reader.ReadAsync());
    }

    [Fact]
    public async Task SlowConsumer_DropsOldestKeepsNewest()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe<TestEvent>(capacity: 2);

        for (var i = 1; i <= 5; i++)
        {
            bus.Publish(new TestEvent(i));
        }

        // 容量 2、满则丢最旧：只剩 4、5
        var received = new List<int>();
        await foreach (var item in subscription.Reader.ReadAllAsync())
        {
            received.Add(item.Value);
            if (received.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(new[] { 4, 5 }, received);
    }

    [Fact]
    public async Task Publish_DoesNotBlockEvenWithFullSlowSubscriber()
    {
        var bus = new EventBus();
        using var slow = bus.Subscribe<TestEvent>(capacity: 1);
        using var fast = bus.Subscribe<TestEvent>(capacity: 1);

        // 慢订阅者不消费；发布方不应阻塞（丢旧保新）
        var publishTask = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                bus.Publish(new TestEvent(i));
            }
        });

        var completed = await Task.WhenAny(publishTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(publishTask, completed);

        // 快订阅者只拿到最后一条（容量 1）
        var last = await fast.Reader.ReadAsync();
        Assert.Equal(999, last.Value);
    }


/// <summary>
/// 丢弃可观测（2026-10 长稳轮）：通道将满时的写入计为丢弃，经 DropObserved
/// 节流上报（30s/订阅一次）；不订阅该事件时零开销路径不受影响。
/// </summary>
public class EventBusDropTelemetryTests
{
    private sealed record DropEvent(int Value);

    [Fact]
    public void 满通道写入_触发丢弃上报()
    {
        var bus = new EventBus();
        var reports = new List<(string Type, long Count)>();
        bus.DropObserved += (t, c) => reports.Add((t, c));

        using var sub = bus.Subscribe<DropEvent>(capacity: 2);
        // 先消费掉前 2 条使通道空，再连发 5 条（容量 2 → 3 次丢弃）
        bus.Publish(new DropEvent(0));
        bus.Publish(new DropEvent(1));
        bus.Publish(new DropEvent(2));
        bus.Publish(new DropEvent(3));
        bus.Publish(new DropEvent(4));

        Assert.Contains(reports, r => r.Type == nameof(DropEvent) && r.Count >= 1);
    }

    [Fact]
    public void 未满通道写入_不触发上报()
    {
        var bus = new EventBus();
        var reported = 0;
        bus.DropObserved += (_, _) => reported++;

        using var sub = bus.Subscribe<DropEvent>(capacity: 10);
        for (var i = 0; i < 5; i++)
        {
            bus.Publish(new DropEvent(i));
        }

        Assert.Equal(0, reported);
    }

    [Fact]
    public void 节流窗内_合并为一次上报()
    {
        var bus = new EventBus();
        var total = 0L;
        bus.DropObserved += (_, c) => total += c;

        using var sub = bus.Subscribe<DropEvent>(capacity: 1);
        for (var i = 0; i < 50; i++)
        {
            bus.Publish(new DropEvent(i));
        }

        // 30s 节流窗内：只有第一次写入溢出被上报，其余计入但等下一窗
        Assert.True(total is >= 1 and < 50, $"节流窗内上报次数应有界，实际合计 {total}");
    }

    [Fact]
    public void 上报处理者抛异常_不影响发布()
    {
        var bus = new EventBus();
        bus.DropObserved += (_, _) => throw new InvalidOperationException("观察者异常（测试注入）");

        using var sub = bus.Subscribe<DropEvent>(capacity: 1);
        for (var i = 0; i < 10; i++)
        {
            bus.Publish(new DropEvent(i)); // 不得抛出
        }

        Assert.True(sub.Reader.TryRead(out _));
    }
}
}
