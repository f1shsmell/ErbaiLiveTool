using System.Text.Json;
using Erbai.Contracts.Queue;
using Erbai.Contracts.QueueUp;

namespace Erbai.App.Overlay;

/// <summary>
/// 点歌/排队悬浮窗数据源（2026-08-29 预案 B）：弃用 WebView2 后改直连 OverlayServer。
/// HTTP 快照轮询（2s，低频可靠）。2026-09 起无 token（回环绑定 + OBS 同机）。
/// 弹幕 WS 数据源（DanmakuOverlayFeed）已于 2026-09 随 WPF 弹幕悬浮窗迁入 Erbai.OverlayWpf。
/// </summary>
internal abstract class OverlayFeed : IDisposable
{
    public abstract void Dispose();

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>
/// 点歌/排队轮询数据源（2026-10 长稳轮改为顺序化异步循环）。
/// 旧实现 System.Threading.Timer(2s) + GetAwaiter().GetResult()：服务端变慢时
/// （HttpClient 超时 4s &gt; 轮询周期 2s）回调重叠堆积，线程池被阻塞的 GetResult
/// 线程占满。顺序化循环天然不重叠：上一轮（含失败/超时）完成才排下一轮。
/// </summary>
internal sealed class PollingOverlayFeed : OverlayFeed
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly string _queueUrl;
    private readonly string _queueUpUrl;
    private readonly bool _pollQueue;
    private readonly bool _pollQueueUp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile bool _disposed;

    /// <summary>点歌快照更新（线程池线程）。</summary>
    public event Action<QueueSnapshot>? QueueUpdated;

    /// <summary>排队快照更新（线程池线程）。</summary>
    public event Action<QueueUpSnapshot>? QueueUpUpdated;

    public PollingOverlayFeed(string baseUrl, bool pollQueue, bool pollQueueUp)
    {
        _pollQueue = pollQueue;
        _pollQueueUp = pollQueueUp;
        _queueUrl = $"{baseUrl}/api/v1/queue";
        _queueUpUrl = $"{baseUrl}/api/v1/queueup";
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            if (_pollQueue)
            {
                await PollAsync(_queueUrl, json =>
                {
                    var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(json, JsonOptions);
                    if (snapshot is not null)
                    {
                        QueueUpdated?.Invoke(snapshot);
                    }
                });
            }

            if (_pollQueueUp && !ct.IsCancellationRequested && !_disposed)
            {
                await PollAsync(_queueUpUrl, json =>
                {
                    var snapshot = JsonSerializer.Deserialize<QueueUpSnapshot>(json, JsonOptions);
                    if (snapshot is not null)
                    {
                        QueueUpUpdated?.Invoke(snapshot);
                    }
                });
            }

            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task PollAsync(string url, Action<string> handle)
    {
        try
        {
            var json = await Http.GetStringAsync(url);
            handle(json);
        }
        catch
        {
            // 服务未就绪/瞬时失败：下一轮重试（不记日志避免刷屏）
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        // 不 await _loop：HTTP 可能正处于 4s 超时等待，取消令牌会让它在下一个
        // await 点退出；Dispose 调用方（窗口线程）不得被网络等待阻塞
    }
}

