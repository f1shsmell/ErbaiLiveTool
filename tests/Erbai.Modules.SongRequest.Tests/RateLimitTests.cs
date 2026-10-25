using Erbai.Contracts.Configuration;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Erbai.Core.Storage;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>
/// 点歌频率限制（防刷屏，2026-10）：单用户窗口内有效提交（非 rejected 落库行）
/// 达到上限即拒（rate_limited）；AdminBypass 开启时管理员豁免；被拒提交不计入
/// 窗口（不重复惩罚）。检查序：等级门槛之后、重复点歌之前。
/// </summary>
public class RateLimitTests
{
    private static AppConfig RateLimitConfig(int maxRequests = 2, bool enabled = true, bool dedupe = true) =>
        TestHarness.DefaultConfig() with
        {
            Queue = TestHarness.DefaultConfig().Queue with { MaxPerUser = 10, DedupeEnabled = dedupe },
            Permissions = TestHarness.DefaultConfig().Permissions with
            {
                RateLimitEnabled = enabled,
                RateLimitWindowSeconds = 600,
                RateLimitMaxRequests = maxRequests,
            },
        };

    [Fact]
    public async Task Submit_BlocksAfterMaxInWindow()
    {
        var (_, bus, _, _, queue, _, _) = await TestHarness.CreateAsync(RateLimitConfig(maxRequests: 2));
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        Assert.True((await queue.SubmitAsync(TestHarness.NewRequest("晴天"))).Decision.Allowed);
        Assert.True((await queue.SubmitAsync(TestHarness.NewRequest("海阔天空"))).Decision.Allowed);

        var (_, decision) = await queue.SubmitAsync(TestHarness.NewRequest("光辉岁月"));
        Assert.False(decision.Allowed);
        Assert.Equal("rate_limited", decision.ReasonCode);
    }

    [Fact]
    public async Task Submit_RejectedDuplicate_DoesNotConsumeQuota()
    {
        // 重复点歌被拒（rejected 行）不得计入窗口：若计入，第 3 次提交（有效第 2 次）
        // 就会被误判 rate_limited 而不是正常入队
        var (_, bus, _, _, queue, _, _) = await TestHarness.CreateAsync(RateLimitConfig(maxRequests: 2));
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        Assert.True((await queue.SubmitAsync(TestHarness.NewRequest("晴天"))).Decision.Allowed);
        var (_, dup) = await queue.SubmitAsync(TestHarness.NewRequest("晴天"));
        Assert.Equal("duplicate_song", dup.ReasonCode); // 被拒，不计数

        Assert.True((await queue.SubmitAsync(TestHarness.NewRequest("海阔天空"))).Decision.Allowed);
        var (_, third) = await queue.SubmitAsync(TestHarness.NewRequest("光辉岁月"));
        Assert.Equal("rate_limited", third.ReasonCode); // 有效提交已 2 次 → 拒
    }

    [Fact]
    public async Task Submit_AdminBypassesRateLimit()
    {
        var (_, bus, _, _, queue, _, _) = await TestHarness.CreateAsync(RateLimitConfig(maxRequests: 1));
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        for (var i = 0; i < 3; i++)
        {
            var (_, decision) = await queue.SubmitAsync(
                TestHarness.NewRequest($"歌{i}", userId: "anchor1", isAnchor: true));
            Assert.True(decision.Allowed, $"主播第 {i + 1} 次点歌不应被频率限制：{decision.ReasonCode}");
        }
    }

    [Fact]
    public async Task Submit_RateLimitDisabled_AllowsUnlimited()
    {
        var (_, bus, _, _, queue, _, _) = await TestHarness.CreateAsync(RateLimitConfig(enabled: false));
        using var sub = bus.Subscribe<QueueEventEnvelope>();

        for (var i = 0; i < 5; i++)
        {
            var (_, decision) = await queue.SubmitAsync(TestHarness.NewRequest($"歌{i}", userId: $"u{i}"));
            Assert.True(decision.Allowed);
        }
    }

    [Fact]
    public void Policy_RateLimitMessage_MentionsWindowAndMax()
    {
        var config = RateLimitConfig(maxRequests: 3);
        var policy = new Erbai.Modules.SongRequest.Permissions.RequestPolicy(config);
        var decision = policy.CheckRateLimit(3);
        Assert.False(decision.Allowed);
        Assert.Contains("3", decision.Message);
    }

    [Fact]
    public async Task Storage_CountRecentRequests_WindowAndStatusFiltering()
    {
        var path = TestHarness.TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var store = new SqliteStorageEngine(path);
        await store.OpenAsync();

        var now = DateTimeOffset.UtcNow;
        // 窗口内有效 1 条 + 窗口外 1 条 + 窗口内被拒 1 条 → 计数应为 1
        await store.InsertRequestAsync(TestHarness.NewRequest("新歌", userId: "u1") with
        {
            Status = RequestStatus.Queued, CreatedAt = now.AddSeconds(-10),
        });
        await store.InsertRequestAsync(TestHarness.NewRequest("老歌", userId: "u1") with
        {
            Status = RequestStatus.Queued, CreatedAt = now.AddSeconds(-1200),
        });
        await store.InsertRequestAsync(TestHarness.NewRequest("被拒歌", userId: "u1") with
        {
            Status = RequestStatus.Rejected, CreatedAt = now.AddSeconds(-5),
        });
        await store.InsertRequestAsync(TestHarness.NewRequest("别人的歌", userId: "u2") with
        {
            Status = RequestStatus.Queued, CreatedAt = now.AddSeconds(-10),
        });

        var count = await store.CountRecentRequestsAsync("douyin", "1", "u1", now.AddSeconds(-600));
        Assert.Equal(1, count);

        // 窗口拉长到 1 小时 → 窗口外那条也计入（仍排除 rejected/他人）
        var wider = await store.CountRecentRequestsAsync("douyin", "1", "u1", now.AddSeconds(-3600));
        Assert.Equal(2, wider);

        // platform/userId 为空 → 恒 0（防御）
        Assert.Equal(0, await store.CountRecentRequestsAsync("", "1", "u1", now.AddSeconds(-600)));
        Assert.Equal(0, await store.CountRecentRequestsAsync("douyin", "1", "", now.AddSeconds(-600)));
    }
}
