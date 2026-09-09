using System.Text.RegularExpressions;

namespace Erbai.Modules.SongRequest.Search;

/// <summary>
/// 歌手匹配辅助：用于三源候选做"原唱优先"排序。
/// 候选方歌手以多歌手分隔符（/、、,、&、feat. 等）连接；请求歌手可能带多个名字。
/// 判定为"请求歌手任一名字 与 候选歌手任一名字 归一化后互相包含"即视为命中原唱。
/// </summary>
public static partial class SongIdentity
{
    /// <summary>歌手分隔符（含 "feat." 变体）；空格不做分隔（歌名里的空格/姓名仍是一体）。</summary>
    private static readonly Regex Separators = SeparatorsRegex();

    /// <summary>
    /// 判断候选歌手是否命中请求歌手。请求歌手为空时返回 true（不重排、保持源序）。
    /// 归一化后双向包含判定，避免 "周杰伦" vs "周杰伦/" 或大小写差异误判。
    /// </summary>
    public static bool SingerMatches(string? requestedSinger, string? candidateSinger)
    {
        if (string.IsNullOrWhiteSpace(requestedSinger))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(candidateSinger))
        {
            return false;
        }

        var wanted = SplitSingers(requestedSinger);
        var actual = SplitSingers(candidateSinger);
        foreach (var w in wanted)
        {
            if (w.Length == 0)
            {
                continue;
            }

            foreach (var a in actual)
            {
                if (a.Length == 0)
                {
                    continue;
                }

                if (ContainsFolded(w, a))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>归一化歌手并按分隔符拆分为集合。</summary>
    private static List<string> SplitSingers(string value)
    {
        // 先把 "feat. / ft." 统一成 "/" 便于拆分
        var normalized = FeatRegex().Replace(value.Trim().ToLowerInvariant(), "/");
        var names = Separators.Split(normalized)
            .Select(Fold)
            .Where(n => n.Length > 0)
            .ToList();
        return names;
    }

    private static bool ContainsFolded(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        // 归一化后双向包含（任一方向命中即算，兼顾 "周杰伦/陈奕迅" vs "陈奕迅"）
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    private static string Fold(string name)
    {
        var value = name.Trim()
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);
        return WhitespaceRegex().Replace(value, " ");
    }

    [GeneratedRegex(@"[\s\u3000]+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[/\\、,，&;；|｜·]+", RegexOptions.Compiled)]
    private static partial Regex SeparatorsRegex();

    [GeneratedRegex(@"\b(?:feat(?:\.|uring)?|ft\.?)\b", RegexOptions.Compiled)]
    private static partial Regex FeatRegex();
}