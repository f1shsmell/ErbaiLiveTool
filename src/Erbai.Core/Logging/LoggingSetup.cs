using System.Text;
using Serilog;

namespace Erbai.Core.Logging;

/// <summary>Serilog 初始化：文件按天滚动保留 14 天（对齐旧 logs/ 行为）。</summary>
public static class LoggingSetup
{
    /// <summary>
    /// 创建 LogBus 并挂接 Serilog 文件 sink（<paramref name="logDirectory"/> 下
    /// erbai-YYYYMMDD.log，按天滚动，保留 14 天）。
    /// </summary>
    public static LogBus CreateLogBus(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "erbai-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                encoding: Encoding.UTF8)
            .CreateLogger();

        var logBus = new LogBus();
        logBus.AttachSerilog(logger);
        return logBus;
    }
}
