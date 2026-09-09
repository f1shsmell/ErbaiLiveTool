using System.Text.RegularExpressions;

namespace Erbai.Contracts.Players;

/// <summary>
/// 曲目同一性判定（对齐 AwooMusicBot queue-head-policy）。next 对账守卫与开播识别都依赖它。
/// </summary>
public static class TrackIdentity
{
    /// <summary>NFKC 归一化（全角→半角/兼容字符）后去空白并小写，用于曲目比对。</summary>
    public static string NormalizeText(string? value) =>
        string.Concat((value ?? "").Normalize(System.Text.NormalizationForm.FormKC)
            .ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// 版本/备注注释正则：出现在括号里时说明标题带版本标记
    /// （Inst./纯音乐/伴奏/Live 等）。同曲不同版本名（搜索候选
    /// "September (纯音乐)" vs 播放器实报 "September (Inst.)"）是真实的点歌
    /// 失败来源，仅当双方都带这类注释时才启用主标题宽容匹配，避免把
    /// "September" 与 "September Rain" 这类前缀同名误判。
    /// </summary>
    private static readonly Regex VersionAnnotationRe = new(
        @"[（(][^（）()]*(?:inst|instrumental|纯音乐|伴奏|live|remix|cover|acoustic|piano|钢琴|karaoke|卡拉ok|版|现场|演唱会)[^（）()]*[)）]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>标题是否带版本/备注注释，如 (Inst.)、（纯音乐）、(Live) 等。</summary>
    public static bool HasVersionAnnotation(string? value) =>
        !string.IsNullOrEmpty(value) && VersionAnnotationRe.IsMatch(value);

    /// <summary>
    /// 曲目标题主部：版本/备注注释（(Inst.)/（纯音乐））之前的正文部分。
    /// 例："September (Inst.) (September (Inst.)|Sparky Deathcap)" → "september"。
    /// 标题以括号开头时退回"去括号内容后整体"归一化。
    /// </summary>
    public static string PrimaryTitle(string? value)
    {
        var text = (value ?? "").Trim();
        // .NET Regex.Split(input, count) 的 count 是最大元素数：2 个元素 = 拆分 1 次
        var head = new Regex(@"[（(]").Split(text, 2)[0].Trim();
        if (head.Length > 0)
        {
            return NormalizeText(head);
        }

        text = Regex.Replace(text, @"[（(][^（）()]*[)）]", " ");
        text = Regex.Replace(text, @"[|｜].*$", " ");
        return NormalizeText(text);
    }

    /// <summary>
    /// 两首曲目是否同一首歌：
    /// - 双方都有稳定 ID 且不同 → 权威不同；相同 → 同一首；
    /// - ID 缺失/单方缺失 → 标题子串或归一化相等兜底，再退回歌手相等；
    /// - 双方标题都带版本注释（(Inst.)/（纯音乐）等）时，主标题互相包含视为同一首。
    /// </summary>
    public static bool TracksRepresentSame(PlayerTrack? a, PlayerTrack? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var aId = (a.Id ?? "").Trim();
        var bId = (b.Id ?? "").Trim();
        if (aId.Length > 0 && bId.Length > 0)
        {
            return aId == bId;
        }

        var aTitle = NormalizeText(a.Title);
        var bTitle = NormalizeText(b.Title);
        if (aTitle.Length > 0 && bTitle.Length > 0
            && (aTitle.Contains(bTitle, StringComparison.Ordinal) || bTitle.Contains(aTitle, StringComparison.Ordinal)))
        {
            return true;
        }

        if (HasVersionAnnotation(a.Title) && HasVersionAnnotation(b.Title))
        {
            var aPrimary = PrimaryTitle(a.Title);
            var bPrimary = PrimaryTitle(b.Title);
            if (aPrimary.Length >= 2 && aPrimary == bPrimary)
            {
                return true;
            }
        }

        if (aTitle.Length == 0 || bTitle.Length == 0)
        {
            return true; // 单方无标题：无法判定不同（宽松，避免误重插）
        }

        var aArtist = NormalizeText(a.Artist);
        var bArtist = NormalizeText(b.Artist);
        return aArtist.Length > 0 && aArtist == bArtist;
    }
}
