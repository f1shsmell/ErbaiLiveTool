using Erbai.Contracts.Configuration;

namespace Erbai.Live.Douyin.Tests;

/// <summary>
/// Grabber 死亡自愈（2026-10 长稳轮）：本地 WS 端口连续 ReviveMinFailures 次连不上
/// → 回调宿主 reviver 重拉抓包器；两次拉起之间受节流窗约束；回调异常被隔离。
/// 旧行为：抓包器进程中途死亡，插件每 5s 空转重连直到直播结束，弹幕静默丢失。
/// </summary>
public class GrabberReviveTests
{
    private static AppConfig ConfigWith(int port) =>
        AppConfig.CreateDefault() with { DouyinWsUrl = $"ws://127.0.0.1:{port}" };

    /// <summary>等条件成立（轮询，默认 5s）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task 端口连续连不上_触发自愈回调()
    {
        var revives = 0;
        await using var plugin = new DouyinLivePlugin(
            reconnectDelay: TimeSpan.FromMilliseconds(100),
            reviver: _ =>
            {
                revives++;
                return Task.CompletedTask;
            });
        // 端口 1 无监听：连接必失败（拒绝/重置 → WebSocketException）
        await plugin.StartAsync(ConfigWith(1), CancellationToken.None);

        // 连续失败 ≥ ReviveMinFailures(3) 次后 reviver 必须被调用（重连 100ms/次）
        Assert.True(await WaitUntilAsync(() => revives >= 1, 8000),
            $"连续连接失败后应触发自愈回调，实际 {revives} 次");
        Assert.True(plugin.IsRunning, "自愈路径不得拖垮监听循环");
        await plugin.StopAsync();
    }

    [Fact]
    public async Task 节流窗内_不重复拉起()
    {
        var revives = 0;
        await using var plugin = new DouyinLivePlugin(
            reconnectDelay: TimeSpan.FromMilliseconds(80),
            reviver: _ =>
            {
                revives++;
                return Task.CompletedTask;
            },
            reviveThrottle: TimeSpan.FromSeconds(30));
        await plugin.StartAsync(ConfigWith(1), CancellationToken.None);

        // 首次触发后，30s 节流窗内持续失败也不得再次拉起
        Assert.True(await WaitUntilAsync(() => revives >= 1, 8000), "首次失败窗口应触发一次拉起");
        await Task.Delay(1200); // 80ms 重连 → 十几次失败窗口，均应被节流
        Assert.Equal(1, revives);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task 自愈回调抛异常_不拖垮重连循环()
    {
        await using var plugin = new DouyinLivePlugin(
            reconnectDelay: TimeSpan.FromMilliseconds(100),
            reviver: _ => throw new InvalidOperationException("拉起失败（测试注入）"));
        await plugin.StartAsync(ConfigWith(1), CancellationToken.None);

        // 回调持续抛异常：循环必须继续活着（下一次失败窗口再试）
        await Task.Delay(1500);
        Assert.True(plugin.IsRunning, "自愈回调异常不得拖垮监听循环");
        await plugin.StopAsync();
    }
}
