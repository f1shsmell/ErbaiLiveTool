using System.Text.RegularExpressions;
using Erbai.Contracts.Players;

namespace Erbai.Connectors.Management.Catalog;

/// <summary>
/// 清单合法性校验。全部规则都在这里，纯函数、无 IO，便于用 JSON 字符串直接单测。
/// </summary>
/// <remarks>
/// 校验立场：<b>宁严勿宽</b>。清单是信任边界的入口，一旦放行一个字段被篡改的条目，
/// 后面的 SHA-256 / Ed25519 校验仍然会挡住内容替换——但下载地址、rid、资产名这些
/// 会影响"往哪里下、下完怎么装"的字段必须在这里挡住。
/// </remarks>
internal static class ConnectorCatalogValidator
{
    /// <summary>本实现唯一支持的清单版本。</summary>
    internal const int SupportedSchemaVersion = 2;

    /// <summary>清单里允许出现的下载主机白名单。</summary>
    internal static readonly string[] AllowedDownloadHosts = ["app.enkianss.us"];

    /// <summary>单个归档的字节上限，防止清单被改成让我们下载任意大文件。</summary>
    internal const long MaxArchiveBytes = 256L * 1024 * 1024;

    private static readonly Regex VersionPattern =
        new(@"^\d+(?:\.\d+){2,4}$", RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Pattern =
        new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 校验整份清单，返回其中<b>本应用认可且逐项合法</b>的连接器条目。
    /// </summary>
    /// <exception cref="ConnectorManagementException">清单结构、信任锚不合法，或无任何可用条目。</exception>
    internal static IReadOnlyList<ConnectorCatalogEntry> Validate(ConnectorCatalog catalog) =>
        ValidateWithRejections(catalog).Entries;

    /// <summary>
    /// 校验整份清单，返回可用条目与<b>被逐条拒绝</b>的条目。
    /// </summary>
    /// <remarks>
    /// 条目级问题一律降级为"跳过该条目"而不是让整份清单失败：四个平台由同一上游发布，
    /// 但版本推进并不保证同步，某一平台先切到新协议时不该把其余三个一起拖下水。
    /// 与"未知 key 跳过"是同一个立场。只有清单级问题（schemaVersion / 公钥标识 / 结构）
    /// 才致命——那些说明我们拿到的根本不是本应用该用的清单。
    /// </remarks>
    /// <exception cref="ConnectorManagementException">清单结构、信任锚不合法，或无任何可用条目。</exception>
    internal static ConnectorCatalogSnapshot ValidateWithRejections(ConnectorCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.SchemaVersion != SupportedSchemaVersion)
        {
            throw new ConnectorManagementException(
                $"连接器清单版本不兼容：{catalog.SchemaVersion}，本应用仅支持 {SupportedSchemaVersion}。");
        }

        if (!ConnectorTrust.IsExpectedKeyId(catalog.PublicKeyId))
        {
            // 注意：这里拒绝，而不是改用清单自带密钥。详见 ConnectorTrust 的注释。
            throw new ConnectorManagementException(
                $"连接器清单签名密钥标识不匹配：{catalog.PublicKeyId ?? "(缺失)"}，"
                + $"本应用内置 {ConnectorTrust.PublicKeyId}。");
        }

        if (catalog.Connectors is null || catalog.Connectors.Count == 0)
        {
            throw new ConnectorManagementException("连接器清单未包含任何连接器条目。");
        }

        List<ConnectorCatalogEntry> usable = [];
        List<ConnectorCatalogRejection> rejected = [];

        foreach ((string key, ConnectorCatalogEntry entry) in catalog.Connectors)
        {
            // 上游新增平台时不应让整份清单失效：只跳过不认识的 key。
            // 这不会削弱安全性——不认识的 key 我们永远不会去安装。
            if (!ConnectorPlayers.IsPluginPlayerKey(key))
            {
                continue;
            }

            try
            {
                ValidateEntry(key, entry);
            }
            catch (ConnectorManagementException ex)
            {
                rejected.Add(new ConnectorCatalogRejection(key, ex.Message));
                continue;
            }

            usable.Add(entry);
        }

        if (usable.Count == 0)
        {
            string detail = rejected.Count == 0
                ? "清单中没有本应用支持的连接器条目。"
                : "清单中本应用支持的连接器条目全部不合法：" + string.Join("；", rejected.Select(r => r.Reason));

            throw new ConnectorManagementException($"连接器清单中没有可用的连接器条目。{detail}");
        }

        return new ConnectorCatalogSnapshot(usable, rejected);
    }

    private static void ValidateEntry(string key, ConnectorCatalogEntry entry)
    {
        if (!string.Equals(entry.Id, key, StringComparison.Ordinal))
        {
            throw new ConnectorManagementException(
                $"连接器条目 id 与键名不一致：键 {key}，id {entry.Id ?? "(缺失)"}。");
        }

        if (string.IsNullOrEmpty(entry.Version) || !VersionPattern.IsMatch(entry.Version))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的版本号不合法：{entry.Version ?? "(缺失)"}。");
        }

        if (entry.ProtocolVersion != ConnectorProtocol.ProtocolVersion)
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的协议版本不兼容：{entry.ProtocolVersion}，"
                + $"本应用仅支持 {ConnectorProtocol.ProtocolVersion}。");
        }

        ConnectorPackage package = entry.Package
            ?? throw new ConnectorManagementException($"连接器 {key} 缺少 package 字段。");

        if (package.Deployment is not ("framework-dependent" or "self-contained"))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的部署方式不支持：{package.Deployment ?? "(缺失)"}。");
        }

        if (!ConnectorPlayers.IsSupportedRuntime(package.Runtime))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的运行时标识不支持：{package.Runtime ?? "(缺失)"}。");
        }

        if (!ConnectorPlayers.IsRecognizedAssetName(package.Asset, key, entry.Version, package.Runtime!))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的资产名不符合规范：{package.Asset ?? "(缺失)"}。");
        }

        if (package.Size <= 0 || package.Size > MaxArchiveBytes)
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的归档大小超出允许范围：{package.Size}。");
        }

        if (string.IsNullOrEmpty(package.Sha256) || !Sha256Pattern.IsMatch(package.Sha256))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的 sha256 字段不合法：{package.Sha256 ?? "(缺失)"}。");
        }

        if (!IsValidSignature(package.Signature))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的 signature 字段不是 64 字节 base64 的 Ed25519 签名。");
        }

        ValidateDownloadUrl(key, package);
    }

    private static void ValidateDownloadUrl(string key, ConnectorPackage package)
    {
        if (string.IsNullOrEmpty(package.DownloadUrl)
            || !Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的下载地址不合法：{package.DownloadUrl ?? "(缺失)"}。");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的下载地址必须使用 HTTPS：{uri}。");
        }

        if (!AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的下载主机不在白名单内：{uri.Host}。");
        }

        // URL 基名必须与资产名一致，否则清单可以声明一个合法资产名却把流量导到别的文件。
        string basename = Uri.UnescapeDataString(uri.Segments[^1]);
        if (!string.Equals(basename, package.Asset, StringComparison.Ordinal))
        {
            throw new ConnectorManagementException(
                $"连接器 {key} 的下载地址基名与资产名不一致：{basename} vs {package.Asset}。");
        }
    }

    private static bool IsValidSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(signature).Length == Crypto.Ed25519Verifier.SignatureSize;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
