using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Erbai.Connectors.Management;

/// <summary>
/// 某平台当前生效的连接器版本记录（<c>active.json</c> 的内容）。
/// </summary>
public sealed record ActiveConnector
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("executable")]
    public required string Executable { get; init; }

    /// <summary><c>framework-dependent</c> 或 <c>self-contained</c>。</summary>
    [JsonPropertyName("deployment")]
    public required string Deployment { get; init; }

    /// <summary>framework-dependent 时使用的私有运行时 rid。</summary>
    [JsonPropertyName("runtimeRid")]
    public string? RuntimeRid { get; init; }

    /// <summary>framework-dependent 时使用的私有运行时根目录。</summary>
    [JsonPropertyName("runtimeRoot")]
    public string? RuntimeRoot { get; init; }

    /// <summary>
    /// 是否通过了上游签名校验。仅「从本地 ZIP 安装」且用户明确接受未校验包时为 false。
    /// 记录下来是为了让 UI 能如实提示，而不是让未校验状态隐形。
    /// </summary>
    [JsonPropertyName("verified")]
    public bool Verified { get; init; } = true;

    [JsonPropertyName("activatedAt")]
    public string? ActivatedAt { get; init; }
}

/// <summary>
/// <c>active.json</c> 的读写（原子写：临时文件 → rename → 失败回滚）。
/// </summary>
/// <remarks>
/// <para>
/// 读路径会做一次完整的自校验（id / 版本号 / 可执行文件确实落在版本目录内且存在）。
/// 任何一项不成立都返回 <see langword="null"/>，等同于「未安装」——这样即使 active.json
/// 被手工改坏或指向了别处，也不会被执行，只会触发重装。
/// </para>
/// <para>
/// 写路径先写临时文件再 rename，并且先把旧 active.json 改名成 <c>.bak</c>：
/// 这样"写到一半断电"的最坏结果是留下一个 <c>.bak</c>，而不是一个被截断的 active.json。
/// </para>
/// </remarks>
public sealed class ConnectorStore : IConnectorStore
{
    private static readonly Regex VersionPattern =
        new(@"^\d+(?:\.\d+){2,4}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConnectorInstallLayout _layout;

    /// <summary>
    /// 可选的私有运行时根校验。P3 引入运行时管理器后由它提供；
    /// 未提供时只校验 rid 合法且路径为绝对路径。
    /// </summary>
    private readonly Func<string, string, bool>? _isRuntimeRootAcceptable;

    public ConnectorStore(
        ConnectorInstallLayout layout,
        Func<string, string, bool>? isRuntimeRootAcceptable = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _isRuntimeRootAcceptable = isRuntimeRootAcceptable;
    }

    /// <summary>
    /// 读取并自校验当前生效记录；不存在或不合法时返回 <see langword="null"/>。
    /// </summary>
    public ActiveConnector? ReadActive(string playerKey)
    {
        string activePath = _layout.GetActiveFilePath(playerKey);

        ActiveConnector? active;
        try
        {
            active = JsonSerializer.Deserialize<ActiveConnector>(File.ReadAllText(activePath), SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }

        if (active is null
            || !string.Equals(active.Id, playerKey, StringComparison.Ordinal)
            || string.IsNullOrEmpty(active.Version)
            || !VersionPattern.IsMatch(active.Version)
            || string.IsNullOrEmpty(active.Executable))
        {
            return null;
        }

        // 可执行文件必须确实是该版本目录下的规范文件名之一，且真实存在。
        string versionDirectory = _layout.GetVersionDirectory(playerKey, active.Version);
        string? expected = ConnectorPlayers.GetExecutableNames(playerKey)
            .Select(name => Path.Combine(versionDirectory, name))
            .FirstOrDefault(candidate => string.Equals(
                Path.GetFullPath(candidate),
                Path.GetFullPath(active.Executable),
                StringComparison.OrdinalIgnoreCase));

        if (expected is null || !File.Exists(expected))
        {
            return null;
        }

        if (!IsDeploymentAcceptable(active))
        {
            return null;
        }

        return active with { Executable = expected };
    }

    private bool IsDeploymentAcceptable(ActiveConnector active)
    {
        switch (active.Deployment)
        {
            case "self-contained":
                return true;

            case "framework-dependent":
                // rid 必须有：它是挑选私有运行时的依据（决策 D8）。
                if (!ConnectorPlayers.IsSupportedRuntime(active.RuntimeRid))
                {
                    return false;
                }

                // runtimeRoot 允许缺省（此时连接器使用系统运行时）。一旦记录在案，就必须是
                // 绝对路径并通过注入的校验——P3 会用这个校验把运行时根锁进私有目录，
                // 届时本项自然变成强制。
                if (string.IsNullOrEmpty(active.RuntimeRoot))
                {
                    return true;
                }

                return Path.IsPathRooted(active.RuntimeRoot)
                    && (_isRuntimeRootAcceptable?.Invoke(active.RuntimeRid!, active.RuntimeRoot!) ?? true);

            default:
                return false;
        }
    }

    /// <summary>
    /// 原子写入 active.json。
    /// </summary>
    /// <exception cref="ConnectorManagementException">写入失败且旧文件无法恢复。</exception>
    public void WriteActive(string playerKey, ActiveConnector active)
    {
        ArgumentNullException.ThrowIfNull(active);

        string connectorRoot = _layout.GetConnectorRoot(playerKey);
        Directory.CreateDirectory(connectorRoot);

        string activePath = _layout.GetActiveFilePath(playerKey);
        string nonce = Guid.NewGuid().ToString("N");
        string temporaryPath = $"{activePath}.{Environment.ProcessId}.{nonce}.tmp";
        string backupPath = $"{activePath}.{Environment.ProcessId}.{nonce}.bak";

        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(active, SerializerOptions) + Environment.NewLine);

        bool movedPrevious = false;
        bool activatedNew = false;
        bool preserveBackup = false;

        try
        {
            if (File.Exists(activePath))
            {
                File.Move(activePath, backupPath);
                movedPrevious = true;
            }

            File.Move(temporaryPath, activePath);
            activatedNew = true;

            if (movedPrevious)
            {
                File.Delete(backupPath);
                movedPrevious = false;
            }
        }
        catch (Exception ex)
        {
            if (activatedNew)
            {
                TryDelete(activePath);
            }

            if (movedPrevious && File.Exists(backupPath))
            {
                try
                {
                    File.Move(backupPath, activePath);
                    movedPrevious = false;
                }
                catch (Exception restoreError)
                {
                    preserveBackup = true;
                    throw new ConnectorManagementException(
                        $"连接器激活失败且 active.json 自动恢复失败：{ex.Message}；"
                        + $"{restoreError.Message}；备份保留于 {backupPath}",
                        ex);
                }
            }

            throw new ConnectorManagementException($"写入 active.json 失败：{ex.Message}", ex);
        }
        finally
        {
            TryDelete(temporaryPath);

            if (!preserveBackup && !movedPrevious)
            {
                TryDelete(backupPath);
            }
        }
    }

    /// <summary>删除 active.json（卸载 / 重置用）。不存在时静默返回。</summary>
    public void DeleteActive(string playerKey) => TryDelete(_layout.GetActiveFilePath(playerKey));

    /// <summary>判断某平台是否已安装且记录可用。</summary>
    public bool IsInstalled(string playerKey) => ReadActive(playerKey) is not null;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理是尽力而为：残留的 .tmp/.bak 不影响正确性，下次安装会覆盖。
        }
    }
}
