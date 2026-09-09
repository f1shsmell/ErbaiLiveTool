namespace Erbai.Modules.SongRequest.Search;

/// <summary>
/// 同 (source, keyword) 的 TTL LRU 缓存：热词不反复请求上游；只缓存非空结果（无结果很快
/// 可能有，空缓存会挡住重试）。
/// </summary>
public sealed class SearchResultCache
{
    private readonly TimeSpan _ttl;
    private readonly int _maxSize;
    private readonly object _lock = new();
    private readonly Dictionary<(string Source, string Keyword), (DateTimeOffset StoredAt, IReadOnlyList<SongSearchResult> Value)>
        _cache = new();
    private readonly LinkedList<(string Source, string Keyword)> _lru = new();

    public SearchResultCache(TimeSpan? ttl = null, int maxSize = 256)
    {
        _ttl = ttl ?? TimeSpan.FromSeconds(300);
        _maxSize = Math.Max(1, maxSize);
    }

    public IReadOnlyList<SongSearchResult>? Get(string source, string keyword)
    {
        lock (_lock)
        {
            var key = (source, keyword);
            if (!_cache.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - entry.StoredAt > _ttl)
            {
                _cache.Remove(key);
                _lru.Remove(key);
                return null;
            }

            _lru.Remove(key);
            _lru.AddFirst(key);
            return entry.Value;
        }
    }

    public void Put(string source, string keyword, IReadOnlyList<SongSearchResult> value)
    {
        lock (_lock)
        {
            var key = (source, keyword);
            _cache[key] = (DateTimeOffset.UtcNow, value);
            _lru.Remove(key);
            _lru.AddFirst(key);
            while (_lru.Count > _maxSize)
            {
                var last = _lru.Last!;
                _cache.Remove(last.Value);
                _lru.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }
}
