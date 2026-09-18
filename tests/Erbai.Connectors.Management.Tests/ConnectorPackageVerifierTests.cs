using System.Security.Cryptography;
using Erbai.Connectors.Management.Catalog;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// <see cref="ConnectorPackageVerifier"/> 的校验顺序与失败归因。
/// </summary>
/// <remarks>
/// 顺序（size → SHA-256 → Ed25519）不只是性能优化，也决定了错误归因是否准确：
/// 用户看到"大小不匹配"和看到"签名无效"要采取的行动完全不同。
/// 因此这里既测"该失败"，也测"失败原因必须是第一个不通过的那一步"。
/// </remarks>
public class ConnectorPackageVerifierTests
{
    private static readonly byte[] Archive = "synthetic connector archive"u8.ToArray();

    [Fact]
    public void Verify_ReportsSizeMismatchFirst()
    {
        // 三个字段全错：必须报 size，而不是 sha256 或签名。
        ConnectorPackage package = CreatePackage(
            size: 1,
            sha256: new string('0', 64),
            signature: Convert.ToBase64String(new byte[64]));

        ConnectorPackageVerificationResult result = ConnectorPackageVerifier.Verify(package, Archive);

        Assert.False(result.IsValid);
        Assert.Equal(ConnectorPackageVerificationFailure.SizeMismatch, result.Failure);
        Assert.Contains("大小不匹配", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_ReportsSha256MismatchWhenSizeMatches()
    {
        ConnectorPackage package = CreatePackage(
            size: Archive.Length,
            sha256: new string('0', 64),
            signature: Convert.ToBase64String(new byte[64]));

        ConnectorPackageVerificationResult result = ConnectorPackageVerifier.Verify(package, Archive);

        Assert.False(result.IsValid);
        Assert.Equal(ConnectorPackageVerificationFailure.Sha256Mismatch, result.Failure);
    }

    [Fact]
    public void Verify_ReportsSignatureInvalidWhenSizeAndHashMatch()
    {
        // 大小与哈希都真实，只有签名是 64 字节垃圾 → 必须走到第三步并报签名失败。
        ConnectorPackage package = CreatePackage(
            size: Archive.Length,
            sha256: Convert.ToHexString(SHA256.HashData(Archive)).ToLowerInvariant(),
            signature: Convert.ToBase64String(new byte[64]));

        ConnectorPackageVerificationResult result = ConnectorPackageVerifier.Verify(package, Archive);

        Assert.False(result.IsValid);
        Assert.Equal(ConnectorPackageVerificationFailure.SignatureInvalid, result.Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex-at-all")]
    [InlineData("abc")]
    public void Verify_TreatsBadSha256FieldAsMismatch(string? sha256)
    {
        ConnectorPackage package = CreatePackage(
            size: Archive.Length,
            sha256: sha256,
            signature: Convert.ToBase64String(new byte[64]));

        Assert.Equal(
            ConnectorPackageVerificationFailure.Sha256Mismatch,
            ConnectorPackageVerifier.Verify(package, Archive).Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!not-base64!!!")]
    [InlineData("c2hvcnQ=")]
    public void Verify_TreatsBadSignatureFieldAsInvalid(string? signature)
    {
        ConnectorPackage package = CreatePackage(
            size: Archive.Length,
            sha256: Convert.ToHexString(SHA256.HashData(Archive)).ToLowerInvariant(),
            signature: signature);

        Assert.Equal(
            ConnectorPackageVerificationFailure.SignatureInvalid,
            ConnectorPackageVerifier.Verify(package, Archive).Failure);
    }

    [Fact]
    public void VerifyOrThrow_ThrowsWithTheFailureMessage()
    {
        ConnectorPackage package = CreatePackage(1, new string('0', 64), Convert.ToBase64String(new byte[64]));

        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => ConnectorPackageVerifier.VerifyOrThrow(package, Archive));

        Assert.Contains("大小不匹配", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsNullPackage()
    {
        Assert.Throws<ArgumentNullException>(() => ConnectorPackageVerifier.Verify(null!, Archive));
    }

    /// <summary>
    /// 真实上游包 + 真实清单元数据走完整校验链（含 Ed25519）。
    /// 6.9 MB 的真实包不入库，缺失时跳过。
    /// </summary>
    [Fact]
    public void Verify_AcceptsRealUpstreamNeteasePackage()
    {
        string candidate = Path.Combine(Path.GetTempPath(), "connector-spike", "netease.zip");
        string? configured = Environment.GetEnvironmentVariable("ERBAI_CONNECTOR_TEST_ARCHIVE");
        string? path = !string.IsNullOrEmpty(configured) && File.Exists(configured)
            ? configured
            : File.Exists(candidate) ? candidate : null;

        if (path is null)
        {
            return;
        }

        byte[] archive = File.ReadAllBytes(path);

        ConnectorPackage package = CreatePackage(
            size: archive.LongLength,
            sha256: "6cf83940fc93c69f9af4b97717095e2ef26c117ac4a6e57939f9bab5f4f22cfb",
            signature: "vF1Zd+RDePv18cazRL3qYuuvLq/Qf4hCFHaT7kZAHpFFe0DjeIXTSddwxNta1bOiLnCjgkvrnpRxKudWr7QZDw==");

        ConnectorPackageVerificationResult result = ConnectorPackageVerifier.Verify(package, archive);

        Assert.True(result.IsValid, result.Message);
    }

    private static ConnectorPackage CreatePackage(long size, string? sha256, string? signature) => new()
    {
        Deployment = "framework-dependent",
        Runtime = "win-x64",
        Asset = "awoo-connector-netease-1.0.0-win-x64-framework-dependent.zip",
        Size = size,
        Sha256 = sha256,
        Signature = signature,
        DownloadUrl = "https://app.enkianss.us/connectors/v2/download/netease/1.0.0/"
            + "awoo-connector-netease-1.0.0-win-x64-framework-dependent.zip",
    };
}
