using System.Net;
using Erbai.Connectors.Management.Catalog;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// <see cref="ConnectorInstaller"/> 的安装 / 替换 / 回滚行为。
/// </summary>
/// <remarks>
/// 全部用例都跑在临时目录里，健康检查用替身（真实连接器进程的行为由
/// <c>ThirdPartyConnectorIntegrationTests</c> 和 P7 的端到端验收覆盖）。
/// 这里要钉死的是<b>编排语义</b>：什么时候替换、失败后旧版本还在不在、有没有残留。
/// </remarks>
public class ConnectorInstallerTests : IDisposable
{
    private const string Player = "netease";
    private const string ExecutableName = "Awoo.Connector.Netease.exe";

    private readonly TempDirectory _temp = new();
    private readonly ConnectorInstallLayout _layout;
    private readonly ConnectorStore _store;
    private readonly StubHealthChecker _health = new();

    public ConnectorInstallerTests()
    {
        _layout = new ConnectorInstallLayout(_temp.Combine("player-connectors"));
        _store = new ConnectorStore(_layout);
    }

    public void Dispose() => _temp.Dispose();

    // ---------------------------------------------------------------------------------
    // 本地 ZIP 安装（决策 D6）
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task InstallFromLocalArchive_CreatesVersionDirectoryAndActiveRecord()
    {
        string archive = WriteLocalArchive("payload-v1");

        ConnectorInstallResult result = await InstallLocalAsync(archive, "1.0.0");

        Assert.Equal("1.0.0", result.Version);
        Assert.False(result.ReusedExisting);
        Assert.False(result.Verified);

        Assert.True(File.Exists(result.ExecutablePath));
        Assert.Equal("payload-v1", File.ReadAllText(result.ExecutablePath));

        ActiveConnector active = Assert.IsType<ActiveConnector>(_store.ReadActive(Player));
        Assert.Equal("1.0.0", active.Version);
        Assert.False(active.Verified);
        Assert.Equal("self-contained", active.Deployment);
    }

    [Fact]
    public async Task InstallFromLocalArchive_RequiresExplicitOptInWithoutPackageMetadata()
    {
        string archive = WriteLocalArchive("payload-v1");

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateInstaller().InstallFromLocalArchiveAsync(Player, archive, "1.0.0"));

        Assert.Contains("无法校验", ex.Message, StringComparison.Ordinal);
        Assert.Null(_store.ReadActive(Player));
        Assert.False(Directory.Exists(_layout.GetVersionDirectory(Player, "1.0.0")));
    }

    /// <summary>
    /// 拿不到清单元数据时不能猜部署方式：猜成 framework-dependent 但没有 rid，
    /// 会写出一份 <see cref="ConnectorStore"/> 读不回来的 active.json。
    /// </summary>
    [Fact]
    public async Task InstallFromLocalArchive_RequiresExplicitDeploymentWithoutPackageMetadata()
    {
        string archive = WriteLocalArchive("payload-v1");

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateInstaller().InstallFromLocalArchiveAsync(
                Player, archive, "1.0.0", allowUnverified: true));

        Assert.Contains("必须显式指定部署方式", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_layout.GetVersionDirectory(Player, "1.0.0")));
    }

    /// <summary>framework-dependent 缺 rid 必须在下载/解压之前就被拦下。</summary>
    [Fact]
    public async Task InstallFromLocalArchive_RejectsFrameworkDependentWithoutRuntimeId()
    {
        string archive = WriteLocalArchive("payload-v1");

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateInstaller().InstallFromLocalArchiveAsync(
                Player, archive, "1.0.0", allowUnverified: true, deployment: "framework-dependent"));

        Assert.Contains("必须提供运行时标识", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_layout.GetVersionDirectory(Player, "1.0.0")));
    }

    /// <summary>
    /// 清单不可达时（离线安装的典型场景）用户可以显式指定 rid，安装照常进行——
    /// 这正是 UI 上"从本地 ZIP 安装"的未校验分支所依赖的能力。
    /// </summary>
    [Fact]
    public async Task InstallFromLocalArchive_AcceptsExplicitRuntimeIdWithoutPackageMetadata()
    {
        string archive = WriteLocalArchive("payload-v1");

        ConnectorInstallResult result = await CreateInstaller().InstallFromLocalArchiveAsync(
            Player,
            archive,
            "1.0.0",
            allowUnverified: true,
            deployment: "framework-dependent",
            runtimeRid: "win-x86");

        Assert.Equal("1.0.0", result.Version);
        Assert.False(result.Verified);

        ActiveConnector active = Assert.IsType<ActiveConnector>(_store.ReadActive(Player));
        Assert.Equal("framework-dependent", active.Deployment);
        Assert.Equal("win-x86", active.RuntimeRid);

        // 没给运行时 provider 时 runtimeRoot 为空，记录仍然合法（退回机器级运行时）。
        Assert.Null(active.RuntimeRoot);
    }

    [Fact]
    public async Task InstallFromLocalArchive_RejectsPackageWithMismatchedMetadata()
    {
        string archive = WriteLocalArchive("payload-v1");

        // 声明的大小与实际不符 → 校验第一步就该失败，连解压都不该发生。
        ConnectorPackage bogus = CreatePackage(size: 999_999, sha256: new string('0', 64));

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateInstaller().InstallFromLocalArchiveAsync(
                Player, archive, "1.0.0", expectedPackage: bogus));

        Assert.Contains("大小不匹配", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_layout.GetVersionDirectory(Player, "1.0.0")));
    }

    [Fact]
    public async Task InstallFromLocalArchive_FailsWhenPackageLacksExecutable()
    {
        byte[] zip = ArchiveBuilder.CreateZip(("SomeOther.exe", "x"), ("README.txt", "y"));
        string archive = WriteArchiveFile("no-exe.zip", zip);

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => InstallLocalAsync(archive, "1.0.0"));

        Assert.Contains("缺少预期的可执行文件", ex.Message, StringComparison.Ordinal);
        Assert.Null(_store.ReadActive(Player));
    }

    [Fact]
    public async Task InstallFromLocalArchive_FailsWhenHealthCheckFails_AndLeavesNothingBehind()
    {
        string archive = WriteLocalArchive("payload-v1");
        _health.IsHealthy = false;
        _health.Message = "hostfxr.dll not found";

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => InstallLocalAsync(archive, "1.0.0"));

        Assert.Contains("健康检查未通过", ex.Message, StringComparison.Ordinal);
        Assert.Contains("hostfxr.dll not found", ex.Message, StringComparison.Ordinal);

        Assert.False(Directory.Exists(_layout.GetVersionDirectory(Player, "1.0.0")));
        Assert.Null(_store.ReadActive(Player));

        AssertNoStagingOrBackupLeftovers();
    }

    // ---------------------------------------------------------------------------------
    // 替换
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// 本地 ZIP 安装刻意<b>总是</b>重装（用户显式给了包，复用会让人以为"装上了"其实没动）。
    /// 这里确认它确实重新走了一遍健康检查、并把内容换成了新包的内容。
    /// </summary>
    [Fact]
    public async Task InstallFromLocalArchive_AlwaysReplacesEvenWhenSameVersionIsHealthy()
    {
        string first = WriteLocalArchive("payload-v1");
        ConnectorInstaller installer = CreateInstaller();

        await installer.InstallFromLocalArchiveAsync(
            Player, first, "1.0.0", allowUnverified: true, deployment: "self-contained");
        _health.CallCount = 0;

        string second = WriteLocalArchive("payload-v2");
        ConnectorInstallResult result = await installer.InstallFromLocalArchiveAsync(
            Player, second, "1.0.0", allowUnverified: true, deployment: "self-contained");

        Assert.False(result.ReusedExisting);
        Assert.Equal(1, _health.CallCount);
        Assert.Equal("payload-v2", File.ReadAllText(result.ExecutablePath));
    }

    [Fact]
    public async Task InstallFromLocalArchive_CleansUpStagingAndBackupDirectories()
    {
        string archive = WriteLocalArchive("payload-v1");

        await InstallLocalAsync(archive, "1.0.0");
        await InstallLocalAsync(archive, "1.0.0");

        AssertNoStagingOrBackupLeftovers();
    }

    /// <summary>残留的 <c>.backup-*</c>（例如上次文件被占用）应在下次安装时被清掉。</summary>
    [Fact]
    public async Task InstallFromLocalArchive_RemovesStaleBackupDirectories()
    {
        string connectorRoot = _layout.GetConnectorRoot(Player);
        string stale = Path.Combine(connectorRoot, ".backup-0.9.0-stale");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "junk.bin"), "x");

        await InstallLocalAsync(WriteLocalArchive("payload-v1"), "1.0.0");

        Assert.False(Directory.Exists(stale));
    }

    // ---------------------------------------------------------------------------------
    // 回滚
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// 核心保证：升级失败不能把原本能用的版本弄没。
    /// 通过"激活写入失败"把安装器逼进 <c>.backup-*</c> 回滚分支。
    /// </summary>
    [Fact]
    public async Task Rollback_RestoresPreviousVersionWhenActivationFails()
    {
        // 1) 先装好 v1。
        await InstallLocalAsync(WriteLocalArchive("payload-v1"), "1.0.0");

        string executable = _store.ReadActive(Player)!.Executable;
        Assert.Equal("payload-v1", File.ReadAllText(executable));

        // 2) 用"写 active.json 必失败"的存储再装一次同版本（内容不同），强制走替换 + 回滚。
        string archiveV2 = WriteLocalArchive("payload-v2");
        ConnectorInstaller failing = CreateInstaller(new FailingWriteStore(_store));

        await Assert.ThrowsAsync<ConnectorManagementException>(
            () => failing.InstallFromLocalArchiveAsync(
                Player, archiveV2, "1.0.0", allowUnverified: true, deployment: "self-contained"));

        // 3) 旧版本必须原样回来：目录在、内容还是 v1、active.json 仍指向它。
        ActiveConnector active = Assert.IsType<ActiveConnector>(_store.ReadActive(Player));
        Assert.Equal("1.0.0", active.Version);
        Assert.True(File.Exists(active.Executable));
        Assert.Equal("payload-v1", File.ReadAllText(active.Executable));

        AssertNoStagingOrBackupLeftovers();
    }

    // ---------------------------------------------------------------------------------
    // 清单路径（下载）
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// 下载内容与清单声明不一致时必须在解压前失败。这里让大小吻合、SHA-256 不吻合，
    /// 以便确认校验确实走到了哈希这一步（而不是停在下载阶段）。
    /// </summary>
    [Fact]
    public async Task InstallAsync_RejectsPayloadWhoseHashDoesNotMatchCatalog()
    {
        byte[] zip = ArchiveBuilder.CreateConnectorPackage(ExecutableName);

        StubHttpMessageHandler handler = new((request, _) =>
        {
            Assert.Equal("app.enkianss.us", request.RequestUri!.Host);
            return StubHttpMessageHandler.WholeContent(zip);
        });

        ConnectorCatalogEntry entry = CreateCatalogEntry(size: zip.LongLength, sha256: new string('a', 64));

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateInstaller(downloaderHandler: handler).InstallAsync(Player, entry));

        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.Null(_store.ReadActive(Player));
        Assert.False(Directory.Exists(_layout.GetVersionDirectory(Player, entry.Version!)));
    }

    [Fact]
    public async Task InstallAsync_FallsBackToGitHubWhenPrimaryHostFails()
    {
        byte[] zip = ArchiveBuilder.CreateConnectorPackage(ExecutableName);

        StubHttpMessageHandler handler = new((request, _) => request.RequestUri!.Host == "app.enkianss.us"
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : StubHttpMessageHandler.WholeContent(zip));

        ConnectorCatalogEntry entry = CreateCatalogEntry(size: zip.LongLength, sha256: new string('b', 64));

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateInstaller(downloaderHandler: handler, downloaderMaxAttempts: 1)
                .InstallAsync(Player, entry));

        // 关键点：主站 502 之后确实换到了 GitHub——否则不会走到"SHA-256 不匹配"这一步。
        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task InstallAsync_RejectsEntryWithoutPackage()
    {
        ConnectorCatalogEntry entry = new() { Id = Player, Version = "1.0.0", ProtocolVersion = 1 };

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateInstaller().InstallAsync(Player, entry));

        Assert.Contains("缺少 package", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------

    private Task<ConnectorInstallResult> InstallLocalAsync(string archivePath, string version) =>
        CreateInstaller().InstallFromLocalArchiveAsync(
            Player, archivePath, version, allowUnverified: true, deployment: "self-contained");

    private ConnectorInstaller CreateInstaller(
        IConnectorStore? store = null,
        StubHttpMessageHandler? downloaderHandler = null,
        int downloaderMaxAttempts = 5)
    {
        ConnectorDownloader downloader = new(
            new HttpClient(downloaderHandler ?? new StubHttpMessageHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.NotImplemented))),
            TimeSpan.Zero,
            downloaderMaxAttempts,
            TimeSpan.FromSeconds(5));

        return new ConnectorInstaller(_layout, store ?? _store, downloader, _health);
    }

    private string WriteLocalArchive(string executableContent)
    {
        byte[] zip = ArchiveBuilder.CreateZip(
            (ExecutableName, executableContent),
            ("Awoo.Connector.Netease.deps.json", "{}"));

        return WriteArchiveFile($"connector-{Guid.NewGuid():N}.zip", zip);
    }

    private string WriteArchiveFile(string fileName, byte[] content)
    {
        string path = _temp.Combine(fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private void AssertNoStagingOrBackupLeftovers()
    {
        string connectorRoot = _layout.GetConnectorRoot(Player);
        if (!Directory.Exists(connectorRoot))
        {
            return;
        }

        string[] leftovers = [.. Directory.GetDirectories(connectorRoot)
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith(".staging-", StringComparison.Ordinal)
                || name.StartsWith(".backup-", StringComparison.Ordinal))!];

        Assert.Empty(leftovers);
    }

    private static ConnectorCatalogEntry CreateCatalogEntry(long size, string sha256) => new()
    {
        Id = Player,
        Name = "网易云音乐",
        Channel = "stable",
        Version = "3.1.38.205386.1",
        ProtocolVersion = 1,
        MinimumCoreVersion = "1.1.10",
        Package = CreatePackage(size, sha256),
    };

    private static ConnectorPackage CreatePackage(long size, string sha256) => new()
    {
        Deployment = "framework-dependent",
        Runtime = "win-x64",
        RuntimeChannel = "8.0",
        Asset = "awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip",
        Size = size,
        Sha256 = sha256,
        Signature = Convert.ToBase64String(new byte[64]),
        DownloadUrl = "https://app.enkianss.us/connectors/v2/download/netease/3.1.38.205386.1/"
            + "awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip",
    };
}
