using System.Threading.Channels;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Logging;

namespace Erbai.Core.Hosting;

/// <summary>
/// 模块循环辅助：从事件流读事件，handler 抛出的任何异常记录日志后继续，
/// 循环本身不逃逸（任何异常必须结算当前请求，否则整个队列永久停摆）。
/// </summary>
public static class ModuleLoops
{
    /// <summary>
    /// 运行消费者循环直到 <paramref name="ct"/> 取消或流完成。
    /// handler 异常被捕获并记入 <paramref name="logs"/>（不中断循环）。
    /// </summary>
    public static Task RunConsumerLoopAsync<T>(
        Subscription<T> subscription,
        Func<T, CancellationToken, Task> handler,
        ILogBus logs,
        string consumerName,
        CancellationToken ct) =>
        RunConsumerLoopAsync(subscription.Reader, handler, logs, consumerName, ct);

    /// <summary>
    /// 运行消费者循环直到 <paramref name="ct"/> 取消或流完成。
    /// handler 异常被捕获并记入 <paramref name="logs"/>（不中断循环）。
    /// </summary>
    public static async Task RunConsumerLoopAsync<T>(
        ChannelReader<T> reader,
        Func<T, CancellationToken, Task> handler,
        ILogBus logs,
        string consumerName,
        CancellationToken ct)
    {
        await foreach (var item in reader.ReadAllAsync(ct))
        {
            try
            {
                await handler(item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logs.Error($"消费者 {consumerName} 处理事件异常（已隔离，循环继续）: {ex.Message}", ex.ToString());
            }
        }
    }
}
