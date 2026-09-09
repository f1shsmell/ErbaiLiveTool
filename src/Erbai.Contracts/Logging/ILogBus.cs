using Erbai.Contracts.Abstractions;

namespace Erbai.Contracts.Logging;

/// <summary>
/// 进程内日志总线：Serilog 写文件（按天滚动保留 14 天）+ 进程内广播给
/// 弹幕日志页等订阅者。异常不得逃逸任何日志路径。
/// </summary>
public interface ILogBus
{
    void Log(LogLevel level, string message, string? exception = null);

    void Debug(string message) => Log(LogLevel.Debug, message);
    void Information(string message) => Log(LogLevel.Information, message);
    void Warning(string message) => Log(LogLevel.Warning, message);
    void Error(string message, string? exception = null) => Log(LogLevel.Error, message, exception);

    /// <summary>订阅日志流（有界通道，慢消费者丢旧保新）。</summary>
    Subscription<LogEntry> Subscribe(int capacity = 512);
}
