using System.Text.RegularExpressions;

namespace Erbai.Modules.SongRequest.Services;

/// <summary>
/// 弹幕点歌命令解析：`点歌 歌名` / `点歌 歌名 - 歌手` / 切歌命令 /
/// 管理命令（设置管理员@XX 等由调用方按正则先行匹配）。
/// </summary>
public sealed record SongRequestCommand
{
    /// <summary>"request" | "skip"。</summary>
    public required string Action { get; init; }

    public string SongName { get; init; } = "";

    public string Singer { get; init; } = "";
}

public static partial class SongRequestParser
{
    private static readonly IReadOnlySet<string> SkipCommands = new HashSet<string>
    {
        "下一首", "切歌", "跳过",
    };

    /// <summary>点歌命令（可带可选 # 前缀）："点歌:查询" / "点歌 查询"。</summary>
    [GeneratedRegex(@"^(?:#\s*)?点歌(?:\s*[:：]\s*|\s+)(?<query>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestRegex();

    /// <summary>歌名/歌手分隔符（2026-09 扩展）："-" / "_" / "—"（连续多个也匹配）/
    /// "－" / "ˉ"（macron）/ "＿"（全角下划线）/ "|" / "｜"；两侧空白可选。</summary>
    [GeneratedRegex(@"\s*[-—－_ˉ＿|｜]+\s*")]
    private static partial Regex SingerSeparatorRegex();

    /// <summary>管理命令（弹幕层在点歌解析之前匹配）。</summary>
    [GeneratedRegex(@"^设置管理员\s*[@＠]?\s*(.+)$")]
    public static partial Regex AdminSetRegex();

    [GeneratedRegex(@"^取消管理员\s*[@＠]?\s*(.+)$")]
    public static partial Regex AdminClearRegex();

    [GeneratedRegex(@"^拉黑\s*[@＠]?\s*(.+)$")]
    public static partial Regex BanSetRegex();

    [GeneratedRegex(@"^取消拉黑\s*[@＠]?\s*(.+)$")]
    public static partial Regex BanClearRegex();

    /// <summary>歌曲黑名单命令（在用户拉黑命令之前匹配，避免「拉黑歌曲」被当作「拉黑 @歌曲」）。</summary>
    [GeneratedRegex(@"^拉黑歌曲\s*(.+)$")]
    public static partial Regex BanSongSetRegex();

    [GeneratedRegex(@"^取消拉黑歌曲\s*(.+)$")]
    public static partial Regex BanSongClearRegex();

    public static string Normalize(string text) =>
        string.Join(" ", (text ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static SongRequestCommand? Parse(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (SkipCommands.Contains(normalized))
        {
            return new SongRequestCommand { Action = "skip" };
        }

        var match = RequestRegex().Match(normalized);
        if (!match.Success)
        {
            return null;
        }

        var query = match.Groups["query"].Value.Trim();
        if (query.Length == 0)
        {
            return null;
        }

        var separated = SingerSeparatorRegex().Split(query, 2);
        if (separated.Length == 2 && separated[0].Trim().Length > 0)
        {
            return new SongRequestCommand
            {
                Action = "request",
                SongName = separated[0].Trim(),
                Singer = separated[1].Trim(),
            };
        }

        // 无分隔符：整段查询都是歌名（R7），带空格标题原样搜索；
        // 旧式"首词歌名、余下歌手"回退在搜索编排层（里程碑 5）。
        return new SongRequestCommand { Action = "request", SongName = query, Singer = "" };
    }
}
