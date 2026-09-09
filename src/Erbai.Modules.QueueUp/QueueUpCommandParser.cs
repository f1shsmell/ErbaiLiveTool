namespace Erbai.Modules.QueueUp;

/// <summary>排队弹幕命令（docs/01 §3.7 最小版入口面）。</summary>
public enum QueueUpCommandKind
{
    /// <summary>「排队 [内容]」——入队；已在队中则更新自己的内容（用户已确认语义）。</summary>
    Enqueue,

    /// <summary>「取消排队」——把自己的条目移出队列。</summary>
    Cancel,

    /// <summary>「完成」——队首出队（仅 admin/anchor 可执行）。</summary>
    Complete,
}

/// <summary>解析后的排队命令。</summary>
public sealed record QueueUpCommand(QueueUpCommandKind Kind, string Content = "");

/// <summary>
/// 弹幕排队命令解析：`排队 [内容]` / `取消排队` / `完成`。
/// 前缀规则：`排队` 后必须跟空白/全角空白/冒号或直接结尾（避免误吃「排队点歌」类词）；
/// 空内容允许（占位排队）。非排队命令返回 null（与点歌解析器互不冲突）。
/// </summary>
public static class QueueUpCommandParser
{
    public static QueueUpCommand? Parse(string text)
    {
        var normalized = (text ?? "").Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized == "取消排队")
        {
            return new QueueUpCommand(QueueUpCommandKind.Cancel);
        }

        if (normalized == "完成")
        {
            return new QueueUpCommand(QueueUpCommandKind.Complete);
        }

        if (normalized == "排队")
        {
            return new QueueUpCommand(QueueUpCommandKind.Enqueue, "");
        }

        if (normalized.StartsWith("排队", StringComparison.Ordinal) &&
            normalized.Length > 2 && IsSeparator(normalized[2]))
        {
            // 跳过前导分隔符（空白/冒号），取内容
            var content = normalized[2..].Trim();
            while (content.Length > 0 && IsSeparator(content[0]))
            {
                content = content[1..].Trim();
            }

            return new QueueUpCommand(QueueUpCommandKind.Enqueue, content);
        }

        return null;
    }

    private static bool IsSeparator(char c) =>
        c is ' ' or '\t' or '\u3000' or ':' or '：';
}
