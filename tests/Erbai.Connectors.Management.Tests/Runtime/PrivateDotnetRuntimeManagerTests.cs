using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Erbai.Connectors.Management.Runtime;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// <see cref="PrivateDotnetRuntimeLayout"/> 的路径与安全约束。
/// </summary>
public class PrivateDotnetRuntimeLayoutTests
{
    [Fact]
    public void Layout_BuildsRidAndVersionDirectories()
    {
        PrivateDotnetRuntimeLayout layout = new(@"C:\temp\rt");

        Assert.Equal(Path.Combine(@"C:\temp\rt", "win-x86"), layout.GetRidRoot("win-x86"));
        Assert.Equal(
            Path.Combine(@"C:\temp\rt", "win-x86", "8.0.31"),
            layout.GetVersionDirectory("win-x86", "8.0.31"));
        Assert.Equal(
            Path.Combine(@"C:\temp\rt", "win-x86", "8.0.31", PrivateDotnetRuntimeLayout.MarkerFileName),
            layout.GetMarkerPath("win-x86", "8.0.31"));
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("win-arm64")]
    [InlineData("")]
    public void GetRidRoot_RejectsUnsupportedRid(string rid)
    {
        PrivateDotnetRuntimeLayout layout = new(@"C:\temp\rt");

        Assert.Throws<ConnectorManagementException>(() => layout.GetRidRoot(rid));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("8.0.31/../../x")]
    [InlineData("8.0.31\\x")]
    [InlineData("v8.0.31")]
    [InlineData(".8.0.31")]
    [InlineData("")]
    public void GetVersionDirectory_RejectsUnsafeVersion(string version)
    {
        PrivateDotnetRuntimeLayout layout = new(@"C:\temp\rt");

        Assert.ThrowsAny<Exception>(() => layout.GetVersionDirectory("win-x86", version));
    }

    [Fact]
    public void IsInsideRidRoot_RespectsDirectoryBoundaries()
    {
        PrivateDotnetRuntimeLayout layout = new(@"C:\temp\rt");

        Assert.True(layout.IsInsideRidRoot("win-x86", Path.Combine(@"C:\temp\rt", "win-x86", "8.0.31")));

        // 前缀相同但不同目录：不能被误判为在 win-x86 之内。
        Assert.False(layout.IsInsideRidRoot("win-x86", Path.Combine(@"C:\temp\rt", "win-x866", "8.0.31")));

        // 另一个 rid 的目录也不算。
        Assert.False(layout.IsInsideRidRoot("win-x86", Path.Combine(@"C:\temp\rt", "win-x64", "8.0.31")));
    }
}

/// <summary>
/// <see cref="PrivateDotnetRuntimeManager"/> 的 provisioning 行为（用假 CDN 驱动）。
/// </summary>
/// <remarks>
/// 真实运行时有 29–32 MB，不入库；这里用"结构正确、体积过 1 MB 下限"的合成归档代替。
/// 体积下限（<see cref="DotnetRuntimeSelector.MinArchiveBytes"/>）必须真的满足，
/// 否则会走不到分块下载分支，测出来的东西就不是生产路径了。
/// </remarks>
public class PrivateDotnetRuntimeManagerTests : IDisposable
{
    private const string RuntimeVersion = "8.0.31";
    private const string Channel = "8.0";
    private const string ZipUrl =
        "https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-win-x86.zip";

    private readonly TempDirectory _temp = new();
    private readonly PrivateDotnetRuntimeLayout _layout;

    public PrivateDotnetRuntimeManagerTests()
    {
        _layout = new PrivateDotnetRuntimeLayout(_temp.Combine("dotnet-runtimes"));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task EnsureAsync_ProvisionsRuntimeFromMetadata()
    {
        byte[] zip = RuntimeZip.Value;
        StubHttpMessageHandler handler = CreateHandler(zip);

        string root = await CreateManager(handler).EnsureAsync("win-x86", Channel);

        Assert.Equal(_layout.GetVersionDirectory("win-x86", RuntimeVersion), root);
        Assert.True(File.Exists(Path.Combine(root, "dotnet.exe")));
        Assert.True(File.Exists(Path.Combine(root, "shared", "Microsoft.NETCore.App", RuntimeVersion, "coreclr.dll")));
        Assert.True(File.Exists(Path.Combine(root, PrivateDotnetRuntimeLayout.MarkerFileName)));

        AssertNoStagingLeftovers();
    }

    /// <summary>标记文件必须自洽（含 sha512），否则"半装成功"的目录会被误当可用。</summary>
    [Fact]
    public async Task EnsureAsync_WritesSelfDescribingMarker()
    {
        byte[] zip = RuntimeZip.Value;
        string root = await CreateManager(CreateHandler(zip)).EnsureAsync("win-x86", Channel);

        string marker = File.ReadAllText(Path.Combine(root, PrivateDotnetRuntimeLayout.MarkerFileName));

        Assert.Contains("\"schemaVersion\": 1", marker, StringComparison.Ordinal);
        Assert.Contains($"\"channel\": \"{Channel}\"", marker, StringComparison.Ordinal);
        Assert.Contains("\"rid\": \"win-x86\"", marker, StringComparison.Ordinal);
        Assert.Contains($"\"version\": \"{RuntimeVersion}\"", marker, StringComparison.Ordinal);
        Assert.Contains(Sha512Of(zip), marker, StringComparison.Ordinal);
    }

    /// <summary>第二次调用必须直接复用，不再发任何请求。</summary>
    [Fact]
    public async Task EnsureAsync_ReusesInstalledRuntimeWithoutNetwork()
    {
        byte[] zip = RuntimeZip.Value;
        StubHttpMessageHandler handler = CreateHandler(zip);
        PrivateDotnetRuntimeManager manager = CreateManager(handler);

        await manager.EnsureAsync("win-x86", Channel);
        int callsAfterFirst = handler.CallCount;
        Assert.True(callsAfterFirst > 0);

        string root = await manager.EnsureAsync("win-x86", Channel);

        Assert.Equal(callsAfterFirst, handler.CallCount);
        Assert.Equal(_layout.GetVersionDirectory("win-x86", RuntimeVersion), root);
    }

    [Fact]
    public async Task EnsureAsync_RejectsSha512Mismatch()
    {
        byte[] zip = RuntimeZip.Value;

        // 元数据声明的 hash 与归档实际 hash 不符。
        StubHttpMessageHandler handler = CreateHandler(zip, declaredSha512: new string('a', 128));

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateManager(handler).EnsureAsync("win-x86", Channel));

        Assert.Contains("SHA-512 校验失败", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_layout.GetVersionDirectory("win-x86", RuntimeVersion)));
        AssertNoStagingLeftovers();
    }

    /// <summary>归档结构不完整（缺 coreclr.dll）时必须失败，不能留下一个"看起来装好了"的目录。</summary>
    [Fact]
    public async Task EnsureAsync_RejectsArchiveMissingCoreclr()
    {
        byte[] padding = new byte[1_200_000];
        Random.Shared.NextBytes(padding);

        byte[] zip = ArchiveBuilder.CreateZipBytes(
            ("dotnet.exe", "stub"u8.ToArray()),
            ("padding.bin", padding));

        StubHttpMessageHandler handler = CreateHandler(zip);

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateManager(handler).EnsureAsync("win-x86", Channel));

        Assert.Contains("coreclr.dll", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_layout.GetVersionDirectory("win-x86", RuntimeVersion)));
        AssertNoStagingLeftovers();
    }

    [Fact]
    public async Task EnsureAsync_RejectsArchiveWithoutDotnetExe()
    {
        byte[] padding = new byte[1_200_000];
        Random.Shared.NextBytes(padding);

        byte[] zip = ArchiveBuilder.CreateZipBytes(
            ($"shared/Microsoft.NETCore.App/{RuntimeVersion}/coreclr.dll", "stub"u8.ToArray()),
            ("padding.bin", padding));

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateManager(CreateHandler(zip)).EnsureAsync("win-x86", Channel));

        Assert.Contains("dotnet.exe", ex.Message, StringComparison.Ordinal);
        AssertNoStagingLeftovers();
    }

    /// <summary>
    /// 并发调用必须合并成一次 provisioning：否则多个连接器同时安装会重复下载 30 MB。
    /// </summary>
    [Fact]
    public async Task EnsureAsync_CoalescesConcurrentRequests()
    {
        byte[] zip = RuntimeZip.Value;
        StubHttpMessageHandler handler = CreateHandler(zip);
        PrivateDotnetRuntimeManager manager = CreateManager(handler);

        string[] roots = await Task.WhenAll(
            manager.EnsureAsync("win-x86", Channel),
            manager.EnsureAsync("win-x86", Channel),
            manager.EnsureAsync("win-x86", Channel));

        Assert.All(roots, root => Assert.Equal(_layout.GetVersionDirectory("win-x86", RuntimeVersion), root));

        // 元数据只应被拉一次（HEAD + 分块 GET 不计入）。
        Assert.Equal(1, handler.CountRequests(url => url.Contains("releases.json", StringComparison.Ordinal)));
    }

    /// <summary>某个调用方取消只应终止它自己的等待，不应把共享的 provisioning 一起弄失败。</summary>
    [Fact]
    public async Task EnsureAsync_CallerCancellationDoesNotBreakOtherCallers()
    {
        byte[] zip = RuntimeZip.Value;
        StubHttpMessageHandler handler = CreateHandler(zip);

        // 必须用闸门把请求按在"飞行中"：内存替身是同步完成的，provisioning 会在
        // 首个调用方的线程上一口气跑完，此时 WaitAsync(已取消的 token) 对已完成任务
        // 不会抛——断言就变成了竞态。闸门让取消发生在真正的进行中状态。
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Gate = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        PrivateDotnetRuntimeManager manager = CreateManager(handler);

        using CancellationTokenSource cts = new();
        Task<string> cancelled = manager.EnsureAsync("win-x86", Channel, cts.Token);
        Task<string> survivor = manager.EnsureAsync("win-x86", Channel);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        // 取消不能牵连共享任务：放行后幸存者仍须拿到可用运行时。
        release.SetResult();

        string root = await survivor.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(_layout.GetVersionDirectory("win-x86", RuntimeVersion), root);
        Assert.True(File.Exists(Path.Combine(root, "dotnet.exe")));
    }

    [Fact]
    public async Task EnsureAsync_RejectsUnsupportedRidAndChannel()
    {
        StubHttpMessageHandler handler = CreateHandler(RuntimeZip.Value);
        PrivateDotnetRuntimeManager manager = CreateManager(handler);

        await Assert.ThrowsAsync<ConnectorManagementException>(
            () => manager.EnsureAsync("linux-x64", Channel));

        await Assert.ThrowsAsync<ConnectorManagementException>(
            () => manager.EnsureAsync("win-x86", "8"));

        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>已装目录的标记与请求不符（例如换了 channel）时必须重新 provisioning。</summary>
    [Fact]
    public async Task EnsureAsync_ReprovisionsWhenMarkerDoesNotMatch()
    {
        byte[] zip = RuntimeZip.Value;
        PrivateDotnetRuntimeManager manager = CreateManager(CreateHandler(zip));

        string root = await manager.EnsureAsync("win-x86", Channel);

        // 把标记改成另一个 rid 的，模拟目录被外部破坏。
        File.WriteAllText(
            Path.Combine(root, PrivateDotnetRuntimeLayout.MarkerFileName),
            """{"schemaVersion":1,"channel":"8.0","rid":"win-x64","version":"8.0.31","sha512":"00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"}""");

        string again = await manager.EnsureAsync("win-x86", Channel);

        Assert.Equal(root, again);
        Assert.Contains(
            "\"rid\": \"win-x86\"",
            File.ReadAllText(Path.Combine(again, PrivateDotnetRuntimeLayout.MarkerFileName)),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------
    // 环境变量注入
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("win-x86", "DOTNET_ROOT_X86")]
    [InlineData("win-x64", "DOTNET_ROOT_X64")]
    public void BuildEnvironment_InjectsRootAndDisablesMultilevelLookup(string rid, string expectedRidVariable)
    {
        IReadOnlyDictionary<string, string> environment =
            PrivateDotnetRuntimeManager.BuildEnvironment(rid, @"C:\rt\8.0.31");

        Assert.Equal(@"C:\rt\8.0.31", environment["DOTNET_ROOT"]);

        // 不关掉 multilevel lookup，hostfxr 会继续往机器级目录找，私有运行时就成了摆设。
        Assert.Equal("0", environment["DOTNET_MULTILEVEL_LOOKUP"]);
        Assert.Equal(@"C:\rt\8.0.31", environment[expectedRidVariable]);
    }

    [Fact]
    public async Task PrepareEnvironmentAsync_ReturnsInjectionForProvisionedRuntime()
    {
        byte[] zip = RuntimeZip.Value;
        IReadOnlyDictionary<string, string> environment =
            await CreateManager(CreateHandler(zip)).PrepareEnvironmentAsync("win-x86", Channel);

        Assert.Equal(_layout.GetVersionDirectory("win-x86", RuntimeVersion), environment["DOTNET_ROOT"]);
        Assert.Equal("0", environment["DOTNET_MULTILEVEL_LOOKUP"]);
    }

    [Fact]
    public void IsAcceptableRuntimeRoot_RequiresPrivateDirectoryAndDotnetExe()
    {
        PrivateDotnetRuntimeManager manager = CreateManager(CreateHandler(RuntimeZip.Value));

        string good = _layout.GetVersionDirectory("win-x86", RuntimeVersion);
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, "dotnet.exe"), "stub");
        Assert.True(manager.IsAcceptableRuntimeRoot("win-x86", good));

        // 在私有目录内但缺 dotnet.exe。
        string empty = _layout.GetVersionDirectory("win-x86", "8.0.30");
        Directory.CreateDirectory(empty);
        Assert.False(manager.IsAcceptableRuntimeRoot("win-x86", empty));

        // 私有目录之外。
        Assert.False(manager.IsAcceptableRuntimeRoot("win-x86", _temp.Path));

        // rid 与目录不匹配。
        Assert.False(manager.IsAcceptableRuntimeRoot("win-x64", good));
    }

    // ---------------------------------------------------------------------------------
    // 真实运行时（默认跳过）
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// 真实下载官方 8.0 运行时并完成校验解压（约 29–33 MB）。默认跳过，避免拖慢常规测试；
    /// 需要时置 <c>ERBAI_TEST_REAL_RUNTIME=1</c> 运行。
    /// </summary>
    /// <remarks>
    /// 刻意不写死版本号：官方 <c>latest-runtime</c> 会随补丁版本上浮，写死会让这个测试
    /// 过一段时间就变成假失败。版本从返回路径推导。
    /// </remarks>
    [Fact]
    public async Task EnsureAsync_DownloadsRealRuntimeFromUpstream()
    {
        if (Environment.GetEnvironmentVariable("ERBAI_TEST_REAL_RUNTIME") != "1")
        {
            return;
        }

        using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
        PrivateDotnetRuntimeManager manager = new(_layout, http, message => Console.WriteLine(message));

        string root = await manager.EnsureAsync("win-x86", Channel);
        string version = Path.GetFileName(root);

        Assert.True(manager.IsAcceptableRuntimeRoot("win-x86", root));
        Assert.Equal(_layout.GetVersionDirectory("win-x86", version), root);
        Assert.True(File.Exists(Path.Combine(root, "dotnet.exe")));
        Assert.True(File.Exists(Path.Combine(root, "host", "fxr", version, "hostfxr.dll")));
        Assert.True(File.Exists(
            Path.Combine(root, "shared", "Microsoft.NETCore.App", version, "coreclr.dll")));
        Assert.True(File.Exists(Path.Combine(root, PrivateDotnetRuntimeLayout.MarkerFileName)));

        // 注入的环境必须真的能驱动这个私有运行时——这是"运行时可用"的终局判据。
        IReadOnlyDictionary<string, string> environment = PrivateDotnetRuntimeManager.BuildEnvironment("win-x86", root);
        Assert.Equal(root, environment["DOTNET_ROOT"]);
        Assert.Equal("0", environment["DOTNET_MULTILEVEL_LOOKUP"]);
        Assert.Equal(root, environment["DOTNET_ROOT_X86"]);

        // 探活必须用 muxer 命令 --list-runtimes。私有归档是纯运行时（无 sdk/），
        // --version 是 SDK 命令，在纯运行时上必然失败（实测退出码 2147516561，
        // 提示 "No .NET SDKs were found"）——用它探活会得到一个假失败。
        string listed = await RunDotnetAsync(root, environment, "--list-runtimes");
        Assert.Contains($"Microsoft.NETCore.App {version} [{Path.Combine(root, "shared", "Microsoft.NETCore.App")}]",
            listed,
            StringComparison.Ordinal);
    }

    private static async Task<string> RunDotnetAsync(
        string root,
        IReadOnlyDictionary<string, string> environment,
        string arguments)
    {
        System.Diagnostics.ProcessStartInfo startInfo = new(Path.Combine(root, "dotnet.exe"), arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // 工作目录必须在仓库之外：仓库根的 global.json 锁了 SDK，会让任何 SDK 命令
            // 走上另一条错误路径，干扰对运行时本身的判断。
            WorkingDirectory = Path.GetTempPath(),
        };

        foreach ((string key, string value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using System.Diagnostics.Process probe = System.Diagnostics.Process.Start(startInfo)!;
        string stdout = await probe.StandardOutput.ReadToEndAsync();
        string stderr = await probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync();

        Assert.True(probe.ExitCode == 0, $"dotnet {arguments} 退出码 {probe.ExitCode}：{stderr}");

        return stdout;
    }

    // ---------------------------------------------------------------------------------

    private PrivateDotnetRuntimeManager CreateManager(StubHttpMessageHandler handler) =>
        new(_layout, new HttpClient(handler), retryDelay: TimeSpan.Zero);

    /// <summary>结构与真实运行时一致、体积过 1 MB 下限的合成归档（惰性构造，多个用例共用）。</summary>
    private static readonly Lazy<byte[]> RuntimeZip = new(() =>
    {
        // 用不可压缩的随机数据把归档撑到 1 MB 以上，否则会走不到分块下载分支。
        byte[] padding = new byte[1_200_000];
        Random.Shared.NextBytes(padding);

        return ArchiveBuilder.CreateZipBytes(
            ("dotnet.exe", "stub-dotnet"u8.ToArray()),
            ("host/fxr/" + RuntimeVersion + "/hostfxr.dll", "stub-hostfxr"u8.ToArray()),
            ($"shared/Microsoft.NETCore.App/{RuntimeVersion}/coreclr.dll", "stub-coreclr"u8.ToArray()),
            ("padding.bin", padding));
    });

    private static StubHttpMessageHandler CreateHandler(byte[] zip, string? declaredSha512 = null)
    {
        string sha512 = declaredSha512 ?? Sha512Of(zip);
        string metadata = BuildMetadata(sha512);

        return new StubHttpMessageHandler((request, _) =>
        {
            string url = request.RequestUri!.AbsoluteUri;

            if (url.Contains("releases.json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(metadata, System.Text.Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Head)
            {
                // 只用到 Content-Length；内容不参与。
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            }

            RangeItemHeaderValue? range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range?.From is not null && range.To is not null)
            {
                long start = range.From.Value;
                long end = range.To.Value;
                return StubHttpMessageHandler.PartialContent(zip[(int)start..(int)(end + 1)], start, zip.LongLength);
            }

            return StubHttpMessageHandler.WholeContent(zip);
        });
    }

    private static string BuildMetadata(string sha512) => $$"""
        {
          "channel-version": "{{Channel}}",
          "latest-runtime": "{{RuntimeVersion}}",
          "releases": [
            {
              "release-version": "{{RuntimeVersion}}",
              "runtime": {
                "version": "{{RuntimeVersion}}",
                "files": [
                  {
                    "name": "dotnet-runtime-{{RuntimeVersion}}-win-x86.exe",
                    "rid": "win-x86",
                    "url": "https://builds.dotnet.microsoft.com/dotnet/Runtime/{{RuntimeVersion}}/dotnet-runtime-{{RuntimeVersion}}-win-x86.exe",
                    "hash": "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
                  },
                  {
                    "name": "dotnet-runtime-{{RuntimeVersion}}-win-x86.zip",
                    "rid": "win-x86",
                    "url": "{{ZipUrl}}",
                    "hash": "{{sha512}}"
                  }
                ]
              }
            }
          ]
        }
        """;

    private static string Sha512Of(byte[] data) => Convert.ToHexString(SHA512.HashData(data)).ToLowerInvariant();

    private void AssertNoStagingLeftovers()
    {
        string ridRoot = _layout.GetRidRoot("win-x86");
        if (!Directory.Exists(ridRoot))
        {
            return;
        }

        string[] leftovers = [.. Directory.GetDirectories(ridRoot)
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith(".staging-", StringComparison.Ordinal))!];

        Assert.Empty(leftovers);
    }
}
