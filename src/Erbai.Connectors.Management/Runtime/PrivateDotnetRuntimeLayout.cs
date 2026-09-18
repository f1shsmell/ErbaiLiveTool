namespace Erbai.Connectors.Management.Runtime;

/// <summary>
/// 私有 .NET 运行时的安装目录布局：<c>&lt;root&gt;\&lt;rid&gt;\&lt;version&gt;\</c>。
/// </summary>
/// <remarks>
/// 与连接器安装根并列放在应用目录下（默认
/// <c>%LOCALAPPDATA%\ErbaiLiveTool\dotnet-runtimes</c>）。
/// 每个版本目录里写一份标记文件，记录 channel / rid / version / sha512——
/// 只有标记自洽且目录结构完整的版本才会被复用，避免"半装成功"的目录被当成可用运行时。
/// </remarks>
public sealed class PrivateDotnetRuntimeLayout
{
    /// <summary>运行时根目录名。</summary>
    public const string RuntimeFolderName = "dotnet-runtimes";

    /// <summary>
    /// 版本目录内的标记文件名。刻意带 <c>erbai-</c> 前缀：同机上可能并存其他应用的
    /// 私有运行时（例如参考实现用的 <c>.awoo-dotnet-runtime.json</c>），
    /// 标记名不同才不会互相误认。
    /// </summary>
    public const string MarkerFileName = ".erbai-dotnet-runtime.json";

    public PrivateDotnetRuntimeLayout(string? root = null)
    {
        Root = Path.GetFullPath(root ?? GetDefaultRoot());
    }

    /// <summary>运行时根目录。</summary>
    public string Root { get; }

    /// <summary>默认根：<c>%LOCALAPPDATA%\ErbaiLiveTool\dotnet-runtimes</c>。</summary>
    public static string GetDefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ConnectorInstallLayout.ApplicationFolderName,
        RuntimeFolderName);

    /// <summary>某 rid 的目录。</summary>
    public string GetRidRoot(string rid)
    {
        DotnetRuntimeSelector.ValidateRid(rid);
        return Path.Combine(Root, rid);
    }

    /// <summary>某 rid 某版本的运行时目录。</summary>
    public string GetVersionDirectory(string rid, string version)
    {
        DotnetRuntimeSelector.ValidateRid(rid);

        if (string.IsNullOrEmpty(version)
            || !IsSafeVersionSegment(version))
        {
            throw new ConnectorManagementException($"非法的 .NET 运行时版本号：{version}。");
        }

        return Path.Combine(GetRidRoot(rid), version);
    }

    /// <summary>版本目录内的标记文件路径。</summary>
    public string GetMarkerPath(string rid, string version) =>
        Path.Combine(GetVersionDirectory(rid, version), MarkerFileName);

    /// <summary>
    /// <paramref name="candidate"/> 是否位于该 rid 的运行时目录之内。
    /// 供 <see cref="ConnectorStore"/> 校验 active.json 里记录的 runtimeRoot。
    /// </summary>
    public bool IsInsideRidRoot(string rid, string candidate) =>
        ConnectorInstallLayout.IsInside(GetRidRoot(rid), candidate);

    /// <summary>版本号必须是纯数字点分形式，避免拼出带路径分隔符的目录。</summary>
    private static bool IsSafeVersionSegment(string version)
    {
        foreach (char c in version)
        {
            if (!char.IsAsciiDigit(c) && c != '.')
            {
                return false;
            }
        }

        return version[0] != '.';
    }
}
