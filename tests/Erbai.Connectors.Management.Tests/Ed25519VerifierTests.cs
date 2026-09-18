using System.Security.Cryptography;
using Erbai.Connectors.Management.Crypto;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// Ed25519 验签实现（<see cref="Ed25519Verifier"/>）的正确性验证。
/// </summary>
/// <remarks>
/// <para>
/// 自研密码学实现必须有独立于"我们自己的真实用例"的权威校验，否则一旦实现有偏，
/// 用一个同样有偏的用例是发现不了的。因此这里先用 <b>RFC 8032 §7.1 官方向量</b>打底，
/// 再叠加真实上游包的端到端验证。
/// </para>
/// <para>
/// 真实包 6.9 MB，不入库。用例会在临时目录 / 环境变量里找它，找不到就跳过
/// （与 <c>ThirdPartyConnectorIntegrationTests</c> 对 <c>vendor/</c> 的处理一致）。
/// </para>
/// </remarks>
public class Ed25519VerifierTests
{
    // RFC 8032 §7.1 TEST 1 / TEST 2 / TEST 3。
    private const string Rfc8032Test1PublicKey =
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";

    private const string Rfc8032Test1Signature =
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155"
        + "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";

    private const string Rfc8032Test2PublicKey =
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c";

    private const string Rfc8032Test2Signature =
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da"
        + "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00";

    private const string Rfc8032Test3PublicKey =
        "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025";

    private const string Rfc8032Test3Signature =
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac"
        + "18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a";

    // 真实上游 netease 包（3.1.38.205386.1）在 v2 清单里声明的元数据，2026-09-18 抓取。
    private const long NeteasePackageSize = 6_893_678;

    private const string NeteasePackageSha256 =
        "6cf83940fc93c69f9af4b97717095e2ef26c117ac4a6e57939f9bab5f4f22cfb";

    private const string NeteasePackageSignature =
        "vF1Zd+RDePv18cazRL3qYuuvLq/Qf4hCFHaT7kZAHpFFe0DjeIXTSddwxNta1bOiLnCjgkvrnpRxKudWr7QZDw==";

    [Theory]
    [InlineData(Rfc8032Test1PublicKey, "", Rfc8032Test1Signature)]
    [InlineData(Rfc8032Test2PublicKey, "72", Rfc8032Test2Signature)]
    [InlineData(Rfc8032Test3PublicKey, "af82", Rfc8032Test3Signature)]
    public void Verify_AcceptsRfc8032Vectors(string publicKeyHex, string messageHex, string signatureHex)
    {
        byte[] publicKey = Convert.FromHexString(publicKeyHex);
        byte[] message = Convert.FromHexString(messageHex);
        byte[] signature = Convert.FromHexString(signatureHex);

        Assert.True(Ed25519Verifier.Verify(publicKey, message, signature));
    }

    [Theory]
    [InlineData(Rfc8032Test1PublicKey, "", Rfc8032Test1Signature)]
    [InlineData(Rfc8032Test2PublicKey, "72", Rfc8032Test2Signature)]
    [InlineData(Rfc8032Test3PublicKey, "af82", Rfc8032Test3Signature)]
    public void Verify_RejectsTamperedMessage(string publicKeyHex, string messageHex, string signatureHex)
    {
        byte[] publicKey = Convert.FromHexString(publicKeyHex);
        byte[] signature = Convert.FromHexString(signatureHex);

        // 空消息的用例改成 1 字节；非空的翻转最后一位。
        byte[] tampered = messageHex.Length == 0
            ? [0x00]
            : Convert.FromHexString(messageHex);
        tampered[^1] ^= 0x01;

        Assert.False(Ed25519Verifier.Verify(publicKey, tampered, signature));
    }

    [Fact]
    public void Verify_RejectsFlippedSignatureBit()
    {
        byte[] publicKey = Convert.FromHexString(Rfc8032Test2PublicKey);
        byte[] message = Convert.FromHexString("72");
        byte[] signature = Convert.FromHexString(Rfc8032Test2Signature);
        signature[0] ^= 0x01;

        Assert.False(Ed25519Verifier.Verify(publicKey, message, signature));
    }

    [Fact]
    public void Verify_RejectsWrongPublicKey()
    {
        byte[] publicKey = Convert.FromHexString(Rfc8032Test2PublicKey);
        publicKey[0] ^= 0x01;

        Assert.False(Ed25519Verifier.Verify(
            publicKey,
            Convert.FromHexString("72"),
            Convert.FromHexString(Rfc8032Test2Signature)));
    }

    /// <summary>
    /// S + L 与 S 编码同一个点。若不显式拒绝 <c>S &gt;= L</c>，签名方案就是可延展的
    /// （同一份消息会出现多个"有效"签名），因此这一条必须挡掉。
    /// </summary>
    [Fact]
    public void Verify_RejectsNonCanonicalScalar()
    {
        byte[] publicKey = Convert.FromHexString(Rfc8032Test2PublicKey);
        byte[] message = Convert.FromHexString("72");
        byte[] signature = Convert.FromHexString(Rfc8032Test2Signature);

        // L = 2^252 + 27742317777372353535851937790883648493，按小端写入 S。
        byte[] orderL = Convert.FromHexString(
            "edd3f55c1a631258d69cf7a2def9de1400000000000000000000000000000010");

        // 用 BigInteger 把 S 换成 S + L，再写回小端。
        var s = new System.Numerics.BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: false);
        var l = new System.Numerics.BigInteger(orderL, isUnsigned: true, isBigEndian: false);
        var malleable = s + l;

        byte[] bytes = malleable.ToByteArray(isUnsigned: true, isBigEndian: false);
        Assert.True(bytes.Length <= 32, "测试构造的 S+L 应仍在 32 字节内。");
        bytes.CopyTo(signature.AsSpan(32, 32));

        Assert.False(Ed25519Verifier.Verify(publicKey, message, signature));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(65)]
    public void Verify_RejectsMalformedLengths(int length)
    {
        byte[] publicKey = new byte[length];
        byte[] signature = new byte[length];

        Assert.False(Ed25519Verifier.Verify(publicKey, "m"u8, signature));
    }

    /// <summary>
    /// 真实上游包 + 真实清单签名。这是"vendored 实现能否用于生产"的决定性验证。
    /// </summary>
    [Fact]
    public void Verify_AcceptsRealUpstreamNeteasePackage()
    {
        string? archivePath = FindRealArchive();
        if (archivePath is null)
        {
            // 6.9 MB 的真实包不入库；缺失时跳过（与 vendor/ 的处理一致）。
            return;
        }

        byte[] archive = File.ReadAllBytes(archivePath);
        Assert.Equal(NeteasePackageSize, archive.LongLength);

        string actualSha = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        Assert.Equal(NeteasePackageSha256, actualSha);

        byte[] signature = Convert.FromBase64String(NeteasePackageSignature);

        Assert.True(
            Ed25519Verifier.Verify(ConnectorTrust.Ed25519PublicKey, archive, signature),
            "真实上游 netease 包的 Ed25519 签名应当校验通过。");

        // 负面对照：翻转一个字节必须失败，否则上面的 true 可能只是"永远返回 true"。
        byte[] tampered = (byte[])archive.Clone();
        tampered[^1] ^= 0x01;
        Assert.False(Ed25519Verifier.Verify(ConnectorTrust.Ed25519PublicKey, tampered, signature));
    }

    /// <summary>内置公钥必须是合法的 Ed25519 SPKI（长度 + OID 前缀）。</summary>
    [Fact]
    public void ConnectorTrust_PublicKeyIsWellFormed()
    {
        Assert.Equal(32, ConnectorTrust.Ed25519PublicKey.Length);
        Assert.Equal("bilincm-connectors-2026-01", ConnectorTrust.PublicKeyId);
        Assert.True(ConnectorTrust.IsExpectedKeyId("bilincm-connectors-2026-01"));
        Assert.False(ConnectorTrust.IsExpectedKeyId("bilincm-connectors-2027-01"));
        Assert.False(ConnectorTrust.IsExpectedKeyId(null));
    }

    private static string? FindRealArchive()
    {
        string? configured = Environment.GetEnvironmentVariable("ERBAI_CONNECTOR_TEST_ARCHIVE");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            return configured;
        }

        string candidate = Path.Combine(
            Path.GetTempPath(), "connector-spike", "netease.zip");

        return File.Exists(candidate) ? candidate : null;
    }
}
