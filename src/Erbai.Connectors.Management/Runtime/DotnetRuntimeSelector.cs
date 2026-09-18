using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Erbai.Connectors.Management.Runtime;

/// <summary>
/// 从 .NET 官方发布元数据里选出的运行时归档。
/// </summary>
public sealed record DotnetRuntimeArtifact(
    string Channel,
    string Version,
    string Rid,
    string Url,
    string Sha512,
    string Name);

// ---------------------------------------------------------------------------------------
// 元数据模型。只声明用得到的字段——官方 releases.json 有 1.5 MB，System.Text.Json 会跳过
// 未声明的属性而不materialize，因此不必为 sdk / symbols / sdks 等分支建模型。
// ---------------------------------------------------------------------------------------

internal sealed record DotnetReleaseMetadata
{
    [JsonPropertyName("channel-version")]
    public string? ChannelVersion { get; init; }

    /// <summary>可能是字符串（<c>"8.0.31"</c>），也可能是 <c>{ "version": "8.0.31" }</c>。</summary>
    [JsonPropertyName("latest-runtime")]
    public JsonElement LatestRuntime { get; init; }

    [JsonPropertyName("releases")]
    public IReadOnlyList<DotnetRelease>? Releases { get; init; }

    /// <summary>精简元数据（测试夹具）可能直接给顶层 <c>runtime</c>。</summary>
    [JsonPropertyName("runtime")]
    public DotnetRuntimeRelease? Runtime { get; init; }
}

internal sealed record DotnetRelease
{
    [JsonPropertyName("release-version")]
    public string? ReleaseVersion { get; init; }

    [JsonPropertyName("runtime")]
    public DotnetRuntimeRelease? Runtime { get; init; }
}

internal sealed record DotnetRuntimeRelease
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("files")]
    public IReadOnlyList<DotnetRuntimeFile>? Files { get; init; }
}

internal sealed record DotnetRuntimeFile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("rid")]
    public string? Rid { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>官方字段名是 <c>hash</c>；少数精简夹具用 <c>sha512</c>。</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; init; }

    [JsonPropertyName("sha512")]
    public string? Sha512 { get; init; }
}

/// <summary>
/// 发布元数据的解析与选包。纯函数、无 IO，可用 JSON 字符串直接单测。
/// </summary>
/// <remarks>
/// 选包规则与参考实现一致：<c>latest-runtime</c> 给出目标版本 → 在 <c>releases</c> 里找
/// <c>release-version</c> 相同的那个 → 取其 <c>runtime.files</c> 中 <c>rid</c> 匹配且
/// 文件名以 <c>.zip</c> 结尾的条目。
/// <para>
/// 必须挑 <c>.zip</c> 而不是 <c>.exe</c>：官方每个 rid 同时发布
/// <c>dotnet-runtime-&lt;ver&gt;-&lt;rid&gt;.exe</c>（安装器）和同名 <c>.zip</c>（可解压归档），
/// 两者 sha512 不同。挑错就会"校验通过但解压失败"。
/// </para>
/// </remarks>
internal static class DotnetRuntimeSelector
{
    internal const string DefaultChannel = "8.0";

    /// <summary>可接受的运行时归档下载主机白名单（防元数据被篡改成任意下载源）。</summary>
    internal static readonly string[] AllowedHosts =
    [
        "builds.dotnet.microsoft.com",
        "download.visualstudio.microsoft.com",
    ];

    /// <summary>运行时归档的最小合理体积。低于此值说明元数据或响应异常。</summary>
    internal const long MinArchiveBytes = 1024 * 1024;

    private static readonly Regex VersionPattern =
        new(@"^(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant);

    private static readonly Regex ChannelPattern =
        new(@"^(\d+)\.(\d+)$", RegexOptions.CultureInvariant);

    private static readonly Regex Sha512Pattern =
        new("^[0-9a-fA-F]{128}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>解析元数据文本并选出目标归档。</summary>
    /// <exception cref="ConnectorManagementException">元数据不合法或找不到匹配归档。</exception>
    internal static DotnetRuntimeArtifact SelectFromJson(string json, string rid, string channel)
    {
        DotnetReleaseMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<DotnetReleaseMetadata>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new ConnectorManagementException($".NET 运行时发布元数据不是合法 JSON：{ex.Message}", ex);
        }

        if (metadata is null)
        {
            throw new ConnectorManagementException(".NET 运行时发布元数据为空。");
        }

        return Select(metadata, rid, channel);
    }

    /// <summary>从已解析的元数据里选出目标归档。</summary>
    /// <exception cref="ConnectorManagementException">元数据不合法或找不到匹配归档。</exception>
    internal static DotnetRuntimeArtifact Select(DotnetReleaseMetadata metadata, string rid, string channel)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateRid(rid);
        ValidateChannel(channel);

        if (metadata.ChannelVersion is not null
            && !string.Equals(metadata.ChannelVersion, channel, StringComparison.Ordinal))
        {
            throw new ConnectorManagementException(
                $".NET 运行时频道不匹配：元数据为 {metadata.ChannelVersion}，期望 {channel}。");
        }

        string latestRuntime = ReadLatestRuntimeVersion(metadata.LatestRuntime)
            ?? throw new ConnectorManagementException(".NET 运行时元数据缺少 latest-runtime。");

        if (!VersionPattern.IsMatch(latestRuntime)
            || GetChannelOf(latestRuntime) != channel)
        {
            throw new ConnectorManagementException(
                $".NET 运行时最新版本不合法或不属于频道 {channel}：{latestRuntime}。");
        }

        DotnetRuntimeRelease? runtime = FindRuntimeForVersion(metadata, latestRuntime)
            ?? throw new ConnectorManagementException(
                $".NET 运行时元数据中找不到版本 {latestRuntime} 的 runtime 信息。");

        if (!string.Equals(runtime.Version, latestRuntime, StringComparison.Ordinal))
        {
            throw new ConnectorManagementException(
                $".NET 运行时版本不一致：latest-runtime 为 {latestRuntime}，runtime.version 为 {runtime.Version ?? "(缺失)"}。");
        }

        if (runtime.Files is null || runtime.Files.Count == 0)
        {
            throw new ConnectorManagementException(".NET 运行时元数据的文件列表为空。");
        }

        DotnetRuntimeFile? file = runtime.Files.FirstOrDefault(candidate =>
            string.Equals(candidate.Rid, rid, StringComparison.Ordinal)
            && candidate.Name is not null
            && candidate.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            throw new ConnectorManagementException($".NET 运行时元数据中找不到 {rid} 的 zip 归档。");
        }

        string url = ValidateUrl(file.Url);
        string sha512 = ValidateSha512(file.Hash ?? file.Sha512);

        // 官方元数据的 name 字段**不带版本号**（实测 8.0.31：name="dotnet-runtime-win-x64.zip"，
        // 而 URL 末段是 "dotnet-runtime-8.0.31-win-x64.zip"）。因此 name 只能用来判定
        // zip/exe，不能当归档文件名用。归档名改取 URL 末段：它是真实且逐版本唯一的文件名，
        // 日志与错误信息里带上版本才有可诊断性。
        string name = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(name))
        {
            throw new ConnectorManagementException($".NET 运行时归档名称无法从 URL 推导：{url}。");
        }

        return new DotnetRuntimeArtifact(channel, latestRuntime, rid, url, sha512, name);
    }

    private static DotnetRuntimeRelease? FindRuntimeForVersion(
        DotnetReleaseMetadata metadata,
        string version)
    {
        if (metadata.Releases is not null)
        {
            foreach (DotnetRelease release in metadata.Releases)
            {
                if (string.Equals(release.ReleaseVersion, version, StringComparison.Ordinal)
                    && release.Runtime is not null)
                {
                    return release.Runtime;
                }
            }
        }

        // 精简夹具（测试）可能省略 releases，直接在顶层给 runtime。
        return metadata.Runtime;
    }

    private static string? ReadLatestRuntimeVersion(JsonElement latest)
    {
        switch (latest.ValueKind)
        {
            case JsonValueKind.String:
                return latest.GetString();

            case JsonValueKind.Object when latest.TryGetProperty("version", out JsonElement version)
                                           && version.ValueKind == JsonValueKind.String:
                return version.GetString();

            default:
                return null;
        }
    }

    private static string GetChannelOf(string version)
    {
        Match match = VersionPattern.Match(version);
        return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
    }

    /// <summary>只接受 https + 无自定义端口 + 白名单主机 + 路径以 .zip 结尾。</summary>
    internal static string ValidateUrl(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw new ConnectorManagementException($".NET 运行时归档 URL 无效：{value ?? "(缺失)"}。");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectorManagementException($".NET 运行时归档 URL 必须使用 HTTPS：{uri}。");
        }

        if (!uri.IsDefaultPort)
        {
            throw new ConnectorManagementException($".NET 运行时归档 URL 不允许指定端口：{uri}。");
        }

        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new ConnectorManagementException($".NET 运行时归档主机不在白名单内：{uri.Host}。");
        }

        if (!uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectorManagementException($".NET 运行时归档 URL 必须指向 zip：{uri}。");
        }

        return value;
    }

    internal static string ValidateSha512(string? value)
    {
        if (string.IsNullOrEmpty(value) || !Sha512Pattern.IsMatch(value))
        {
            throw new ConnectorManagementException(".NET 运行时 SHA-512 校验值无效（应为 128 位十六进制）。");
        }

        return value.ToLowerInvariant();
    }

    internal static void ValidateRid(string rid)
    {
        if (!ConnectorPlayers.IsSupportedRuntime(rid))
        {
            throw new ConnectorManagementException($"不支持的 .NET 运行时标识：{rid}。");
        }
    }

    internal static void ValidateChannel(string channel)
    {
        if (string.IsNullOrEmpty(channel) || !ChannelPattern.IsMatch(channel))
        {
            throw new ConnectorManagementException($"不支持的 .NET 运行时频道：{channel}。");
        }
    }
}
