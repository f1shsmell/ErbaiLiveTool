using System.Text.Json;
using Erbai.Connector.Folia;
using Erbai.Connector.Shared;
using Erbai.Contracts.Players;
using Xunit;

namespace Erbai.Connector.Tests;

public sealed class FoliaConnectorTests
{
    private static FoliaOptions Options(string token = "test-token", string stageUrl = "http://127.0.0.1:32107") =>
        new() { Token = token, StageUrl = stageUrl };

    private static PlayerTrack Track(string id = "123456", string title = "晴天", string artist = "周杰伦") =>
        new() { Platform = "folia", Id = id, Title = title, Artist = artist };

    [Fact]
    public async Task Activate_WithoutToken_Throws()
    {
        var connector = new FoliaConnector(Options(token: ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.ActivateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Search_Keyword_CallsStageSearch()
    {
        var stage = new FakeStageHandler
        {
            Routes =
            {
                ["/stage/player/search"] = """{"songs":[{"id":9,"name":"晴","artist":"周"}]}""",
            },
        };
        var connector = new FoliaConnector(Options(), stageHandler: stage);

        var result = await connector.SearchAsync("晴天", CancellationToken.None);
        var tracks = JsonSerializer.Deserialize<JsonElement>(result.GetRawText());
        Assert.Equal(1, tracks.GetArrayLength());
        Assert.Equal("9", tracks[0].GetProperty("id").GetString());
        Assert.Single(stage.Calls, c => c.Path == "/stage/player/search");
    }

    [Fact]
    public async Task Search_ExplicitId_UsesNeteaseDetail()
    {
        var netease = new FakeStageHandler
        {
            Routes =
            {
                ["/api/v3/song/detail"] = """{"code":200,"songs":[{"id":347230,"name":"晴天","ar":[{"name":"周杰伦"}]}]}""",
            },
        };
        // neteaseHandler 参数走 FakeStageHandler（同 HttpMessageHandler 面）
        var connector = new FoliaConnector(Options(), neteaseHandler: netease);

        var result = await connector.SearchAsync("id=347230", CancellationToken.None);
        var tracks = JsonSerializer.Deserialize<JsonElement>(result.GetRawText());
        Assert.Equal(1, tracks.GetArrayLength());
        Assert.Equal("347230", tracks[0].GetProperty("id").GetString());
        Assert.Equal("周杰伦", tracks[0].GetProperty("artist").GetString());
    }

    [Fact]
    public async Task Search_SuspectedId_NeteaseDetailWinsOverStage()
    {
        var stage = new FakeStageHandler
        {
            Routes =
            {
                ["/stage/player/search"] = """{"songs":[{"id":347230,"title":"from-stage"}]}""",
            },
        };
        var netease = new FakeStageHandler
        {
            Routes =
            {
                ["/api/v3/song/detail"] = """{"code":200,"songs":[{"id":347230,"name":"晴天","ar":[{"name":"周杰伦"}]}]}""",
            },
        };
        var connector = new FoliaConnector(Options(), stageHandler: stage, neteaseHandler: netease);

        var result = await connector.SearchAsync("347230", CancellationToken.None);
        var tracks = JsonSerializer.Deserialize<JsonElement>(result.GetRawText());
        Assert.Equal(1, tracks.GetArrayLength());
        Assert.Equal("晴天", tracks[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Execute_PlaySelected_InsertsNextThenControlsNext()
    {
        var stage = new FakeStageHandler
        {
            Routes =
            {
                ["/stage/player/queue"] = "{}",
                ["/stage/player/control"] = "{}",
            },
        };
        var connector = new FoliaConnector(Options(), stageHandler: stage);

        var result = await connector.ExecuteAsync(PlayerCommand.PlaySelected, Track(), CancellationToken.None);
        var outcome = JsonSerializer.Deserialize<JsonElement>(result.GetRawText()).GetProperty("outcome").GetString();
        Assert.Equal("accepted", outcome);

        var queueCall = stage.Calls.Single(c => c.Path == "/stage/player/queue");
        Assert.Contains("insert-next", queueCall.Body);
        Assert.Contains("123456", queueCall.Body);
        var controlCall = stage.Calls.Single(c => c.Path == "/stage/player/control");
        Assert.Contains("\"next\"", controlCall.Body);
    }

    [Fact]
    public async Task Execute_InsertNext_InsertsAndArmsGuard()
    {
        var stage = new FakeStageHandler
        {
            Routes =
            {
                ["/stage/player/queue"] = "{}",
                ["/stage/player/control"] = "{}",
            },
        };
        var connector = new FoliaConnector(Options(), stageHandler: stage);

        var result = await connector.ExecuteAsync(PlayerCommand.InsertNext, Track(), CancellationToken.None);
        var outcome = JsonSerializer.Deserialize<JsonElement>(result.GetRawText()).GetProperty("outcome").GetString();
        Assert.Equal("accepted", outcome);
        Assert.Single(stage.Calls, c => c.Path == "/stage/player/queue");
        Assert.DoesNotContain(stage.Calls, c => c.Path == "/stage/player/control");
    }

    [Fact]
    public async Task Execute_PauseAndNext_UseControl()
    {
        var stage = new FakeStageHandler
        {
            Routes =
            {
                ["/stage/player/control"] = "{}",
            },
        };
        var connector = new FoliaConnector(Options(), stageHandler: stage);

        var pause = JsonSerializer.Deserialize<JsonElement>(
            (await connector.ExecuteAsync(PlayerCommand.Pause, null, CancellationToken.None)).GetRawText());
        Assert.Equal("accepted", pause.GetProperty("outcome").GetString());
        Assert.Contains("\"pause\"", stage.Calls.Single(c => c.Path == "/stage/player/control").Body);

        var next = JsonSerializer.Deserialize<JsonElement>(
            (await connector.ExecuteAsync(PlayerCommand.Next, null, CancellationToken.None)).GetRawText());
        Assert.Equal("accepted", next.GetProperty("outcome").GetString());
        Assert.Contains("\"next\"", stage.Calls.Last(c => c.Path == "/stage/player/control").Body);
    }

    [Fact]
    public async Task Execute_UnknownCommand_Unsupported()
    {
        var connector = new FoliaConnector(Options());
        var result = await connector.ExecuteAsync(PlayerCommand.InterruptSelected, Track(), CancellationToken.None);
        var outcome = JsonSerializer.Deserialize<JsonElement>(result.GetRawText()).GetProperty("outcome").GetString();
        Assert.Equal("unsupported", outcome);
    }

    [Fact]
    public async Task Execute_PlaySelected_InvalidTrack_Rejected()
    {
        var connector = new FoliaConnector(Options());
        var result = await connector.ExecuteAsync(PlayerCommand.PlaySelected, Track(id: "abc"), CancellationToken.None);
        var outcome = JsonSerializer.Deserialize<JsonElement>(result.GetRawText()).GetProperty("outcome").GetString();
        Assert.Equal("rejected", outcome);
    }

    [Fact]
    public async Task WatchSnapshots_StageEvent_ProducesSnapshot()
    {
        var socket = new FakeFoliaSocket(
            """{"event":"STATUS","track":{"id":1,"title":"T1","artist":"A1"}}""",
            """{"event":"TRACK_CHANGED","data":{"track":{"id":2,"title":"T2"},"next":{"id":3,"name":"T3"}}}""");
        var connector = new FoliaConnector(
            Options(),
            socketFactory: (_, _) => socket);

        var events = connector.WatchSnapshotsAsync(CancellationToken.None);
        Assert.NotNull(events);
        var snapshots = new List<JsonElement>();
        await foreach (var snapshot in events!)
        {
            snapshots.Add(snapshot);
            if (snapshots.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(2, snapshots.Count);
        Assert.Equal("T1", snapshots[0].GetProperty("current").GetProperty("title").GetString());
        Assert.Equal("T2", snapshots[1].GetProperty("current").GetProperty("title").GetString());
        Assert.Equal("T3", snapshots[1].GetProperty("next").GetProperty("title").GetString());
        Assert.Equal("track", snapshots[1].GetProperty("nextSource").GetString());
    }

    [Fact]
    public async Task Probe_AfterStageEvents_Connected()
    {
        var socket = new FakeFoliaSocket("""{"event":"STATUS","track":{"id":1,"title":"T"}}""");
        var connector = new FoliaConnector(Options(), socketFactory: (_, _) => socket);

        _ = connector.ActivateAsync(CancellationToken.None);
        var events = connector.WatchSnapshotsAsync(CancellationToken.None)!;
        await foreach (var _ in events)
        {
            break; // 消费第一个事件
        }

        var probe = JsonSerializer.Deserialize<JsonElement>((await connector.ProbeAsync(CancellationToken.None)).GetRawText());
        Assert.True(probe.GetProperty("connected").GetBoolean());
        Assert.Equal("T", probe.GetProperty("current").GetProperty("title").GetString());
    }
}

public sealed class GuardedNextMonitorTests
{
    private static PlayerTrack Track(string id, string title) => new() { Platform = "folia", Id = id, Title = title };

    [Fact]
    public void Arm_NoCurrent_Rejected()
    {
        var monitor = new GuardedNextMonitor();
        var armed = monitor.Arm(null, Track("2", "B"), _ => Task.FromResult<PlayerTrack?>(null), (_, _) => Task.FromResult(""), CancellationToken.None, out var message);
        Assert.False(armed);
        Assert.Contains("不可识别", message);
    }

    [Fact]
    public async Task Arm_HitTarget_CompletesWithoutTakeOver()
    {
        var monitor = new GuardedNextMonitor();
        var initial = Track("1", "A");
        var target = Track("2", "B");
        var takeOverCalled = false;
        var readCurrent = Task.FromResult<PlayerTrack?>(target); // 下一次轮询即命中

        var armed = monitor.Arm(initial, target, _ => readCurrent, (_, _) =>
        {
            takeOverCalled = true;
            return Task.FromResult("takeover");
        }, CancellationToken.None, out _);
        Assert.True(armed);

        await Task.Delay(200);
        Assert.False(takeOverCalled);
        Assert.Contains("命中", monitor.Status);
    }

    [Fact]
    public async Task Arm_WrongTrack_InvokesTakeOver()
    {
        var monitor = new GuardedNextMonitor();
        var initial = Track("1", "A");
        var target = Track("2", "B");
        var takeOverCalled = false;
        var readCurrent = Task.FromResult<PlayerTrack?>(Track("3", "C"));

        var armed = monitor.Arm(initial, target, _ => readCurrent, (_, _) =>
        {
            takeOverCalled = true;
            return Task.FromResult("兜底完成");
        }, CancellationToken.None, out _);
        Assert.True(armed);

        await Task.Delay(200);
        Assert.True(takeOverCalled);
        Assert.Contains("兜底完成", monitor.Status);
    }

    [Fact]
    public void Cancel_StopsMonitoring()
    {
        var monitor = new GuardedNextMonitor();
        var initial = Track("1", "A");
        monitor.Arm(initial, Track("2", "B"), _ => Task.FromResult<PlayerTrack?>(null), (_, _) => Task.FromResult(""), CancellationToken.None, out _);
        monitor.Cancel("已取消");
        Assert.Equal("已取消", monitor.Status);
    }
}
