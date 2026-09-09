using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Logging;
using Erbai.Core.Events;

namespace Erbai.Core.Logging;

/// <summary>
/// 进程内日志总线：Serilog 写文件（按天滚动保留 14 天）+ 进程内广播。
/// 日志路径不得抛异常（任何日志调用失败都被吞掉，不影响业务）。
/// Dispose 释放 Serilog（文件句柄），供宿主关闭时调用。
/// </summary>
public sealed class LogBus : ILogBus, IDisposable
{
    private readonly EventBus _eventBus = new();
    private Serilog.ILogger? _serilog;

    /// <summary>挂接 Serilog（文件 sink 等）；可重复调用（替换 logger）。</summary>
    public void AttachSerilog(Serilog.ILogger logger) => _serilog = logger;

    public void Dispose()
    {
        try
        {
            (_serilog as IDisposable)?.Dispose();
        }
        catch
        {
        }

        _serilog = null;
    }

    public void Log(LogLevel level, string message, string? exception = null)
    {
        try
        {
            var serilog = _serilog;
            if (serilog is not null)
            {
                var serilogLevel = MapLevel(level);
                serilog.Write(serilogLevel, "{Message}", message);
                if (exception is not null)
                {
                    serilog.Write(serilogLevel, "{Exception}", exception);
                }
            }

            _eventBus.Publish(new LogEntry(DateTimeOffset.UtcNow, level, message, exception));
        }
        catch
        {
            // 日志路径绝不抛异常
        }
    }

    public Subscription<LogEntry> Subscribe(int capacity = 512) => _eventBus.Subscribe<LogEntry>(capacity);

    private static Serilog.Events.LogEventLevel MapLevel(LogLevel level) => level switch
    {
        LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
        LogLevel.Information => Serilog.Events.LogEventLevel.Information,
        LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
        LogLevel.Error => Serilog.Events.LogEventLevel.Error,
        LogLevel.Fatal => Serilog.Events.LogEventLevel.Fatal,
        _ => Serilog.Events.LogEventLevel.Information,
    };
}
