using System.Net;
using System.Text;
using System.Text.Json;
using Erbai.Connector.Kugou;
using Erbai.Connector.Shared;
using Erbai.Contracts.Players;
using Xunit;

namespace Erbai.Connector.Tests;

/// <summary>酷狗测试 fake native：可编程窗口/状态/命令响应。</summary>
public sealed class FakeKugouNative : IKugouNativeControl
{
    public bool HasMainWindow { get; set; } = true;

    public bool HasValidIpcEndpoint { get; set; } = true;

    public string WindowTitle { get; set; } = "晴天 - 周杰伦 - 酷狗音乐";

    public string IniTitle { get; set; } = "晴天";

    public int SendCommandCalls { get; private set; }

    public int SendInsertNextCalls { get; private set; }

    public KugouAppCommand? LastCommand { get; private set; }

    public string? LastInsertPayload { get; private set; }

    /// <summary>SendCommand(NextTrack) 时切换到该标题（模拟切歌）。</summary>
    public string? NextTrackTitle { get; set; }

    public (nint, int)? FindMainWindow() => HasMainWindow ? (new nint(0x1234), 4242) : null;

    public (nint, int)? InspectIpcEndpoint() =>
        HasValidIpcEndpoint ? (new nint(0x5678), 4242) : null;

    public KugouPlaybackState ReadPlaybackState()
    {
        // 真实流程：窗口标题先经 ticker 提取（去 " - 酷狗音乐" 后缀），再解析 歌手/歌名
        var liveTitle = KugouNativeApi.ExtractTitleFromTicker(WindowTitle);
        var rawTitle = string.IsNullOrWhiteSpace(liveTitle) ? IniTitle : liveTitle;
        var (artist, parsedTitle) = KugouNativeApi.ParseArtistAndTitle(rawTitle);
        return new KugouPlaybackState(
            string.IsNullOrWhiteSpace(liveTitle) ? "KuGou.ini" : "WindowTitle",
            WindowTitle,
            rawTitle,
            artist,
            parsedTitle,
            1, 1, 1, 0);
    }

    public KugouCommandResult SendCommand(KugouAppCommand command)
    {
        SendCommandCalls++;
        LastCommand = command;
        if (command == KugouAppCommand.NextTrack && NextTrackTitle is not null)
        {
            WindowTitle = NextTrackTitle;
        }

        return new KugouCommandResult(command.ToString(), "fake", true, new nint(0x1234), 4242, null);
    }

    public KugouCommandResult SendInsertNext(nint targetHandle, string payload)
    {
        SendInsertNextCalls++;
        LastInsertPayload = payload;
        return new KugouCommandResult("InsertNext", "fake", true, targetHandle, 4242, null);
    }
}

public sealed class KugouNativeApiTests
{
    [Theory]
    [InlineData("晴天 - 周杰伦 - 酷狗音乐", "周杰伦", "晴天")]
    [InlineData("晴天 - 周杰伦", "周杰伦", "晴天")]
    [InlineData("晴天", "", "晴天")]
    [InlineData("", "", "")]
    public void ParseArtistAndTitle_Variants(string raw, string artist, string title)
    {
        var (a, t) = KugouNativeApi.ParseArtistAndTitle(raw);
        Assert.Equal(artist, a);
        Assert.Equal(title, t);
    }

    [Theory]
    [InlineData("晴天 - 酷狗音乐", "晴天")]
    [InlineData("晴天 - 酷狗音乐 周杰伦", "周杰伦晴天")]
    [InlineData("酷狗音乐 晴天 -", "晴天")]
    [InlineData("某个窗口标题", "")]
    public void ExtractTitleFromTicker_Variants(string windowTitle, string expected)
    {
        Assert.Equal(expected, KugouNativeApi.ExtractTitleFromTicker(windowTitle));
    }
}

public sealed class KugouSongQueryTests
{
    [Theory]
    [InlineData("chain=AbCdEf123456", KugouSongQueryKind.Chain, "AbCdEf123456")]
    [InlineData("https://m.kugou.com/share/song.html?chain=AbCdEf123456&x=1", KugouSongQueryKind.Chain, "AbCdEf123456")]
    [InlineData("AbCdEf123456", KugouSongQueryKind.Chain, "AbCdEf123456")]
    [InlineData("#12345#", KugouSongQueryKind.ShareCode, "12345")]
    [InlineData("12345", KugouSongQueryKind.ShareCode, "12345")]
    [InlineData("晴天", KugouSongQueryKind.Keyword, "晴天")]
    public void ParseKugou_Classifies(string input, KugouSongQueryKind kind, string value)
    {
        var parsed = SongQueryPolicy.ParseKugou(input);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(value, parsed.Value);
    }
}

public sealed class KugouInsertPayloadTests
{
    [Fact]
    public void Build_ProducesEnvelopeWithUppercaseHash()
    {
        var payload = KugouInsertPayload.Build(
            """{"songname":"晴天","singername":"周杰伦","hash":"abc123","timelength":200000,"filesize":"123","album_name":"叶惠美"}""");
        Assert.NotNull(payload);
        Assert.Equal("ABC123", payload.Hash);
        using var doc = JsonDocument.Parse(payload.Json);
        Assert.Equal("UnifiedPlayerControlPoc", doc.RootElement.GetProperty("Source").GetString());
        Assert.Equal("1", doc.RootElement.GetProperty("Count").GetString());
        var file = doc.RootElement.GetProperty("Files")[0];
        Assert.Equal("晴天", file.GetProperty("songname").GetString());
        Assert.Equal("周杰伦 - 晴天", file.GetProperty("filename").GetString());
        Assert.Equal("200000", file.GetProperty("duration").GetString());
        Assert.Equal("ABC123", file.GetProperty("hash").GetString());
    }

    [Fact]
    public void Build_InvalidJson_ReturnsNull()
    {
        Assert.Null(KugouInsertPayload.Build("not-json"));
    }
}

public sealed class KugouConnectorTests
{
    private static string NativeData() =>
        """{"songname":"晴天","singername":"周杰伦","hash":"abc123","timelength":200000}""";

    private static PlayerTrack Track(string title = "晴天", string artist = "周杰伦") =>
        new() { Platform = "kugou", Id = "hash:ABC123", Title = title, Artist = artist, NativeData = NativeData() };

    [Fact]
    public async Task Probe_NoWindow_Disconnected()
    {
        var native = new FakeKugouNative { HasMainWindow = false };
        var connector = new KugouConnector(native);
        var probe = JsonSerializer.Deserialize<JsonElement>((await connector.ProbeAsync(CancellationToken.None)).GetRawText());
        Assert.False(probe.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Probe_WithWindow_ParsesCurrentTrack()
    {
        var connector = new KugouConnector(new FakeKugouNative());
        var probe = JsonSerializer.Deserialize<JsonElement>((await connector.ProbeAsync(CancellationToken.None)).GetRawText());
        Assert.True(probe.GetProperty("connected").GetBoolean());
        Assert.Equal("晴天", probe.GetProperty("current").GetProperty("title").GetString());
        Assert.Equal("周杰伦", probe.GetProperty("current").GetProperty("artist").GetString());
    }

    [Fact]
    public async Task Search_Keyword_CallsSignedApi()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/api/v3/search/song"] =
                    """{"data":{"info":[{"hash":"ABC","audio_id":9,"songname":"晴天","singername":"周杰伦","album_name":"叶惠美"}]}}""",
            },
        };
        var connector = new KugouConnector(new FakeKugouNative(), new KugouSearchClient(handler));
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.SearchAsync("晴天", CancellationToken.None)).GetRawText());
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("9", result[0].GetProperty("id").GetString());
        Assert.Equal("晴天", result[0].GetProperty("title").GetString());
        Assert.NotNull(result[0].GetProperty("nativeData").GetString());
    }

    [Fact]
    public async Task Search_Chain_ParsesPhpParam()
    {
        var html = "<script>var phpParam = {\"song_info\":{\"data\":{\"hash\":\"XYZ\",\"songname\":\"夜曲\",\"singername\":\"周杰伦\"}}};</script>";
        var handler = new FakeStageHandler { Routes = { ["/share/song.html"] = html } };
        // share 页走 m.kugou.com 域：fake handler 按路径匹配即可
        var connector = new KugouConnector(new FakeKugouNative(), new KugouSearchClient(handler));
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.SearchAsync("chain=AbCdEf123456", CancellationToken.None)).GetRawText());
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("夜曲", result[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Execute_Next_WhenTrackChanges_Applied()
    {
        var native = new FakeKugouNative { NextTrackTitle = "夜曲 - 周杰伦 - 酷狗音乐" };
        var connector = new KugouConnector(native);
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.Next, null, CancellationToken.None)).GetRawText());
        Assert.Equal("applied", result.GetProperty("outcome").GetString());
        Assert.Equal(KugouAppCommand.NextTrack, native.LastCommand);
    }

    [Fact]
    public async Task Execute_Pause_Unsupported()
    {
        var connector = new KugouConnector(new FakeKugouNative());
        var pause = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.Pause, null, CancellationToken.None)).GetRawText());
        Assert.Equal("unsupported", pause.GetProperty("outcome").GetString());
        var resume = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.Resume, null, CancellationToken.None)).GetRawText());
        Assert.Equal("unsupported", resume.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Execute_PlaySelected_InsertsAdvancesAndConfirms()
    {
        var native = new FakeKugouNative
        {
            WindowTitle = "夜曲 - 周杰伦 - 酷狗音乐",
            NextTrackTitle = "晴天 - 周杰伦 - 酷狗音乐",
        };
        var connector = new KugouConnector(native);
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.PlaySelected, Track(), CancellationToken.None)).GetRawText());
        Assert.Equal("verified", result.GetProperty("outcome").GetString());
        Assert.Equal(1, native.SendInsertNextCalls);
        Assert.NotNull(native.LastInsertPayload);
        Assert.Equal(KugouAppCommand.NextTrack, native.LastCommand);
    }

    [Fact]
    public async Task Execute_PlaySelected_AlreadyCurrent_VerifiedWithoutInsert()
    {
        var native = new FakeKugouNative();
        var connector = new KugouConnector(native);
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.PlaySelected, Track(), CancellationToken.None)).GetRawText());
        Assert.Equal("verified", result.GetProperty("outcome").GetString());
        Assert.Equal(0, native.SendInsertNextCalls);
    }

    [Fact]
    public async Task Execute_PlaySelected_NoIpcEndpoint_Rejected()
    {
        var native = new FakeKugouNative { HasValidIpcEndpoint = false };
        native.WindowTitle = "夜曲 - 周杰伦 - 酷狗音乐"; // 目标不是当前曲
        var connector = new KugouConnector(native);
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.PlaySelected, Track(), CancellationToken.None)).GetRawText());
        Assert.Equal("rejected", result.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Execute_InsertNext_InsertsAndArmsGuard()
    {
        var native = new FakeKugouNative { WindowTitle = "夜曲 - 周杰伦 - 酷狗音乐" };
        var connector = new KugouConnector(native);
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.InsertNext, Track(), CancellationToken.None)).GetRawText());
        Assert.Equal("accepted", result.GetProperty("outcome").GetString());
        Assert.Equal(1, native.SendInsertNextCalls);
    }

    [Fact]
    public async Task Execute_InsertNext_WithoutNativeData_Unsupported()
    {
        var connector = new KugouConnector(new FakeKugouNative());
        var track = new PlayerTrack { Platform = "kugou", Id = "1", Title = "T", NativeData = null };
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.InsertNext, track, CancellationToken.None)).GetRawText());
        Assert.Equal("unsupported", result.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Execute_NotConnected_Rejected()
    {
        var connector = new KugouConnector(new FakeKugouNative { HasMainWindow = false });
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.Next, null, CancellationToken.None)).GetRawText());
        Assert.Equal("rejected", result.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task WatchSnapshots_EmitsOnTrackChange()
    {
        var native = new FakeKugouNative();
        var connector = new KugouConnector(native);
        var events = connector.WatchSnapshotsAsync(CancellationToken.None)!;
        var first = await events.FirstAsyncWithTimeout();
        Assert.Equal("晴天", first.GetProperty("current").GetProperty("title").GetString());

        native.WindowTitle = "夜曲 - 周杰伦 - 酷狗音乐";
        var second = await events.FirstAsyncWithTimeout();
        Assert.Equal("夜曲", second.GetProperty("current").GetProperty("title").GetString());
    }
}

internal static class AsyncEnumerableExtensions
{
    public static async Task<T> FirstAsyncWithTimeout<T>(this IAsyncEnumerable<T> source)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in source.WithCancellation(cts.Token))
        {
            return item;
        }

        throw new InvalidOperationException("no item");
    }
}
