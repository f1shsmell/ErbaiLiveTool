using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connectors.Management;

/// <summary>启动健康检查结果。</summary>
public sealed record ConnectorHealthResult(bool IsHealthy, string Message)
{
    internal static ConnectorHealthResult Healthy { get; } = new(true, string.Empty);

    internal static ConnectorHealthResult Unhealthy(string message) => new(false, message);
}

/// <summary>
/// 连接器可执行文件的启动健康检查：拉起进程 → 发 <c>ping</c> → 校验回包 → 发 <c>shutdown</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这一步不能省。签名校验只证明"包是发布方签过的"，不证明"它在这台机器上能跑起来"——
/// x86 连接器在只有 x64 运行时的机器上会以 <c>hostfxr.dll not found</c> 直接退出，
/// 这正是 F4 实测到的现象。只有真正跑一次 ping 才能把这类问题挡在"写入 active.json"之前。
/// </para>
/// <para>
/// 校验项：<c>ok == true</c>、<c>result.protocolVersion == 1</c>、
/// <c>result.connectorId == 平台键</c>，以及在给了期望版本时 <c>result.connectorVersion</c> 必须相等。
/// 最后一项能挡住"包装的是旧版本"或"平台键张冠李戴"。
/// </para>
/// </remarks>
public sealed class ConnectorHealthChecker : IConnectorHealthChecker
{
    /// <summary>等待 ping 回包的超时。</summary>
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(6);

    /// <summary>发出 shutdown 后等待进程自行退出的宽限期，超时则强杀。</summary>
    public static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromMilliseconds(1500);

    /// <summary>stdout 上最多容忍多少行非协议内容，避免被刷屏拖住。</summary>
    private const int MaxIgnoredLines = 200;

    private const int StderrTailLength = 300;

    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _shutdownGrace;

    public ConnectorHealthChecker(
        TimeSpan? startupTimeout = null,
        TimeSpan? shutdownGrace = null)
    {
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        _shutdownGrace = shutdownGrace ?? DefaultShutdownGrace;
    }

    /// <summary>对指定可执行文件执行一次健康检查。</summary>
    public async Task<ConnectorHealthResult> CheckAsync(
        string executablePath,
        string playerKey,
        string? expectedVersion = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentException.ThrowIfNullOrEmpty(playerKey);

        if (!File.Exists(executablePath))
        {
            return ConnectorHealthResult.Unhealthy($"可执行文件不存在：{executablePath}");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using Process process = new() { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return ConnectorHealthResult.Unhealthy($"{playerKey} 连接器进程启动失败。");
            }
        }
        catch (Exception ex)
        {
            return ConnectorHealthResult.Unhealthy($"{playerKey} 连接器进程启动失败：{ex.Message}");
        }

        StringBuilder stderr = new();
        Task drainStderr = DrainStderrAsync(process, stderr, cancellationToken);

        string requestId = $"health-{Environment.ProcessId}-{Guid.NewGuid():N}";

        try
        {
            await process.StandardInput
                .WriteAsync($"{{\"id\":\"{requestId}\",\"action\":\"ping\"}}\n")
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_startupTimeout);

            string? failure = null;
            int ignoredLines = 0;

            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    failure = $"{playerKey} 连接器启动健康检查超时";
                    break;
                }

                if (line is null)
                {
                    // stdout 关闭 = 进程已退出，且从未给出匹配的回包。
                    failure = $"{playerKey} 连接器在健康检查完成前退出";
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string? envelopeFailure = EvaluateEnvelope(line, requestId, playerKey, expectedVersion);
                if (envelopeFailure is null)
                {
                    // 命中匹配回包 → 校验通过。
                    await RequestShutdownAsync(process, requestId, cancellationToken).ConfigureAwait(false);
                    return ConnectorHealthResult.Healthy;
                }

                if (envelopeFailure.Length > 0)
                {
                    failure = envelopeFailure;
                    break;
                }

                // 空字符串表示"这一行不是我们的回包"（连接器往 stdout 打了别的东西）。
                if (++ignoredLines > MaxIgnoredLines)
                {
                    failure = $"{playerKey} 连接器在 {MaxIgnoredLines} 行内未给出 ping 回包";
                    break;
                }
            }

            string detail = stderr.Length > 0 ? $"：{stderr}" : string.Empty;
            return ConnectorHealthResult.Unhealthy(failure + detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ConnectorHealthResult.Unhealthy($"{playerKey} 连接器健康检查异常：{ex.Message}");
        }
        finally
        {
            KillIfRunning(process);
            await SafeAwaitAsync(drainStderr).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 判断一行 stdout 是否为本次 ping 的回包。
    /// 返回 <see langword="null"/> 表示校验通过；返回空串表示"不是我们的回包，继续读"；
    /// 返回非空串表示明确的失败原因。
    /// </summary>
    private static string? EvaluateEnvelope(
        string line,
        string requestId,
        string playerKey,
        string? expectedVersion)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("id", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.String
                || !string.Equals(idElement.GetString(), requestId, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (!root.TryGetProperty("ok", out JsonElement okElement)
                || okElement.ValueKind != JsonValueKind.True)
            {
                return $"{playerKey} 连接器 ping 返回失败。";
            }

            if (!root.TryGetProperty("result", out JsonElement result)
                || result.ValueKind != JsonValueKind.Object)
            {
                return $"{playerKey} 连接器 ping 回包缺少 result。";
            }

            // 走 ConnectorProtocol.ReadInt32：上游会显式发 "protocolVersion": null，
            // 直接 TryGetInt32 会在 null 上抛 InvalidOperationException（见该方法注释）。
            int protocolVersion = ConnectorProtocol.ReadInt32(result, "protocolVersion") ?? 0;

            if (protocolVersion != ConnectorProtocol.ProtocolVersion)
            {
                return $"{playerKey} 连接器协议版本不匹配：{protocolVersion}，"
                    + $"期望 {ConnectorProtocol.ProtocolVersion}。";
            }

            string? connectorId = result.TryGetProperty("connectorId", out JsonElement idProp)
                && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;

            if (!string.Equals(connectorId, playerKey, StringComparison.Ordinal))
            {
                return $"{playerKey} 连接器自报标识不匹配：{connectorId ?? "(缺失)"}。";
            }

            if (expectedVersion is not null)
            {
                string? connectorVersion = result.TryGetProperty("connectorVersion", out JsonElement versionProp)
                    && versionProp.ValueKind == JsonValueKind.String
                        ? versionProp.GetString()
                        : null;

                if (!string.Equals(connectorVersion, expectedVersion, StringComparison.Ordinal))
                {
                    return $"{playerKey} 连接器自报版本不匹配：{connectorVersion ?? "(缺失)"}，"
                        + $"期望 {expectedVersion}。";
                }
            }

            return null;
        }
    }

    private async Task RequestShutdownAsync(
        Process process,
        string requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput
                .WriteAsync($"{{\"id\":\"{requestId}-shutdown\",\"action\":\"shutdown\"}}\n")
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // 进程可能已经自行退出；这不影响健康检查结论。
            return;
        }

        using CancellationTokenSource grace =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(_shutdownGrace);

        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillIfRunning(process);
        }
    }

    private static async Task DrainStderrAsync(
        Process process,
        StringBuilder sink,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                string? line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                sink.Append(line).Append(' ');

                // 只保留尾部若干字符，避免异常连接器刷爆内存。
                if (sink.Length > StderrTailLength * 4)
                {
                    sink.Remove(0, sink.Length - StderrTailLength);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            // 排空 stderr 是尽力而为。
        }
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // 已经退出或句柄已失效。
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 排空任务的异常不影响健康检查结论。
        }
    }
}
