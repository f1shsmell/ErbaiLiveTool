using System.Text.Json.Serialization;

namespace Erbai.Connectors.Management.Catalog;

/// <summary>
/// 上游连接器更新清单（<c>schemaVersion: 2</c>）。
/// </summary>
/// <remarks>
/// 字段与上游 <c>https://app.enkianss.us/connectors/v2/catalog.json</c> 一一对应。
/// 本类型只承载数据；所有合法性判断集中在 <see cref="ConnectorCatalogValidator"/>，
/// 便于单测直接喂 JSON 字符串，不必起 HTTP。
/// </remarks>
public sealed record ConnectorCatalog
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("publicKeyId")]
    public string? PublicKeyId { get; init; }

    [JsonPropertyName("connectors")]
    public IReadOnlyDictionary<string, ConnectorCatalogEntry>? Connectors { get; init; }
}

/// <summary>清单中单个连接器的条目。</summary>
public sealed record ConnectorCatalogEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }

    /// <summary>
    /// 发布方自家应用的版本口径（AwooMusicBot），与本应用版本号<b>不可比</b>（决策 D4）。
    /// 仅作展示提示，绝不作为门控条件。
    /// </summary>
    [JsonPropertyName("minimumCoreVersion")]
    public string? MinimumCoreVersion { get; init; }

    [JsonPropertyName("playerVersionPolicy")]
    public string? PlayerVersionPolicy { get; init; }

    [JsonPropertyName("testedPlayerVersion")]
    public string? TestedPlayerVersion { get; init; }

    [JsonPropertyName("publishedAt")]
    public string? PublishedAt { get; init; }

    [JsonPropertyName("package")]
    public ConnectorPackage? Package { get; init; }
}

/// <summary>连接器发布包描述。</summary>
public sealed record ConnectorPackage
{
    /// <summary><c>framework-dependent</c>（需私有 .NET 运行时）或 <c>self-contained</c>。</summary>
    [JsonPropertyName("deployment")]
    public string? Deployment { get; init; }

    /// <summary>运行时标识，取值见 <see cref="ConnectorPlayers.SupportedRuntimes"/>。</summary>
    [JsonPropertyName("runtime")]
    public string? Runtime { get; init; }

    [JsonPropertyName("runtimeChannel")]
    public string? RuntimeChannel { get; init; }

    /// <summary>资产文件名；必须与平台 / 版本 / rid 匹配。</summary>
    [JsonPropertyName("asset")]
    public string? Asset { get; init; }

    /// <summary>归档字节数；下载后首先核对这一项。</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>归档的 SHA-256（小写十六进制）。</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    /// <summary>对<b>原始归档字节</b>的 Ed25519 签名（base64，64 字节）。</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }
}

/// <summary>
/// 被逐条拒绝的清单条目及其原因。
/// </summary>
/// <remarks>
/// 逐条拒绝而非整份清单失败，是因为清单条目属于<b>非信任元数据</b>：真正的内容信任锚是
/// 每个发布包的 Ed25519 签名（用本应用内置公钥校验），清单本身没有签名。因此某一条目
/// 被篡改或写坏，既不能让其余三个平台一起不可用，也不会削弱安全性。
/// 被拒绝的条目仍要上报，否则平台会"凭空消失"，用户无从得知原因。
/// </remarks>
public sealed record ConnectorCatalogRejection(string PlayerKey, string Reason);

/// <summary>清单解析结果：可用条目 + 被逐条拒绝的条目。</summary>
public sealed record ConnectorCatalogSnapshot(
    IReadOnlyList<ConnectorCatalogEntry> Entries,
    IReadOnlyList<ConnectorCatalogRejection> Rejected)
{
    /// <summary>按 key 查可用条目；不存在返回 <see langword="null"/>。</summary>
    public ConnectorCatalogEntry? Find(string playerKey) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Id, playerKey, StringComparison.Ordinal));

    /// <summary>按 key 查拒绝原因；未被拒绝返回 <see langword="null"/>。</summary>
    public string? FindRejection(string playerKey) =>
        Rejected.FirstOrDefault(rejection =>
            string.Equals(rejection.PlayerKey, playerKey, StringComparison.Ordinal))?.Reason;
}
