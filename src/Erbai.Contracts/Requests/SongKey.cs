using System.Text.RegularExpressions;

namespace Erbai.Contracts.Requests;

/// <summary>
/// 规范化歌键：
/// casefold + 空白/标点折叠；用于去重与歌名匹配的同一性判定。
/// </summary>
public static partial class SongKey
{
    public static string Normalize(string songName, string singer = "")
    {
        var value = $"{songName} {singer}".ToLowerInvariant();
        value = WhitespaceRegex().Replace(value, " ");
        value = PunctuationRegex().Replace(value, " ");
        return InnerWhitespaceRegex().Replace(value.Trim(), " ").Trim();
    }

    [GeneratedRegex(@"[\s\u3000]+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\-—－|｜:：,，。.!！?？]+")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex InnerWhitespaceRegex();
}
