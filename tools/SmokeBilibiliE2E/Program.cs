// 阶段3 退出标准冒烟：真实 B站房间 → 弹幕 → LiveEvent → 点歌命令 → 队列（不打真搜索，固定假处理器）。
// 用法: dotnet run --project tools/SmokeBilibiliE2E -- <房间号> [超时秒]
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Queue;
using Erbai.Core.Events;
using Erbai.Core.Logging;
using Erbai.Core.Storage;
using Erbai.Live.Bilibili;
using Erbai.Live.Bilibili.Protocol;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;

var roomId = args.Length > 0 ? args[0] : "7777";
var timeoutSeconds = args.Length > 1 ? int.Parse(args[1]) : 150;

var baseDir = Path.Combine(Path.GetTempPath(), "erbai-smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(baseDir);

var bus = new EventBus();
var logs = new LogBus();
var store = new SqliteStorageEngine(Path.Combine(baseDir, "smoke.db"));
await store.OpenAsync();

var queue = new SongQueueService(store, bus, AppConfig.CreateDefault());
// 固定假搜索：冒烟只验证"真实弹幕→点歌命令→队列"，不依赖三源真网
queue.Processor = (req, _) => Task.FromResult<IReadOnlyList<SongSearchResult>?>(
    [new SongSearchResult { Source = "kugou", Name = req.SongName, Singer = req.Singer, SongMid = "smoke-mid" }]);
var commands = new SongRequestService(queue, store, logs, new SongBlacklist(store, logs));

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
var bridgeTask = LiveEventBridge.RunAsync(bus, commands, logs, cts.Token);

// 插件日志诊断输出
using var logSub = logs.Subscribe(capacity: 2048);
_ = Task.Run(async () =>
{
    await foreach (var entry in logSub.Reader.ReadAllAsync(cts.Token))
    {
        Console.WriteLine($"[log:{entry.Level}] {entry.Message}");
    }
});

var danmakuSeen = 0;
var danmakuSamples = new List<string>();
var eventKinds = new Dictionary<LiveEventKind, int>();
var injected = false;
using var liveSub = bus.Subscribe<LiveEvent>(capacity: 4096);
_ = Task.Run(async () =>
{
    await foreach (var evt in liveSub.Reader.ReadAllAsync(cts.Token))
    {
        eventKinds[evt.Kind] = eventKinds.GetValueOrDefault(evt.Kind) + 1;
        if (evt.Kind == LiveEventKind.Danmaku && evt.Platform == "bilibili")
        {
            Interlocked.Increment(ref danmakuSeen);
            lock (danmakuSamples)
            {
                if (danmakuSamples.Count < 5)
                {
                    danmakuSamples.Add($"{evt.Nickname}: {evt.Text}");
                }
            }

            // 收到第一条真实弹幕后，用该观众身份注入"点歌"（同构于真实点歌弹幕），
            // 验证 真实房间弹幕流 → 点歌命令 → 队列 全链路
            if (!injected)
            {
                injected = true;
                var ctx = LiveEventBridge.ToDanmakuContext(evt) with { Text = "点歌 晴天 - 周杰伦" };
                Console.WriteLine($"[smoke] 收到真实弹幕（{evt.Nickname}），以该观众身份注入点歌命令");
                try
                {
                    await commands.HandleMessageAsync(ctx, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }
});

using var queueSub = bus.Subscribe<QueueEventEnvelope>(capacity: 1024);
var queueEvents = new List<string>();
_ = Task.Run(async () =>
{
    await foreach (var envelope in queueSub.Reader.ReadAllAsync(cts.Token))
    {
        lock (queueEvents)
        {
            queueEvents.Add(envelope.Event);
        }
    }
});

var plugin = new BilibiliLivePlugin(new BilibiliApiClient(), logs);
await using (plugin)
{
    var config = AppConfig.CreateDefault() with { Platform = "bilibili", RoomId = roomId };
    Console.WriteLine($"[smoke] 启动 B站插件监听房间 {roomId}（{timeoutSeconds}s 超时）");
    await plugin.StartAsync(config, cts.Token);

    // 手动桥接插件事件流 → EventBus（App 里由 AppServices.StartBilibiliAsync 完成同样转发；
    // 冒烟工具自建。必须在 StartAsync 之后读取 Events——StartAsync 会重建事件通道）
    _ = Task.Run(async () =>
    {
        await foreach (var evt in plugin.Events.ReadAllAsync(cts.Token))
        {
            bus.Publish(evt);
        }
    });

    var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    while (DateTime.UtcNow < deadline)
    {
        lock (queueEvents)
        {
            if (queueEvents.Contains("queue.added"))
            {
                break;
            }
        }

        try
        {
            await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    var snapshot = await queue.GetSnapshotAsync();
    Console.WriteLine($"[smoke] 收到真实弹幕 {danmakuSeen} 条；事件种类: " +
                      string.Join(",", eventKinds.Select(k => $"{k.Key}×{k.Value}")));
    lock (danmakuSamples)
    {
        foreach (var sample in danmakuSamples)
        {
            Console.WriteLine($"[smoke]   弹幕样例: {sample}");
        }
    }

    Console.WriteLine($"[smoke] 队列事件: {string.Join(",", queueEvents.Distinct())}");
    Console.WriteLine($"[smoke] 队列当前: {snapshot.Items.Count} 条");

    var passed = danmakuSeen > 0 && snapshot.Items.Any(i => i.Request.SongName == "晴天");
    Console.WriteLine(passed
        ? "PASS: 真实房间 B站弹幕→点歌→队列 端到端打通"
        : $"FAIL: {(danmakuSeen == 0 ? "超时未收到真实弹幕" : "点歌命令未进入队列")}");
    await plugin.StopAsync();
    return passed ? 0 : 1;
}
