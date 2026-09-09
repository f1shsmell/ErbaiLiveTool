using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Logging;
using Erbai.Core.Logging;

namespace Erbai.Core.Tests;

/// <summary>日志总线：进程内广播 + 日志路径不抛异常。</summary>
public class LogBusTests
{
    [Fact]
    public async Task Log_BroadcastsToSubscribers()
    {
        ILogBus logBus = new LogBus();
        using var subscription = logBus.Subscribe();

        logBus.Information("hello 42");

        var entry = await subscription.Reader.ReadAsync();
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("hello", entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Log_WithException_CarriesStackTraceText()
    {
        ILogBus logBus = new LogBus();
        using var subscription = logBus.Subscribe();

        logBus.Error("failed", "System.Exception: boom");

        var entry = await subscription.Reader.ReadAsync();
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("System.Exception: boom", entry.Exception);
    }

    [Fact]
    public void Log_WithoutSerilogAttached_DoesNotThrow()
    {
        ILogBus logBus = new LogBus();
        logBus.Error("x");
        logBus.Warning("y");
    }

    [Fact]
    public async Task LogBus_WithFileSink_WritesRollingFile()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}", "logs");
        Directory.CreateDirectory(dir);
        try
        {
            var logBus = LoggingSetup.CreateLogBus(dir);
            logBus.Log(Erbai.Contracts.Logging.LogLevel.Information, "滚到磁盘");

            // Serilog 异步写盘（shared 句柄），轮询等待
            var file = Path.Combine(dir, $"erbai-{DateTime.Now:yyyyMMdd}.log");
            string? text = null;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(file))
                {
                    try
                    {
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(stream);
                        text = await reader.ReadToEndAsync();
                        if (text.Contains("滚到磁盘"))
                        {
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        // Serilog 正在滚动文件，稍后重试
                    }
                }

                await Task.Delay(100);
            }

            Assert.NotNull(text);
            Assert.Contains("滚到磁盘", text);

            logBus.Dispose(); // 释放 Serilog 文件句柄，才能删除目录
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
