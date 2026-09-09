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
}
