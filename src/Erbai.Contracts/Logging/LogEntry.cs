namespace Erbai.Contracts.Logging;

/// <summary>进程内日志级别（与弹幕日志页/文件滚动共用）。</summary>
public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

/// <summary>一条日志记录（LogBus 广播载荷）。</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message, string? Exception);
