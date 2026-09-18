using System.Security.Cryptography;
using Erbai.Connectors.Management.Catalog;
using Erbai.Connectors.Management.Crypto;

namespace Erbai.Connectors.Management;

/// <summary>包校验的失败原因。</summary>
public enum ConnectorPackageVerificationFailure
{
    /// <summary>校验通过。</summary>
    None = 0,

    /// <summary>归档字节数与清单声明不符。</summary>
    SizeMismatch,

    /// <summary>归档 SHA-256 与清单声明不符。</summary>
    Sha256Mismatch,

    /// <summary>Ed25519 签名无效。</summary>
    SignatureInvalid,
}

/// <summary>包校验结果。</summary>
public sealed record ConnectorPackageVerificationResult(
    bool IsValid,
    ConnectorPackageVerificationFailure Failure,
    string Message)
{
    internal static ConnectorPackageVerificationResult Valid { get; } =
        new(true, ConnectorPackageVerificationFailure.None, string.Empty);
}

/// <summary>
/// 安装包校验：<c>size</c> → SHA-256 → Ed25519（决策 D2）。
/// </summary>
/// <remarks>
/// <para>
/// 顺序与参考实现一致，且顺序本身有意义：先比字节数（最便宜，挡掉截断下载），
/// 再比 SHA-256（中等，挡掉内容损坏或替换），最后验签（最贵，挡掉攻击者重新打包并
/// 自算哈希的情况）。跳过前两步会让验签对每个字节都做一次哈希，白白浪费。
/// </para>
/// <para>
/// 签名对象是<b>原始归档字节</b>，不是归档的哈希——这一点容易搞错，已用真实上游
/// 包与真实签名验证过（6,893,678 字节的 netease 包，2026-09-18）。
/// </para>
/// </remarks>
public static class ConnectorPackageVerifier
{
    /// <summary>
    /// 按 size → SHA-256 → Ed25519 顺序校验，返回结果而不抛异常。
    /// </summary>
    public static ConnectorPackageVerificationResult Verify(
        ConnectorPackage package,
        ReadOnlySpan<byte> archive)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (archive.Length != package.Size)
        {
            return new ConnectorPackageVerificationResult(
                false,
                ConnectorPackageVerificationFailure.SizeMismatch,
                $"归档大小不匹配：实际 {archive.Length} 字节，清单声明 {package.Size} 字节。");
        }

        if (!MatchesSha256(package.Sha256, archive))
        {
            return new ConnectorPackageVerificationResult(
                false,
                ConnectorPackageVerificationFailure.Sha256Mismatch,
                "归档 SHA-256 与清单声明不一致。");
        }

        if (!MatchesSignature(package.Signature, archive))
        {
            return new ConnectorPackageVerificationResult(
                false,
                ConnectorPackageVerificationFailure.SignatureInvalid,
                "归档 Ed25519 签名校验失败。");
        }

        return ConnectorPackageVerificationResult.Valid;
    }

    /// <summary>校验失败时抛出 <see cref="ConnectorManagementException"/>。</summary>
    /// <exception cref="ConnectorManagementException">任一校验步骤不通过。</exception>
    public static void VerifyOrThrow(ConnectorPackage package, ReadOnlySpan<byte> archive)
    {
        ConnectorPackageVerificationResult result = Verify(package, archive);
        if (!result.IsValid)
        {
            throw new ConnectorManagementException(result.Message);
        }
    }

    private static bool MatchesSha256(string? expectedHex, ReadOnlySpan<byte> archive)
    {
        if (string.IsNullOrEmpty(expectedHex) || expectedHex.Length != 64)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[32];
        try
        {
            if (!Convert.FromHexString(expectedHex).AsSpan().TryCopyTo(expected))
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[32];
        if (!SHA256.TryHashData(archive, actual, out _))
        {
            return false;
        }

        // 定长比较：避免用比较耗时泄露前缀匹配长度。
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool MatchesSignature(string? signatureBase64, ReadOnlySpan<byte> archive)
    {
        if (string.IsNullOrEmpty(signatureBase64))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (signature.Length != Ed25519Verifier.SignatureSize)
        {
            return false;
        }

        return Ed25519Verifier.Verify(ConnectorTrust.Ed25519PublicKey, archive, signature);
    }
}
