using System.Net;
using System.Text;
using Erbai.Contracts.Storage;
using Erbai.Core.Storage;
using Erbai.Modules.SongRequest.Import;
using Erbai.Modules.SongRequest.Search;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>
/// 歌单导入：分享链接解析（三平台长/短链/用户主页）、网易云 v6 / QQ 移动端 /
/// 酷狗 v3 客户端解析（fake handler，不打真网）、短链重定向解析、去重落库与
/// 同源对账覆盖更新。
/// </summary>
public class PlaylistImportTests
{
    // ---- ShareLinkParser ----

    [Fact]
    public void Parse_NeteasePlaylistUrls_ExtractsId()
    {
        var long1 = ShareLinkParser.Parse("分享张三创建的歌单《青春》: https://music.163.com/playlist?id=7530558459&userid=1 (来自@网易云音乐)");
        Assert.NotNull(long1);
        Assert.Equal(PlaylistSource.Netease, long1.Platform);
        Assert.Equal(PlaylistLinkKind.Playlist, long1.Kind);
        Assert.Equal("7530558459", long1.PlaylistId);

        var mobile = ShareLinkParser.Parse("https://y.music.163.com/m/playlist?id=123456&appmac=xx");
        Assert.Equal("123456", mobile!.PlaylistId);
    }

    [Fact]
    public void Parse_NeteaseShortLink_MarksPlatformWithoutId()
    {
        var link = ShareLinkParser.Parse("♪ 一起听 https://163cn.tv/AbCdE 点击收听");
        Assert.NotNull(link);
        Assert.Equal(PlaylistSource.Netease, link.Platform);
        Assert.Null(link.PlaylistId);
    }

    [Fact]
    public void Parse_NeteaseUserHome_ExtractsUserId()
    {
        var link = ShareLinkParser.Parse("关注我 https://music.163.com/#/user/home?id=32953014 网易云");
        Assert.NotNull(link);
        Assert.Equal(PlaylistLinkKind.UserHome, link.Kind);
        Assert.Equal("32953014", link.UserId);
    }

    [Fact]
    public void Parse_QqUrls_ExtractsDissId()
    {
        var modern = ShareLinkParser.Parse("https://i2.y.qq.com/n3/other/pages/details/playlist.html?hosteuin=oK6kow&id=7503304369");
        Assert.Equal("7503304369", modern!.PlaylistId);
        Assert.Equal(PlaylistSource.Qqmusic, modern.Platform);

        var old = ShareLinkParser.Parse("https://y.qq.com/taoge/taoge.html?id=3778678");
        Assert.Equal("3778678", old!.PlaylistId);

        var path = ShareLinkParser.Parse("https://y.qq.com/n/yqq/playlist/7707261125.html");
        Assert.Equal("7707261125", path!.PlaylistId);

        var shortLink = ShareLinkParser.Parse("歌单 https://c6.y.qq.com/base/fcgi-bin/u?__=abc123 分享");
        Assert.NotNull(shortLink);
        Assert.Equal(PlaylistSource.Qqmusic, shortLink.Platform);
        Assert.Null(shortLink.PlaylistId);
    }

    [Fact]
    public void Parse_KugouUrls_ExtractsSpecialId()
    {
        var t1 = ShareLinkParser.Parse("@酷狗音乐 分享了歌单《精选集》 https://t1.kugou.com/xYz12 ");
        Assert.NotNull(t1);
        Assert.Equal(PlaylistSource.Kugou, t1.Platform);
        Assert.Null(t1.PlaylistId);

        var special = ShareLinkParser.Parse("https://www.kugou.com/mixsong/list.html?specialid=8268942");
        Assert.Equal("8268942", special!.PlaylistId);

        var plist = ShareLinkParser.Parse("http://m.kugou.com/plist/list/125032.html");
        Assert.Equal("125032", plist!.PlaylistId);

        var single = ShareLinkParser.Parse("https://www.kugou.com/yy/special/single/6914288.html");
        Assert.Equal("6914288", single!.PlaylistId);
    }

    [Fact]
    public void Parse_NoUrlOrForeignUrl_ReturnsNull()
    {
        Assert.Null(ShareLinkParser.Parse("普通文本没有链接"));
        Assert.Null(ShareLinkParser.Parse("看看这个 https://example.com/playlist?id=123 不是音乐平台"));
    }

    [Fact]
    public void ExtractIds_FromRedirectUrlsAndHtml()
    {
        Assert.Equal("7530558459", ShareLinkParser.ExtractNeteasePlaylistId(
            "https://music.163.com/playlist?id=7530558459&userid=1"));
        Assert.Equal("7503304369", ShareLinkParser.ExtractQqPlaylistId(
            "<script>location.replace('https://i.y.qq.com/n2/m/share/details/taoge.html?id=7503304369')</script>"));
        Assert.Equal("6914288", ShareLinkParser.ExtractKugouSpecialId(
            "https://www.kugou.com/yy/special/single/6914288.html"));
    }

    // ---- 网易云客户端（v6 detail 解析）----

    [Fact]
    public async Task Netease_FetchById_ParsesNameSongsAndCandidate()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("""
                {"code":200,"playlist":{"id":7530558459,"name":"青春回忆","trackCount":2,"tracks":[
                    {"id":287398,"name":"我不难过","ar":[{"id":1,"name":"孙燕姿"}],"al":{"id":9,"name":"未完成"},"dt":265000},
                    {"id":2617646167,"name":"普通女孩","ar":[{"id":2,"name":"甲"},{"id":3,"name":"乙"}],"al":{"id":10,"name":"demo"},"dt":60000}
                ]}}
                """),
        };
        using var http = new SearchHttpClient(handler);
        var client = new NeteasePlaylistClient(http);
        var imported = await client.FetchByIdAsync("7530558459");

        Assert.Equal("青春回忆", imported.Name);
        Assert.Equal("7530558459", imported.SourceId);
        Assert.Equal(2, imported.Songs.Count);

        var first = imported.Songs[0];
        Assert.Equal("我不难过", first.Name);
        Assert.Equal("孙燕姿", first.Singer);
        Assert.NotNull(first.Candidate);
        Assert.Equal("netease", first.Candidate.Source);
        Assert.Equal("287398", first.Candidate.SongMid);
        Assert.Equal("04:25", first.Candidate.Interval); // dt 毫秒 → mm:ss
        Assert.Equal("未完成", first.Candidate.AlbumName);

        Assert.Equal("甲/乙", imported.Songs[1].Singer);
        Assert.Equal("01:00", imported.Songs[1].Candidate!.Interval);
    }

    [Fact]
    public async Task Netease_FetchById_NoTracks_ThrowsReadableError()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => FakeHttpHandler.Json("""{"code":200,"playlist":{"id":1,"name":"空","tracks":[]}}"""),
        };
        using var http = new SearchHttpClient(handler);
        var client = new NeteasePlaylistClient(http);
        var ex = await Assert.ThrowsAsync<PlaylistImportException>(async () => await client.FetchByIdAsync("1"));
        Assert.Contains("未返回任何曲目", ex.Message);
    }

    [Fact]
    public async Task Netease_FetchById_MissingPlaylist_ThrowsReadableError()
    {
        var handler = new FakeHttpHandler { Responder = _ => FakeHttpHandler.Json("""{"code":404}""") };
        using var http = new SearchHttpClient(handler);
        var client = new NeteasePlaylistClient(http);
        var ex = await Assert.ThrowsAsync<PlaylistImportException>(async () => await client.FetchByIdAsync("404"));
        Assert.Contains("不存在或未公开", ex.Message);
    }

    // ---- QQ 客户端（移动端 qzone 变体解析）----

    [Fact]
    public async Task Qq_FetchById_ParsesDissnameAndSongs()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request =>
            {
                Assert.Contains("disstid=7707261125", request.RequestUri!.ToString());
                return FakeHttpHandler.Json("""
                    {"code":0,"cdlist":[{
                        "disstid":7707261125,"dissname":"甜度爆表","total_song_num":2,
                        "songlist":[
                            {"songname":"你的","singer":[{"name":"DouDou"},{"name":"Viva宋佩豫"}],
                             "songmid":"002xTzGb2UBQRk","songid":127404639,"type":0,
                             "interval":163,"albummid":"00ABC","albumid":123,"albumname":"专辑",
                             "size128":3000000,"size320":6000000,"sizeflac":20000000},
                            {"songname":"俗气且油腻","singer":[{"name":"歌手乙"}],
                             "songmid":"003abc","songid":2,"type":0,"interval":200,
                             "albummid":"00DEF","albumid":456,"albumname":"专辑二"}
                        ]}]}
                    """);
            },
        };
        using var http = new SearchHttpClient(handler, userAgent: QqPlaylistClient.MobileUserAgent);
        var client = new QqPlaylistClient(http);
        var imported = await client.FetchByIdAsync("7707261125");

        Assert.Equal("甜度爆表", imported.Name);
        Assert.Equal(PlaylistSource.Qqmusic, imported.Platform);
        var first = imported.Songs[0];
        Assert.Equal("你的", first.Name);
        Assert.Equal("DouDou/Viva宋佩豫", first.Singer);
        Assert.Equal("002xTzGb2UBQRk", first.Candidate!.SongMid);
        Assert.Equal(127404639, first.Candidate.SongId);
        Assert.Equal("02:43", first.Candidate.Interval);
        // 歌单行无 file.media_mid：strMediaMid 回落 songmid
        Assert.Equal("002xTzGb2UBQRk", first.Candidate.StrMediaMid);
        Assert.Contains("128k", first.Candidate.Types.Select(t => t.Type));
    }

    // ---- 酷狗客户端（v3 info + 分页 song 解析）----

    [Fact]
    public async Task Kugou_FetchById_ParsesInfoPaginatedSongsAndFilenameSplit()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains("/special/info"))
                {
                    return FakeHttpHandler.Json("""{"status":1,"data":{"specialid":8268942,"specialname":"2026伤感情歌","songcount":3}}""");
                }

                // 分页：第 1 页 2 首（pagesize=100 未满也不会终止——total=3 驱动翻页）
                if (url.Contains("page=1"))
                {
                    return FakeHttpHandler.Json("""
                        {"status":1,"data":{"total":3,"info":[
                            {"filename":"暴躁默 - 解药","hash":"H1","320hash":"H2","sqhash":"H3","duration":268,"album_id":"A1"},
                            {"filename":"无分隔歌手名","hash":"H4","duration":100,"album_id":"A2"}
                        ]}}
                        """);
                }

                return FakeHttpHandler.Json("""
                    {"status":1,"data":{"total":3,"info":[
                        {"filename":"歌手丙 - 第三首歌","hash":"H5","duration":150,"album_id":"A3"}
                    ]}}
                    """);
            },
        };
        using var http = new SearchHttpClient(handler);
        var client = new KugouPlaylistClient(http);
        var imported = await client.FetchByIdAsync("8268942");

        Assert.Equal("2026伤感情歌", imported.Name);
        Assert.Equal(3, imported.Songs.Count);

        var first = imported.Songs[0];
        Assert.Equal("解药", first.Name); // filename 按首个 " - " 拆分
        Assert.Equal("暴躁默", first.Singer);
        Assert.Equal("H1", first.Candidate!.SongMid);
        Assert.Equal("H1", first.Candidate.Hash);
        Assert.Equal("04:28", first.Candidate.Interval);
        Assert.Contains("flac", first.Candidate.Types.Select(t => t.Type));

        // filename 无 " - "：整串作歌名、歌手留空
        Assert.Equal("无分隔歌手名", imported.Songs[1].Name);
        Assert.Equal("", imported.Songs[1].Singer);
    }

    // ---- 短链解析（302 链 + HTML 兜底）----

    [Fact]
    public async Task ShortLinkResolver_FollowsRedirectChain_ExtractsFromFinalUrl()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains("163cn.tv"))
                {
                    return new HttpResponseMessage(HttpStatusCode.Found)
                    {
                        Headers = { Location = new Uri("https://music.163.com/playlist?id=7530558459&userid=1") },
                    };
                }

                return FakeHttpHandler.Json("{}");
            },
        };

        var id = await ShortLinkResolver.ResolveAsync(
            "https://163cn.tv/AbCdE", ShareLinkParser.ExtractNeteasePlaylistId, handler);
        Assert.Equal("7530558459", id);
    }

    [Fact]
    public async Task ShortLinkResolver_FallsBackToBodyExtraction()
    {
        // 短链 200 直接回 HTML（meta/JS 跳转无 HTTP Location）——从响应体提取
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<script>window.location.href='https://music.163.com/playlist?id=123456'</script>",
                    Encoding.UTF8, "text/html"),
            },
        };

        var id = await ShortLinkResolver.ResolveAsync(
            "https://163cn.tv/AbCdE", ShareLinkParser.ExtractNeteasePlaylistId, handler);
        Assert.Equal("123456", id);
    }

    // ---- 编排与落库（去重 + 同源对账）----

    private static async Task<SqliteStorageEngine> OpenStoreAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-import-{Guid.NewGuid():N}", "test.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var engine = new SqliteStorageEngine(path);
        await engine.OpenAsync();
        return engine;
    }

    [Fact]
    public async Task Importer_Upsert_DedupsBySongKey_AndKeepsSourceId()
    {
        await using var store = await OpenStoreAsync();
        var importer = new PlaylistImporter(store, Array.Empty<IPlaylistImportClient>());
        var imported = new ImportedPlaylist(PlaylistSource.Netease, "青春", "7530558459",
        [
            new ImportedSong("晴天", "周杰伦", null),
            new ImportedSong("晴天", "周杰伦", null),                       // 重复
            new ImportedSong("晴天 (Live)", "周杰伦", null),                // SongKey 归一化后不同 → 保留
            new ImportedSong("海阔天空", "Beyond", null),
        ]);

        var (playlist, added, duplicates) = await importer.UpsertAsync(imported);

        Assert.Equal(PlaylistSource.Netease, playlist.Source);
        Assert.Equal("7530558459", playlist.SourceId);
        Assert.Equal(3, added);
        Assert.Equal(1, duplicates);

        var songs = await store.ListPlaylistSongsAsync(playlist.Id);
        Assert.Equal(3, songs.Count);
        Assert.Equal("晴天", songs[0].Name);
    }

    [Fact]
    public async Task Importer_Upsert_Reimport_SameSourceId_UpdatesInPlace()
    {
        await using var store = await OpenStoreAsync();
        var importer = new PlaylistImporter(store, Array.Empty<IPlaylistImportClient>());

        var first = await importer.UpsertAsync(new ImportedPlaylist(
            PlaylistSource.Qqmusic, "旧名", "7707261125",
            [new ImportedSong("旧歌", "歌手", null)]));
        await store.SetIdlePlaylistAsync(first.Playlist.Id); // 用户已标记空闲播放源

        var second = await importer.UpsertAsync(new ImportedPlaylist(
            PlaylistSource.Qqmusic, "新名", "7707261125",
            [new ImportedSong("新歌一", "歌手", null), new ImportedSong("新歌二", "歌手", null)]));

        Assert.Equal(first.Playlist.Id, second.Playlist.Id); // 同源对账：原歌单更新
        Assert.Equal("新名", second.Playlist.Name);          // 名称刷新
        Assert.Equal(2, second.Added);

        // 空闲标记与播放模式保留（曲目替换不动元数据）；不产生重复歌单
        var idle = await store.GetIdlePlaylistAsync();
        Assert.NotNull(idle);
        Assert.Equal(first.Playlist.Id, idle.Id);
        Assert.Single(await store.ListPlaylistsAsync());
        var songs = await store.ListPlaylistSongsAsync(first.Playlist.Id);
        Assert.Equal(["新歌一", "新歌二"], songs.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task Importer_ImportAsync_InvalidText_ThrowsReadableError()
    {
        // 解析失败在任何存储访问之前抛出（store 不会被动到）
        var importer = new PlaylistImporter(null!, Array.Empty<IPlaylistImportClient>());
        var ex = await Assert.ThrowsAsync<PlaylistImportException>(
            async () => await importer.ImportAsync("没有链接的文本"));
        Assert.Contains("未识别出歌单分享链接", ex.Message);
    }
}
