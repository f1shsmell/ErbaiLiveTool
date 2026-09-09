using System.Net;
using System.Text;
using System.Text.Json;
using Erbai.Connector.Folia;
using Erbai.Connector.Shared;
using Xunit;

namespace Erbai.Connector.Tests;

/// <summary>
/// Folia Stage 测试用 fake handler：记录 (路径, JSON body)，按路径返回预设响应。
/// </summary>
public sealed class FakeStageHandler : HttpMessageHandler
{
    public record Call(string Path, string? Body);

    public List<Call> Calls { get; } = [];

    /// <summary>路径 → 响应 JSON（null = 404）。</summary>
    public Dictionary<string, string> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>路径 → 响应状态码（Routes 命中时也按此覆盖，默认 200）。</summary>
    public Dictionary<string, HttpStatusCode> StatusCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        Calls.Add(new Call(path, body));
        if (!Routes.TryGetValue(path, out var responseBody))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var status = StatusCodes.TryGetValue(path, out var code) ? code : HttpStatusCode.OK;
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>Folia Stage 测试用 fake 套接字：预置文本队列 + 关闭标志。</summary>
public sealed class FakeFoliaSocket : IFoliaStageSocket, IDisposable
{
    private readonly Queue<string> _lines;
    private readonly object _sync = new();

    public FakeFoliaSocket(params string[] lines) => _lines = new Queue<string>(lines);

    public bool Closed { get; set; }

    public Task<(string? Text, bool Closed)> ReceiveAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_lines.Count > 0)
            {
                return Task.FromResult<(string?, bool)>((_lines.Dequeue(), false));
            }

            return Task.FromResult<(string?, bool)>((null, Closed));
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
    }
}

public sealed class FoliaStageJsonTests
{
    [Fact]
    public void ParseEvent_StatusWithDirectTrack_ExtractsTrack()
    {
        var json = """{"event":"STATUS","track":{"id":123,"name":"晴天","artist":"周杰伦","album":{"name":"叶惠美"}}}""";
        var parsed = FoliaStageJson.ParseEvent(json);
        Assert.NotNull(parsed);
        Assert.Equal("STATUS", parsed.Name);
        Assert.Equal("123", parsed.Current!.Id);
        Assert.Equal("晴天", parsed.Current.Title);
        Assert.Equal("周杰伦", parsed.Current.Artist);
        Assert.Equal("叶惠美", parsed.Current.Album);
    }

    [Fact]
    public void ParseEvent_DataTrackAndNext_ExtractsBoth()
    {
        var json = """
            {"event":"TRACK_CHANGED","data":{"track":{"id":"1","title":"A"},"next":{"id":"2","name":"B"}}}
            """;
        var parsed = FoliaStageJson.ParseEvent(json);
        Assert.NotNull(parsed);
        Assert.Equal("TRACK_CHANGED", parsed.Name);
        Assert.Equal("1", parsed.Current!.Id);
        Assert.Equal("2", parsed.Next!.Id);
        Assert.Equal("B", parsed.Next.Title);
    }

    [Fact]
    public void ParseEvent_UnknownEvent_ReturnsNull()
    {
        Assert.Null(FoliaStageJson.ParseEvent("""{"event":"HEARTBEAT"}"""));
        Assert.Null(FoliaStageJson.ParseEvent("""{"type":"OTHER"}"""));
    }

    [Fact]
    public void ParseTrack_MissingId_ReturnsNull()
    {
        Assert.Null(FoliaStageJson.ParseTrack(JsonDocument.Parse("""{"name":"x"}""").RootElement));
    }

    [Fact]
    public void ParseTrack_MissingTitle_FallsBackToSongId()
    {
        var track = FoliaStageJson.ParseTrack(JsonDocument.Parse("""{"id":42}""").RootElement);
        Assert.NotNull(track);
        Assert.Equal("歌曲 42", track.Title);
    }

    [Fact]
    public void ParseTrack_NumberId_UsesRawText()
    {
        var track = FoliaStageJson.ParseTrack(JsonDocument.Parse("""{"songId":777,"title":"T"}""").RootElement);
        Assert.NotNull(track);
        Assert.Equal("777", track.Id);
    }

    [Fact]
    public void FindSongs_SupportsNestedShapes()
    {
        Assert.True(FoliaStageJson.FindSongs(JsonDocument.Parse("""{"songs":[{"id":1}]}""").RootElement) is { });
        Assert.True(FoliaStageJson.FindSongs(JsonDocument.Parse("""{"data":{"songs":[{"id":1}]}}""").RootElement) is { });
        Assert.True(FoliaStageJson.FindSongs(JsonDocument.Parse("""{"result":{"songs":[{"id":1}]}}""").RootElement) is { });
        Assert.Null(FoliaStageJson.FindSongs(JsonDocument.Parse("""{"items":[]}""").RootElement));
    }

    [Fact]
    public void ParseArtists_ArrayAndStringForms()
    {
        var json = """{"artists":[{"name":"甲"},{"name":"乙"}]}""";
        Assert.Equal("甲/乙", FoliaStageJson.ParseArtists(JsonDocument.Parse(json).RootElement));
        var str = """{"artist":"丙"}""";
        Assert.Equal("丙", FoliaStageJson.ParseArtists(JsonDocument.Parse(str).RootElement));
    }
}

public sealed class SongQueryPolicyTests
{
    [Theory]
    [InlineData("id=123456", NeteaseSongQueryKind.ExplicitId, "123456")]
    [InlineData("ID = 42", NeteaseSongQueryKind.ExplicitId, "42")]
    [InlineData("123456789", NeteaseSongQueryKind.SuspectedId, "123456789")]
    [InlineData("晴天", NeteaseSongQueryKind.Keyword, "晴天")]
    [InlineData("12345", NeteaseSongQueryKind.Keyword, "12345")]
    public void ParseNetease_Classifies(string input, NeteaseSongQueryKind kind, string value)
    {
        var parsed = SongQueryPolicy.ParseNetease(input);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(value, parsed.Value);
    }
}
