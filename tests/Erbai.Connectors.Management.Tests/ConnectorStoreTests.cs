using System.Text.Json;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// <see cref="ConnectorStore"/> 的读写与自校验行为。
/// </summary>
/// <remarks>
/// 读路径的自校验是关键防线：active.json 是可被手工编辑的普通文件，
/// 一旦它能指向"任意路径的可执行文件"，就等于把任意代码执行权交了出去。
/// </remarks>
public class ConnectorStoreTests : IDisposable
{
    private const string Player = "netease";

    private readonly TempDirectory _temp = new();
    private readonly ConnectorInstallLayout _layout;
    private readonly ConnectorStore _store;

    public ConnectorStoreTests()
    {
        _layout = new ConnectorInstallLayout(_temp.Combine("player-connectors"));
        _store = new ConnectorStore(_layout);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadActive_ReturnsNullWhenNothingInstalled()
    {
        Assert.Null(_store.ReadActive(Player));
        Assert.False(_store.IsInstalled(Player));
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        ActiveConnector active = CreateActive("1.2.3");

        _store.WriteActive(Player, active);
        ActiveConnector? read = _store.ReadActive(Player);

        Assert.NotNull(read);
        Assert.Equal(Player, read.Id);
        Assert.Equal("1.2.3", read.Version);
        Assert.Equal(active.Executable, read.Executable);
        Assert.True(read.Verified);
        Assert.True(_store.IsInstalled(Player));
    }

    [Fact]
    public void WriteActive_LeavesNoTemporaryFilesBehind()
    {
        _store.WriteActive(Player, CreateActive("1.2.3"));

        string connectorRoot = _layout.GetConnectorRoot(Player);
        string[] leftovers = [.. Directory.GetFiles(connectorRoot)
            .Where(f => f.EndsWith(".tmp", StringComparison.Ordinal) || f.EndsWith(".bak", StringComparison.Ordinal))];

        Assert.Empty(leftovers);
    }

    [Fact]
    public void WriteActive_OverwritesPreviousRecord()
    {
        _store.WriteActive(Player, CreateActive("1.2.3"));
        _store.WriteActive(Player, CreateActive("1.2.4"));

        Assert.Equal("1.2.4", _store.ReadActive(Player)!.Version);
    }

    [Fact]
    public void DeleteActive_MakesStoreReportNotInstalled()
    {
        _store.WriteActive(Player, CreateActive("1.2.3"));
        _store.DeleteActive(Player);

        Assert.Null(_store.ReadActive(Player));
    }

    [Fact]
    public void DeleteActive_IsIdempotentWhenMissing()
    {
        _store.DeleteActive(Player);
        _store.DeleteActive(Player);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    public void ReadActive_ReturnsNullForUnparseableContent(string content)
    {
        WriteRawActiveFile(content);

        Assert.Null(_store.ReadActive(Player));
    }

    [Fact]
    public void ReadActive_ReturnsNullWhenIdDoesNotMatch()
    {
        WriteRawActiveFile(JsonSerializer.Serialize(CreateActive("1.2.3") with { Id = "kugou" }));

        Assert.Null(_store.ReadActive(Player));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("")]
    public void ReadActive_ReturnsNullForInvalidVersion(string version)
    {
        WriteRawActiveFile(JsonSerializer.Serialize(CreateActive("1.2.3") with { Version = version }));

        Assert.Null(_store.ReadActive(Player));
    }

    /// <summary>
    /// active.json 把可执行文件指向版本目录之外时必须被拒——否则改一行 JSON 就能让应用
    /// 去执行任意路径的程序。
    /// </summary>
    [Fact]
    public void ReadActive_ReturnsNullWhenExecutableEscapesVersionDirectory()
    {
        string outside = _temp.Combine("evil.exe");
        File.WriteAllText(outside, "not really an exe");

        ActiveConnector active = CreateActive("1.2.3") with { Executable = outside };
        WriteRawActiveFile(JsonSerializer.Serialize(active));

        Assert.Null(_store.ReadActive(Player));
    }

    [Fact]
    public void ReadActive_ReturnsNullWhenExecutableNameIsNotCanonical()
    {
        string versionDirectory = _layout.GetVersionDirectory(Player, "1.2.3");
        Directory.CreateDirectory(versionDirectory);

        string unexpected = Path.Combine(versionDirectory, "SomethingElse.exe");
        File.WriteAllText(unexpected, "x");

        WriteRawActiveFile(JsonSerializer.Serialize(CreateActive("1.2.3") with { Executable = unexpected }));

        Assert.Null(_store.ReadActive(Player));
    }

    [Fact]
    public void ReadActive_ReturnsNullWhenExecutableIsMissing()
    {
        // 记录里的可执行文件路径合法，但文件被删掉了（例如用户手工清理了版本目录）。
        // 注意：这里刻意不用 CreateActive()，因为它会顺手把文件建出来。
        string versionDirectory = _layout.GetVersionDirectory(Player, "1.2.3");
        Directory.CreateDirectory(versionDirectory);

        WriteRawActiveFile(JsonSerializer.Serialize(new ActiveConnector
        {
            Id = Player,
            Version = "1.2.3",
            Executable = Path.Combine(versionDirectory, "Awoo.Connector.Netease.exe"),
            Deployment = "self-contained",
        }));

        Assert.Null(_store.ReadActive(Player));
    }

    [Fact]
    public void ReadActive_ReturnsNullForUnknownDeployment()
    {
        CreateExecutable("1.2.3");
        WriteRawActiveFile(JsonSerializer.Serialize(CreateActive("1.2.3") with { Deployment = "portable" }));

        Assert.Null(_store.ReadActive(Player));
    }

    /// <summary>
    /// framework-dependent 必须带 rid——它是挑选私有运行时的依据（决策 D8）。
    /// </summary>
    [Fact]
    public void ReadActive_ReturnsNullWhenFrameworkDependentLacksRuntimeId()
    {
        CreateExecutable("1.2.3");
        WriteRawActiveFile(JsonSerializer.Serialize(CreateActive("1.2.3") with
        {
            Deployment = "framework-dependent",
            RuntimeRid = null,
        }));

        Assert.Null(_store.ReadActive(Player));
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("win-arm64")]
    [InlineData("")]
    public void ReadActive_ReturnsNullForUnsupportedRuntimeId(string runtimeRid)
    {
        CreateExecutable("1.2.3");
        WriteRawActiveFile(JsonSerializer.Serialize(CreateActive("1.2.3") with
        {
            Deployment = "framework-dependent",
            RuntimeRid = runtimeRid,
        }));

        Assert.Null(_store.ReadActive(Player));
    }

    /// <summary>
    /// P2 阶段允许 runtimeRoot 缺省（连接器退回系统运行时）；P3 引入私有运行时后
    /// 会通过注入的校验把它变成强制项。
    /// </summary>
    [Fact]
    public void ReadActive_AcceptsFrameworkDependentWithoutRuntimeRoot()
    {
        CreateExecutable("1.2.3");
        WriteRawActiveFile(JsonSerializer.Serialize(CreateActive("1.2.3") with
        {
            Deployment = "framework-dependent",
            RuntimeRid = "win-x86",
            RuntimeRoot = null,
        }));

        Assert.NotNull(_store.ReadActive(Player));
    }

    [Fact]
    public void ReadActive_AcceptsFrameworkDependentWithRuntimeRoot()
    {
        CreateExecutable("1.2.3");
        WriteRawActiveFile(JsonSerializer.Serialize(CreateActive("1.2.3") with
        {
            Deployment = "framework-dependent",
            RuntimeRid = "win-x86",
            RuntimeRoot = _temp.Combine("dotnet-runtimes", "win-x86", "8.0.0"),
        }));

        Assert.NotNull(_store.ReadActive(Player));
    }

    /// <summary>注入的运行时根校验应当被采纳（P3 会用它把运行时根锁在私有目录内）。</summary>
    [Fact]
    public void ReadActive_HonoursInjectedRuntimeRootValidator()
    {
        CreateExecutable("1.2.3");

        ConnectorStore store = new(_layout, (_, _) => false);

        WriteRawActiveFile(JsonSerializer.Serialize(CreateActive("1.2.3") with
        {
            Deployment = "framework-dependent",
            RuntimeRid = "win-x86",
            RuntimeRoot = _temp.Combine("dotnet-runtimes", "win-x86", "8.0.0"),
        }));

        Assert.Null(store.ReadActive(Player));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("ne.tease")]
    [InlineData("")]
    public void GetConnectorRoot_RejectsUnsafePlayerKeys(string playerKey)
    {
        Assert.ThrowsAny<Exception>(() => _layout.GetConnectorRoot(playerKey));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    public void GetVersionDirectory_RejectsUnsafeVersions(string version)
    {
        Assert.ThrowsAny<Exception>(() => _layout.GetVersionDirectory(Player, version));
    }

    private ActiveConnector CreateActive(string version)
    {
        string executable = CreateExecutable(version);

        return new ActiveConnector
        {
            Id = Player,
            Version = version,
            Executable = executable,
            Deployment = "self-contained",
        };
    }

    private string CreateExecutable(string version)
    {
        string versionDirectory = _layout.GetVersionDirectory(Player, version);
        Directory.CreateDirectory(versionDirectory);

        string executable = Path.Combine(versionDirectory, "Awoo.Connector.Netease.exe");
        File.WriteAllText(executable, "stub");

        return executable;
    }

    private void WriteRawActiveFile(string content)
    {
        string connectorRoot = _layout.GetConnectorRoot(Player);
        Directory.CreateDirectory(connectorRoot);
        File.WriteAllText(_layout.GetActiveFilePath(Player), content);
    }
}
