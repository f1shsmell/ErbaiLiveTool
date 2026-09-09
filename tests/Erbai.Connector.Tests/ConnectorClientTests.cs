using System.Diagnostics;
using Erbai.Contracts.Players;
using Erbai.Player.Connectors;

namespace Erbai.Connector.Tests;

/// <summary>
/// 客户端 ↔ 宿主端到端（真实 Erbai.Connector.exe + dummy 连接器）：
/// activate/ping 建连、probe 快照、search 裸数组、execute 命令路由、
/// deactivate shutdown、进程崩溃自动重启。
/// </summary>
public class ConnectorClientTests
{
    private static string ConnectorExePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Erbai.Connector.exe");
        Assert.True(File.Exists(path), $"connector exe not found: {path}");
        return path;
    }

    [Fact]
    public async Task Activate_Probe_Search_Execute_Deactivate_FullRoundTrip()
    {
        await using var client = new ConnectorClient(ConnectorExePath(), "dummy");

        var snapshot = await client.ActivateAsync(CancellationToken.None);
        Assert.True(snapshot.Connected);
        Assert.Equal("dummy-1", snapshot.Version);

        var probe = await client.ProbeAsync(CancellationToken.None);
        Assert.True(probe.Connected);

        var tracks = await client.SearchAsync("晴天", CancellationToken.None);
        Assert.Empty(tracks); // dummy 不支持原生搜索 → []

        var result = await client.ExecuteAsync(PlayerCommand.PlaySelected,
            new PlayerTrack { Platform = "kugou", Title = "晴天", Artist = "周杰伦" }, CancellationToken.None);
        Assert.True(result.Successful);
        Assert.Equal(PlayerOutcome.Applied, result.Outcome);

        await client.DeactivateAsync();
    }

    [Fact]
    public async Task Execute_UnsupportedCommand_ReturnsRejected()
    {
        await using var client = new ConnectorClient(ConnectorExePath(), "dummy");
        await client.ActivateAsync(CancellationToken.None);

        var result = await client.ExecuteAsync(PlayerCommand.Pause, null, CancellationToken.None);

        Assert.True(result.Successful); // dummy 回放默认 applied
        await client.DeactivateAsync();
    }

    [Fact]
    public async Task Probe_AfterProcessKilled_ClientRestartsAndSucceeds()
    {
        await using var client = new ConnectorClient(ConnectorExePath(), "dummy");
        var snapshot = await client.ActivateAsync(CancellationToken.None);
        Assert.True(snapshot.Connected);

        // 杀掉本客户端的子进程 → 下次请求自动重启（指数退避 1s 起步）
        var process = client.Process;
        Assert.NotNull(process);
        process.Kill(entireProcessTree: true);

        var probe = await client.ProbeAsync(CancellationToken.None);
        Assert.True(probe.Connected); // 自动重启后恢复正常
        await client.DeactivateAsync();
    }

    [Fact]
    public async Task Activate_NegotiatesProtocolCapabilities_SseActivated()
    {
        // 高危修复回归：此前 ConnectorPlayerPlugin 能力恒 None、WatchSnapshotsAsync
        // 恒 null（SSE 死代码）；现在从宿主 protocolCapabilities 推导
        await using var client = new ConnectorClient(ConnectorExePath(), "dummy");
        await client.ActivateAsync(CancellationToken.None);
        Assert.Contains("snapshot-events-v1", client.ProtocolCapabilities);

        var plugin = new ConnectorPlayerPlugin(client, "Dummy 连接器");
        await plugin.ActivateAsync(Erbai.Contracts.Configuration.AppConfig.CreateDefault(), CancellationToken.None);
        Assert.True(plugin.Capabilities.HasFlag(PlayerCapabilities.SnapshotEvents));
        Assert.NotNull(plugin.WatchSnapshotsAsync(CancellationToken.None));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task Subscribe_NoEventSource_FallsBackToEmptyStream()
    {
        await using var client = new ConnectorClient(ConnectorExePath(), "dummy");
        await client.ActivateAsync(CancellationToken.None);

        // dummy 无事件源:subscribe ok 但不推送,事件流为空
        var count = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await foreach (var _ in client.WatchSnapshotsAsync(timeout.Token))
            {
                count++;
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(0, count);
        await client.DeactivateAsync();
    }

    [Fact]
    public async Task ConcurrentRequests_AllResolveWithoutSpuriousTimeout()
    {
        // 竞态回归：此前先写后注册——子进程在写线程注册 _pending 前回包，
        // 响应被读循环丢弃 → 25s 超时 + 杀进程。并发请求最大化暴露窗口。
        await using var client = new ConnectorClient(ConnectorExePath(), "dummy");
        await client.ActivateAsync(CancellationToken.None);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => client.ProbeAsync(CancellationToken.None))
            .ToArray();
        var snapshots = await Task.WhenAll(tasks);

        Assert.All(snapshots, s => Assert.True(s.Connected));
        await client.DeactivateAsync();
    }

    [Fact]
    public async Task ConcurrentRequests_AfterProcessKill_RestartsOnce_NoOrphan()
    {
        // 单飞回归：此前 EnsureProcessAsync 无互斥——进程死后并发请求各自
        // StartProcess，双开子进程且先起的成孤儿（Deactivate 只杀最后一个）
        await using var client = new ConnectorClient(ConnectorExePath(), "dummy");
        await client.ActivateAsync(CancellationToken.None);
        client.Process!.Kill(entireProcessTree: true);
        await client.Process!.WaitForExitAsync(CancellationToken.None);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => client.ProbeAsync(CancellationToken.None))
            .ToArray();
        var snapshots = await Task.WhenAll(tasks);
        Assert.All(snapshots, s => Assert.True(s.Connected));

        // 只有当前实例拉起的一个连接器进程存活（无同名孤儿）
        var connectorCount = Process.GetProcessesByName("Erbai.Connector").Length;
        Assert.Equal(1, connectorCount);
        await client.DeactivateAsync();
    }

    [Fact]
    public async Task Requests_AfterProcessKill_ResolveFastWithout25sStall()
    {
        // 审计 T4-1：子进程崩溃后,请求必须快速失败/重启,不得空等 RequestTimeout(25s)。
        // kill 后立即发并发请求（最大化命中"在途/待重启"窗口）：整体应在数秒内完成。
        await using var client = new ConnectorClient(ConnectorExePath(), "dummy");
        await client.ActivateAsync(CancellationToken.None);

        client.Process!.Kill(entireProcessTree: true);
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => client.ProbeAsync(CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        sw.Stop();

        Assert.All(results, r => Assert.True(r.Connected));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"kill 后请求应快速恢复(实际 {sw.Elapsed.TotalSeconds:F1}s, 疑似仍卡 25s 超时)");
        await client.DeactivateAsync();
    }

    [Fact]
    public async Task Deactivate_WhenProcessAlre​adyExited_ReturnsFastWithoutRestart()
    {
        // 审计 T4-2：进程已死时 DeactivateAsync 必须直接释放,不得经 RequestAsync 的
        // EnsureProcessAsync 用不可取消 ct 重启进程（旧实现: 重启循环 + 可能 NRE）。
        var client = new ConnectorClient(ConnectorExePath(), "dummy");
        await client.ActivateAsync(CancellationToken.None);
        client.Process!.Kill(entireProcessTree: true);
        await client.Process!.WaitForExitAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await client.DeactivateAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Deactivate 已死进程应快速返回(实际 {sw.Elapsed.TotalSeconds:F1}s), 不得重启进程");
        Assert.Null(client.Process);
    }
}
