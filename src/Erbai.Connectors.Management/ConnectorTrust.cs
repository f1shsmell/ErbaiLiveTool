using Erbai.Connectors.Management.Crypto;

namespace Erbai.Connectors.Management;

/// <summary>
/// 上游连接器发布方的信任锚（决策 D3）。
/// </summary>
/// <remarks>
/// <para>
/// 公钥<b>固定在本仓库内</b>。清单里的 <c>publicKeyId</c> 只用于「发现发布方轮换密钥」，
/// 而不是「选择要信任哪把密钥」——两者语义完全不同：id 与内置值不符时我们<b>拒绝整份清单</b>，
/// 而不是改用清单自带的密钥。否则攻击者只要同时改掉清单和签名就能自签自验，签名校验形同虚设。
/// </para>
/// <para>
/// 密钥来源：awoo-connectors 发布方；与参考实现 <c>electron/connector-updater.ts</c> 的
/// <c>RELEASE_PUBLIC_KEY</c> 一致（2026-09-18 核对）。
/// </para>
/// </remarks>
public static class ConnectorTrust
{
    /// <summary>内置公钥对应的密钥标识，与清单 <c>publicKeyId</c> 必须等值。</summary>
    public const string PublicKeyId = "bilincm-connectors-2026-01";

    /// <summary>发布方 Ed25519 公钥的 SubjectPublicKeyInfo（DER，base64）。</summary>
    private const string PublicKeySpkiBase64 =
        "MCowBQYDK2VwAyEApFy/TMxhGKlxzOS2b1gjvQxnvFhjefK0sbxsCXFS2uc=";

    // Ed25519 SPKI 前缀固定为 12 字节：SEQUENCE(42) { SEQUENCE(5) { OID 1.3.101.112 }, BIT STRING(33) }，
    // 后接 32 字节裸公钥。这里断言前缀，避免将来误换成一串别的算法密钥却继续"能跑"。
    private static readonly byte[] ExpectedSpkiPrefix =
    [
        0x30, 0x2A, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70, 0x03, 0x21, 0x00,
    ];

    private static readonly byte[] RawPublicKey = ExtractRawPublicKey();

    /// <summary>裸 32 字节 Ed25519 公钥，供验签使用。</summary>
    internal static ReadOnlySpan<byte> Ed25519PublicKey => RawPublicKey;

    /// <summary>判断清单声明的密钥标识是否为内置值。</summary>
    public static bool IsExpectedKeyId(string? publicKeyId) =>
        string.Equals(publicKeyId, PublicKeyId, StringComparison.Ordinal);

    private static byte[] ExtractRawPublicKey()
    {
        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(PublicKeySpkiBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("内置 Ed25519 公钥不是合法 base64。", ex);
        }

        int expectedLength = ExpectedSpkiPrefix.Length + Ed25519Verifier.PublicKeySize;
        if (spki.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"内置 Ed25519 公钥长度异常：{spki.Length}，期望 {expectedLength}。");
        }

        if (!spki.AsSpan(0, ExpectedSpkiPrefix.Length).SequenceEqual(ExpectedSpkiPrefix))
        {
            throw new InvalidOperationException("内置 Ed25519 公钥的 SPKI 前缀不是 Ed25519（OID 1.3.101.112）。");
        }

        return spki[^Ed25519Verifier.PublicKeySize..];
    }
}
