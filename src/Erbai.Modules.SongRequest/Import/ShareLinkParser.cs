using System.Text.RegularExpressions;
using Erbai.Contracts.Storage;

namespace Erbai.Modules.SongRequest.Import;

/// <summary>
/// 歌单分享链接解析：从分享文案（常带标题 + 短链 +「来自@APP」尾巴）中提取
/// URL 并识别平台。直接带 id 的链接就地取 id；短链（163cn.tv / c6.y.qq.com /
/// t1.kugou.com）只标记平台，id 由对应客户端解析重定向取得。
/// </summary>
public static partial class ShareLinkParser
{
    /// <summary>URL 字符集含 RFC3986 常用保留字（分享文案里 URL 后多接中文/空格，天然截断）。</summary>
    [GeneratedRegex(@"https?://[A-Za-z0-9._~:/?#\[\]@!$&'()*+,;=%-]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    // 网易云：music.163.com/playlist?id=…（含 y.music.163.com/m/…）与 /playlist/123 拼接式
    [GeneratedRegex(@"music\.163\.com/(?:[^#\s]*[?&])?id=(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NeteaseIdRegex();

    [GeneratedRegex(@"music\.163\.com/(?:#/)?(?:m/)?playlist[/?](?:[^#\s]*[?&])?id=(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NeteasePlaylistRegex();

    // 网易云用户主页：music.163.com/#/user/home?id=… 或 /user/home?id=…
    [GeneratedRegex(@"music\.163\.com/(?:#/)?user/home\?[^#\s]*[?&]?id=(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NeteaseUserHomeRegex();

    // QQ：i2/i.y.qq.com…playlist.html?id=…、taoge.html?id=…、c6 短链解析后的
    // y.qq.com/n/yqq/playlist/{dissid}.html 拼接式
    [GeneratedRegex(@"y\.qq\.com/(?:[^#\s]*[?&])?id=(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex QqIdRegex();

    [GeneratedRegex(@"y\.qq\.com/n/yqq/playlist/(?<id>\d+)\.html", RegexOptions.IgnoreCase)]
    private static partial Regex QqPathRegex();

    // 酷狗：specialid=…、/plist/list/{id}、分享重定向落地的 /yy/special/single/{id}.html
    [GeneratedRegex(@"kugou\.com/(?:[^#\s]*[?&])?specialid=(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex KugouSpecialIdRegex();

    [GeneratedRegex(@"kugou\.com/plist/list/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex KugouPlistRegex();

    [GeneratedRegex(@"kugou\.com/(?:yy/)?special/single/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex KugouSingleRegex();

    public static PlaylistShareLink? Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        foreach (Match match in UrlRegex().Matches(input))
        {
            var url = match.Value;
            if (TryParseUrl(url, out var link))
            {
                return link;
            }
        }

        return null;
    }

    /// <summary>解析单个 URL（内部用；短链返回无 id 的链接记录）。</summary>
    public static bool TryParseUrl(string url, out PlaylistShareLink link)
    {
        link = null!;

        // 网易云
        if (url.Contains("music.163.com", StringComparison.OrdinalIgnoreCase))
        {
            var user = NeteaseUserHomeRegex().Match(url);
            if (user.Success)
            {
                link = new PlaylistShareLink(PlaylistSource.Netease, PlaylistLinkKind.UserHome, url, UserId: user.Groups["id"].Value);
                return true;
            }

            var playlist = NeteasePlaylistRegex().Match(url);
            if (playlist.Success)
            {
                link = new PlaylistShareLink(PlaylistSource.Netease, PlaylistLinkKind.Playlist, url, PlaylistId: playlist.Groups["id"].Value);
                return true;
            }
        }
        else if (url.Contains("163cn.tv", StringComparison.OrdinalIgnoreCase))
        {
            // 短链可能指向歌单也可能指向用户主页——客户端解析重定向后再判别
            link = new PlaylistShareLink(PlaylistSource.Netease, PlaylistLinkKind.Playlist, url);
            return true;
        }

        // QQ 音乐
        if (url.Contains("y.qq.com", StringComparison.OrdinalIgnoreCase))
        {
            var qq = QqIdRegex().Match(url);
            if (!qq.Success)
            {
                qq = QqPathRegex().Match(url);
            }

            if (qq.Success)
            {
                link = new PlaylistShareLink(PlaylistSource.Qqmusic, PlaylistLinkKind.Playlist, url, PlaylistId: qq.Groups["id"].Value);
                return true;
            }

            // c6.y.qq.com/base/fcgi-bin/u?__=… 短链
            link = new PlaylistShareLink(PlaylistSource.Qqmusic, PlaylistLinkKind.Playlist, url);
            return true;
        }

        // 酷狗
        if (url.Contains("kugou.com", StringComparison.OrdinalIgnoreCase))
        {
            var special = KugouSpecialIdRegex().Match(url);
            if (!special.Success)
            {
                special = KugouPlistRegex().Match(url);
            }

            if (!special.Success)
            {
                special = KugouSingleRegex().Match(url);
            }

            if (special.Success)
            {
                link = new PlaylistShareLink(PlaylistSource.Kugou, PlaylistLinkKind.Playlist, url, PlaylistId: special.Groups["id"].Value);
                return true;
            }

            // t1.kugou.com/… 短链
            link = new PlaylistShareLink(PlaylistSource.Kugou, PlaylistLinkKind.Playlist, url);
            return true;
        }

        return false;
    }

    /// <summary>从任意 URL/HTML 文本提取网易云歌单 id（短链解析兜底用）。</summary>
    public static string? ExtractNeteasePlaylistId(string text) =>
        NeteaseIdRegex().Match(text ?? "") is { Success: true } m ? m.Groups["id"].Value : null;

    /// <summary>从任意 URL/HTML 文本提取 QQ 歌单 disstid（短链解析兜底用）。</summary>
    public static string? ExtractQqPlaylistId(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return QqIdRegex().Match(text) is { Success: true } q ? q.Groups["id"].Value
            : QqPathRegex().Match(text) is { Success: true } p ? p.Groups["id"].Value
            : null;
    }

    /// <summary>从任意 URL/HTML 文本提取酷狗 specialid（短链解析兜底用）。</summary>
    public static string? ExtractKugouSpecialId(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return KugouSpecialIdRegex().Match(text) is { Success: true } s ? s.Groups["id"].Value
            : KugouPlistRegex().Match(text) is { Success: true } p ? p.Groups["id"].Value
            : KugouSingleRegex().Match(text) is { Success: true } g ? g.Groups["id"].Value
            : null;
    }
}
