using System.Security.Cryptography;
using System.Text;

namespace Erbai.Live.Bilibili.Protocol;

/// <summary>
/// WBI 签名（docs/04-协议与接口契约.md §2.2）：
/// nav 接口 wbi_img 拿 img_url/sub_url → 文件名拼 64 字符 → KEY_MAP 重排取前 32 = mixin key；
/// 参数按键排序、值剔除 <c>!'()*</c>、追加 wts，URL 编码拼串 + mixin key 后 MD5 = w_rid。
/// </summary>
public static class WbiSigner
{
    private static readonly int[] KeyMap =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52,
    };

    /// <summary>从 wbi_img 的 img_url/sub_url 提取 64 字符原始 key（文件名去掉扩展名与路径）。</summary>
    public static string ExtractRawKey(string imgUrl, string subUrl) => ImgKey(imgUrl) + ImgKey(subUrl);

    /// <summary>wbi_img 文件名 key：去掉路径与扩展名，取第一部分。</summary>
    public static string ImgKey(string url)
    {
        var fileName = Path.GetFileNameWithoutExtension(url);
        var dot = fileName.IndexOf('.');
        return dot < 0 ? fileName : fileName[..dot];
    }

    /// <summary>KEY_MAP 重排取前 32 字符。</summary>
    public static string GetMixinKey(string rawKey)
    {
        if (rawKey.Length < 64)
        {
            throw new ArgumentException($"raw key 必须至少 64 字符，实际 {rawKey.Length}", nameof(rawKey));
        }

        return new string(KeyMap.Take(32).Select(i => rawKey[i]).ToArray());
    }

    /// <summary>
    /// 对参数签名：返回按键排序、剔除 <c>!'()*</c>、含 wts/w_rid 的查询串
    /// （wts 自动取当前 Unix 秒；调用方已传 wts 时沿用）。
    /// </summary>
    public static string SignQuery(IReadOnlyDictionary<string, string> parameters, string mixinKey)
    {
        var withWts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            withWts[key] = Filter(value);
        }

        if (!withWts.ContainsKey("wts"))
        {
            withWts["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        }

        var qs = string.Join("&", withWts.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var wRid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(qs + mixinKey))).ToLower();
        return $"{qs}&w_rid={wRid}";
    }

    /// <summary>剔除官方约定排除的字符（<c>!'()*</c>）。</summary>
    public static string Filter(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is not ('!' or '\'' or '(' or ')' or '*'))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
