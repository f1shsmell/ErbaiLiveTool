using System.Text.Json;
using Erbai.Connector.LxMusic;
using Erbai.Contracts.Players;

namespace Erbai.Connector.Tests;

/// <summary>
/// lxmusic 连接器（官方三通道封装，docs/04 §1.3；00 简报：自动化测试全走
/// fake 不打真网）：PlaySelected 构造 music/searchPlay Scheme URL（自搜自播，
/// source 归一；参考 blive-vod-fork）、Pause 必须走 HTTP、Next/Resume 按
/// use_http_control 分流、不可编程命令 Unsupported、search 只开搜索页、
/// /status 快照、SSE 订阅事件。
/// </summary>
public class LxMusicConnectorTests
{
    private static (LxMusicConnector Connector, List<string> Launched) CreateConnector(
        FakeLxHandler handler, LxMusicOptions? options = null)
    {
        var launched = new List<string>();
        var connector = new LxMusicConnector(options ?? new LxMusicOptions(), handler, uri =>
        {
            launched.Add(uri);
            return Task.CompletedTask;
        });
        return (connector, launched);
    }

    private static (bool Successful, PlayerOutcome Outcome, string Message) ParseResult(JsonElement result)
    {
        using var doc = JsonDocument.Parse(result.GetRawText());
        var root = doc.RootElement;
        var outcome = root.TryGetProperty("outcome", out var o) ? o.GetString() : "indeterminate";
        var parsed = outcome switch
        {
            "accepted" => PlayerOutcome.Accepted,
            "applied" => PlayerOutcome.Applied,
            "verified" => PlayerOutcome.Verified,
            "indeterminate" => PlayerOutcome.Indeterminate,
            "rejected" => PlayerOutcome.Rejected,
            "unsupported" => PlayerOutcome.Unsupported,
            _ => PlayerOutcome.Indeterminate,
        };
        return (PlayerOperationResult.IsSuccessful(parsed), parsed,
            root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
    }

    private static string UriData(string uri)
    {
        // lxmusic://music/play?data=... → 解码 data 参数
        var query = uri[(uri.IndexOf('?') + 1)..];
        return Uri.UnescapeDataString(query["data=".Length..]);
    }

    [Fact]
    public async Task PlaySelected_LaunchesMusicSearchPlayUrl_WithNativeDataPayload()
    {
        var handler = new FakeLxHandler();
        var (connector, launched) = CreateConnector(handler);
        var track = new PlayerTrack
        {
            Platform = "kugou",
            Title = "晴天",
            Artist = "周杰伦",
            Id = "H1",
            Album = "叶惠美",
            NativeData = JsonSerializer.Serialize(new
            {
                source = "kugou",
                name = "晴天",
                singer = "周杰伦",
                songmid = "H1",
                img = "http://img/1.jpg",
                albumId = "A1",
                interval = "04:25",
                albumName = "叶惠美",
                types = new[] { new { type = "128k", size = "", hash = "H1" }, new { type = "320k", size = "", hash = "H2" } },
                hash = "H1",
            }),
        };

        var result = await connector.ExecuteAsync(PlayerCommand.PlaySelected, track, CancellationToken.None);

        Assert.True(ParseResult(result).Successful);
        var uri = Assert.Single(launched);
        Assert.StartsWith("lxmusic://music/searchPlay?data=", uri);
        using var doc = JsonDocument.Parse(UriData(uri));
        var root = doc.RootElement;
        Assert.Equal("kg", root.GetProperty("source").GetString()); // kugou → kg 归一
        Assert.Equal("晴天", root.GetProperty("name").GetString());
        Assert.Equal("周杰伦", root.GetProperty("singer").GetString());
        Assert.Equal("04:25", root.GetProperty("interval").GetString());
        Assert.Equal("叶惠美", root.GetProperty("albumName").GetString());
        Assert.False(root.GetProperty("playLater").GetBoolean()); // 立即播放（自有队列节奏）
    }

    /// <summary>
    /// 回归（自搜自播语义）：NativeData 来自 SongSearchResult 序列化——
    /// 线格式属性名（JsonPropertyName 小写驼峰）必须能被 searchPlay 载荷读回
    /// （source/name/singer/albumName/interval），由 lxmusic 内部自搜第一可播
    /// 候选（参考 blive-vod-fork song_handler 的 music_searchPlay 语义）。
    /// </summary>
    [Fact]
    public async Task PlaySelected_SongSearchResultNativeData_ReadsSearchPlayFields()
    {
        var handler = new FakeLxHandler();
        var (connector, launched) = CreateConnector(handler);
        var search = new Erbai.Modules.SongRequest.Search.SongSearchResult
        {
            Source = "kugou",
            Name = "晴天",
            Singer = "周杰伦",
            SongMid = "H1",
            AlbumName = "叶惠美",
            Interval = "04:25",
            Types =
            [
                new Erbai.Modules.SongRequest.Search.SongType { Type = "128k", Hash = "H1" },
                new Erbai.Modules.SongRequest.Search.SongType { Type = "320k", Hash = "H2" },
            ],
            Hash = "H1",
        };
        var track = new PlayerTrack
        {
            Platform = search.Source,
            Title = search.Name,
            Artist = search.Singer,
            Id = search.SongMid,
            NativeData = JsonSerializer.Serialize(search),
        };

        await connector.ExecuteAsync(PlayerCommand.PlaySelected, track, CancellationToken.None);

        using var doc = JsonDocument.Parse(UriData(launched[0]));
        var root = doc.RootElement;
        Assert.Equal("kg", root.GetProperty("source").GetString());
        Assert.Equal("晴天", root.GetProperty("name").GetString());
        Assert.Equal("周杰伦", root.GetProperty("singer").GetString());
        Assert.Equal("叶惠美", root.GetProperty("albumName").GetString());
        Assert.Equal("04:25", root.GetProperty("interval").GetString());
        Assert.False(root.TryGetProperty("types", out _)); // searchPlay 载荷不带音质表
        Assert.False(root.TryGetProperty("songmid", out _)); // 不硬编码 songmid
    }

    /// <summary>回归：PascalCase NativeData（旧序列化产物）也能读回 searchPlay 字段。</summary>
    [Fact]
    public async Task PlaySelected_PascalCaseNativeData_StillRead()
    {
        var handler = new FakeLxHandler();
        var (connector, launched) = CreateConnector(handler);
        var track = new PlayerTrack
        {
            Platform = "kugou",
            Title = "晴天",
            Artist = "周杰伦",
            Id = "H1",
            NativeData = JsonSerializer.Serialize(new
            {
                Source = "kugou",
                Name = "晴天",
                Singer = "周杰伦",
                SongMid = "H1",
                Interval = "04:25",
                AlbumName = "叶惠美",
                Types = new[]
                {
                    new { Type = "128k", Size = "", Hash = "H1" },
                    new { Type = "flac", Size = "", Hash = "HF" },
                },
                Hash = "H1",
            }),
        };

        await connector.ExecuteAsync(PlayerCommand.PlaySelected, track, CancellationToken.None);

        using var doc = JsonDocument.Parse(UriData(launched[0]));
        var root = doc.RootElement;
        Assert.Equal("kg", root.GetProperty("source").GetString());
        Assert.Equal("晴天", root.GetProperty("name").GetString());
        Assert.Equal("叶惠美", root.GetProperty("albumName").GetString());
        Assert.Equal("04:25", root.GetProperty("interval").GetString());
    }

    /// <summary>searchPlay 不依赖音质表：NativeData 只有最小字段也能正常发出。</summary>
    [Fact]
    public async Task PlaySelected_NativeDataMinimal_StillLaunchesSearchPlay()
    {
        var handler = new FakeLxHandler();
        var (connector, launched) = CreateConnector(handler);
        var track = new PlayerTrack
        {
            Platform = "netease",
            Title = "晴天",
            Artist = "周杰伦",
            Id = "123",
            NativeData = JsonSerializer.Serialize(new
            {
                source = "netease",
                name = "晴天",
                singer = "周杰伦",
                songmid = "123",
            }),
        };

        await connector.ExecuteAsync(PlayerCommand.PlaySelected, track, CancellationToken.None);

        using var doc = JsonDocument.Parse(UriData(launched[0]));
        var root = doc.RootElement;
        Assert.Equal("wy", root.GetProperty("source").GetString()); // netease → wy 归一
        Assert.Equal("晴天", root.GetProperty("name").GetString());
    }

    [Fact]
    public async Task PlaySelected_IdleSource_NormalizedToKw()
    {
        var handler = new FakeLxHandler();
        var (connector, launched) = CreateConnector(handler);
        var track = new PlayerTrack { Platform = "idle", Title = "晴天", Artist = "周杰伦", Id = "x" };

        await connector.ExecuteAsync(PlayerCommand.PlaySelected, track, CancellationToken.None);

        Assert.StartsWith("lxmusic://music/searchPlay?data=", launched[0]);
        using var doc = JsonDocument.Parse(UriData(launched[0]));
        Assert.Equal("kw", doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Pause_GoesThroughHttp_NotScheme()
    {
        var handler = new FakeLxHandler { Routes = { ["/pause"] = "ok" } };
        var (connector, launched) = CreateConnector(handler);

        var result = await connector.ExecuteAsync(PlayerCommand.Pause, null, CancellationToken.None);

        Assert.True(ParseResult(result).Successful);
        Assert.Contains(handler.RequestedPaths, p => p.Contains("/pause"));
        Assert.Empty(launched); // Pause 无 Scheme 端点,绝不能走 scheme
    }

    [Fact]
    public async Task Pause_HttpDisabled_Rejected()
    {
        var handler = new FakeLxHandler();
        var (connector, _) = CreateConnector(handler, new LxMusicOptions { HttpEnabled = false });

        var result = await connector.ExecuteAsync(PlayerCommand.Pause, null, CancellationToken.None);

        Assert.False(ParseResult(result).Successful);
        Assert.Contains("http", ParseResult(result).Message);
    }

    [Fact]
    public async Task Next_UseHttpControl_GoesHttp_ElseScheme()
    {
        var handler = new FakeLxHandler { Routes = { ["/skip-next"] = "ok" } };
        var (connector, _) = CreateConnector(handler, new LxMusicOptions { UseHttpControl = true });
        var result = await connector.ExecuteAsync(PlayerCommand.Next, null, CancellationToken.None);
        Assert.True(ParseResult(result).Successful);
        Assert.Contains(handler.RequestedPaths, p => p.Contains("/skip-next"));

        var handler2 = new FakeLxHandler();
        var (connector2, launched2) = CreateConnector(handler2, new LxMusicOptions { UseHttpControl = false });
        var result2 = await connector2.ExecuteAsync(PlayerCommand.Next, null, CancellationToken.None);
        Assert.True(ParseResult(result2).Successful);
        Assert.Single(launched2, uri => uri == "lxmusic://player/skipNext");
    }

    [Fact]
    public async Task UnsupportedCommands_ReturnUnsupported()
    {
        var handler = new FakeLxHandler();
        var (connector, _) = CreateConnector(handler);
        foreach (var command in new[] { PlayerCommand.InsertNext, PlayerCommand.ArmNextGuard, PlayerCommand.InterruptSelected })
        {
            var result = await connector.ExecuteAsync(command, null, CancellationToken.None);
            Assert.False(ParseResult(result).Successful);
            Assert.Equal(PlayerOutcome.Unsupported, ParseResult(result).Outcome);
        }
    }

    [Fact]
    public async Task Search_OpensSearchPage_ReturnsEmptyArray()
    {
        var handler = new FakeLxHandler();
        var (connector, launched) = CreateConnector(handler);

        var result = await connector.SearchAsync("晴天", CancellationToken.None);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(0, result.GetArrayLength());
        Assert.Single(launched, uri => uri.StartsWith("lxmusic://music/search"));
    }

    [Fact]
    public async Task Probe_ParsesStatusSnapshot()
    {
        var handler = new FakeLxHandler
        {
            Routes = { ["/status"] = """{"status":"playing","name":"晴天","singer":"周杰伦","duration":265,"progress":42}""" },
        };
        var (connector, _) = CreateConnector(handler);

        var result = await connector.ProbeAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(result.GetRawText());
        Assert.True(doc.RootElement.GetProperty("connected").GetBoolean());
        Assert.Equal("playing", doc.RootElement.GetProperty("rawStatus").GetString());
        Assert.Equal("晴天", doc.RootElement.GetProperty("current").GetProperty("title").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("progressSeconds").GetDouble());
    }

    /// <summary>
    /// 回归（用户实测"lxmusic 一首歌结束很久才从队列移除"）：落雪 /status 的
    /// status 值域是 play/pause/stop（动词原形），不在状态机词表内——不映射则
    /// 播完后 RawStatus 无法归一为 stoped，DeriveStatus 回退按 Current 推断成
    /// "playing"，队列要等总预算（时长+60s 缓冲）才结算。映射后播完即 stoped。
    /// </summary>
    [Theory]
    [InlineData("play", "playing")]
    [InlineData("pause", "paused")]
    [InlineData("stop", "stoped")]
    [InlineData("error", "error")]
    [InlineData("playing", "playing")] // 词表值原样透传
    public async Task Probe_MapsLxStatusToVocabulary(string rawStatus, string expected)
    {
        var handler = new FakeLxHandler
        {
            Routes = { ["/status"] = $$"""{"status":"{{rawStatus}}","name":"晴天","duration":265,"progress":42}""" },
        };
        var (connector, _) = CreateConnector(handler);

        var result = await connector.ProbeAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(result.GetRawText());
        Assert.Equal(expected, doc.RootElement.GetProperty("rawStatus").GetString());
    }

    [Fact]
    public async Task Probe_HttpUnavailable_ConnectedFalse()
    {
        var handler = new FakeLxHandler(); // 无 /status 路由 → 404
        var (connector, _) = CreateConnector(handler);

        var result = await connector.ProbeAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(result.GetRawText());
        Assert.False(doc.RootElement.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task WatchSnapshots_SseEventsFlow()
    {
        var handler = new FakeLxHandler();
        var sse = new SsePushStream();
        handler.SseStreams["/subscribe-player-status"] = sse;
        var (connector, _) = CreateConnector(handler, new LxMusicOptions { SseEnabled = true });

        var events = connector.WatchSnapshotsAsync(CancellationToken.None);
        var enumerator = events!.GetAsyncEnumerator(CancellationToken.None);

        // 连接后推送字段事件块：event:/data:/空行
        sse.Push("event: status");
        sse.Push("data: \"playing\"");
        sse.Push("");
        sse.Push("event: name");
        sse.Push("data: \"晴天\"");
        sse.Push("");

        var moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(moved);
        var snapshot = enumerator.Current;
        Assert.Equal("playing", snapshot.GetProperty("rawStatus").GetString());

        // 第二次字段事件 → 第二帧快照(合并了 name)
        moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(moved);
        Assert.Equal("晴天", enumerator.Current.GetProperty("current").GetProperty("title").GetString());

        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task WatchSnapshots_SseDisconnected_ReconnectsWithBackoff()
    {
        var handler = new FakeLxHandler();
        var sse = new SsePushStream();
        handler.SseStreams["/subscribe-player-status"] = sse;
        var (connector, _) = CreateConnector(handler);

        var events = connector.WatchSnapshotsAsync(CancellationToken.None);
        var enumerator = events!.GetAsyncEnumerator(CancellationToken.None);

        sse.Push("event: status");
        sse.Push("data: \"paused\"");
        sse.Push("");
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));

        // 断线(流结束)→ 退避重连:新连接再次收到事件
        sse.CloseWriter();
        await Task.Delay(1200); // 1s 重连退避
        var sse2 = new SsePushStream();
        handler.SseStreams["/subscribe-player-status"] = sse2;
        sse2.Push("event: status");
        sse2.Push("data: \"playing\"");
        sse2.Push("");

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("playing", enumerator.Current.GetProperty("rawStatus").GetString());

        await enumerator.DisposeAsync();
    }

    /// <summary>
    /// 回归（用户实测"lxmusic 一首歌结束很久才从队列移除"）：SSE 字段事件与
    /// /status 同源（落雪 status 值域 play/pause/stop），同样需要映射到词表。
    /// </summary>
    [Fact]
    public async Task WatchSnapshots_SseStopStatus_MappedToStoped()
    {
        var handler = new FakeLxHandler();
        var sse = new SsePushStream();
        handler.SseStreams["/subscribe-player-status"] = sse;
        var (connector, _) = CreateConnector(handler);

        var events = connector.WatchSnapshotsAsync(CancellationToken.None);
        var enumerator = events!.GetAsyncEnumerator(CancellationToken.None);

        sse.Push("event: status");
        sse.Push("data: \"stop\"");
        sse.Push("");

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("stoped", enumerator.Current.GetProperty("rawStatus").GetString());

        await enumerator.DisposeAsync();
    }
}
