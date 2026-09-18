namespace Erbai.Connectors.Management;

/// <summary>
/// 连接器平台注册表：本应用认可哪些 player key、每个 key 的发布包内可执行文件名、
/// 以及可接受的运行时标识（rid）。
/// </summary>
/// <remarks>
/// 这里的四个 key 与 <c>ConfigValidator.SupportedPlayers</c> 保持一致。四平台在改造后
/// 仍是<b>合法 player key</b>，只是改由第三方连接器提供实现，因此配置校验不应把它们判为非法。
/// 可执行文件名与参考实现 <c>electron/connector-branding-policy.ts</c> 对齐：
/// 新版为 <c>Awoo.Connector.*.exe</c>，旧版为 <c>BiliNCM.Connector.*.exe</c>，两者都接受。
/// </remarks>
public static class ConnectorPlayers
{
    /// <summary>内置支持的平台（不含由本仓库原生实现的 lxmusic）。</summary>
    public static readonly IReadOnlyList<string> PluginPlayerKeys =
        ["netease", "kugou", "qqmusic", "folia"];

    /// <summary>可接受的运行时标识；rid 一律取自清单 <c>package.runtime</c>（决策 D8）。</summary>
    public static readonly IReadOnlyList<string> SupportedRuntimes = ["win-x64", "win-x86"];

    private static readonly Dictionary<string, string[]> ExecutableNames =
        new(StringComparer.Ordinal)
        {
            ["netease"] = ["Awoo.Connector.Netease.exe", "BiliNCM.Connector.Netease.exe"],
            ["kugou"] = ["Awoo.Connector.Kugou.exe", "BiliNCM.Connector.Kugou.exe"],
            ["qqmusic"] = ["Awoo.Connector.QQMusic.exe", "BiliNCM.Connector.QQMusic.exe"],
            ["folia"] = ["Awoo.Connector.Folia.exe", "BiliNCM.Connector.Folia.exe"],
        };

    /// <summary>是否为本应用认可的插件平台 key。</summary>
    public static bool IsPluginPlayerKey(string? playerKey) =>
        playerKey is not null && ExecutableNames.ContainsKey(playerKey);

    /// <summary>是否为可接受的 rid。</summary>
    public static bool IsSupportedRuntime(string? runtime) =>
        runtime is not null && SupportedRuntimes.Contains(runtime, StringComparer.Ordinal);

    /// <summary>
    /// 按优先级返回该平台发布包内可接受的可执行文件名；首个即首选名。
    /// 未知平台返回空数组（调用方据此判定为不支持的连接器）。
    /// </summary>
    public static IReadOnlyList<string> GetExecutableNames(string playerKey) =>
        ExecutableNames.TryGetValue(playerKey, out string[]? names) ? names : [];

    /// <summary>
    /// 校验发布包资产名是否为该平台该版本该 rid 的规范命名。
    /// </summary>
    /// <remarks>
    /// 比"资产名等于 URL 基名"更强：把 id / version / rid 三者与资产名绑定，
    /// 避免清单被替换成指向任意 zip 的下载地址而仍然通过校验。
    /// 同时接受旧版 <c>bilincm-</c> 前缀。
    /// </remarks>
    public static bool IsRecognizedAssetName(
        string? asset,
        string playerKey,
        string version,
        string runtime)
    {
        if (string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(version) || string.IsNullOrEmpty(runtime))
        {
            return false;
        }

        foreach (string prefix in (string[])["awoo-connector-", "bilincm-connector-"])
        {
            if (string.Equals(
                    asset,
                    $"{prefix}{playerKey}-{version}-{runtime}.zip",
                    StringComparison.Ordinal)
                || string.Equals(
                    asset,
                    $"{prefix}{playerKey}-{version}-{runtime}-framework-dependent.zip",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>按清单的 rid 构造 GitHub Release 回退直链（站点不可达时使用）。</summary>
    public static string BuildGitHubReleaseUrl(string playerKey, string version, string asset)
    {
        string tag = $"{playerKey}-v{version}";
        return "https://github.com/Enkianssus/awoo-connectors/releases/download/"
            + $"{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}";
    }
}
