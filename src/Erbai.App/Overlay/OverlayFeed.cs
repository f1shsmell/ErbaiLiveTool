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

/// <summary>点歌/排队轮询数据源。</summary>
internal sealed class PollingOverlayFeed : OverlayFeed
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    private readonly string _queueUrl;
    private readonly string _queueUpUrl;
    private readonly bool _pollQueue;
    private readonly bool _pollQueueUp;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>点歌快照更新（任意线程）。</summary>
    public event Action<QueueSnapshot>? QueueUpdated;

    /// <summary>排队快照更新（任意线程）。</summary>
    public event Action<QueueUpSnapshot>? QueueUpUpdated;

    public PollingOverlayFeed(string baseUrl, bool pollQueue, bool pollQueueUp)
    {
        _pollQueue = pollQueue;
        _pollQueueUp = pollQueueUp;
        _queueUrl = $"{baseUrl}/api/v1/queue";
        _queueUpUrl = $"{baseUrl}/api/v1/queueup";
        _timer = new System.Threading.Timer(_ => Poll(), null, 0, 2000);
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        if (_pollQueue)
        {
            PollQueue();
        }

        if (_pollQueueUp && !_disposed)
        {
            PollQueueUp();
        }
    }

    private void PollQueue()
    {
        try
        {
            var json = Http.GetStringAsync(_queueUrl).GetAwaiter().GetResult();
            var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(json, JsonOptions);
            if (snapshot is not null)
            {
                lock (_gate)
                {
                    QueueUpdated?.Invoke(snapshot);
                }
            }
        }
        catch
        {
            // 服务未就绪/瞬时失败：下一轮重试（不记日志避免刷屏）
        }
    }

    private void PollQueueUp()
    {
        try
        {
            var json = Http.GetStringAsync(_queueUpUrl).GetAwaiter().GetResult();
            var snapshot = JsonSerializer.Deserialize<QueueUpSnapshot>(json, JsonOptions);
            if (snapshot is not null)
            {
                lock (_gate)
                {
                    QueueUpUpdated?.Invoke(snapshot);
                }
            }
        }
        catch
        {
            // 同上
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }
}

