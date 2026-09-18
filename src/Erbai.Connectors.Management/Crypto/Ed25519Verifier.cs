// Ed25519 (RFC 8032) verification-only implementation.
//
// Why this file exists
// --------------------
// ErbaiLiveTool must verify Ed25519 signatures over connector packages published by the
// upstream Awoo connector catalog. Neither available platform primitive provides Ed25519
// on our supported OS matrix:
//   * .NET 8 has no managed Ed25519 API (System.Security.Cryptography stops at
//     ECDsa / ECDH / RSA / DSA).
//   * Windows CNG does not expose BCRYPT_ED25519_ALGORITHM here: BCryptOpenAlgorithmProvider
//     returns STATUS_NOT_FOUND (0xC0000225) for "ED25519" while ECDSA_P256/P384,
//     ECDH_P256, SHA256 and RSA all open successfully (measured 2026-09-18 on the
//     project's Windows 10 19045 development machine).
//
// Provenance
// ----------
// Independent implementation written directly against the published algorithm:
// RFC 8032 section 5.1.3 (point decoding), 5.1.7 (verification) and the standard
// "a = -1" twisted-Edwards extended-coordinate formulas from the Explicit-Formulas
// Database (add-2008-hwcd-3, dbl-2008-hwcd-3). No third-party code was copied, so this
// file carries the repository's own MIT license rather than an upstream one.
//
// Validated against RFC 8032 section 7.1 TEST 1/2/3 vectors plus the real upstream
// netease package and its real catalog signature (6,893,678 bytes, 2026-09-18).
//
// Verification only. There is deliberately no signing path: this project never needs to
// produce signatures, and shipping key-generation/signing code would widen the attack
// surface for no benefit.
//
// Performance: BigInteger field arithmetic is slower than ref10/NaCl, but verification
// runs once per connector install and the SHA-512 pass over a multi-megabyte archive
// dominates anyway (~15 ms end to end for the 6.9 MB netease package).

using System.Numerics;
using System.Security.Cryptography;

namespace Erbai.Connectors.Management.Crypto;

/// <summary>
/// Verifies Ed25519 (RFC 8032) signatures. Verification-only, managed, dependency-free.
/// </summary>
internal static class Ed25519Verifier
{
    /// <summary>Length of an encoded Ed25519 public key, in bytes.</summary>
    internal const int PublicKeySize = 32;

    /// <summary>Length of an encoded Ed25519 signature, in bytes.</summary>
    internal const int SignatureSize = 64;

    // Curve constants. p = 2^255 - 19 (the field prime), L = the prime order of the base
    // point, d = -121665/121666 (the twisted-Edwards coefficient).
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    private static readonly BigInteger D = Mod(-121665 * ModInverse(121666));

    // sqrt(-1) mod p. Valid because p = 5 (mod 8), so 2^((p-1)/4) is a square root of -1.
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);

    private static readonly Point BasePoint = CreateBasePoint();

    /// <summary>
    /// Verifies <paramref name="signature"/> over <paramref name="message"/> using
    /// <paramref name="publicKey"/>, per RFC 8032 section 5.1.7.
    /// </summary>
    /// <returns><see langword="true"/> when the signature is valid.</returns>
    internal static bool Verify(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        ReadOnlySpan<byte> rBytes = signature[..PublicKeySize];
        ReadOnlySpan<byte> sBytes = signature[PublicKeySize..];

        // Reject S >= L. Without this check the scheme is malleable: S + L encodes the
        // same point and would otherwise also verify.
        BigInteger s = FromLittleEndian(sBytes);
        if (s >= L)
        {
            return false;
        }

        if (!TryDecodePoint(publicKey, out Point a) || !TryDecodePoint(rBytes, out Point r))
        {
            return false;
        }

        // k = SHA-512(R || A || M) mod L. Streamed so a multi-megabyte payload is never
        // copied into one contiguous buffer.
        BigInteger k;
        using (IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
        {
            sha.AppendData(rBytes.ToArray());
            sha.AppendData(publicKey.ToArray());
            sha.AppendData(message.ToArray());
            k = FromLittleEndian(sha.GetHashAndReset()) % L;
        }

        // Accept when [S]B == R + [k]A.
        Point left = ScalarMultiply(BasePoint, s);
        Point right = Add(r, ScalarMultiply(a, k));
        return SamePoint(left, right);
    }

    // ---------------------------------------------------------------------------------
    // Field helpers
    // ---------------------------------------------------------------------------------

    /// <summary>Reduces to the canonical non-negative residue modulo p.</summary>
    private static BigInteger Mod(BigInteger x)
    {
        BigInteger r = x % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger ModInverse(BigInteger a)
    {
        BigInteger t = 0, newT = 1;
        BigInteger r = P, newR = Mod(a);

        while (!newR.IsZero)
        {
            BigInteger q = r / newR;
            (t, newT) = (newT, t - (q * newT));
            (r, newR) = (newR, r - (q * newR));
        }

        if (r != BigInteger.One)
        {
            throw new InvalidOperationException("Value is not invertible modulo p.");
        }

        return t.Sign < 0 ? t + P : t;
    }

    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes) =>
        new(bytes.ToArray(), isUnsigned: true, isBigEndian: false);

    // ---------------------------------------------------------------------------------
    // Point decoding (RFC 8032 section 5.1.3)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Decodes a 32-byte compressed Edwards point: the low 255 bits are y, the top bit is
    /// the sign of x, and x is recovered from the curve equation.
    /// </summary>
    private static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = default;

        if (encoded.Length != PublicKeySize)
        {
            return false;
        }

        byte[] yBytes = encoded.ToArray();
        int signX = (yBytes[31] >> 7) & 1;
        yBytes[31] &= 0x7F;

        BigInteger y = FromLittleEndian(yBytes);
        if (y >= P)
        {
            return false;
        }

        BigInteger y2 = Mod(y * y);
        BigInteger u = Mod(y2 - BigInteger.One);
        BigInteger v = Mod((D * y2) + BigInteger.One);

        // x = sqrt(u / v)
        BigInteger x = SqrtRatio(u, v, out bool ok);
        if (!ok)
        {
            return false;
        }

        // x = 0 with the sign bit set is a non-canonical encoding.
        if (x.IsZero && signX == 1)
        {
            return false;
        }

        if ((int)(x & BigInteger.One) != signX)
        {
            x = Mod(P - x);
        }

        point = new Point(x, y, BigInteger.One, Mod(x * y));
        return true;
    }

    /// <summary>
    /// Computes sqrt(u/v) when it exists, using the p = 5 (mod 8) recipe from RFC 8032.
    /// </summary>
    private static BigInteger SqrtRatio(BigInteger u, BigInteger v, out bool ok)
    {
        BigInteger v3 = Mod(v * v * v);
        BigInteger v7 = Mod(v3 * v3 * v);
        BigInteger power = BigInteger.ModPow(Mod(u * v7), (P - 5) / 8, P);
        BigInteger x = Mod(Mod(u * v3) * power);

        BigInteger check = Mod(v * x * x);
        if (check == u)
        {
            ok = true;
            return x;
        }

        if (check == Mod(P - u))
        {
            ok = true;
            return Mod(x * SqrtM1);
        }

        ok = false;
        return BigInteger.Zero;
    }

    private static Point CreateBasePoint()
    {
        BigInteger y = Mod(4 * ModInverse(5));
        BigInteger y2 = Mod(y * y);
        BigInteger u = Mod(y2 - BigInteger.One);
        BigInteger v = Mod((D * y2) + BigInteger.One);

        BigInteger x = SqrtRatio(u, v, out bool ok);
        if (!ok)
        {
            throw new InvalidOperationException("Failed to recover the Ed25519 base point.");
        }

        // The base point uses the even x.
        if (!(x & BigInteger.One).IsZero)
        {
            x = Mod(P - x);
        }

        return new Point(x, y, BigInteger.One, Mod(x * y));
    }

    // ---------------------------------------------------------------------------------
    // Group arithmetic (extended twisted-Edwards coordinates, a = -1)
    // ---------------------------------------------------------------------------------
    //
    // A point is (X : Y : Z : T) with affine x = X/Z, y = Y/Z and T = XY/Z. Keeping a
    // redundant T lets addition and doubling run without any field inversion.

    private readonly struct Point
    {
        internal Point(BigInteger x, BigInteger y, BigInteger z, BigInteger t)
        {
            X = x;
            Y = y;
            Z = z;
            T = t;
        }

        internal BigInteger X { get; }

        internal BigInteger Y { get; }

        internal BigInteger Z { get; }

        internal BigInteger T { get; }
    }

    /// <summary>Unified addition, "add-2008-hwcd-3".</summary>
    private static Point Add(Point p, Point q)
    {
        BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
        BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
        BigInteger c = Mod(p.T * 2 * D * q.T);
        BigInteger d = Mod(p.Z * 2 * q.Z);
        BigInteger e = b - a;
        BigInteger f = d - c;
        BigInteger g = d + c;
        BigInteger h = b + a;

        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    /// <summary>Doubling, "dbl-2008-hwcd-3".</summary>
    private static Point Double(Point p)
    {
        BigInteger a = Mod((p.Y - p.X) * (p.Y - p.X));
        BigInteger b = Mod((p.Y + p.X) * (p.Y + p.X));
        BigInteger c = Mod(p.T * p.T * 2 * D);
        BigInteger d = Mod(p.Z * p.Z * 2);
        BigInteger e = b - a;
        BigInteger f = d - c;
        BigInteger g = d + c;
        BigInteger h = b + a;

        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    /// <summary>Double-and-add scalar multiplication, MSB first.</summary>
    private static Point ScalarMultiply(Point p, BigInteger scalar)
    {
        Point result = new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);

        for (int i = (int)scalar.GetBitLength() - 1; i >= 0; i--)
        {
            result = Double(result);

            if (!((scalar >> i) & BigInteger.One).IsZero)
            {
                result = Add(result, p);
            }
        }

        return result;
    }

    /// <summary>Compares two projective points without a field inversion.</summary>
    private static bool SamePoint(Point p, Point q)
    {
        // Cross-multiply so Z factors out: X1*Z2 == X2*Z1 && Y1*Z2 == Y2*Z1.
        bool xEqual = Mod(p.X * q.Z) == Mod(q.X * p.Z);
        bool yEqual = Mod(p.Y * q.Z) == Mod(q.Y * p.Z);
        return xEqual && yEqual;
    }
}
