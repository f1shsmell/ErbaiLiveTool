using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Plugins;
using Erbai.Core.Events;
using Erbai.Core.Logging;
using Erbai.Core.Storage;
using Erbai.Modules.QueueUp;

namespace Erbai.Modules.QueueUp.Tests;

/// <summary>
/// 「完成」特权判定与点歌一致（2026-10 修复）：事件标志缺失时回落持久化用户
/// （跨房间 MAX 合并）——主播在应用内「设置管理员@XX」后，该管理员即使弹幕
/// 不带 admin 标志也能完成队首。旧实现只看事件标志，持久化管理员无法推进队列。
/// </summary>
public class QueueUpPrivilegeTests
{
    private static string TempDb() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "test.db");

    private static async Task<(SqliteStorageEngine Store, EventBus Bus, QueueUpModule Module)> CreateAsync()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new SqliteStorageEngine(path);
        await store.OpenAsync();
        var bus = new EventBus();
        var module = new QueueUpModule(store, bus, new LogBus(), () => AppConfig.CreateDefault());
        await module.StartAsync(new ModuleContext
        {
            EventBus = bus,
            Config = new Erbai.Core.Configuration.ConfigStore(path),
            Storage = store,
            Logs = new LogBus(),
            Overlay = null,
        }, CancellationToken.None);
        return (store, bus, module);
    }

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

    [Fact]
    public async Task Complete_ByPersistedAdmin_Works()
    {
        var (store, bus, module) = await CreateAsync();
        try
        {
            bus.Publish(Danmaku("排队 上麦唱一首", userId: 10, nickname: "甲"));
            Assert.True(await PollAsync(() => module.Service.Count == 1));

            // 应用内设置管理员（只写 users 表，弹幕不带任何平台标志）→「完成」应生效
            // （SetUserAdminAsync 对不存在用户返回 null，先落用户行）
            await store.SaveUserAsync(new Erbai.Contracts.Storage.User
            {
                Platform = "douyin", RoomId = "1", UserId = "5", Nickname = "管理员",
            });
            Assert.NotNull(await store.SetUserAdminAsync("douyin", "1", "5", admin: true));
            bus.Publish(Danmaku("完成", userId: 5, nickname: "管理员"));

            Assert.True(await PollAsync(() => module.Service.Count == 0),
                "持久化管理员的「完成」应完成队首");
        }
        finally
        {
            await module.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task Complete_ByRegularViewer_Ignored()
    {
        var (store, bus, module) = await CreateAsync();
        try
        {
            bus.Publish(Danmaku("排队 上麦唱一首", userId: 10, nickname: "甲"));
            Assert.True(await PollAsync(() => module.Service.Count == 1));

            // 无任何特权的观众「完成」→ 静默忽略
            bus.Publish(Danmaku("完成", userId: 99, nickname: "路人"));
            await Task.Delay(150);
            Assert.Equal(1, module.Service.Count);
        }
        finally
        {
            await module.DisposeAsync();
            await store.DisposeAsync();
        }
    }
}
