using Erbai.Connectors.Management.Catalog;
using Erbai.Connectors.Management.Runtime;

namespace Erbai.Connectors.Management;

/// <summary>安装结果。</summary>
public sealed record ConnectorInstallResult(
    string PlayerKey,
    string Version,
    string ExecutablePath,
    bool ReusedExisting,
    bool Verified);

/// <summary>
/// 连接器安装器：下载 → 校验 → 解压到 staging → 定位 exe → 健康检查 → 原子替换 → 写 active.json。
/// </summary>
/// <remarks>
/// <para>
/// <b>任一步失败都会回滚</b>：新目录被删掉、旧目录从 <c>.backup-*</c> 改回来。这是"更新连接器"
/// 场景的硬要求——用户已经有一个能用的版本，升级失败绝不能把可用的那个也弄没。
/// </para>
/// <para>
/// 替换用 <c>Directory.Move</c>（同卷 rename）而非"先删后拷"：rename 是原子的，
/// 不会出现"旧版本已删、新版本拷了一半"的中间态。staging / backup 都刻意放在
/// <c>&lt;connectorRoot&gt;</c> 下，保证与目标目录同卷。
/// </para>
/// <para>
/// 与参考实现的一处差异（有意推广）：参考实现只在 netease 上特殊处理"旧备份目录被短暂占用
/// 导致删除失败"，其余平台直接失败。本实现把"删除 backup 失败"统一降级为<b>保留该目录并记日志</b>，
/// 并在下次安装前统一清理所有 <c>.backup-*</c>——因为这是文件锁的通用现象（连接器进程退出后
/// 句柄释放有延迟），不是网易云独有。
/// </para>
/// </remarks>
public sealed class ConnectorInstaller
{
    private const string BackupPrefix = ".backup-";
    private const string StagingPrefix = ".staging-";

    private readonly ConnectorInstallLayout _layout;
    private readonly IConnectorStore _store;
    private readonly ConnectorDownloader _downloader;
    private readonly IConnectorHealthChecker _healthChecker;
    private readonly IPrivateRuntimeProvider? _runtimeProvider;
    private readonly Action<string>? _log;

    /// <param name="runtimeProvider">
    /// 私有 .NET 运行时提供方。给了它，framework-dependent 连接器在安装时会自动把对应 rid
    /// 的运行时备好并注入环境变量（决策 D-E：用户零操作）。为 <see langword="null"/> 时
    /// 沿用调用方传入的环境变量。
    /// </param>
    public ConnectorInstaller(
        ConnectorInstallLayout layout,
        IConnectorStore store,
        ConnectorDownloader downloader,
        IConnectorHealthChecker healthChecker,
        Action<string>? log = null,
        IPrivateRuntimeProvider? runtimeProvider = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
        _log = log;
        _runtimeProvider = runtimeProvider;
    }

    /// <summary>从上游清单条目安装或更新。</summary>
    /// <param name="runtimeEnvironment">
    /// 启动连接器时要注入的环境变量（P3 的私有运行时在此注入 <c>DOTNET_ROOT</c> 等）。
    /// </param>
    public async Task<ConnectorInstallResult> InstallAsync(
        string playerKey,
        ConnectorCatalogEntry entry,
        bool forceReinstall = false,
        IReadOnlyDictionary<string, string>? runtimeEnvironment = null,
        IProgress<ConnectorDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        ConnectorPackage package = entry.Package
            ?? throw new ConnectorManagementException($"连接器 {playerKey} 的清单条目缺少 package。");

        string version = entry.Version
            ?? throw new ConnectorManagementException($"连接器 {playerKey} 的清单条目缺少 version。");

        string? fallbackUrl = package.Asset is null
            ? null
            : ConnectorPlayers.BuildGitHubReleaseUrl(playerKey, version, package.Asset);

        byte[] archive = await _downloader.DownloadAsync(
            package.DownloadUrl!,
            package.Size,
            fallbackUrl,
            progress,
            retry => Log($"[{playerKey}] 分块 {retry.Start}-{retry.End} 第 {retry.Attempt}/{retry.MaxAttempts} 次重试：{retry.Error}"),
            cancellationToken).ConfigureAwait(false);

        return await InstallCoreAsync(
            playerKey,
            version,
            package,
            archive,
            deployment: package.Deployment!,
            runtimeRid: package.Runtime,
            runtimeChannel: package.RuntimeChannel,
            forceReinstall: forceReinstall,
            runtimeEnvironment: runtimeEnvironment,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从本地 ZIP 安装（决策 D6）。走的是<b>同一条</b>管线（校验 + 解压 + 健康检查 + 原子替换），
    /// 供离线或上游不可达时使用。
    /// </summary>
    /// <param name="expectedPackage">
    /// 若能拿到该版本的清单条目，传入它即可完成完整校验（size + SHA-256 + Ed25519）。
    /// </param>
    /// <param name="allowUnverified">
    /// 拿不到清单条目时必须显式置为 <see langword="true"/> 才允许安装，且会在 active.json 里
    /// 记录 <c>verified: false</c>。默认拒绝——静默接受未签名包会让整套签名校验形同虚设。
    /// </param>
    /// <param name="runtimeRid">
    /// 清单条目缺失时显式指定的运行时标识（<c>win-x64</c> / <c>win-x86</c>）。
    /// 有 <paramref name="expectedPackage"/> 时以条目里的 <c>package.runtime</c> 为准，本参数被忽略。
    /// </param>
    public async Task<ConnectorInstallResult> InstallFromLocalArchiveAsync(
        string playerKey,
        string archivePath,
        string version,
        ConnectorPackage? expectedPackage = null,
        bool allowUnverified = false,
        string? deployment = null,
        string? runtimeRid = null,
        IReadOnlyDictionary<string, string>? runtimeEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        ArgumentException.ThrowIfNullOrEmpty(version);

        if (!File.Exists(archivePath))
        {
            throw new ConnectorManagementException($"本地安装包不存在：{archivePath}");
        }

        if (expectedPackage is null && !allowUnverified)
        {
            throw new ConnectorManagementException(
                "无法校验本地安装包：未提供清单条目（缺少 size / SHA-256 / Ed25519 签名）。"
                + "如确认要安装未校验的包，请显式允许。");
        }

        // 拿不到清单条目时不能猜部署方式：猜错会写出一个"读不回来"的 active.json
        // （framework-dependent 必须有 rid，而本地包无从得知）。
        if (expectedPackage is null && deployment is null)
        {
            throw new ConnectorManagementException(
                "本地安装包缺少清单元数据时，必须显式指定部署方式（self-contained 或 framework-dependent）。");
        }

        byte[] archive = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);

        // 上面的校验保证了：expectedPackage 为空时 deployment 一定非空。
        string resolvedDeployment = deployment
            ?? expectedPackage?.Deployment
            ?? throw new ConnectorManagementException("无法确定安装包的部署方式。");

        return await InstallCoreAsync(
            playerKey,
            version,
            expectedPackage,
            archive,
            deployment: resolvedDeployment,
            runtimeRid: expectedPackage?.Runtime ?? runtimeRid,
            runtimeChannel: expectedPackage?.RuntimeChannel,
            forceReinstall: true,
            runtimeEnvironment: runtimeEnvironment,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConnectorInstallResult> InstallCoreAsync(
        string playerKey,
        string version,
        ConnectorPackage? package,
        byte[] archive,
        string deployment,
        string? runtimeRid,
        string? runtimeChannel,
        bool forceReinstall,
        IReadOnlyDictionary<string, string>? runtimeEnvironment,
        CancellationToken cancellationToken)
    {
        string connectorRoot = _layout.GetConnectorRoot(playerKey);
        string versionDirectory = _layout.GetVersionDirectory(playerKey, version);

        // 先做零副作用的参数自洽校验：宁可在下载解压之前失败，也不要写出一个
        // 读不回来的 active.json（framework-dependent 缺 rid 时 <see cref="ConnectorStore"/>
        // 会判定记录非法）。
        if (deployment is not ("framework-dependent" or "self-contained"))
        {
            throw new ConnectorManagementException($"不支持的部署方式：{deployment}。");
        }

        if (deployment == "framework-dependent" && string.IsNullOrEmpty(runtimeRid))
        {
            throw new ConnectorManagementException(
                "framework-dependent 安装必须提供运行时标识（清单 package.runtime）。");
        }

        // framework-dependent 连接器需要私有 .NET 运行时（决策 D-E）。给了 provider 就自动备好，
        // 用户零操作——否则 x86 连接器在只有 x64 运行时的机器上会直接起不来（F4 实测
        // hostfxr.dll not found），而"让用户自己去装 x86 运行时"既不可靠也不该要求。
        IReadOnlyDictionary<string, string>? effectiveEnvironment = runtimeEnvironment;

        if (deployment == "framework-dependent" && _runtimeProvider is not null)
        {
            effectiveEnvironment = await _runtimeProvider
                .PrepareEnvironmentAsync(
                    runtimeRid!,
                    runtimeChannel ?? DotnetRuntimeSelector.DefaultChannel,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // 已装同版本且文件仍在 → 不重复下载安装，但仍跑一次健康检查，
        // 这样"文件在但跑不起来"也能被发现并触发重装。
        if (!forceReinstall && await TryReuseAsync(
                playerKey, version, deployment, effectiveEnvironment, cancellationToken)
            .ConfigureAwait(false) is { } reused)
        {
            return reused;
        }

        Directory.CreateDirectory(connectorRoot);

        string nonce = Guid.NewGuid().ToString("N")[..16];
        string stagingDirectory = Path.Combine(connectorRoot, $"{StagingPrefix}{version}-{nonce}");
        string backupDirectory = Path.Combine(connectorRoot, $"{BackupPrefix}{version}-{nonce}");

        bool installedNewDirectory = false;
        bool movedPreviousDirectory = false;

        try
        {
            if (package is not null)
            {
                ConnectorPackageVerifier.VerifyOrThrow(package, archive);
            }
            else
            {
                Log($"[{playerKey}] 本地安装包未做签名校验（用户已确认）。");
            }

            SafeZipExtractor.Extract(new MemoryStream(archive, writable: false), stagingDirectory);

            string stagedExecutableName = ConnectorPlayers.GetExecutableNames(playerKey)
                .FirstOrDefault(name => File.Exists(Path.Combine(stagingDirectory, name)))
                ?? throw new ConnectorManagementException(
                    $"发布包内缺少预期的可执行文件：{string.Join(" / ", ConnectorPlayers.GetExecutableNames(playerKey))}。");

            string stagedExecutable = Path.Combine(stagingDirectory, stagedExecutableName);

            ConnectorHealthResult health = await _healthChecker.CheckAsync(
                stagedExecutable,
                playerKey,
                version,
                effectiveEnvironment,
                cancellationToken).ConfigureAwait(false);

            if (!health.IsHealthy)
            {
                throw new ConnectorManagementException($"连接器健康检查未通过：{health.Message}");
            }

            CleanupStaleBackups(connectorRoot, backupDirectory);

            if (Directory.Exists(versionDirectory))
            {
                Directory.Move(versionDirectory, backupDirectory);
                movedPreviousDirectory = true;
            }

            Directory.Move(stagingDirectory, versionDirectory);
            installedNewDirectory = true;

            string executable = Path.Combine(versionDirectory, stagedExecutableName);

            _store.WriteActive(playerKey, new ActiveConnector
            {
                Id = playerKey,
                Version = version,
                Executable = executable,
                Deployment = deployment,
                RuntimeRid = runtimeRid,
                RuntimeRoot = runtimeRid is null ? null : GetRuntimeRoot(effectiveEnvironment),
                Verified = package is not null,
                ActivatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });

            if (movedPreviousDirectory)
            {
                movedPreviousDirectory = !TryRemoveDirectory(backupDirectory, connectorRoot, playerKey);
            }

            Log($"[{playerKey}] 已安装 {version} → {executable}");

            return new ConnectorInstallResult(playerKey, version, executable, ReusedExisting: false, Verified: package is not null);
        }
        catch (Exception ex)
        {
            Rollback(playerKey, connectorRoot, versionDirectory, backupDirectory, installedNewDirectory, movedPreviousDirectory);

            // 统一对外契约：可预期的失败一律是 ConnectorManagementException（UI 只需 catch 一种），
            // 但取消必须原样透出，否则调用方的取消语义会被吞掉。
            if (ex is ConnectorManagementException or OperationCanceledException)
            {
                throw;
            }

            throw new ConnectorManagementException($"安装连接器 {playerKey} {version} 失败：{ex.Message}", ex);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                TryRemoveDirectory(stagingDirectory, connectorRoot, playerKey);
            }

            if (!movedPreviousDirectory && Directory.Exists(backupDirectory))
            {
                TryRemoveDirectory(backupDirectory, connectorRoot, playerKey);
            }
        }
    }

    private async Task<ConnectorInstallResult?> TryReuseAsync(
        string playerKey,
        string version,
        string deployment,
        IReadOnlyDictionary<string, string>? runtimeEnvironment,
        CancellationToken cancellationToken)
    {
        ActiveConnector? active = _store.ReadActive(playerKey);
        if (active is null
            || !string.Equals(active.Version, version, StringComparison.Ordinal)
            || !string.Equals(active.Deployment, deployment, StringComparison.Ordinal))
        {
            return null;
        }

        // 私有运行时根变了（例如升级到新的 8.0.x）就不能算"同一次安装"：记录里的 runtimeRoot
        // 会指向旧目录，继续复用会让连接器跑在一个可能已被清理的运行时上。
        if (string.Equals(deployment, "framework-dependent", StringComparison.Ordinal))
        {
            string? desiredRoot = GetRuntimeRoot(runtimeEnvironment);

            if (desiredRoot is not null
                && !string.Equals(
                    Path.GetFullPath(active.RuntimeRoot ?? string.Empty),
                    Path.GetFullPath(desiredRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        ConnectorHealthResult health = await _healthChecker.CheckAsync(
            active.Executable,
            playerKey,
            version,
            runtimeEnvironment,
            cancellationToken).ConfigureAwait(false);

        if (!health.IsHealthy)
        {
            Log($"[{playerKey}] 已装版本 {version} 健康检查未通过，将重新安装：{health.Message}");
            return null;
        }

        return new ConnectorInstallResult(playerKey, version, active.Executable, ReusedExisting: true, active.Verified);
    }

    /// <summary>
    /// 安装失败后的回滚：删掉新目录，把旧目录从 <c>.backup-*</c> 改回来。
    /// </summary>
    /// <remarks>
    /// <b>刻意不动 active.json。</b>因为 <see cref="ConnectorStore.WriteActive"/> 是原子写：
    /// 失败时它自己会把旧的 active.json 恢复回来，所以这里看到的永远是"上一版本仍然生效"的状态。
    /// 反过来，如果在这里删掉 active.json，就会出现一种退化——"v1 装好了、v2 装到一半失败"，
    /// 结果 v1 的目录还在但记录被抹掉，用户从"能用"变成"什么都没装"。
    /// 万一 active.json 确实指向了已被删除的目录，<see cref="ConnectorStore.ReadActive"/> 会因为
    /// 可执行文件不存在而返回 null，自动按"未安装"处理，无需在这里补救。
    /// </remarks>
    private void Rollback(
        string playerKey,
        string connectorRoot,
        string versionDirectory,
        string backupDirectory,
        bool installedNewDirectory,
        bool movedPreviousDirectory)
    {
        try
        {
            if (installedNewDirectory && Directory.Exists(versionDirectory))
            {
                TryRemoveDirectory(versionDirectory, connectorRoot, playerKey);
            }

            if (movedPreviousDirectory && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, versionDirectory);
                Log($"[{playerKey}] 安装失败，已从备份回滚到上一版本。");
            }
        }
        catch (Exception ex)
        {
            // 回滚本身失败时不再掩盖原始异常，只记录，让调用方看到真正的失败原因。
            Log($"[{playerKey}] 回滚时出错：{ex.Message}；备份保留于 {backupDirectory}");
        }
    }

    private void CleanupStaleBackups(string connectorRoot, string currentBackupDirectory)
    {
        string playerKey = Path.GetFileName(connectorRoot);

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(connectorRoot, BackupPrefix + "*"))
            {
                if (string.Equals(directory, currentBackupDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryRemoveDirectory(directory, connectorRoot, playerKey);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"[{playerKey}] 清理历史备份失败：{ex.Message}");
        }
    }

    private bool TryRemoveDirectory(string target, string connectorRoot, string playerKey)
    {
        try
        {
            ConnectorInstallLayout.RemoveInside(connectorRoot, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件锁是常见现象（连接器进程刚退出，句柄释放有延迟）。
            // 保留目录不影响正确性：下次安装会统一清理。
            Log($"[{playerKey}] 目录暂被占用，已保留待下次清理：{target}");
            return false;
        }
    }

    private static string? GetRuntimeRoot(IReadOnlyDictionary<string, string>? environment) =>
        environment is not null && environment.TryGetValue("DOTNET_ROOT", out string? root)
            ? root
            : null;

    private void Log(string message) => _log?.Invoke(message);
}
