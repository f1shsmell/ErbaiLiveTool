using System.Threading.Channels;

namespace Erbai.Contracts.Abstractions;

/// <summary>
/// 一次订阅的句柄：持有事件流读取端，Dispose 即退订。
/// </summary>
public sealed class Subscription<T> : IDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    /// <summary>创建订阅句柄（仅供事件总线实现调用）。</summary>
    public Subscription(ChannelReader<T> reader, Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    /// <summary>事件流读取端（有界通道，慢消费者丢旧保新）。</summary>
    public ChannelReader<T> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose();
        }
    }
}
