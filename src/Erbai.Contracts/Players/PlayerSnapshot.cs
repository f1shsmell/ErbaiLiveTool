namespace Erbai.Contracts.Players;

/// <summary>播放器 next 的可观察性（对齐 AwooMusicBot queue-head-policy）。</summary>
public enum NextObservation
{
    /// <summary>旧式/未知形态（无法枚举队首）。</summary>
    Legacy,

    /// <summary>未知（连接器未上报 nextSource）。</summary>
    Unknown,

    /// <summary>可观测到具体队首曲目。</summary>
    Track,

    /// <summary>明确为空队列。</summary>
    Empty,
}

/// <summary>播放器状态快照。</summary>
public sealed record PlayerSnapshot
{
    public required bool Connected { get; init; }

    public string? Version { get; init; }

    public PlayerTrack? Current { get; init; }

    public PlayerTrack? Next { get; init; }

    public required NextObservation NextObservation { get; init; }

    /// <summary>
    /// 播放器原始状态（连接器透传，播放状态机消费）：
    /// "playing" / "stoped" / "stopped" / "error" / "paused" / "waiting" / "idle"；
    /// 空 = 不可用（状态机按快照 Current 推断）。部分连接器只提供人类可读
    /// 诊断文本（非词表），消费方经 <see cref="PlayerStatusVocabulary.Normalize"/>
    /// 归一化后同样按"不可用"回退推断。
    /// </summary>
    public string? RawStatus { get; init; }

    /// <summary>当前播放进度（秒）；部分状态源提供，缺失为 null。</summary>
    public double? ProgressSeconds { get; init; }
}

/// <summary>
/// RawStatus 词表归一化：只有词表内的状态是机器可读的；空或非词表文本
/// （连接器的诊断性中文描述）返回 null，调用方按契约回退 Current 推断。
/// </summary>
public static class PlayerStatusVocabulary
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "playing", "stoped", "stopped", "error", "paused", "waiting", "idle",
    };

    /// <summary>归一化为小写词表词；非词表/空返回 null。</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().ToLowerInvariant();
        return Known.Contains(value) ? value : null;
    }
}
