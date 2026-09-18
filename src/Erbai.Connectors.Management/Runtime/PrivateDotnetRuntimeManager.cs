using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erbai.Connectors.Management.Runtime;

/// <summary>
/// 为 framework-dependent 连接器准备私有 .NET 运行时的能力。
/// </summary>
/// <remarks>
/// 抽象成接口是为了让 <see cref="ConnectorInstaller"/> 不必依赖具体实现，
/// 单测可以给一个"永远就绪"的替身，而不必真的下载 30 MB 运行时。
/// </remarks>
public interface IPrivateRuntimeProvider
{
    /// <summary>确保指定 rid 的运行时可用，返回启动连接器所需的环境变量。</summary>
    Task<IReadOnlyDictionary<string, string>> PrepareEnvironmentAsync(
        string rid,
        string channel,
        CancellationToken cancellationToken = default);
}

/// <summary>版本目录内的标记文件内容。</summary>
internal sealed record DotnetRuntimeMarker
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("rid")]
    public string? Rid { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("sha512")]
    public string? Sha512 { get; init; }
}

/// <summary>
/// 私有共享 .NET 运行时管理器：按需下载、校验、解压、注入（决策 D-E，完全照搬参考实现）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有这个</b>：上游 kugou / qqmusic / folia 三个连接器是 <c>win-x86</c>
/// framework-dependent，在没有 x86 运行时的机器上直接启动会报
/// <c>Failed to resolve hostfxr.dll [not found]</c>（本机实测）。让用户自己去装 x86 运行时
/// 既不可靠也不可接受，所以由应用自己拉一份私有的。
/// </para>
/// <para>
/// 注入时把 <c>DOTNET_MULTILEVEL_LOOKUP=0</c> 一并设上很关键：否则 hostfxr 会继续往
/// 机器级安装目录找，私有运行时就成了摆设，连接器会跑在用户机器上那个版本未知的运行时上。
/// </para>
/// <para>
/// 并发：同一 (rid, channel) 的 provisioning 通过 <see cref="Lazy{T}"/> 合并成一次，
/// 避免多个连接器同时安装时下载多份。调用方各自的取消只影响"等待"，不中断共享任务——
/// 否则先取消的那个会把别人的下载也弄失败。
/// </para>
/// </remarks>
public sealed class PrivateDotnetRuntimeManager : IPrivateRuntimeProvider
{
    /// <summary>元数据地址模板。</summary>
    internal const string MetadataUrlTemplate =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/{0}/releases.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.Ordinal);
    private readonly PrivateDotnetRuntimeLayout _layout;
    private readonly HttpClient _http;
    private readonly ConnectorDownloader _downloader;
    private readonly Action<string>? _log;

    public PrivateDotnetRuntimeManager(
        PrivateDotnetRuntimeLayout layout,
        HttpClient httpClient,
        Action<string>? log = null,
        TimeSpan? retryDelay = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
        _downloader = new ConnectorDownloader(httpClient, retryDelay);
    }

    /// <summary>
    /// 确保指定 rid 的运行时可用，返回其版本目录路径。已装好时直接复用，不发任何请求。
    /// </summary>
    /// <exception cref="ConnectorManagementException">元数据、下载或校验任一步失败。</exception>
    public async Task<string> EnsureAsync(
        string rid,
        string channel = DotnetRuntimeSelector.DefaultChannel,
        CancellationToken cancellationToken = default)
    {
        DotnetRuntimeSelector.ValidateRid(rid);
        DotnetRuntimeSelector.ValidateChannel(channel);

        string key = $"{rid}:{channel}";

        // Lazy(ExecutionAndPublication) 保证工厂最多执行一次：GetOrAdd 的工厂本身可能被
        // 并发调用多次，但只有一个 Lazy 会被存入，因此也只有一个真正开始 provisioning。
        Lazy<Task<string>> lazy = _inFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<string>>(() => ProvisionAsync(rid, channel)));

        Task<string> provisioning = lazy.Value;

        // 完成后移除，让后续调用重新走一遍"先查已装"的廉价路径。
        // 注意 lambda 参数不能叫 `_`：那会遮蔽 `out _` 的弃元，让 TryRemove 推导出错误的类型。
        _ = provisioning.ContinueWith(
            completed =>
            {
                // 主动读一次 Exception 把故障标记为已观察：调用方可能已取消（WaitAsync 抛
                // OperationCanceledException 而不是 provisioning 的异常），此时没有任何人
                // await 过 provisioning，未观察异常会在 GC 时冒到 UnobservedTaskException。
                _ = completed.Exception;

                _inFlight.TryRemove(key, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return cancellationToken.CanBeCanceled
            ? await provisioning.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await provisioning.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> PrepareEnvironmentAsync(
        string rid,
        string channel,
        CancellationToken cancellationToken = default)
    {
        string root = await EnsureAsync(rid, channel, cancellationToken).ConfigureAwait(false);
        return BuildEnvironment(rid, root);
    }

    /// <summary>
    /// 构造启动 framework-dependent 连接器所需的环境变量。
    /// </summary>
    /// <remarks>
    /// 这几个变量解决的是**隔离确定性**，不是"否则跑不起来"。实测（2026-09-18，官方 8.0.31
    /// win-x64 归档）：直接调用 <c>&lt;私有根&gt;\dotnet.exe app.dll</c> 时，即使完全不注入任何
    /// <c>DOTNET_*</c>，muxer 也会相对自身位置找到 <c>shared/Microsoft.NETCore.App</c> 并正常
    /// 运行。注入的意义在于：
    /// <list type="bullet">
    ///   <item><description><c>DOTNET_ROOT</c>：明确声明框架根，避免宿主从别处推断出一个
    ///   非私有的根。</description></item>
    ///   <item><description><c>DOTNET_MULTILEVEL_LOOKUP=0</c>：禁止回退到机器级安装。少了它，
    ///   当连接器要求私有根里没有的版本时，宿主会静默用用户机器上恰好装着的运行时——
    ///   行为就变成"取决于用户装了什么"，这正是私有运行时想消除的。</description></item>
    ///   <item><description><c>DOTNET_ROOT_X86|X64</c>：x86 连接器（kugou/qqmusic/folia）在
    ///   64 位宿主里启动时需要它，否则会被当成 x64 解析。</description></item>
    /// </list>
    /// 注意私有归档是**纯运行时**（无 <c>sdk/</c>），因此 <c>dotnet --version</c> 这类 SDK 命令
    /// 一定失败（实测退出码 2147516561）；探活用 <c>dotnet --list-runtimes</c>，那是 muxer 命令。
    /// </remarks>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(string rid, string root)
    {
        DotnetRuntimeSelector.ValidateRid(rid);
        ArgumentException.ThrowIfNullOrEmpty(root);

        string resolved = Path.GetFullPath(root);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = resolved,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            [rid == "win-x86" ? "DOTNET_ROOT_X86" : "DOTNET_ROOT_X64"] = resolved,
        };
    }

    /// <summary>
    /// 校验 active.json 里记录的 runtimeRoot 是否可信：必须落在该 rid 的私有目录内，
    /// 且确实含 <c>dotnet.exe</c>。注入给 <see cref="ConnectorStore"/> 使用。
    /// </summary>
    public bool IsAcceptableRuntimeRoot(string rid, string root)
    {
        if (!ConnectorPlayers.IsSupportedRuntime(rid) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        if (!_layout.IsInsideRidRoot(rid, root))
        {
            return false;
        }

        return File.Exists(Path.Combine(root, "dotnet.exe"));
    }

    private async Task<string> ProvisionAsync(string rid, string channel)
    {
        string ridRoot = _layout.GetRidRoot(rid);
        Directory.CreateDirectory(ridRoot);

        string? cached = FindVerifiedRuntime(ridRoot, rid, channel);
        if (cached is not null)
        {
            Log($"[.NET 运行时] 复用 {rid}/{Path.GetFileName(cached)}");
            return cached;
        }

        string metadataUrl = string.Format(MetadataUrlTemplate, channel);
        string metadataJson = await FetchStringAsync(metadataUrl, ".NET 运行时发布元数据").ConfigureAwait(false);
        DotnetRuntimeArtifact artifact = DotnetRuntimeSelector.SelectFromJson(metadataJson, rid, channel);

        string nonce = Guid.NewGuid().ToString("N")[..16];
        string stagingDirectory = Path.Combine(ridRoot, $".staging-{channel}-{nonce}");
        string versionDirectory = _layout.GetVersionDirectory(rid, artifact.Version);

        try
        {
            long size = await ResolveArchiveSizeAsync(artifact.Url).ConfigureAwait(false);
            Log($"[.NET 运行时] {rid} 需要下载 {artifact.Name}（{size:N0} 字节）");

            byte[] archive = await _downloader.DownloadAsync(
                artifact.Url,
                size,
                fallbackUrl: null,
                progress: null,
                onRetry: retry => Log(
                    $"[.NET 运行时] {rid} 分块 {retry.Start}-{retry.End} "
                    + $"第 {retry.Attempt}/{retry.MaxAttempts} 次重试：{retry.Error}"),
                CancellationToken.None).ConfigureAwait(false);

            VerifySha512(archive, artifact.Sha512);

            SafeZipExtractor.Extract(new MemoryStream(archive, writable: false), stagingDirectory);
            VerifyRuntimeDirectory(stagingDirectory, artifact.Version);

            File.WriteAllText(
                Path.Combine(stagingDirectory, PrivateDotnetRuntimeLayout.MarkerFileName),
                JsonSerializer.Serialize(
                    new DotnetRuntimeMarker
                    {
                        SchemaVersion = 1,
                        Channel = channel,
                        Rid = rid,
                        Version = artifact.Version,
                        Sha512 = artifact.Sha512,
                    },
                    SerializerOptions));

            if (Directory.Exists(versionDirectory))
            {
                if (VerifyExistingRuntime(versionDirectory, rid, channel, artifact.Version))
                {
                    Log($"[.NET 运行时] {rid}/{artifact.Version} 已存在且完好，保留现有目录。");
                    return versionDirectory;
                }

                ConnectorInstallLayout.RemoveInside(ridRoot, versionDirectory);
            }

            Directory.Move(stagingDirectory, versionDirectory);
            Log($"[.NET 运行时] {rid}/{artifact.Version} 已就绪 → {versionDirectory}");

            return versionDirectory;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                TryRemoveInside(ridRoot, stagingDirectory);
            }
        }
    }

    /// <summary>
    /// 先试 HEAD，失败或拿不到合理大小时再用 1 字节 Range 探测。
    /// </summary>
    /// <remarks>
    /// 之所以不直接用 Range 分块下载而要先问大小：分块下载需要一个总量来切块与判完成。
    /// 两种探测都失败时宁可报错，也不做"未知大小的流式下载"——那会引入另一套落盘与超时逻辑，
    /// 而官方 CDN 实测两者都可用。
    /// </remarks>
    private async Task<long> ResolveArchiveSizeAsync(string url)
    {
        try
        {
            using HttpRequestMessage head = new(HttpMethod.Head, url);
            using HttpResponseMessage response = await _http
                .SendAsync(head, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode
                && response.Content.Headers.ContentLength is long length
                && length >= DotnetRuntimeSelector.MinArchiveBytes)
            {
                return length;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log($"[.NET 运行时] HEAD 探测失败，改用 Range 探测：{ex.Message}");
        }

        using HttpRequestMessage probe = new(HttpMethod.Get, url);
        probe.Headers.Range = new RangeHeaderValue(0, 0);
        using HttpResponseMessage probeResponse = await _http
            .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        if (probeResponse.StatusCode == HttpStatusCode.PartialContent
            && probeResponse.Content.Headers.ContentRange?.Length is long total
            && total >= DotnetRuntimeSelector.MinArchiveBytes)
        {
            return total;
        }

        throw new ConnectorManagementException(
            $".NET 运行时归档大小无法确定：HEAD 与 Range 探测均失败（HTTP {(int)probeResponse.StatusCode}）。");
    }

    private async Task<string> FetchStringAsync(string url, string label)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ConnectorManagementException($"{label}请求失败：HTTP {(int)response.StatusCode}。");
            }

            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (ConnectorManagementException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConnectorManagementException($"{label}不可达：{ex.Message}", ex);
        }
    }

    private static void VerifySha512(byte[] archive, string expectedHex)
    {
        byte[] actual = SHA512.HashData(archive);
        byte[] expected = Convert.FromHexString(expectedHex);

        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new ConnectorManagementException(".NET 运行时 SHA-512 校验失败。");
        }
    }

    /// <summary>目录必须同时具备 <c>dotnet.exe</c> 与对应版本的 <c>coreclr.dll</c>。</summary>
    private static void VerifyRuntimeDirectory(string directory, string version)
    {
        if (!File.Exists(Path.Combine(directory, "dotnet.exe")))
        {
            throw new ConnectorManagementException("私有 .NET 运行时缺少 dotnet.exe。");
        }

        string coreclr = Path.Combine(
            directory, "shared", "Microsoft.NETCore.App", version, "coreclr.dll");

        if (!File.Exists(coreclr))
        {
            throw new ConnectorManagementException($"私有 .NET 运行时缺少 coreclr.dll（{version}）。");
        }
    }

    /// <summary>按版本从高到低找第一个标记自洽且目录完整的运行时。</summary>
    private string? FindVerifiedRuntime(string ridRoot, string rid, string channel)
    {
        if (!Directory.Exists(ridRoot))
        {
            return null;
        }

        List<string> candidates = [.. Directory.GetDirectories(ridRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not null && IsVersionSegment(name))
            .Select(name => name!)];

        // 版本从高到低：优先复用最新的那个完好运行时。
        candidates.Sort((left, right) => CompareVersions(right, left));

        foreach (string version in candidates)
        {
            string directory = Path.Combine(ridRoot, version);
            if (VerifyExistingRuntime(directory, rid, channel, version))
            {
                return directory;
            }
        }

        return null;
    }

    private bool VerifyExistingRuntime(string directory, string rid, string channel, string version)
    {
        try
        {
            DotnetRuntimeMarker? marker = JsonSerializer.Deserialize<DotnetRuntimeMarker>(
                File.ReadAllText(_layout.GetMarkerPath(rid, version)));

            if (marker is null
                || marker.SchemaVersion != 1
                || !string.Equals(marker.Channel, channel, StringComparison.Ordinal)
                || !string.Equals(marker.Rid, rid, StringComparison.Ordinal)
                || !string.Equals(marker.Version, version, StringComparison.Ordinal)
                || string.IsNullOrEmpty(marker.Sha512)
                || marker.Sha512.Length != 128)
            {
                return false;
            }

            VerifyRuntimeDirectory(directory, version);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ConnectorManagementException)
        {
            return false;
        }
    }

    private static bool IsVersionSegment(string value)
    {
        int dots = 0;

        foreach (char c in value)
        {
            if (c == '.')
            {
                dots++;
            }
            else if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return dots == 2 && value[0] != '.';
    }

    private static int CompareVersions(string left, string right)
    {
        string[] leftParts = left.Split('.');
        string[] rightParts = right.Split('.');

        for (int i = 0; i < 3; i++)
        {
            int comparison = int.Parse(leftParts[i]).CompareTo(int.Parse(rightParts[i]));
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private void TryRemoveInside(string parent, string target)
    {
        try
        {
            ConnectorInstallLayout.RemoveInside(parent, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"[.NET 运行时] 清理临时目录失败（已忽略）：{target}");
        }
    }

    private void Log(string message) => _log?.Invoke(message);
}
