using System.Text.Json;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Queue;
using Erbai.Core.Events;
using Erbai.Core.Logging;
using Erbai.Core.Storage;
using Erbai.Live.Douyin;
using Erbai.Live.Douyin.Messages;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Live.Douyin.Tests;

/// <summary>
/// 阶段 4 退出标准端到端（自动化）：fake WS 推送 {Type,Data} → DouyinLivePlugin → EventBus →
/// LiveEventBridge → 点歌命令 → 队列。参照阶段 3 的假 WS 服务器模式（docs/00 阶段 4 开工简报）。
/// </summary>
public class EndToEndTests
{
    private static string Envelope(int type, object data) =>
        JsonSerializer.Serialize(new { Type = type, Data = JsonSerializer.Serialize(data) });

    [Fact]
    public async Task 假WS推送弹幕点歌全链路()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"erbai-douyin-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        await using var server = new FakeDouyinWsServer();

        var bus = new EventBus();
        var logs = new LogBus();
        var store = new SqliteStorageEngine(Path.Combine(baseDir, "e2e.db"));
        await store.OpenAsync();

        var settings = AppConfig.CreateDefault() with { DouyinWsUrl = $"ws://127.0.0.1:{server.Port}" };
        var queue = new SongQueueService(store, bus, settings);
        // 固定假搜索：端到端只验证"fake WS 弹幕 → 点歌命令 → 队列"，不打真网
        queue.Processor = (req, _) => Task.FromResult<IReadOnlyList<SongSearchResult>?>(
            [new SongSearchResult { Source = "kugou", Name = req.SongName, Singer = req.Singer, SongMid = "e2e-mid" }]);
        var commands = new SongRequestService(queue, store, logs, new SongBlacklist(store, logs));

        using var bridgeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var bridgeTask = LiveEventBridge.RunAsync(bus, commands, logs, bridgeCts.Token);

        // 插件事件 → EventBus（App 里由平台监督者 runner 完成同样转发）
        await using var plugin = new DouyinLivePlugin(
            logs,
            reconnectDelay: TimeSpan.FromMilliseconds(200),
            allowedRoomIds: new HashSet<string> { "123456" });
        await plugin.StartAsync(settings, bridgeCts.Token);
        var forwardTask = Task.Run(async () =>
        {
            await foreach (var evt in plugin.Events.ReadAllAsync(bridgeCts.Token))
            {
                bus.Publish(evt);
            }
        });

        // 队列事件收集
        using var queueSub = bus.Subscribe<QueueEventEnvelope>(capacity: 256);
        var queueEvents = new List<string>();
        var queueTask = Task.Run(async () =>
        {
            await foreach (var envelope in queueSub.Reader.ReadAllAsync(bridgeCts.Token))
            {
                lock (queueEvents)
                {
                    queueEvents.Add(envelope.Event);
                }
            }
        });

        await Task.Delay(300); // 等 WS 握手

        // fake WS 推送一条真实形态的弹幕点歌
        await server.PushAsync(Envelope(1, new
        {
            Content = "点歌 晴天 - 周杰伦",
            WebRoomId = "123456",
            User = new { Nickname = "端到端观众", Id = "777", FansClub = new { Level = 4 } },
        }));

        // 等待 queue.added
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (queueEvents)
            {
                if (queueEvents.Contains("queue.added"))
                {
                    break;
                }
            }

            await Task.Delay(100);
        }

        var snapshot = await queue.GetSnapshotAsync();
        Assert.Contains("queue.added", queueEvents);
        Assert.Contains(snapshot.Items, i => i.Request.SongName == "晴天" &&
                                              i.Request.Nickname == "端到端观众" &&
                                              i.Request.FanLevel == 4 &&
                                              i.Request.Platform == "douyin");

        bridgeCts.Cancel();
        await queue.StopAsync();
        await store.DisposeAsync();
        try
        {
            Directory.Delete(baseDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task 粉丝团事件持久化且不产生点歌()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"erbai-douyin-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        var store = new SqliteStorageEngine(Path.Combine(baseDir, "e2e.db"));
        await store.OpenAsync();

        var bus = new EventBus();
        var logs = new LogBus();
        var settings = AppConfig.CreateDefault();
        var queue = new SongQueueService(store, bus, settings);
        var commands = new SongRequestService(queue, store, logs, new SongBlacklist(store, logs));
        var users = new Erbai.Modules.SongRequest.Services.UserService(store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var userSyncTask = Erbai.Live.Douyin.Services.DouyinUserSync.RunAsync(bus, users, logs, cts.Token);

        // 粉丝团事件（Type=7）经 EventBus → DouyinUserSync 持久化等级
        bus.Publish(new LiveEvent
        {
            Platform = DouyinMessageParser.Platform,
            RoomId = "1",
            Kind = LiveEventKind.Subscribe,
            UserId = 9001,
            Nickname = "粉丝",
            FanLevel = 6,
            Timestamp = DateTimeOffset.UtcNow,
        });

        var deadline = DateTime.UtcNow.AddSeconds(8);
        Erbai.Contracts.Storage.User? stored = null;
        while (DateTime.UtcNow < deadline)
        {
            stored = await store.GetUserAsync("douyin", "1", "9001");
            if (stored is not null)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.NotNull(stored);
        Assert.Equal(6, stored!.FanLevel);

        // 粉丝团事件不产生点歌（队列空）
        var snapshot = await queue.GetSnapshotAsync();
        Assert.Empty(snapshot.Items);

        cts.Cancel();
        await queue.StopAsync();
        await store.DisposeAsync();
        try
        {
            Directory.Delete(baseDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
