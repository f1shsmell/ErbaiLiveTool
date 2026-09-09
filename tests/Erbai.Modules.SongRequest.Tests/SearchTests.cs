using System.Net;
using System.Text;
using Erbai.Modules.SongRequest.Search;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>里程碑 5 测试设施：fake HttpMessageHandler（不打真网）。</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

    public int RequestCount { get; private set; }

    public List<string> RequestedUrls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestCount++;
        RequestedUrls.Add(request.RequestUri!.ToString());
        return Task.FromResult(Responder(request));
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// 搜索三源与韧性层：
/// 解析字段面、非 200 无结果、网络错误计熔断、熔断 3 败 60s 半开、TTL 缓存
/// 只存非空、prefer_hot 边界、多源编排配置序、R7 回退。
/// </summary>
public class SearchTests
{
    private static SearchCoordinator CreateCoordinator(FakeHttpHandler handler,
        IReadOnlyList<string>? sources = null, bool preferHot = true,
        SearchCircuitBreaker? breaker = null, SearchResultCache? cache = null)
    {
        var client = new SearchHttpClient(handler);
        return new SearchCoordinator(client, sources ?? ["kugou", "netease", "qqmusic"], preferHot, breaker, cache);
    }

    // ---- 三源解析 ----

    [Fact]
    public async Task Kugou_ParsesFirstHit()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("""
                {"data":{"info":[{"songname":"晴天","singername":"周杰伦","hash":"H1",
                "320hash":"H2","sqhash":"H3","duration":265,"album_id":"A1",
                "album_name":"叶惠美","imgUrl":"http://img/1.jpg"}]}}
                """),
        };
        using var client = new SearchHttpClient(handler);
        var provider = new KugouSearchProvider(client);

        var song = await provider.SearchFirstAsync("晴天 周杰伦", preferHot: true, CancellationToken.None);

        Assert.NotNull(song);
        Assert.Equal("kugou", song.Source);
        Assert.Equal("晴天", song.Name);
        Assert.Equal("周杰伦", song.Singer);
        Assert.Equal("H1", song.SongMid);
        Assert.Equal("04:25", song.Interval);
        Assert.Equal(3, song.Types.Count);
        Assert.Equal("320k", song.Types[1].Type);
        Assert.Equal("H3", song.Types[2].Hash);
        Assert.Contains("keyword=", handler.RequestedUrls[0]);
        Assert.Contains("showtype=1", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task Netease_ParsesFirstHit_MultiArtistJoin()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("""
                {"code":200,"result":{"songs":[{"name":"光年之外","artists":[{"name":"邓紫棋"},{"name":"G.E.M."}],
                "album":{"name":"新的心跳","id":123,"picUrl":"http://img/a.jpg"},"id":456,"duration":235000}]}}
                """),
        };
        using var client = new SearchHttpClient(handler);
        var provider = new NeteaseSearchProvider(client);

        var song = await provider.SearchFirstAsync("光年之外", preferHot: true, CancellationToken.None);

        Assert.NotNull(song);
        Assert.Equal("netease", song.Source);
        Assert.Equal("邓紫棋/G.E.M.", song.Singer);
        Assert.Equal("456", song.SongMid);
        Assert.Equal("03:55", song.Interval); // 毫秒转秒
        Assert.Equal("123", song.AlbumId);
    }

    [Fact]
    public async Task QqMusic_ParsesFirstHit_StrMediaMidAndCover()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("""
                {"req_0":{"code":0,"data":{"body":{"song":{"list":[{"name":"晴天","mid":"SM1",
                "id":347230,"type":1,"interval":265,"singer":[{"name":"周杰伦"}],
                "album":{"mid":"AM1","name":"叶惠美","id":77},
                "file":{"media_mid":"MM1","size_128mp3":1234,"size_320mp3":0,"size_flac":0}}]}}}}}
                """),
        };
        using var client = new SearchHttpClient(handler);
        var provider = new QqMusicSearchProvider(client);

        var song = await provider.SearchFirstAsync("晴天", preferHot: true, CancellationToken.None);

        Assert.NotNull(song);
        Assert.Equal("qqmusic", song.Source);
        Assert.Equal("SM1", song.SongMid);
        Assert.Equal("MM1", song.StrMediaMid);
        Assert.Equal(347230, song.SongId); // songid：QQ 原生插队必需载荷
        Assert.Equal(1, song.SongType);    // songtype：QQ 原生插队必需载荷
        Assert.Equal("04:25", song.Interval);
        Assert.Contains("T002R300x300M000AM1.jpg", song.Img);
        Assert.Single(song.Types); // 只有 128k 有 size
    }

    [Fact]
    public async Task QqMusic_PostJsonBody_NoUnicodeEscape_NoQueryData_EncodingPitfalls()
    {
        // 编码双坑回归（2026-09，docs/00）：QQ musicu.fcg 必须 POST body 发送原始
        // UTF-8 中文——① JsonSerializer 默认 \uXXXX 转义服务器不认；② rpc 放 query
        // 会经 new Uri 规范化改写百分号编码（%22 变裸引号/中文原样），酷狗/网易云
        // 宽容而 QQ 严格。测试钉住：POST + body 无 \u 转义 + 中文原文 + URL 无 data 参数。
        string? capturedMethod = null;
        string? capturedBody = null;
        var handler = new FakeHttpHandler
        {
            Responder = request =>
            {
                capturedMethod = request.Method.Method;
                capturedBody = request.Content is null
                    ? null
                    : new StreamReader(request.Content.ReadAsStream(), Encoding.UTF8).ReadToEnd();
                return FakeHttpHandler.Json("""
                    {"req_0":{"code":0,"data":{"body":{"song":{"list":[{"name":"晴天","mid":"SM1",
                    "id":1,"type":0,"interval":100,"file":{"media_mid":"MM1"}}]}}}}}
                    """);
            },
        };
        using var client = new SearchHttpClient(handler);
        var provider = new QqMusicSearchProvider(client);

        var song = await provider.SearchFirstAsync("晴天", preferHot: true, CancellationToken.None);

        Assert.NotNull(song);
        Assert.Equal("POST", capturedMethod);
        Assert.False(string.IsNullOrEmpty(capturedBody));
        Assert.DoesNotContain("\\u", capturedBody!);   // 坑1：无 \uXXXX 转义序列
        Assert.Contains("晴天", capturedBody!);        // 坑1：中文以 UTF-8 原文输出
        Assert.Contains("\"grp\":1", capturedBody!);   // grp:1 必需，否则空列表
        Assert.Single(handler.RequestedUrls);          // 坑2：rpc 走 body → URL 恒定无 query
        Assert.DoesNotContain("data=", handler.RequestedUrls[0]); // 坑2：rpc 走 body → URL 无 data 参数
    }

    [Fact]
    public async Task QqMusic_MusicuEmpty_FallsBackToSmartboxAndDetail()
    {
        // 主通道(musicu)空结果 → 回退 smartbox 搜索 + songmid 详情补全。
        // 详情字段与 musicu 结构一致,复用 ParseSong:mid/id/type/interval/album/file 全补齐。
        var handler = new FakeHttpHandler
        {
            Responder = request =>
            {
                var uri = request.RequestUri!.ToString();
                if (uri.Contains("u.y.qq.com"))
                {
                    // musicu 空(风控窗口的典型返回:code 2001 + 空 list)
                    return FakeHttpHandler.Json("""{"req_0":{"code":2001,"data":{"body":{"song":{"list":[]}}}}}""");
                }

                if (uri.Contains("smartbox_new"))
                {
                    return FakeHttpHandler.Json("""
                        {"code":0,"data":{"song":{"itemlist":[
                            {"id":347230,"mid":"SM1","name":"晴天","singer":"周杰伦"}]}}}
                        """);
                }

                // fcg_play_single_song 详情
                return FakeHttpHandler.Json("""
                    {"code":0,"data":[{"name":"晴天","mid":"SM1",
                    "id":347230,"type":1,"interval":265,"singer":[{"name":"周杰伦"}],
                    "album":{"mid":"AM1","name":"叶惠美","id":77},
                    "file":{"media_mid":"MM1","size_128mp3":1234,"size_320mp3":0,"size_flac":0}}]}
                    """);
            },
        };
        using var client = new SearchHttpClient(handler);
        var provider = new QqMusicSearchProvider(client);

        var songs = await provider.SearchTopAsync("晴天", preferHot: true, limit: 1, CancellationToken.None);

        var song = Assert.Single(songs);
        Assert.Equal("qqmusic", song.Source);
        Assert.Equal("SM1", song.SongMid);
        Assert.Equal(347230, song.SongId);
        Assert.Equal(1, song.SongType);                 // 详情补全的 songtype
        Assert.Equal("MM1", song.StrMediaMid);          // 详情补全的 media_mid
        Assert.Contains("smartbox_new", handler.RequestedUrls[1]);   // 主通道空后走了回退
        Assert.Contains("fcg_play_single_song", handler.RequestedUrls[2]);
    }

    [Fact]
    public async Task QqMusic_Fallback_DetailFails_KeepsSmartboxBasics()
    {
        // 详情接口失败时:保留 smartbox 基础字段(songid/songmid/name/singer),songtype 默认 0
        var handler = new FakeHttpHandler
        {
            Responder = request =>
            {
                var uri = request.RequestUri!.ToString();
                if (uri.Contains("u.y.qq.com"))
                {
                    return FakeHttpHandler.Json("""{"req_0":{"code":2001,"data":{"body":{"song":{"list":[]}}}}}""");
                }

                if (uri.Contains("smartbox_new"))
                {
                    return FakeHttpHandler.Json("""
                        {"code":0,"data":{"song":{"itemlist":[
                            {"id":888,"mid":"MB1","name":"情歌","singer":"梁静茹"}]}}}
                        """);
                }

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); // 详情失败
            },
        };
        using var client = new SearchHttpClient(handler);
        var provider = new QqMusicSearchProvider(client);

        var songs = await provider.SearchTopAsync("情歌 梁静茹", preferHot: true, limit: 1, CancellationToken.None);

        var song = Assert.Single(songs);
        Assert.Equal("qqmusic", song.Source);
        Assert.Equal("MB1", song.SongMid);
        Assert.Equal(888, song.SongId);
        Assert.Equal("情歌", song.Name);
        Assert.Equal("梁静茹", song.Singer);
        Assert.Equal(0, song.SongType);
    }

    // ---- 韧性：非 200 / 网络错误 / 熔断 / 缓存 ----

    [Fact]
    public async Task Non200_ReturnsNoResult_NotAFailure()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        };
        using var client = new SearchHttpClient(handler);
        var provider = new KugouSearchProvider(client);

        var song = await provider.SearchFirstAsync("晴天", preferHot: true, CancellationToken.None);

        Assert.Null(song); // 非 200 → 无结果,不熔断
    }

    [Fact]
    public async Task NetworkError_SourceSkipped_OtherSourcesSucceed()
    {
        // 编排层不抛：失败源跳过（熔断计数），其他源正常
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.Host.Contains("kugou")
                ? throw new HttpRequestException("connection refused")
                : FakeHttpHandler.Json("""{"code":200,"result":{"songs":[{"name":"晴天","id":1,"duration":1000}]}}"""),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["kugou", "netease"]);

        var songs = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);

        var song = Assert.Single(songs);
        Assert.Equal("netease", song.Source);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterFailures_ThenSkipsSourceWithoutRequesting()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => throw new HttpRequestException("boom"),
        };
        var breaker = new SearchCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromSeconds(60));
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["kugou"], breaker: breaker);

        // 连续 2 败 → 熔断
        for (var i = 0; i < 2; i++)
        {
            var songs = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);
            Assert.Empty(songs); // 失败源跳过 → 无候选
        }

        var requestsBefore = handler.RequestCount;
        // 熔断中：跳过该源，不再请求上游（编排层不抛，返回空）
        var again = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);
        Assert.Empty(again);
        Assert.Equal(requestsBefore, handler.RequestCount);
    }

    [Fact]
    public async Task CircuitBreaker_HalfOpen_ProbeFailureReopens_ProbeSuccessRecovers()
    {
        var fail = true;
        var handler = new FakeHttpHandler
        {
            Responder = _ => fail
                ? throw new HttpRequestException("boom")
                : FakeHttpHandler.Json("""{"data":{"info":[{"songname":"晴天","singername":"周杰伦","hash":"H1","duration":265}]}}"""),
        };
        var breaker = new SearchCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromMilliseconds(250));
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["kugou"], breaker: breaker);

        for (var i = 0; i < 2; i++)
        {
            await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);
        }

        // 熔断中不请求
        var before = handler.RequestCount;
        await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);
        Assert.Equal(before, handler.RequestCount);

        // 半开到期：放行一次，仍失败 → 立即重新熔断
        await Task.Delay(300);
        await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);
        var afterProbeFail = handler.RequestCount;
        Assert.True(afterProbeFail > before); // 半开探测发出
        await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);
        Assert.Equal(afterProbeFail, handler.RequestCount); // 半开失败立即重熔断

        // 半开到期后上游恢复 → 成功 → 关闭熔断
        await Task.Delay(300);
        fail = false;
        var songs = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);
        Assert.Single(songs); // 熔断关闭,搜索成功
    }

    [Fact]
    public async Task Cache_OnlyCachesNonEmpty_SecondCallSkipsNetwork()
    {
        var calls = 0;
        var handler = new FakeHttpHandler
        {
            Responder = _ =>
            {
                calls++;
                return FakeHttpHandler.Json(
                    $"{{\"data\":{{\"info\":[{{\"songname\":\"晴天\",\"singername\":\"周杰伦\",\"hash\":\"H{calls}\",\"duration\":265}}]}}}}");
            },
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["kugou"]);

        var first = await coordinator.SearchCandidatesAsync("晴天", "周杰伦", CancellationToken.None);
        var second = await coordinator.SearchCandidatesAsync("晴天", "周杰伦", CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, calls); // 第二次命中缓存,不再请求

        // 空结果不缓存
        var handler2 = new FakeHttpHandler { Responder = _ => FakeHttpHandler.Json("{\"data\":{\"info\":[]}}") };
        using var client2 = new SearchHttpClient(handler2);
        var coordinator2 = CreateCoordinator(handler2, sources: ["kugou"]);
        await coordinator2.SearchCandidatesAsync("不存在", "", CancellationToken.None);
        await coordinator2.SearchCandidatesAsync("不存在", "", CancellationToken.None);
        Assert.Equal(2, handler2.RequestCount); // 无结果 → 每次都重试
    }

    // ---- prefer_hot 边界 ----

    [Fact]
    public async Task PreferHot_AllHavePopularity_PicksHottest()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("""
                {"code":200,"result":{"songs":[
                    {"name":"晴天","id":1,"popularity":50,"duration":1000},
                    {"name":"晴天","id":2,"popularity":90,"duration":1000},
                    {"name":"晴天","id":3,"popularity":70,"duration":1000}]}}
                """),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["netease"]);

        var songs = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);

        // 每源只取第一候选（初版语义），但源内仍按热度过 preferHot 排序取最热版本
        var song = Assert.Single(songs);
        Assert.Equal("2", song.SongMid);
    }

    [Fact]
    public async Task PreferHot_PartialPopularity_KeepsProviderOrder()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("""
                {"code":200,"result":{"songs":[
                    {"name":"晴天","id":1,"popularity":50,"duration":1000},
                    {"name":"晴天","id":2,"duration":1000}]}}
                """),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["netease"]);

        var songs = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);

        // 部分带 popularity → 保序取首
        Assert.Equal("1", songs[0].SongMid);
    }

    [Fact]
    public async Task HttpClientTimeout_ClassifiedAsRecoverable_NotUserCancellation()
    {
        // HttpClient.Timeout 抛 TaskCanceledException(继承 OCE)且调用方未取消：
        // 必须归为可恢复错误(失败源跳过+计熔断)，绝不能穿透 worker 停摆队列
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.Host.Contains("kugou")
                ? throw new TaskCanceledException("the operation timed out")
                : FakeHttpHandler.Json("""{"code":200,"result":{"songs":[{"name":"晴天","id":1,"duration":1000}]}}"""),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["kugou", "netease"]);

        // 编排层不抛、不挂：kugou 超时被跳过，netease 正常返回
        var songs = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);

        var song = Assert.Single(songs);
        Assert.Equal("netease", song.Source);

        // 用户取消仍应穿透(与超时区分)
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler2 = new FakeHttpHandler { Responder = _ => throw new OperationCanceledException() };
        using var client2 = new SearchHttpClient(handler2);
        var coordinator2 = CreateCoordinator(handler2, sources: ["kugou"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator2.SearchCandidatesAsync("晴天", "", cts.Token));
    }

    // ---- 多源编排 ----

    [Fact]
    public async Task MultiSource_Concurrent_ConfigOrder_FailureSkipped()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.Host.Contains("kugou")
                ? throw new HttpRequestException("kugou down")
                : request.RequestUri!.Host.Contains("163")
                    ? FakeHttpHandler.Json("""{"code":200,"result":{"songs":[{"name":"晴天","id":1,"duration":1000}]}}""")
                    : FakeHttpHandler.Json("""{"req_0":{"code":0,"data":{"body":{"song":{"list":[{"name":"晴天","mid":"TX1","interval":100,"file":{"media_mid":"M1"}}]}}}}}"""),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["kugou", "netease", "qqmusic"]);

        var songs = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);

        // 失败源(kugou)跳过,候选按配置序:netease 在前,qqmusic 在后
        Assert.Equal(2, songs.Count);
        Assert.Equal("netease", songs[0].Source);
        Assert.Equal("qqmusic", songs[1].Source);
    }

    [Fact]
    public async Task R7Fallback_NoResultsWithSpace_RetyAsFirstWord()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("{\"data\":{\"info\":[]}}"),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["kugou"]);

        var songs = await coordinator.SearchCandidatesAsync("Hotel California", "", CancellationToken.None);

        Assert.Empty(songs);
        // 回退重试一次(仅首词——不再把 singer 拼回 keyword 造成逐字节相同的无效重试)
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("keyword=Hotel", handler.RequestedUrls[1]);
        Assert.DoesNotContain("California", handler.RequestedUrls[1]);
    }

    [Fact]
    public async Task NoSpace_NoFallbackRetry()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("{\"data\":{\"info\":[]}}"),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["kugou"]);

        var songs = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);

        Assert.Empty(songs);
        Assert.Equal(1, handler.RequestCount); // 无空格不触发 R7
    }

    // ---- 选用候选第一个（恢复初版逻辑，参考 blive-vod-fork run_search 取首） ----

    [Fact]
    public async Task SingerGiven_TakesFirstCandidate_NoOriginalReorder()
    {
        // 点歌带歌手后不再做"原唱优先重排"（初版语义：每源只取第一候选，
        // 顺序 = 源返回顺序）。即使翻唱排最前也直接取它——可播性交给
        // lxmusic searchPlay 自搜自播兜底，而不是外部靠多候选排序。
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("""
                {"code":200,"result":{"songs":[
                    {"name":"晴天","id":111,"artists":[{"name":"翻唱者A"}],"duration":1000,"popularity":99},
                    {"name":"晴天","id":222,"artists":[{"name":"周杰伦"}],"duration":1000,"popularity":50}]}}
                """),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["netease"]);

        var songs = await coordinator.SearchCandidatesAsync("晴天", "周杰伦", CancellationToken.None);

        var song = Assert.Single(songs); // 每源只取第一候选
        Assert.Equal("111", song.SongMid);
    }

    [Fact]
    public async Task Reorder_CrossSourceSameSong_KeptBothSources()
    {
        // 同一首歌在不同源命中（SongMid 不同）→ 各源第一候选都保留，供换源兜底
        var kugou = """{"data":{"info":[{"songname":"晴天","singername":"周杰伦","hash":"K1","duration":100}]}}""";
        var netease = """{"code":200,"result":{"songs":[{"name":"晴天","id":222,"artists":[{"name":"周杰伦"}],"duration":1000}]}}""";
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.Host.Contains("163") ? FakeHttpHandler.Json(netease) : FakeHttpHandler.Json(kugou),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["kugou", "netease"]);

        var songs = await coordinator.SearchCandidatesAsync("晴天", "周杰伦", CancellationToken.None);

        Assert.Equal(2, songs.Count); // 跨源不去重
        Assert.Equal("kugou", songs[0].Source); // 源序靠前
        Assert.Equal("netease", songs[1].Source);
    }

    [Fact]
    public async Task NoSinger_TakesFirstCandidate_SingleSearchOnly()
    {
        // 未带歌手：直接取每源第一候选，不再做"原唱锁定"二次搜索（初版语义）。
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("""
                    {"code":200,"result":{"songs":[
                        {"name":"晴天","id":1,"artists":[{"name":"翻唱者A"}],"duration":1000},
                        {"name":"晴天","id":2,"artists":[{"name":"周杰伦"}],"duration":1000}]}}
                    """),
        };
        using var client = new SearchHttpClient(handler);
        var coordinator = CreateCoordinator(handler, sources: ["netease"]);

        var songs = await coordinator.SearchCandidatesAsync("晴天", "", CancellationToken.None);

        // 无二次搜索：首搜 netease 双端点 = 2 请求
        Assert.Equal(2, handler.RequestCount);
        var song = Assert.Single(songs);
        Assert.Equal("1", song.SongMid); // 源返回顺序第一
    }
}
