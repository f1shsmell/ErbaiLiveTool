using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Core.Events;
using Erbai.Core.Logging;
using Erbai.Core.Storage;
using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest.Tests
{
    using SongRequest = Erbai.Contracts.Requests.SongRequest;

    /// <summary>里程碑 2 测试公共设施：临时库 + 事件总线 + 队列服务装配。</summary>
    internal static class TestHarness
{
    public static string TempDb() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "test.db");

    public static AppConfig DefaultConfig() => AppConfig.CreateDefault() with
    {
        Queue = new QueueConfig
        {
            MaxSize = 20,
            DisplayLimit = 5,
            UserLimitEnabled = true,
            MaxPerUser = 1,
            DedupeEnabled = true,
            DisplayOrder = "asc",
        },
    };

    public static async Task<(SqliteStorageEngine Store, EventBus Bus, LogBus Logs, AppConfig Config,
        SongQueueService Queue, SongBlacklist Blacklist, SongRequestService Commands)> CreateAsync(
        AppConfig? config = null)
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new SqliteStorageEngine(path);
        await store.OpenAsync();
        var bus = new EventBus();
        var logs = new LogBus();
        var effective = config ?? DefaultConfig();
        var queue = new SongQueueService(store, bus, effective);
        // 默认搜索处理器：请求会进入 dispatched 并保持活动（等待播放），
        // 让"提交/权限"类测试的队列状态稳定；worker 专项测试自行覆盖
        queue.Processor = SearchHarness.Fixed();
        var blacklist = new SongBlacklist(store, logs);
        await blacklist.LoadAsync();
        var commands = new SongRequestService(queue, store, logs, blacklist);
        return (store, bus, logs, effective, queue, blacklist, commands);
    }

    public static SongRequest NewRequest(
        string songName = "晴天",
        string platform = "douyin",
        string roomId = "1",
        string userId = "u1",
        string nickname = "观众",
        bool isAdmin = false,
        bool isAnchor = false,
        int? fanLevel = 5,
        int? medalLevel = 5) =>
        new()
        {
            Platform = platform,
            RoomId = roomId,
            UserId = userId,
            Nickname = nickname,
            IsAdmin = isAdmin,
            IsAnchor = isAnchor,
            FanLevel = fanLevel,
            MedalLevel = medalLevel,
            SongName = songName,
            Singer = "周杰伦",
            Status = RequestStatus.Received,
        };

    /// <summary>读取订阅队列中的下一条事件（带超时防挂死）。</summary>
    public static async Task<QueueEventEnvelope?> NextEventAsync(Subscription<QueueEventEnvelope> sub, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await sub.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>跳过无关事件（如隐式 start 的 queue.started），读到指定事件为止。</summary>
    public static async Task<QueueEventEnvelope?> AwaitEventAsync(
        Subscription<QueueEventEnvelope> sub, string eventName, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            QueueEventEnvelope ev;
            try
            {
                ev = await sub.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (ev.Event == eventName)
            {
                return ev;
            }
        }
    }

    /// <summary>读取订阅队列中的全部待处理事件（不等待）。</summary>
    public static async Task<List<QueueEventEnvelope>> DrainEventsAsync(Subscription<QueueEventEnvelope> sub)
    {
        var list = new List<QueueEventEnvelope>();
        while (sub.Reader.TryRead(out var ev))
        {
            list.Add(ev);
        }

        await Task.Yield();
        return list;
    }
}

}
