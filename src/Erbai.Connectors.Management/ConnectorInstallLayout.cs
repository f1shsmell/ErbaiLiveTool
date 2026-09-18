namespace Erbai.Connectors.Management;

/// <summary>
/// 连接器安装目录布局（决策 D1）。
/// </summary>
/// <remarks>
/// <para>
/// 根目录：<c>%LOCALAPPDATA%\ErbaiLiveTool\player-connectors\</c>，
/// 每平台一份 <c>&lt;playerKey&gt;/&lt;version&gt;/</c> 加一个 <c>active.json</c>。
/// </para>
/// <para>
/// <b>为什么不复用 <c>Plugins/</c></b>：那是 DLL 插件域，<c>PluginLoader</c> 唯一的加载路径是
/// <c>LoadFromAssemblyPath</c>，且 <c>PluginManifest.File</c> 强制纯文件名。把进程级连接器塞进去
/// 需要改 manifest 语义并让 Loader 理解外部 exe，改动面大且语义混杂。因此 <c>PluginLoader</c> 零改动。
/// </para>
/// <para>
/// 实例化时传入 <c>root</c> 可把整套布局指向任意目录——单测全部落在临时目录，绝不碰真实安装根。
/// </para>
/// </remarks>
public sealed class ConnectorInstallLayout
{
    /// <summary>应用在 LOCALAPPDATA 下的目录名。</summary>
    public const string ApplicationFolderName = "ErbaiLiveTool";

    /// <summary>连接器安装根的子目录名。</summary>
    public const string ConnectorFolderName = "player-connectors";

    /// <summary>active.json 文件名。</summary>
    public const string ActiveFileName = "active.json";

    public ConnectorInstallLayout(string? root = null)
    {
        Root = Path.GetFullPath(root ?? GetDefaultRoot());
    }

    /// <summary>连接器安装根目录。</summary>
    public string Root { get; }

    /// <summary>默认安装根：<c>%LOCALAPPDATA%\ErbaiLiveTool\player-connectors</c>。</summary>
    public static string GetDefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationFolderName,
        ConnectorFolderName);

    /// <summary>某平台的安装目录。</summary>
    public string GetConnectorRoot(string playerKey)
    {
        ValidatePlayerKey(playerKey);
        return Path.Combine(Root, playerKey);
    }

    /// <summary>某平台某版本的安装目录。</summary>
    public string GetVersionDirectory(string playerKey, string version)
    {
        ValidateVersion(version);
        return Path.Combine(GetConnectorRoot(playerKey), version);
    }

    /// <summary>某平台的 active.json 路径。</summary>
    public string GetActiveFilePath(string playerKey) =>
        Path.Combine(GetConnectorRoot(playerKey), ActiveFileName);

    /// <summary>
    /// <paramref name="target"/> 是否位于 <paramref name="parent"/> 之内。
    /// 按目录边界比较（补分隔符），避免 <c>C:\a\bc</c> 被误判为在 <c>C:\a\b</c> 之内。
    /// </summary>
    public static bool IsInside(string parent, string target)
    {
        string parentWithSeparator = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return Path.GetFullPath(target).StartsWith(
            parentWithSeparator,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 删除 <paramref name="parent"/> 内的 <paramref name="target"/>。
    /// 目标不在父目录内时抛异常——这是防误删的最后一道闸门。
    /// </summary>
    /// <exception cref="ConnectorManagementException">目标越出父目录。</exception>
    public static void RemoveInside(string parent, string target)
    {
        if (!IsInside(parent, target))
        {
            throw new ConnectorManagementException($"拒绝删除连接器目录之外的路径：{Path.GetFullPath(target)}");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    private static void ValidatePlayerKey(string playerKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerKey);

        // 平台键会直接参与拼路径，必须限定为单个安全路径段。
        if (playerKey.IndexOfAny(['/', '\\', ':', '.', '\0']) >= 0)
        {
            throw new ConnectorManagementException($"平台键含非法字符：{playerKey}");
        }
    }

    private static void ValidateVersion(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);

        if (version.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || version.StartsWith('.'))
        {
            throw new ConnectorManagementException($"版本号含非法字符：{version}");
        }
    }
}
