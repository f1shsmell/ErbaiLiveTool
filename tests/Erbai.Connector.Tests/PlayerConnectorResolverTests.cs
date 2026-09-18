using Erbai.Connectors.Management;
using Erbai.Player.Connectors;

namespace Erbai.Connector.Tests;

/// <summary>
/// <see cref="PlayerConnectorResolver"/> 的解析语义。
/// </summary>
/// <remarks>
/// 这是插件化改造后"点歌到底拉起哪个 exe"的唯一判据，出错的表现是
/// 播放器静默不工作，所以每条分支都要钉住。全部用例只碰临时目录，不联网。
/// </remarks>
public class PlayerConnectorResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"erbai-resolver-{Guid.NewGuid():N}");

    private readonly ConnectorInstallLayout _layout;
    private readonly ConnectorStore _store;

    public PlayerConnectorResolverTests()
    {
        _layout = new ConnectorInstallLayout(Path.Combine(_root, "player-connectors"));
        _store = new ConnectorStore(_layout);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不该让测试失败。
        }
    }

    // ---------------------------------------------------------------------------------
    // 内置 lxmusic
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Resolve_LxMusicPointsAtTheBundledHostExecutable()
    {
        WriteBuiltInExecutable();

        PlayerConnectorResolution resolution = CreateResolver().Resolve(
            "lxmusic",
            new Dictionary<string, string> { ["LX_HTTP_ENABLED"] = "true" });

        Assert.Equal(PlayerConnectorAvailability.BuiltIn, resolution.Availability);
        Assert.True(resolution.IsUsable);
        Assert.False(resolution.NeedsInstall);
        Assert.Equal(Path.Combine(_root, "Erbai.Connector.exe"), resolution.ExecutablePath);
        Assert.Equal("true", resolution.Environment["LX_HTTP_ENABLED"]);
        Assert.Equal("落雪音乐", resolution.DisplayName);
    }

    [Fact]
    public void Resolve_LxMusicIsBrokenWhenTheBundledExecutableIsMissing()
    {
        PlayerConnectorResolution resolution = CreateResolver().Resolve("lxmusic");

        Assert.Equal(PlayerConnectorAvailability.Broken, resolution.Availability);
        Assert.False(resolution.IsUsable);
        Assert.True(resolution.NeedsInstall);
        Assert.Contains("Erbai.Connector.exe", resolution.Detail, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------
    // 插件：未安装 / 已安装 / 损坏
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Resolve_PluginWithoutAnyTraceIsNotInstalled()
    {
        PlayerConnectorResolution resolution = CreateResolver().Resolve("netease");

        Assert.Equal(PlayerConnectorAvailability.NotInstalled, resolution.Availability);
        Assert.False(resolution.IsUsable);
        Assert.True(resolution.NeedsInstall);
        Assert.Null(resolution.Version);
        Assert.Equal("网易云音乐", resolution.DisplayName);
    }

    /// <summary>
    /// 有目录但记录读不出来（可执行文件被删）→ 必须报"损坏"而不是"未安装"：
    /// 两者的下一步动作不同（重装 vs 首次安装），UI 文案也不同。
    /// </summary>
    [Fact]
    public void Resolve_PluginWithUnusableRecordIsBrokenNotMissing()
    {
        string executable = SeedInstalled("netease", "3.1.38.205386.1", "self-contained");
        File.Delete(executable);

        PlayerConnectorResolution resolution = CreateResolver().Resolve("netease");

        Assert.Equal(PlayerConnectorAvailability.Broken, resolution.Availability);
        Assert.True(resolution.NeedsInstall);
        Assert.Contains("重新安装", resolution.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_SelfContainedPluginNeedsNoRuntimeEnvironment()
    {
        SeedInstalled("netease", "3.1.38.205386.1", "self-contained");

        PlayerConnectorResolution resolution = CreateResolver().Resolve("netease");

        Assert.Equal(PlayerConnectorAvailability.Installed, resolution.Availability);
        Assert.True(resolution.IsUsable);
        Assert.Equal("3.1.38.205386.1", resolution.Version);
        Assert.Empty(resolution.Environment);
        Assert.Null(resolution.Detail);
    }

    /// <summary>
    /// framework-dependent 插件必须带上私有运行时环境——本机没有 x86 .NET，
    /// 少了 <c>DOTNET_ROOT_X86</c> 会在启动阶段报 hostfxr 解析失败。
    /// </summary>
    [Fact]
    public void Resolve_FrameworkDependentPluginInjectsPrivateRuntimeEnvironment()
    {
        string runtimeRoot = Path.Combine(_root, "dotnet-runtimes", "win-x86", "8.0.31");
        Directory.CreateDirectory(runtimeRoot);
        File.WriteAllText(Path.Combine(runtimeRoot, "dotnet.exe"), "stub");

        SeedInstalled("kugou", "20.1.41.1", "framework-dependent", runtimeRid: "win-x86", runtimeRoot: runtimeRoot);

        PlayerConnectorResolution resolution = CreateResolver().Resolve("kugou");

        Assert.Equal(PlayerConnectorAvailability.Installed, resolution.Availability);
        Assert.Equal(runtimeRoot, resolution.Environment["DOTNET_ROOT"]);
        Assert.Equal("0", resolution.Environment["DOTNET_MULTILEVEL_LOOKUP"]);
        Assert.Equal(runtimeRoot, resolution.Environment["DOTNET_ROOT_X86"]);
        Assert.False(resolution.Environment.ContainsKey("DOTNET_ROOT_X64"));
    }

    [Fact]
    public void Resolve_X64PluginInjectsX64VariableOnly()
    {
        string runtimeRoot = Path.Combine(_root, "dotnet-runtimes", "win-x64", "8.0.31");
        Directory.CreateDirectory(runtimeRoot);
        File.WriteAllText(Path.Combine(runtimeRoot, "dotnet.exe"), "stub");

        SeedInstalled("netease", "3.1.38.205386.1", "framework-dependent", runtimeRid: "win-x64", runtimeRoot: runtimeRoot);

        PlayerConnectorResolution resolution = CreateResolver().Resolve("netease");

        Assert.Equal(runtimeRoot, resolution.Environment["DOTNET_ROOT_X64"]);
        Assert.False(resolution.Environment.ContainsKey("DOTNET_ROOT_X86"));
    }

    /// <summary>记录里缺运行时根时仍要能解析出来（退回机器级运行时），但要给出提示。</summary>
    [Fact]
    public void Resolve_FrameworkDependentPluginWithoutRuntimeRootWarnsButStillResolves()
    {
        SeedInstalled("kugou", "20.1.41.1", "framework-dependent", runtimeRid: "win-x86", runtimeRoot: null);

        List<string> log = [];
        PlayerConnectorResolution resolution = CreateResolver(log.Add).Resolve("kugou");

        Assert.Equal(PlayerConnectorAvailability.Installed, resolution.Availability);
        Assert.True(resolution.IsUsable);
        Assert.Empty(resolution.Environment);
        Assert.Contains("私有运行时根", resolution.Detail, StringComparison.Ordinal);
        Assert.Contains(log, line => line.Contains("kugou", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------
    // folia token
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Resolve_FoliaGetsTheTokenFromConfiguration()
    {
        SeedInstalled("folia", "1.1.3", "self-contained");

        PlayerConnectorResolution resolution =
            CreateResolver().Resolve("folia", foliaToken: "tok-123");

        Assert.Equal("tok-123", resolution.Environment[PlayerConnectorResolver.FoliaTokenVariable]);
    }

    [Fact]
    public void Resolve_FoliaWithoutTokenOmitsTheVariable()
    {
        SeedInstalled("folia", "1.1.3", "self-contained");

        PlayerConnectorResolution blank = CreateResolver().Resolve("folia", foliaToken: "   ");
        Assert.False(blank.Environment.ContainsKey(PlayerConnectorResolver.FoliaTokenVariable));

        PlayerConnectorResolution missing = CreateResolver().Resolve("folia");
        Assert.False(missing.Environment.ContainsKey(PlayerConnectorResolver.FoliaTokenVariable));
    }

    /// <summary>token 只下发给 folia，别的平台不该看到它。</summary>
    [Fact]
    public void Resolve_TokenIsNotLeakedToOtherPlugins()
    {
        SeedInstalled("netease", "3.1.38.205386.1", "self-contained");

        PlayerConnectorResolution resolution =
            CreateResolver().Resolve("netease", foliaToken: "tok-123");

        Assert.False(resolution.Environment.ContainsKey(PlayerConnectorResolver.FoliaTokenVariable));
    }

    // ---------------------------------------------------------------------------------
    // 批量解析（设置页 / 插件页的展示顺序与灰显判据）
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ResolveAll_ReturnsBuiltInFirstThenTheFourPluginsInOrder()
    {
        WriteBuiltInExecutable();

        IReadOnlyList<PlayerConnectorResolution> all = CreateResolver().ResolveAll();

        Assert.Equal(
            ["lxmusic", "netease", "kugou", "qqmusic", "folia"],
            all.Select(item => item.PlayerKey));
    }

    [Fact]
    public void ResolveAll_MarksOnlyInstalledPluginsAsUsable()
    {
        WriteBuiltInExecutable();
        SeedInstalled("netease", "3.1.38.205386.1", "self-contained");
        SeedInstalled("folia", "1.1.3", "self-contained");

        IReadOnlyList<PlayerConnectorResolution> all = CreateResolver().ResolveAll();

        Assert.Equal(
            ["lxmusic", "netease", "folia"],
            all.Where(item => item.IsUsable).Select(item => item.PlayerKey));
        Assert.Equal(
            ["kugou", "qqmusic"],
            all.Where(item => item.NeedsInstall).Select(item => item.PlayerKey));
    }

    [Fact]
    public void Resolve_UnknownPlayerKeyIsReportedAsBroken()
    {
        PlayerConnectorResolution resolution = CreateResolver().Resolve("spotify");

        Assert.Equal(PlayerConnectorAvailability.Broken, resolution.Availability);
        Assert.False(resolution.IsUsable);
    }

    // ---------------------------------------------------------------------------------

    private PlayerConnectorResolver CreateResolver(Action<string>? log = null) =>
        new(_layout, _store, _root, log);

    private void WriteBuiltInExecutable() =>
        File.WriteAllText(
            Path.Combine(_root, PlayerConnectorResolver.BuiltInExecutableName),
            "stub");

    /// <summary>造一个"装好了"的插件：版本目录 + 可执行文件 + active.json。</summary>
    private string SeedInstalled(
        string playerKey,
        string version,
        string deployment,
        string? runtimeRid = null,
        string? runtimeRoot = null)
    {
        string executableName = ConnectorPlayers.GetExecutableNames(playerKey)[0];
        string versionDirectory = _layout.GetVersionDirectory(playerKey, version);
        Directory.CreateDirectory(versionDirectory);

        string executablePath = Path.Combine(versionDirectory, executableName);
        File.WriteAllText(executablePath, "stub");

        _store.WriteActive(playerKey, new ActiveConnector
        {
            Id = playerKey,
            Version = version,
            Executable = executablePath,
            Deployment = deployment,
            RuntimeRid = runtimeRid,
            RuntimeRoot = runtimeRoot,
            Verified = true,
            ActivatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        return executablePath;
    }
}
