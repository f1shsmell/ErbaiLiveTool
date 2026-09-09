using System.Text.RegularExpressions;

namespace Erbai.Live.Douyin.Hosting;

/// <summary>Grabber stdout 日志行分级（docs/04 §3.2，纯函数；测试直测）。</summary>
public enum GrabberLogLevel
{
    System,
    Warning,
    Error,
}

/// <summary>
/// Grabber stdout 行分级解析器（docs/04 §3.2）：
/// - 中文 token「错误/异常/失败/无法/exception」→ error；
/// - 英文 <c>\berrors?\b</c> 正则（大小写无关），但先剔除「N errors」计数短语（统计文本误报）；
/// - 「警告」/ <c>\bwarn(ing)?\b</c> → warning；其余 system。
/// 纯函数、无 I/O，宿主与测试共用。
/// </summary>
public static class GrabberLogClassifier
{
    private static readonly Regex ErrorWordRe = new(@"\berrors?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WarnWordRe = new(@"\bwarn(?:ing)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CountErrorRe = new(@"\d+\s+errors?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ErrorCnTokens = ["错误", "异常", "失败", "无法", "exception"];
    private static readonly string[] WarnCnTokens = ["警告"];

    /// <summary>对单行 stdout 文本分级（空行返回 System，调用方自行跳过）。</summary>
    public static GrabberLogLevel Classify(string line)
    {
        var lower = line.ToLowerInvariant();
        if (ErrorCnTokens.Any(t => lower.Contains(t, StringComparison.Ordinal)))
        {
            return GrabberLogLevel.Error;
        }

        var scrubbed = CountErrorRe.Replace(lower, " ");
        if (ErrorWordRe.IsMatch(scrubbed))
        {
            return GrabberLogLevel.Error;
        }

        if (WarnCnTokens.Any(t => lower.Contains(t, StringComparison.Ordinal)) || WarnWordRe.IsMatch(lower))
        {
            return GrabberLogLevel.Warning;
        }

        return GrabberLogLevel.System;
    }
}
