namespace Erbai.Modules.SongRequest.Search;

/// <summary>
/// 单源熔断器：
/// 连续失败 ≥3 次 → 熔断 60s；到期自动放行一次半开探测，成功关闭熔断，
/// 失败立即重新熔断（不等再失败 3 次）。进程级共享状态。
/// </summary>
public sealed class SearchCircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _openDuration;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _failures = new();
    private readonly Dictionary<string, DateTimeOffset> _openedUntil = new();

    public SearchCircuitBreaker(int failureThreshold = 3, TimeSpan? openDuration = null)
    {
        _threshold = Math.Max(1, failureThreshold);
        _openDuration = openDuration ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>该源当前是否熔断（应跳过）。熔断到期自动放行（半开探测）。</summary>
    public bool IsOpen(string source)
    {
        lock (_lock)
        {
            if (!_openedUntil.TryGetValue(source, out var until))
            {
                return false;
            }

            return DateTimeOffset.UtcNow < until;
        }
    }

    /// <summary>一次成功：关闭熔断并清空失败计数。</summary>
    public void RecordSuccess(string source)
    {
        lock (_lock)
        {
            _failures.Remove(source);
            _openedUntil.Remove(source);
        }
    }

    /// <summary>一次失败：半开探测失败立即重新熔断；常规失败累积到阈值熔断。</summary>
    public void RecordFailure(string source)
    {
        lock (_lock)
        {
            if (_openedUntil.ContainsKey(source))
            {
                // 半开探测失败：上游仍不可用，立即重新熔断
                _openedUntil[source] = DateTimeOffset.UtcNow + _openDuration;
                _failures.Remove(source);
                return;
            }

            var count = _failures.GetValueOrDefault(source) + 1;
            if (count >= _threshold)
            {
                _openedUntil[source] = DateTimeOffset.UtcNow + _openDuration;
                _failures.Remove(source);
            }
            else
            {
                _failures[source] = count;
            }
        }
    }
}
