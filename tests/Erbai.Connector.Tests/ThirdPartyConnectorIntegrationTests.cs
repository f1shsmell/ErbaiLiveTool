using Erbai.Contracts.Players;
using Erbai.Player.Connectors;

namespace Erbai.Connector.Tests;

/// <summary>
/// 用本仓库的 <see cref="ConnectorClient"/> 直驱<b>真实的上游连接器 exe</b>
/// （<c>vendor/Awoo.Connector.*.exe</c>），验证「第三方连接器可插拔」这一前提。
///
/// <para>
/// 为何只测 netease：上游按 rid 分发——netease 是 <c>win-x64</c>，
/// kugou / qqmusic / folia 是 <c>win-x86</c>。本机只有 x64 .NET 8 运行时，
/// x86 连接器直接启动会报 <c>Failed to resolve hostfxr.dll [not found]</c>，
/// 需等「私有共享 .NET 运行时」落地后才能端到端验证（见计划 P3）。
/// </para>
///
/// <para>
/// <c>vendor/</c> 不入库（341MB），缺失时本类用例直接跳过——故断言前先探测。
/// </para>
/// </summary>
public class ThirdPartyConnectorIntegrationTests
{
    /// <summary>上游连接器 exe 文件名（注意 QQMusic 的驼峰不规则）。</summary>
    private static readonly Dictionary<string, string> ExeNames = new(StringComparer.Ordinal)
    {
        ["netease"] = "Awoo.Connector.Netease.exe",
        ["kugou"] = "Awoo.Connector.Kugou.exe",
        ["qqmusic"] = "Awoo.Connector.QQMusic.exe",
        ["folia"] = "Awoo.Connector.Folia.exe",
    };

    /// <summary>从测试输出目录向上找 <c>vendor/&lt;player&gt;/Awoo.Connector.*.exe</c>。</summary>
    private static string? FindVendorExe(string player)
    {
        if (!ExeNames.TryGetValue(player, out var exeName))
        {
            return null;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "vendor", player, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public async Task UpstreamNeteaseConnector_NegotiatesCapabilities_ThroughOurClient()
    {
        var exe = FindVendorExe("netease");
        if (exe is null)
        {
            // vendor/ 不入库；无本地上游连接器时跳过（非失败）
            return;
        }

        await using var client = new ConnectorClient(exe, "netease");
        var snapshot = await client.ActivateAsync(CancellationToken.None);

        // 请求面兼容：我们的 ping/probe 被上游连接器正常应答（无播放器时 connected=false）
        Assert.False(snapshot.Connected);

        // 能力面归一：上游把能力放在 result 内，必须仍能推导出正确能力
        var ping = client.Ping;
        Assert.Equal(1, ping.ProtocolVersion);
        Assert.Equal("netease", ping.ConnectorId);
        Assert.False(string.IsNullOrEmpty(ping.ConnectorVersion));
        Assert.True(ping.HasFeature(ConnectorProtocol.FeatureSnapshotEvents));
        Assert.True(ping.Capabilities.InsertNext);

        var plugin = new ConnectorPlayerPlugin(client, "网易云音乐");
        await plugin.ActivateAsync(Erbai.Contracts.Configuration.AppConfig.CreateDefault(), CancellationToken.None);

        Assert.True(plugin.Capabilities.HasFlag(PlayerCapabilities.SnapshotEvents));
        Assert.True(plugin.Capabilities.HasFlag(PlayerCapabilities.QueueProgrammable));
        Assert.NotNull(plugin.WatchSnapshotsAsync(CancellationToken.None));

        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task UpstreamNeteaseConnector_Search_ReturnsTracksWithPlatformInjected()
    {
        var exe = FindVendorExe("netease");
        if (exe is null)
        {
            return;
        }

        await using var client = new ConnectorClient(exe, "netease");
        await client.ActivateAsync(CancellationToken.None);

        var tracks = await client.SearchAsync("晴天", CancellationToken.None);

        if (tracks.Count == 0)
        {
            // 离线环境：上游连接器的 search 走真实网络，拿不到结果时不判失败
            return;
        }

        // 上游 search 结果不带 platform 字段 → 必须由连接器 key 兜底注入
        Assert.All(tracks, t => Assert.Equal("netease", t.Platform));
        Assert.All(tracks, t => Assert.False(string.IsNullOrEmpty(t.Title)));

        await client.DeactivateAsync();
    }

    [Fact]
    public void UpstreamConnectorVendorDirectory_WhenPresent_ContainsExpectedLayout()
    {
        var exe = FindVendorExe("netease");
        if (exe is null)
        {
            return;
        }

        // 上游连接器是 framework-dependent 小包：exe 旁必须有 deps.json + runtimeconfig.json
        var dir = Path.GetDirectoryName(exe)!;
        Assert.True(File.Exists(Path.Combine(dir, "Awoo.Connector.Netease.deps.json")));
        Assert.True(File.Exists(Path.Combine(dir, "Awoo.Connector.Netease.runtimeconfig.json")));
    }
}
