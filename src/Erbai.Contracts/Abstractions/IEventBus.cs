namespace Erbai.Contracts.Abstractions;

/// <summary>
/// 进程内事件总线：有界通道广播，慢消费者丢旧保新（旧事件被丢弃，不阻塞发布方）。
/// 播放器快照、队列事件、直播事件都走总线。
/// </summary>
public interface IEventBus
{
    /// <summary>订阅 <typeparamref name="T"/> 事件流；返回的句柄 Dispose 即退订。</summary>
    Subscription<T> Subscribe<T>(int capacity = 64);

    /// <summary>向所有订阅者广播事件；某订阅者通道满时丢弃其最旧事件。</summary>
    void Publish<T>(T @event);

    /// <summary>当前订阅者数量（调试/测试用）。</summary>
    int SubscriberCount { get; }
}
