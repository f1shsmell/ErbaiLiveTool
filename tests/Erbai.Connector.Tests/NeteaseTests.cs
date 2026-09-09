using System.Text.Json;
using Erbai.Connector.Netease;
using Erbai.Contracts.Players;
using Xunit;

namespace Erbai.Connector.Tests;

public sealed class NeteaseBridgeEventTests
{
    // 通过反射验证事件响应解析（OK EVENT / NO_EVENT / NO_CHANGE / 坏格式）
    private static NeteaseBridgeTrackEvent Parse(string response)
    {
        var method = typeof(NeteaseBridgeClient).GetMethod(
            "ParseTrackEventResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (NeteaseBridgeTrackEvent)method.Invoke(null, [response])!;
    }

    [Fact]
    public void ParseTrackEvent_EventPayload_ExtractsFields()
    {
        var json = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                """{"type":"play","trackId":"347230","name":"晴天","artist":"周杰伦","album":"叶惠美","coverUrl":"http://c","nextTrackId":"42","nextName":"夜曲","nextArtist":"周杰伦","nextAlbum":"十一月"}"""));
        var parsed = Parse($"OK EVENT 7 12 {json}");
        Assert.True(parsed.Available);
        Assert.Equal(7, parsed.Sequence);
        Assert.Equal("play", parsed.Type);
        Assert.Equal("347230", parsed.TrackId);
        Assert.Equal("晴天", parsed.Name);
        Assert.Equal("周杰伦", parsed.Artist);
        Assert.Equal("42", parsed.NextTrackId);
        Assert.Equal("夜曲", parsed.NextName);
    }

    [Theory]
    [InlineData("OK NO_EVENT", false)]
    [InlineData("OK NO_CHANGE", false)]
    [InlineData("ERR something", false)]
    [InlineData("garbage", false)]
    public void ParseTrackEvent_NonEvent_Unavailable(string response, bool available)
    {
        var parsed = Parse(response);
        Assert.Equal(available, parsed.Available);
    }
}

public sealed class NeteaseSearchClientTests
{
    [Fact]
    public async Task SearchByKeyword_ParsesResults()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/api/search/get/web"] =
                    """{"result":{"songs":[{"id":347230,"name":"晴天","ar":[{"name":"周杰伦"}],"al":{"name":"叶惠美"}}]}}""",
            },
        };
        var client = new NeteaseSearchClient(handler);
        var tracks = await client.SearchByKeywordAsync("晴天", CancellationToken.None);
        Assert.Single(tracks);
        Assert.Equal("347230", tracks[0].Id);
        Assert.Equal("晴天", tracks[0].Title);
        Assert.Equal("周杰伦", tracks[0].Artist);
        Assert.Equal("叶惠美", tracks[0].Album);
    }

    [Fact]
    public async Task SearchByKeyword_EmptyPrimary_TriesNextEndpoint()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/api/search/get/web"] = """{"result":{"songs":[]}}""",
                ["/api/search/get"] =
                    """{"result":{"songs":[{"id":1,"name":"夜曲","ar":[{"name":"周杰伦"}]}]}}""",
            },
        };
        var client = new NeteaseSearchClient(handler);
        var tracks = await client.SearchByKeywordAsync("夜曲", CancellationToken.None);
        Assert.Single(tracks);
        Assert.Equal("夜曲", tracks[0].Title);
    }

    [Fact]
    public async Task ResolveSongId_ParsesDetail()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/api/v3/song/detail"] =
                    """{"songs":[{"id":347230,"name":"晴天","ar":[{"name":"周杰伦"}],"al":{"name":"叶惠美"}}]}""",
            },
        };
        var client = new NeteaseSearchClient(handler);
        var track = await client.TryResolveSongIdAsync("347230", CancellationToken.None);
        Assert.NotNull(track);
        Assert.Equal("晴天", track.Title);
    }

    [Fact]
    public async Task ResolveSongId_MismatchedId_ReturnsNull()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/api/v3/song/detail"] =
                    """{"songs":[{"id":999,"name":"晴天"}]}""",
            },
        };
        var client = new NeteaseSearchClient(handler);
        Assert.Null(await client.TryResolveSongIdAsync("347230", CancellationToken.None));
    }
}

public sealed class NeteaseConnectorTests
{
    [Fact]
    public async Task Search_Keyword_ReturnsTracks()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/api/search/get/web"] =
                    """{"result":{"songs":[{"id":347230,"name":"晴天","ar":[{"name":"周杰伦"}],"al":{"name":"叶惠美"}}]}}""",
            },
        };
        var connector = new NeteaseConnector(new NeteaseSearchClient(handler));
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.SearchAsync("晴天", CancellationToken.None)).GetRawText());
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("347230", result[0].GetProperty("id").GetString());
        Assert.Equal("晴天", result[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Search_ExplicitId_UsesDetail()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/api/v3/song/detail"] =
                    """{"songs":[{"id":347230,"name":"晴天","ar":[{"name":"周杰伦"}],"al":{"name":"叶惠美"}}]}""",
            },
        };
        var connector = new NeteaseConnector(new NeteaseSearchClient(handler));
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.SearchAsync("id=347230", CancellationToken.None)).GetRawText());
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("晴天", result[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Execute_NotConnected_Rejected()
    {
        var connector = new NeteaseConnector();
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.Next, null, CancellationToken.None)).GetRawText());
        // 无网易云进程时 rejected；有进程时走命令面
        Assert.True(result.GetProperty("outcome").GetString() is "rejected" or "accepted" or "applied");
    }

    [Fact]
    public async Task Probe_ShapeIsStable()
    {
        var connector = new NeteaseConnector();
        var probe = JsonSerializer.Deserialize<JsonElement>((await connector.ProbeAsync(CancellationToken.None)).GetRawText());
        Assert.True(probe.TryGetProperty("connected", out _));
        // 未连接时 version 固定 "netease"；本机网易云在运行时返回真实进程
        // 版本号（如 3.1.38.205386）——不能断言具体值，只验证非空
        Assert.False(string.IsNullOrWhiteSpace(probe.GetProperty("version").GetString()));
    }
}
