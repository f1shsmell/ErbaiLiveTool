using System.Runtime.InteropServices;
using System.Text.Json;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Login;

/// <summary>
/// B站凭据持久化（docs/04 §2.3）：
/// credentials.json 信封 <c>{"v":2,"cipher":"dpapi"|"plain","payload":base64}</c>；
/// Windows 用 DPAPI（CryptProtectData，禁止 UI 标志 0x1，失败绝不落明文）；
/// 原子写（临时文件 + fsync + replace）；旧版明文 JSON 自动迁移。
/// </summary>
public sealed class BilibiliCredentials
{
    public const string FileName = "bilibili_credentials.json";
    private const uint CryptProtectUiForbidden = 0x1;
    private const int Version = 2;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public BilibiliCredentials(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public string FilePath => _path;

    /// <summary>当前平台是否可用 DPAPI（非 Windows 用明文 fallback，显式标记）。</summary>
    public static bool DpapiAvailable => OperatingSystem.IsWindows();

    /// <summary>
    /// 读取凭据；文件缺失/损坏/解密失败返回 null。
    /// dpapi 文件在非 Windows 上直接拒绝（不隐式兜底明文）；
    /// 旧版明文 JSON（v≠2）自动迁移为保护格式后返回。
    /// </summary>
    public async Task<BilibiliCookies?> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[] raw;
        try
        {
            raw = await File.ReadAllBytesAsync(_path, ct);
        }
        catch (IOException)
        {
            return null;
        }

        var data = Decrypt(raw);
        if (data is not null)
        {
            return ToCookies(data);
        }

        // 旧版明文 JSON（v≠2）：迁移到保护格式（失败保持明文可用并返回）
        if (IsLegacyPlaintext(raw))
        {
            try
            {
                var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
                if (legacy is not null)
                {
                    try
                    {
                        await WriteAsync(ToCookies(legacy), ct);
                    }
                    catch (IOException)
                    {
                        // 迁移失败：明文仍可用，下次保存时重试
                    }

                    return ToCookies(legacy);
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    /// <summary>原子写盘（临时文件 + 写入 + fsync + replace）；DPAPI 失败抛 IOException 且不落盘。</summary>
    public async Task WriteAsync(BilibiliCookies cookies, CancellationToken ct = default)
    {
        var data = new Dictionary<string, string>
        {
            ["sessdata"] = cookies.Sessdata,
            ["bili_jct"] = cookies.BiliJct,
            ["dedeuserid"] = cookies.DedeUserId,
            ["refresh_token"] = cookies.RefreshToken,
            ["save_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        };

        var payload = Protect(Serialize(data));
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["v"] = Version,
            ["cipher"] = DpapiAvailable ? "dpapi" : "plain",
            ["payload"] = Convert.ToBase64String(payload),
        }, JsonOptions);

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".credentials.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            {
                await stream.WriteAsync(envelope, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            // Windows 防病毒扫描可能瞬时锁住刚写出的文件：Move 失败短重试
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, _path, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(50 * (attempt + 1), ct);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    await Task.Delay(50 * (attempt + 1), ct);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>删除凭据文件（退出登录）；不存在返回 false。</summary>
    public bool Delete()
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        File.Delete(_path);
        return true;
    }

    // ── 信封编解码 ─────────────────────────────────────────────────────────

    private static Dictionary<string, string>? Decrypt(byte[] raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("v", out var v) || v.GetInt32() != Version ||
                !root.TryGetProperty("cipher", out var cipher) ||
                !root.TryGetProperty("payload", out var payload))
            {
                return null;
            }

            var protectedBytes = Convert.FromBase64String(payload.GetString() ?? "");
            byte[]? plain = cipher.GetString() switch
            {
                "plain" => protectedBytes,
                "dpapi" when DpapiAvailable => Unprotect(protectedBytes),
                _ => null, // dpapi 文件在非 Windows / 未知 cipher：拒绝
            };
            if (plain is null)
            {
                return null;
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
            return data is null || !data.ContainsKey("sessdata") ? null : data;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsLegacyPlaintext(byte[] raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   (!doc.RootElement.TryGetProperty("v", out var v) || v.GetInt32() != Version);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static BilibiliCookies ToCookies(Dictionary<string, string> data) => new()
    {
        Sessdata = data.GetValueOrDefault("sessdata") ?? "",
        BiliJct = data.GetValueOrDefault("bili_jct") ?? "",
        DedeUserId = data.GetValueOrDefault("dedeuserid") ?? "",
        RefreshToken = data.GetValueOrDefault("refresh_token") ?? "",
    };

    private static byte[] Serialize(Dictionary<string, string> data) =>
        JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);

    private static byte[] Protect(byte[] data)
    {
        if (!DpapiAvailable)
        {
            return data;
        }

        var protectedBlob = ProtectData(data);
        return protectedBlob ?? throw new IOException("CryptProtectData 失败（DPAPI 不可用），凭据未保存");
    }

    private static byte[] Unprotect(byte[] blob)
    {
        return UnprotectData(blob)
               ?? throw new IOException("CryptUnprotectData 失败（DPAPI 凭据无法解密）");
    }

    // ── DPAPI P/Invoke ────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, out IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[]? ProtectData(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                return null;
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.PbData);
        }
    }

    private static byte[]? UnprotectData(byte[] blob)
    {
        var input = ToBlob(blob);
        try
        {
            if (!CryptUnprotectData(ref input, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                return null;
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.PbData);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { CbData = (uint)data.Length, PbData = pointer };
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        var result = new byte[blob.CbData];
        if (result.Length > 0)
        {
            Marshal.Copy(blob.PbData, result, 0, result.Length);
        }

        return result;
    }
}
