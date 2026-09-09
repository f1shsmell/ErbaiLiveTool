using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Plugins;
using Erbai.Contracts.QueueUp;
using Erbai.Core.Events;
using Erbai.Core.Logging;
using Erbai.Core.Storage;
using Erbai.Modules.GiftFx;
using Erbai.Web;

namespace Erbai.Modules.QueueUp.Tests;

/// <summary>
/// 阶段 5 端到端（自动化）：fake 弹幕/礼物事件 → QueueUp/GiftFx 模块 →
/// EventBus → Overlay WS 事件信封全链路（对应真实链路 = 直播间事件→overlay 看板/特效）。
/// </summary>
public class QueueUpOverlayE2ETests
{
    private static string TempDb() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "test.db");

    private static LiveEvent Danmaku(string text, long userId, string nickname = "观众") =>
        new()
        {
            Platform = "douyin",
            RoomId = "1",
            Kind = LiveEventKind.Danmaku,
            UserId = userId,
            Nickname = nickname,
            Text = text,
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static LiveEvent Gift(string giftName, long userId, string nickname = "送礼人") =>
        new()
        {
            Platform = "douyin",
            RoomId = "1",
            Kind = LiveEventKind.Gift,
            UserId = userId,
            Nickname = nickname,
            GiftName = giftName,
            GiftCount = 1,
            TotalCoin = 1000,
            CoinType = "gold",
            Timestamp = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task DanmakuAndGift_FlowToOverlayWebSocket()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var store = new SqliteStorageEngine(path);
        await store.OpenAsync();
        var bus = new EventBus();
        var logs = new LogBus();
        var config = AppConfig.CreateDefault() with
        {
            Api = new ApiConfig(), // 2026-09：overlay token 已移除
            Ui = new UiConfig { Port = Random.Shared.Next(21000, 28000), Host = "127.0.0.1" },
            QueueUp = new QueueUpConfig
            {
                Rules =
                [
                    new QueueUpRuleConfig { MatchKind = "gift_name", MatchValue = "火箭", Action = "insert_at", Position = 1 },
                ],
            },
        };

        // 装配：Overlay + 双功能模块（与 App 组合根同构）
        var overlay = new OverlayServer(config, bus);
        await overlay.StartAsync();
        overlay.StartSnapshotCache();
        var queueUp = new QueueUpModule(store, bus, logs, () => config);
        var giftFx = new GiftFxModule(bus, logs, () => config);
        var context = new ModuleContext
        {
            EventBus = bus,
            Config = new Erbai.Core.Configuration.ConfigStore(path),
            Storage = store,
            Logs = logs,
            Overlay = overlay,
        };
        await queueUp.StartAsync(context, CancellationToken.None);
        await giftFx.StartAsync(context, CancellationToken.None);

        var wsUrl = $"ws://127.0.0.1:{overlay.BoundPort}/api/v1/events?token=e2e-token";
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            // connected
            var connected = await ReceiveTextAsync(socket).WaitAsync(TimeSpan.FromSeconds(5));
            using var connectedDoc = JsonDocument.Parse(connected);
            Assert.Equal("connected", connectedDoc.RootElement.GetProperty("event").GetString());

            // 弹幕「排队」→ queueup.added 到 WS（附快照）
            bus.Publish(Danmaku("排队 上麦唱一首", userId: 1, nickname: "甲"));
            var envelope = await AwaitEventAsync(socket, "queueup.added", TimeSpan.FromSeconds(5));
            using var doc = JsonDocument.Parse(envelope);
            Assert.Equal("queueup.added", doc.RootElement.GetProperty("event").GetString());
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("甲", data.GetProperty("entry").GetProperty("nickname").GetString());
            Assert.Equal("上麦唱一首", data.GetProperty("entry").GetProperty("content").GetString());
            Assert.Equal(1, data.GetProperty("snapshot").GetProperty("total").GetInt32());

            // 礼物「火箭」→ QueueUp 插队事件 + GiftFx giftfx.play 广播
            bus.Publish(Gift("火箭", userId: 2, nickname: "土豪"));
            Assert.True(await PollAsync(() => queueUp.Service.Count == 2),
                $"模块层未入队（Count={queueUp.Service.Count}）");

            // 两个事件到达顺序不保证（GiftFx 推送路径 vs QueueUp 写库）→ 收集式等待
            var collected = await CollectEventsAsync(socket, ["giftfx.play", "queueup.added"], TimeSpan.FromSeconds(5));
            using var giftFxDoc = JsonDocument.Parse(collected["giftfx.play"]);
            var giftData = giftFxDoc.RootElement.GetProperty("data");
            Assert.Equal("banner", giftData.GetProperty("template").GetString());
            Assert.Equal("土豪", giftData.GetProperty("nickname").GetString());
            Assert.Equal("火箭", giftData.GetProperty("gift").GetString());

            using var queueUpDoc = JsonDocument.Parse(collected["queueup.added"]);
            Assert.Equal(2, queueUpDoc.RootElement.GetProperty("data").GetProperty("snapshot").GetProperty("total").GetInt32());

            // /api/v1/queueup 快照端点与 WS 一致
            using var client = new HttpClient();
            var body = await client.GetStringAsync($"http://127.0.0.1:{overlay.BoundPort}/api/v1/queueup?token=e2e-token");
            using var snapshotDoc = JsonDocument.Parse(body);
            Assert.Equal(2, snapshotDoc.RootElement.GetProperty("total").GetInt32());
        }
        finally
        {
            await queueUp.DisposeAsync();
            await giftFx.DisposeAsync();
            await overlay.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<bool> PollAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static async Task<string> AwaitEventAsync(ClientWebSocket socket, string eventName, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var seen = new List<string>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            var text = await ReceiveTextAsync(socket).WaitAsync(timeout);
            using var doc = JsonDocument.Parse(text);
            var actual = doc.RootElement.GetProperty("event").GetString();
            seen.Add(actual ?? "?");
            if (actual == eventName)
            {
                return text;
            }
        }

        throw new TimeoutException($"WS 未收到事件 {eventName}（已收到：{string.Join(",", seen)}）");
    }

    /// <summary>收集指定事件名集合（每个名字一条，顺序不敏感），全部收齐返回。</summary>
    private static async Task<Dictionary<string, string>> CollectEventsAsync(
        ClientWebSocket socket, IReadOnlyList<string> eventNames, TimeSpan timeout)
    {
        var remaining = new HashSet<string>(eventNames, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (remaining.Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            var text = await ReceiveTextAsync(socket).WaitAsync(timeout);
            using var doc = JsonDocument.Parse(text);
            var actual = doc.RootElement.GetProperty("event").GetString();
            if (actual is not null && remaining.Remove(actual))
            {
                result[actual] = text;
            }
        }

        if (remaining.Count > 0)
        {
            throw new TimeoutException($"WS 未收齐事件（缺：{string.Join(",", remaining)}）");
        }

        return result;
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var builder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return builder.ToString();
    }
}
