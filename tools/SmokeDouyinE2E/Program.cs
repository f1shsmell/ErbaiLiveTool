// 阶段4 退出标准冒烟：真实抖音房间 → Grabber 抓包 → 弹幕 → LiveEvent → 点歌命令 → 队列。
// 参照 tools/SmokeBilibiliE2E（不打真搜索，固定假处理器；真实链路手动验收）。
//
// 用法（需管理员终端——Grabber 抓包要提权，且会改写系统代理）:
//   dotnet run --project tools/SmokeDouyinE2E -- <抖音房间号> [--duration 秒] [--ws-port 8888] [--proxy-port 8827]
//   （时长参数是 --duration，不是位置参数；默认 150s）
//
// 副作用（注意）:
// - Grabber 会安装根证书（CurrentUser My+Root，Titanium 自签）并改写系统代理指向 127.0.0.1:8827；
//   结束时宿主按铁律还原代理（仅当当前代理仍指向抓包器端口）；测试装的新根证书清理见
//   tools/remove-test-certs.ps1（Root 存储删除需管理员）；
// - 需要抖音客户端/浏览器开着直播间（流量走系统代理被抓包器 MITM）。
using System.Collections.Concurrent;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Queue;
using Erbai.Core.Events;
using Erbai.Core.Logging;
using Erbai.Core.Storage;
using Erbai.Live.Douyin;
using Erbai.Live.Douyin.Hosting;
using Erbai.Modules.SongRequest.Search;
using Erbai.Modules.SongRequest.Services;

var roomId = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "1";
var timeoutSeconds = 150;
var wsPort = 8888;
var proxyPort = 8827;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--duration" && int.TryParse(args[i + 1], out var d))
    {
        timeoutSeconds = d;
    }
    else if (args[i] == "--ws-port" && int.TryParse(args[i + 1], out var p))
    {
        wsPort = p;
    }
    else if (args[i] == "--proxy-port" && int.TryParse(args[i + 1], out var pp))
    {
        proxyPort = pp;
    }
}

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var grabberExe = Path.Combine(repoRoot, "src", "Erbai.Live.Douyin.Grabber", "bin", "Debug", "net8.0-windows", "WssBarrageServer.exe");
if (!File.Exists(grabberExe))
{
    Console.WriteLine($"FAIL: 新 Grabber 不存在（先构建）: {grabberExe}");
    return 2;
}

var baseDir = Path.Combine(Path.GetTempPath(), "erbai-smoke-douyin", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(baseDir);

var bus = new EventBus();
var logs = new LogBus();
var store = new SqliteStorageEngine(Path.Combine(baseDir, "smoke.db"));
await store.OpenAsync();

var settings = AppConfig.CreateDefault() with
{
    DouyinWsUrl = $"ws://127.0.0.1:{wsPort}",
    DouyinGrabber = AppConfig.CreateDefault().DouyinGrabber with
    {
        AppSettings = new Dictionary<string, object>(AppConfig.CreateDefault().DouyinGrabber.AppSettings)
        {
            ["sysProxy"] = true,     // 冒烟要真抓包：系统代理指向 127.0.0.1:proxyPort
            ["proxyPort"] = proxyPort,
            ["wsListenPort"] = wsPort,
            ["pushFilter"] = "1,4,5,7",
        },
    },
};

var queue = new SongQueueService(store, bus, settings);
queue.Processor = (req, _) => Task.FromResult<IReadOnlyList<SongSearchResult>?>(
    [new SongSearchResult { Source = "kugou", Name = req.SongName, Singer = req.Singer, SongMid = "smoke-mid" }]);
var commands = new SongRequestService(queue, store, logs, new SongBlacklist(store, logs));

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
var bridgeTask = LiveEventBridge.RunAsync(bus, commands, logs, cts.Token);

using var logSub = logs.Subscribe(capacity: 2048);
_ = Task.Run(async () =>
{
    await foreach (var entry in logSub.Reader.ReadAllAsync(cts.Token))
    {
        if (entry.Level is LogLevel.Information or LogLevel.Warning or LogLevel.Error)
        {
            Console.WriteLine($"[log:{entry.Level}] {entry.Message}");
        }
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
        if (evt.Kind == LiveEventKind.Danmaku && evt.Platform == "douyin")
        {
            Interlocked.Increment(ref danmakuSeen);
            lock (danmakuSamples)
            {
                if (danmakuSamples.Count < 5)
                {
                    danmakuSamples.Add($"{evt.Nickname}: {evt.Text}");
                }
            }

            // 收到第一条真实弹幕后，用该观众身份注入"点歌"（同构于真实点歌弹幕）
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

// 宿主：Grabber 子进程（配置下发/端口就绪/代理铁律/stdout 分级）
var grabber = new DouyinGrabberHost(new DouyinGrabberHostOptions
{
    ExecutablePath = grabberExe,
    AppSettings = settings.DouyinGrabber.AppSettings,
    WsPort = wsPort,
    ProxyPort = proxyPort,
    Logs = logs,
});

await using var plugin = new DouyinLivePlugin(
    logs,
    reconnectDelay: TimeSpan.FromSeconds(3),
    allowedRoomIds: new HashSet<string>());

var grabberStatus = await grabber.StartAsync(cts.Token);
if (grabberStatus.Error.Length > 0)
{
    Console.WriteLine($"FAIL: Grabber 启动失败: {grabberStatus.Error}");
    return 2;
}

Console.WriteLine($"[smoke] 启动抖音插件监听 {settings.DouyinWsUrl}（{timeoutSeconds}s 超时）");
await plugin.StartAsync(settings, cts.Token);

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

await plugin.StopAsync();
await grabber.StopAsync(); // 铁律还原系统代理

var passed = danmakuSeen > 0 && snapshot.Items.Any(i => i.Request.SongName == "晴天");
Console.WriteLine(passed
    ? "PASS: 真实抖音房间 弹幕→点歌→队列 端到端打通"
    : $"FAIL: {(danmakuSeen == 0 ? "超时未收到真实弹幕（确认抖音客户端/浏览器开着直播间，且系统代理未被用户代理占用）" : "点歌命令未进入队列")}");
return passed ? 0 : 1;
