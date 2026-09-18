using Erbai.Connectors.Management.Runtime;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// .NET 官方发布元数据的解析与选包规则。
/// </summary>
/// <remarks>
/// 夹具按官方 <c>releases.json</c> 的真实结构构造（含每个 rid 的 <c>.exe</c> 与 <c>.zip</c>
/// 两条记录，hash 取自 2026-09-18 实抓的 8.0.31）。
/// </remarks>
public class DotnetRuntimeSelectorTests
{
    private const string WinX64ZipSha512 =
        "9c55c58694676ee64b0eed2cd6d8cbf58b9aa8288420acc66841e15ca0099c75"
        + "d4af0182d23a641c2342e5a151a325df4a12fa0bde2e47c0fb7e9a33e7b09896";

    private const string WinX86ZipSha512 =
        "c8d8597dcbfa09fa267dde0e04d628360ade272f6ec1dc46f8896a58712bb6ec"
        + "19e8b9ed1a9061786b99e3b4723cfb4c438e06268b1120c3a568bcae54551c9c";

    /// <summary>与官方结构一致的元数据夹具。</summary>
    private const string Metadata = """
        {
          "channel-version": "8.0",
          "latest-release": "8.0.31",
          "latest-release-date": "2026-08-11",
          "latest-runtime": "8.0.31",
          "latest-sdk": "8.0.4xx",
          "releases": [
            {
              "release-version": "8.0.31",
              "release-date": "2026-08-11",
              "runtime": {
                "version": "8.0.31",
                "files": [
                  {
                    "name": "dotnet-runtime-win-x64.exe",
                    "rid": "win-x64",
                    "url": "https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-win-x64.exe",
                    "hash": "2dc2f346a4bcb53ab15ab6ab84348c02eb31b0a3d4e8e06aac73b4c89f1b001af01943ed4c5735fa5585f1dd731a942d2034577dfe937f4454367ff2d6edf929"
                  },
                  {
                    "name": "dotnet-runtime-win-x64.zip",
                    "rid": "win-x64",
                    "url": "https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-win-x64.zip",
                    "hash": "9c55c58694676ee64b0eed2cd6d8cbf58b9aa8288420acc66841e15ca0099c75d4af0182d23a641c2342e5a151a325df4a12fa0bde2e47c0fb7e9a33e7b09896"
                  },
                  {
                    "name": "dotnet-runtime-win-x86.exe",
                    "rid": "win-x86",
                    "url": "https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-win-x86.exe",
                    "hash": "95584ce85dc6395d48b36e04465bf3e26f4f47e5bad677b514a8d75bc0ce025b2b905fa73f17078d9391988ef64f8cf0c0203117255f3d9d68a01fede7e3ea4c"
                  },
                  {
                    "name": "dotnet-runtime-win-x86.zip",
                    "rid": "win-x86",
                    "url": "https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-win-x86.zip",
                    "hash": "c8d8597dcbfa09fa267dde0e04d628360ade272f6ec1dc46f8896a58712bb6ec19e8b9ed1a9061786b99e3b4723cfb4c438e06268b1120c3a568bcae54551c9c"
                  }
                ]
              },
              "sdk": {
                "version": "8.0.4xx",
                "files": []
              }
            },
            {
              "release-version": "8.0.30",
              "runtime": {
                "version": "8.0.30",
                "files": []
              }
            }
          ]
        }
        """;

    [Theory]
    [InlineData("win-x64", WinX64ZipSha512, "dotnet-runtime-8.0.31-win-x64.zip")]
    [InlineData("win-x86", WinX86ZipSha512, "dotnet-runtime-8.0.31-win-x86.zip")]
    public void SelectFromJson_PicksTheZipForTheRequestedRid(
        string rid,
        string expectedSha512,
        string expectedName)
    {
        DotnetRuntimeArtifact artifact = DotnetRuntimeSelector.SelectFromJson(Metadata, rid, "8.0");

        Assert.Equal("8.0", artifact.Channel);
        Assert.Equal("8.0.31", artifact.Version);
        Assert.Equal(rid, artifact.Rid);
        Assert.Equal(expectedName, artifact.Name);
        Assert.Equal(expectedSha512, artifact.Sha512);
    }

    /// <summary>官方每个 rid 同时发布 <c>.exe</c>（安装器）与 <c>.zip</c>（可解压归档），两者 hash 不同。
    /// 选错就会"校验通过但解压失败"。</summary>
    [Fact]
    public void SelectFromJson_NeverPicksTheExeInstaller()
    {
        DotnetRuntimeArtifact artifact = DotnetRuntimeSelector.SelectFromJson(Metadata, "win-x64", "8.0");

        Assert.EndsWith(".zip", artifact.Name, StringComparison.Ordinal);
        Assert.EndsWith(".zip", artifact.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(".exe", artifact.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// 官方元数据的 <c>name</c> 字段**不带版本号**（实测 8.0.31 为 <c>dotnet-runtime-win-x64.zip</c>），
    /// 版本号只出现在 <c>url</c> 末段。归档名必须取 URL 末段，否则多版本共用一个名字，
    /// 日志里看不出到底下了哪个版本。
    /// </summary>
    [Fact]
    public void SelectFromJson_ArchiveNameCarriesTheVersionFromTheUrl()
    {
        DotnetRuntimeArtifact artifact = DotnetRuntimeSelector.SelectFromJson(Metadata, "win-x64", "8.0");

        Assert.Equal("dotnet-runtime-8.0.31-win-x64.zip", artifact.Name);
        Assert.Equal(Path.GetFileName(artifact.Url), artifact.Name);
        Assert.Contains(artifact.Version, artifact.Name, StringComparison.Ordinal);
    }

    /// <summary>旧版本条目不能被误选——必须严格按 latest-runtime 匹配。</summary>
    [Fact]
    public void SelectFromJson_IgnoresOlderReleases()
    {
        DotnetRuntimeArtifact artifact = DotnetRuntimeSelector.SelectFromJson(Metadata, "win-x64", "8.0");

        Assert.Equal("8.0.31", artifact.Version);
    }

    [Theory]
    [InlineData("\"latest-runtime\": \"8.0.31\"", "\"latest-runtime\": { \"version\": \"8.0.31\" }")]
    [InlineData("\"latest-runtime\": \"8.0.31\"", "\"latest-runtime\": { \"version\": \"8.0.31\", \"extra\": 1 }")]
    public void SelectFromJson_AcceptsObjectFormOfLatestRuntime(string from, string to)
    {
        string json = Metadata.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Metadata, json);

        DotnetRuntimeArtifact artifact = DotnetRuntimeSelector.SelectFromJson(json, "win-x64", "8.0");

        Assert.Equal("8.0.31", artifact.Version);
    }

    [Fact]
    public void SelectFromJson_RejectsChannelMismatch()
    {
        string json = Metadata.Replace(
            "\"channel-version\": \"8.0\"", "\"channel-version\": \"9.0\"", StringComparison.Ordinal);

        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson(json, "win-x64", "8.0"));

        Assert.Contains("频道不匹配", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectFromJson_RejectsLatestRuntimeFromAnotherChannel()
    {
        string json = Metadata.Replace(
            "\"latest-runtime\": \"8.0.31\"", "\"latest-runtime\": \"9.0.1\"", StringComparison.Ordinal);

        Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson(json, "win-x64", "8.0"));
    }

    [Fact]
    public void SelectFromJson_RejectsMissingLatestRuntime()
    {
        string json = Metadata.Replace(
            "\"latest-runtime\": \"8.0.31\",", string.Empty, StringComparison.Ordinal);

        Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson(json, "win-x64", "8.0"));
    }

    /// <summary>元数据里没有该 rid 的归档时必须明确失败，而不是回退到别的 rid。</summary>
    [Fact]
    public void SelectFromJson_RejectsRidWithNoArchive()
    {
        string json = Metadata.Replace(
            "\"rid\": \"win-x86\"", "\"rid\": \"win-arm64\"", StringComparison.Ordinal);
        Assert.NotEqual(Metadata, json);

        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson(json, "win-x86", "8.0"));

        Assert.Contains("找不到 win-x86 的 zip 归档", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectFromJson_RejectsUnsupportedRid()
    {
        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson(Metadata, "linux-x64", "8.0"));

        Assert.Contains("不支持的 .NET 运行时标识", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8", "不支持的 .NET 运行时频道")]
    [InlineData("8.0.1", "不支持的 .NET 运行时频道")]
    [InlineData("", "不支持的 .NET 运行时频道")]
    public void SelectFromJson_RejectsBadChannel(string channel, string expectedFragment)
    {
        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson(Metadata, "win-x64", channel));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------
    // URL 白名单
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/x.zip", "必须使用 HTTPS")]
    [InlineData("https://evil.example.com/dotnet/Runtime/8.0.31/x.zip", "不在白名单内")]
    [InlineData("https://builds.dotnet.microsoft.com.evil.com/x.zip", "不在白名单内")]
    [InlineData("https://builds.dotnet.microsoft.com:8443/dotnet/Runtime/x.zip", "不允许指定端口")]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/x.exe", "必须指向 zip")]
    [InlineData("not a url", "URL 无效")]
    [InlineData("", "URL 无效")]
    public void SelectFromJson_RejectsUntrustedArchiveUrl(string url, string expectedFragment)
    {
        string json = Metadata.Replace(
            "https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-win-x64.zip",
            url,
            StringComparison.Ordinal);

        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson(json, "win-x64", "8.0"));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectFromJson_AcceptsVisualStudioCdnHost()
    {
        const string json = """
            {
              "latest-runtime": "8.0.31",
              "runtime": {
                "version": "8.0.31",
                "files": [
                  {
                    "name": "dotnet-runtime-8.0.31-win-x86.zip",
                    "rid": "win-x86",
                    "url": "https://download.visualstudio.microsoft.com/download/pr/x/dotnet-runtime-8.0.31-win-x86.zip",
                    "hash": "c8d8597dcbfa09fa267dde0e04d628360ade272f6ec1dc46f8896a58712bb6ec19e8b9ed1a9061786b99e3b4723cfb4c438e06268b1120c3a568bcae54551c9c"
                  }
                ]
              }
            }
            """;

        DotnetRuntimeArtifact artifact = DotnetRuntimeSelector.SelectFromJson(json, "win-x86", "8.0");

        Assert.Contains("visualstudio.microsoft.com", artifact.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"hash\": \"9c55c58694676ee64b0eed2cd6d8cbf58b9aa8288420acc66841e15ca0099c75d4af0182d23a641c2342e5a151a325df4a12fa0bde2e47c0fb7e9a33e7b09896\"", "\"hash\": \"abc\"", "SHA-512 校验值无效")]
    [InlineData("\"hash\": \"9c55c58694676ee64b0eed2cd6d8cbf58b9aa8288420acc66841e15ca0099c75d4af0182d23a641c2342e5a151a325df4a12fa0bde2e47c0fb7e9a33e7b09896\"", "\"hash\": \"zz55c58694676ee64b0eed2cd6d8cbf58b9aa8288420acc66841e15ca0099c75d4af0182d23a641c2342e5a151a325df4a12fa0bde2e47c0fb7e9a33e7b09896\"", "SHA-512 校验值无效")]
    public void SelectFromJson_RejectsBadSha512(string from, string to, string expectedFragment)
    {
        string json = Metadata.Replace(from, to, StringComparison.Ordinal);

        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson(json, "win-x64", "8.0"));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectFromJson_RejectsMalformedJson()
    {
        Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson("{ not json", "win-x64", "8.0"));
    }

    [Fact]
    public void SelectFromJson_RejectsEmptyFileList()
    {
        const string json = """
            {
              "latest-runtime": "8.0.31",
              "runtime": { "version": "8.0.31", "files": [] }
            }
            """;

        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => DotnetRuntimeSelector.SelectFromJson(json, "win-x64", "8.0"));

        Assert.Contains("文件列表为空", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 真实元数据（1.5 MB）不入库；本地存在夹具时校验一次真实结构仍可被解析。
    /// </summary>
    /// <remarks>
    /// 这条测试的价值在于"官方结构变了要能第一时间发现"——夹具是我按实测结构手写的，
    /// 只有真实文件才能证伪它。抓取方式见 docs 里的 Spike 记录。
    /// </remarks>
    [Fact]
    public void SelectFromJson_ParsesRealUpstreamMetadata()
    {
        string? candidate = new[]
        {
            Path.Combine(Path.GetTempPath(), "connector-spike", "dotnet-8.0-releases.json"),
            Path.Combine(Path.GetTempPath(), "rel8.json"),
        }.FirstOrDefault(File.Exists);

        if (candidate is null)
        {
            return;
        }

        DotnetRuntimeArtifact artifact = DotnetRuntimeSelector.SelectFromJson(
            File.ReadAllText(candidate), "win-x86", "8.0");

        Assert.StartsWith("8.0.", artifact.Version, StringComparison.Ordinal);
        Assert.Equal("win-x86", artifact.Rid);
        Assert.EndsWith(".zip", artifact.Name, StringComparison.Ordinal);
        Assert.Equal(128, artifact.Sha512.Length);

        // 实测：官方 name 字段不带版本号，版本只在 URL 末段——归档名取 URL 末段。
        Assert.Contains(artifact.Version, artifact.Name, StringComparison.Ordinal);
        Assert.Equal(Path.GetFileName(artifact.Url), artifact.Name);
    }
}
