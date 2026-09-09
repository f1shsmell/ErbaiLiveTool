namespace Erbai.Contracts.Abstractions;

/// <summary>
/// Overlay 推送通道（WS 事件信封 {event, version, timestamp, data} 的进程内
/// 前置面）。阶段 2 由 Erbai.Web 实现；在此之前模块持有的引用为 null。
/// </summary>
public interface IOverlayHub
{
    /// <summary>向 overlay 客户端广播一个事件（channel 如 "queue" / "giftfx.play"）。</summary>
    Task PublishAsync(string channel, object payload, CancellationToken ct = default);
}
