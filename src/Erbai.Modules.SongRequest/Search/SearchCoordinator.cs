using System.Text.Json;

namespace Erbai.Modules.SongRequest.Search;

/// <summary>
/// 多源搜索编排（平移旧 backend/song_request_service.py _search_candidates +
/// search_common 韧性层，docs/03 §5）：
/// - 所有启用源并发搜索，失败源跳过（RecoverableSearchException 计熔断），
///   候选按 providers.enabled 配置顺序排列；每源只取第一首候选（恢复初版
///   "选用候选第一个"逻辑，去除后续加入的多候选/原唱锁定二次搜索等复杂度，
///   与 blive-vod-fork/lxmusic 的语义一致）；
/// - 单源韧性：连续 3 败熔断 60s，到期半开，半开失败立即重熔断；TTL 缓存
///   300s/256 条 LRU，只缓存非空结果；非 200 → []（不算失败）；网络错误
///   → RecoverableSearchException（计入熔断）；
/// - prefer_hot：仅当全部结果都带 popularity 才按热度降序（网易云字段），
///   部分带/都不带 → 保持源返回顺序（脏值按 0）；
/// - R7 回退：全部无结果且无歌手且歌名含空格 → 按"首词歌名、其余歌手"
///   重试一次。
/// 产出完整 candidates 供播放状态机换源（每源保留一个候选作换源兜底）。
/// </summary>
public sealed class SearchCoordinator
{
    private readonly SearchHttpClient _client;
    private readonly bool _preferHot;
    private readonly IReadOnlyList<SearchProvider> _providers;
    private readonly SearchCircuitBreaker _breaker;
    private readonly SearchResultCache _cache;

    public SearchCoordinator(SearchHttpClient client, IReadOnlyList<string> enabledSources, bool preferHot = true,
        SearchCircuitBreaker? breaker = null, SearchResultCache? cache = null)
    {
        _client = client;
        _preferHot = preferHot;
        _breaker = breaker ?? new SearchCircuitBreaker();
        _cache = cache ?? new SearchResultCache();
        var registry = new Dictionary<string, SearchProvider>(StringComparer.Ordinal)
        {
            ["kugou"] = new KugouSearchProvider(client),
            ["netease"] = new NeteaseSearchProvider(client),
            ["qqmusic"] = new QqMusicSearchProvider(client),
        };
        _providers = enabledSources
            .Where(registry.ContainsKey)
            .Select(key => registry[key])
            .ToList();
    }

    /// <summary>
    /// 并发搜索所有启用源，按配置序返回每源第一首候选；全部无结果时按 R7
    /// 回退重试一次；仍无结果返回空列表（调用方按 song_not_found 结算）。
    /// </summary>
    public async Task<IReadOnlyList<SongSearchResult>> SearchCandidatesAsync(
        string songName, string singer, CancellationToken ct)
    {
        var candidates = await SearchOnceAsync(songName, singer, ct);
        if (candidates.Count == 0 &&
            string.IsNullOrEmpty(singer) &&
            !string.IsNullOrWhiteSpace(songName) &&
            songName.Contains(' '))
        {
            // R7 兼容：首词歌名、其余歌手——重试时只按首词检索（三源 API 只有
            // 单 keyword；若把 singer 拼回 keyword 则与原查询逐字节相同，等于
            // 无效重复）。"Hotel California" 全名搜不到 → 搜 "Hotel" 放宽。
            var parts = songName.Trim().Split(' ', 2);
            candidates = await SearchOnceAsync(parts[0], "", ct);
        }

        return candidates;
    }

    /// <summary>生成 Worker.Processor 兼容的处理器（点歌/空闲歌共用）。</summary>
    public Func<Erbai.Contracts.Requests.SongRequest, CancellationToken, Task<IReadOnlyList<SongSearchResult>?>> ToProcessor() =>
        (request, ct) => SearchCandidatesAsync(request.SongName, request.Singer, ct)
            .ContinueWith(t => (IReadOnlyList<SongSearchResult>?)t.Result, ct);

    private async Task<IReadOnlyList<SongSearchResult>> SearchOnceAsync(
        string songName, string singer, CancellationToken ct)
    {
        var keyword = singer.Length > 0 ? $"{songName} {singer}" : songName;
        var tasks = _providers.Select(provider => SearchOneSafelyAsync(provider, keyword, ct)).ToList();
        var results = await Task.WhenAll(tasks);
        var candidates = new List<SongSearchResult>();
        foreach (var result in results)
        {
            if (result is not null)
            {
                candidates.Add(result);
            }
        }

        return candidates;
    }

    /// <summary>失败源跳过（熔断/网络/解析异常都只记日志，不拖垮整个编排）。</summary>
    private async Task<SongSearchResult?> SearchOneSafelyAsync(SearchProvider provider, string keyword, CancellationToken ct)
    {
        try
        {
            return await SearchOneAsync(provider, keyword, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RecoverableSearchException ex)
        {
            Console.WriteLine($"[点歌] {provider.DisplayName} 搜索失败：{ex.Message}");
            return null;
        }
    }

    private async Task<SongSearchResult?> SearchOneAsync(SearchProvider provider, string keyword, CancellationToken ct)
    {
        if (_breaker.IsOpen(provider.Key))
        {
            throw new RecoverableSearchException($"搜索源 {provider.DisplayName} 熔断中（连续失败，60s 后自动恢复）");
        }

        var cached = _cache.Get(provider.Key, keyword);
        if (cached is not null)
        {
            return cached.FirstOrDefault();
        }

        IReadOnlyList<SongSearchResult>? results;
        try
        {
            // 单源超时上限：避免卡顿源(如网易云)拖到 HttpClient 10s/端点，
            // 整单点歌等几十秒（docs/00 修复记录 #19）
            using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sourceTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            results = await provider.SearchFirstAsync(keyword, _preferHot, sourceTimeout.Token)
                is { } first
                ? [first]
                : [];
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout 抛 TaskCanceledException(继承 OCE)且调用方
            // 未取消 → 这是上游超时而非用户取消：计熔断并归为可恢复错误。
            // 若当"用户取消"重抛会穿透 worker 让整个队列停摆（高危）。
            _breaker.RecordFailure(provider.Key);
            throw new RecoverableSearchException($"搜索源超时：{ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw; // 用户取消
        }
        catch (Exception ex)
        {
            // 可恢复错误（连接/解析异常）：计入熔断，由下一源兜底
            _breaker.RecordFailure(provider.Key);
            throw new RecoverableSearchException($"搜索源不可达：{ex.Message}");
        }

        _breaker.RecordSuccess(provider.Key);
        // 只缓存非空结果：无结果很快可能有（版权/首发），空缓存会挡住重试
        if (results is { Count: > 0 })
        {
            _cache.Put(provider.Key, keyword, results);
        }

        return results.FirstOrDefault();
    }

    internal static int Popularity(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } &&
        element.Value.TryGetInt32(out var value)
            ? value
            : 0;
}
