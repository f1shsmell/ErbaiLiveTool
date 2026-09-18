using Erbai.Connectors.Management;
using Erbai.Connectors.Management.Runtime;

namespace Erbai.Player.Connectors;

/// <summary>某个 player key 此刻的可用性。</summary>
public enum PlayerConnectorAvailability
{
    /// <summary>随应用发布的连接器（lxmusic）。</summary>
    BuiltIn = 0,

    /// <summary>插件已安装，可执行文件就位。</summary>
    Installed,

    /// <summary>插件未安装——UI 应灰显并给出下载指引（决策 D-D）。</summary>
    NotInstalled,

    /// <summary>装过但当前不可用（记录非法或可执行文件已不在）——UI 应提示重新安装。</summary>
    Broken,
}

/// <summary>
/// 启动某个连接器所需的全部信息。
/// </summary>
/// <remarks>
/// <see cref="Environment"/> 是<b>要注入子进程的环境变量增量</b>，不是完整环境。
/// framework-dependent 插件由私有运行时（<see cref="PrivateDotnetRuntimeManager"/>）注入
/// <c>DOTNET_ROOT</c> 等；lxmusic 注入 <c>LX_*</c>；folia 额外注入 token。
/// </remarks>
public sealed record PlayerConnectorResolution(
    string PlayerKey,
    string DisplayName,
    PlayerConnectorAvailability Availability,
    string? ExecutablePath,
    IReadOnlyDictionary<string, string> Environment,
    string? Version,
    string? Detail)
{
    /// <summary>是否可以直接拉起。</summary>
    public bool IsUsable => ExecutablePath is not null;

    /// <summary>是否需要先安装（UI 灰显 / 下载指引的判据）。</summary>
    public bool NeedsInstall =>
        Availability is PlayerConnectorAvailability.NotInstalled or PlayerConnectorAvailability.Broken;
}

/// <summary>
/// player key → 连接器启动参数。
/// </summary>
/// <remarks>
/// <para>
/// 插件化改造后有两类连接器，来源完全不同：
/// <list type="bullet">
///   <item><description><b>lxmusic</b>：随应用发布的 <c>Erbai.Connector.exe</c>，
///   路径相对应用目录解析，参数经 <c>LX_*</c> 环境变量下发。</description></item>
///   <item><description><b>netease / kugou / qqmusic / folia</b>：第三方连接器插件，
///   路径来自 <c>active.json</c>（<see cref="IConnectorStore"/>），
///   framework-dependent 的还要带上私有运行时环境。</description></item>
/// </list>
/// </para>
/// <para>
/// 解析过程<b>只读盘、不联网、不下载</b>。原因是它会在设置页打开时被调用，
/// 也可能在启动序列里被调用——那时绝不该触发一次运行时下载。
/// 需要联网的信息（最新版本、可更新性）由 <c>ConnectorMaintenance</c> 另行提供。
/// </para>
/// </remarks>
public sealed class PlayerConnectorResolver
{
    /// <summary>本仓库内置支持的播放器 key。</summary>
    public const string BuiltInPlayerKey = "lxmusic";

    /// <summary>内置连接器宿主 exe 文件名。</summary>
    public const string BuiltInExecutableName = "Erbai.Connector.exe";

    /// <summary>folia 连接器读取的 token 环境变量名。</summary>
    public const string FoliaTokenVariable = "BILINCM_FOLIA_TOKEN";

    private readonly ConnectorInstallLayout _layout;
    private readonly IConnectorStore _store;
    private readonly string _baseDirectory;
    private readonly Action<string>? _log;

    /// <param name="baseDirectory">应用目录（内置连接器 exe 所在处）。</param>
    public PlayerConnectorResolver(
        ConnectorInstallLayout layout,
        IConnectorStore store,
        string baseDirectory,
        Action<string>? log = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
        _log = log;
    }

    /// <summary>内置连接器（lxmusic）的 exe 路径。</summary>
    public string BuiltInExecutablePath => Path.Combine(_baseDirectory, BuiltInExecutableName);

    /// <summary>
    /// 解析一个 player key。
    /// </summary>
    /// <param name="playerKey">配置里的 <c>player.key</c>。</param>
    /// <param name="builtInEnvironment">
    /// 内置连接器需要的环境变量（<c>LX_*</c>）。仅当解析到内置连接器时使用。
    /// </param>
    /// <param name="foliaToken">folia 连接器需要的 token；为空则不下发。</param>
    public PlayerConnectorResolution Resolve(
        string playerKey,
        IReadOnlyDictionary<string, string>? builtInEnvironment = null,
        string? foliaToken = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerKey);

        return string.Equals(playerKey, BuiltInPlayerKey, StringComparison.Ordinal)
            ? ResolveBuiltIn(playerKey, builtInEnvironment)
            : ResolvePlugin(playerKey, foliaToken);
    }

    /// <summary>
    /// 解析全部合法 player key（内置 + 四个插件），顺序固定为设置页展示顺序。
    /// </summary>
    public IReadOnlyList<PlayerConnectorResolution> ResolveAll(
        IReadOnlyDictionary<string, string>? builtInEnvironment = null,
        string? foliaToken = null)
    {
        List<PlayerConnectorResolution> results =
        [
            Resolve(BuiltInPlayerKey, builtInEnvironment, foliaToken),
        ];

        foreach (string playerKey in ConnectorPlayers.PluginPlayerKeys)
        {
            results.Add(Resolve(playerKey, builtInEnvironment, foliaToken));
        }

        return results;
    }

    private PlayerConnectorResolution ResolveBuiltIn(
        string playerKey,
        IReadOnlyDictionary<string, string>? builtInEnvironment)
    {
        string executable = BuiltInExecutablePath;
        bool exists = File.Exists(executable);

        return new PlayerConnectorResolution(
            playerKey,
            GetDisplayName(playerKey),
            exists ? PlayerConnectorAvailability.BuiltIn : PlayerConnectorAvailability.Broken,
            exists ? executable : null,
            builtInEnvironment ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Version: null,
            Detail: exists ? null : $"未找到 {BuiltInExecutableName}");
    }

    private PlayerConnectorResolution ResolvePlugin(string playerKey, string? foliaToken)
    {
        string displayName = GetDisplayName(playerKey);

        if (!ConnectorPlayers.IsPluginPlayerKey(playerKey))
        {
            // 配置校验层已经挡住非法 key，走到这里说明配置被外部改坏了。
            return new PlayerConnectorResolution(
                playerKey,
                displayName,
                PlayerConnectorAvailability.Broken,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                Version: null,
                Detail: "不是本应用支持的插件平台。");
        }

        ActiveConnector? active = _store.ReadActive(playerKey);

        if (active is null)
        {
            // ReadActive 会把"记录非法 / 可执行文件已不在"也判为 null。用目录是否存在
            // 区分"从没装过"与"装过但坏了"——这两者对用户的下一步动作不一样。
            bool everInstalled = Directory.Exists(_layout.GetConnectorRoot(playerKey));

            return new PlayerConnectorResolution(
                playerKey,
                displayName,
                everInstalled ? PlayerConnectorAvailability.Broken : PlayerConnectorAvailability.NotInstalled,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                Version: null,
                Detail: everInstalled
                    ? "已安装记录不可用（可执行文件缺失或记录损坏），建议重新安装。"
                    : "未安装。可在插件页下载安装，或从本地 ZIP 安装。");
        }

        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        string? runtimeWarning = null;

        if (string.Equals(active.Deployment, "framework-dependent", StringComparison.Ordinal))
        {
            if (active.RuntimeRid is null)
            {
                // ConnectorStore 不该让这种记录读出来；真出现了也不能猜一个 rid。
                runtimeWarning = "记录缺少运行时标识，无法注入私有运行时。";
            }
            else if (active.RuntimeRoot is null)
            {
                // 私有运行时根缺失时退到机器级 .NET。本机没有 x86 运行时时，
                // win-x86 插件会在启动阶段报 hostfxr 解析失败。
                runtimeWarning = "记录缺少私有运行时根，将退回机器级 .NET 运行时。";
            }
            else
            {
                foreach ((string key, string value) in
                         PrivateDotnetRuntimeManager.BuildEnvironment(active.RuntimeRid, active.RuntimeRoot))
                {
                    environment[key] = value;
                }
            }
        }

        if (string.Equals(playerKey, "folia", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(foliaToken))
        {
            environment[FoliaTokenVariable] = foliaToken;
        }

        if (runtimeWarning is not null)
        {
            _log?.Invoke($"[连接器] {playerKey}：{runtimeWarning}");
        }

        return new PlayerConnectorResolution(
            playerKey,
            displayName,
            PlayerConnectorAvailability.Installed,
            active.Executable,
            environment,
            active.Version,
            runtimeWarning);
    }

    /// <summary>player.key → 显示名。与设置页下拉、概览页保持一致。</summary>
    public static string GetDisplayName(string playerKey) =>
        DisplayNames.TryGetValue(playerKey, out string? name) ? name : playerKey;

    private static IReadOnlyDictionary<string, string> DisplayNames { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BuiltInPlayerKey] = "落雪音乐",
            ["netease"] = "网易云音乐",
            ["kugou"] = "酷狗音乐",
            ["qqmusic"] = "QQ音乐",
            ["folia"] = "Folia",
        };
}
