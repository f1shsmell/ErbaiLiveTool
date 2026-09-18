using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connector;

/// <summary>
/// NDJSON-stdio 服务端循环（决策 #15，协议 docs/04 §1.4）：
/// stdin 逐行读 JSON（垃圾非 JSON 行忽略不中断），按请求 player 字段路由到
/// 平台连接器（首次请求某平台即激活，同一时间只激活一个平台），stdout 写
/// 响应；subscribe 请求启动快照事件推送（有事件源的平台）。请求处理带
/// 宿主侧超时（probe=6s / search=15s / execute=20s，客户端读超时 25s 更大）。
/// </summary>
public sealed class ConnectorHost
{
    private readonly IReadOnlyDictionary<string, IConnectorBackend> _backends;
    private IConnectorBackend? _active;
    private long _sequence;

    public ConnectorHost(IReadOnlyDictionary<string, IConnectorBackend> backends)
    {
        _backends = backends;
    }

    public async Task RunAsync(StreamReader input, StreamWriter output, CancellationToken ct)
    {
        string? line;
        while (!ct.IsCancellationRequested && (line = await input.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ConnectorRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ConnectorRequest>(line, ConnectorProtocol.Json);
            }
            catch (JsonException)
            {
                continue; // 垃圾行忽略不中断
            }

            if (request is null)
            {
                continue;
            }

            if (request.Action == "shutdown")
            {
                await WriteResponseAsync(output, request.Id, true, null, null, ct);
                break;
            }

            await HandleRequestAsync(request, output, ct);
        }
    }

    private async Task HandleRequestAsync(ConnectorRequest request, StreamWriter output, CancellationToken ct)
    {
        try
        {
            // 路由：请求 player 指定平台；null = 当前激活
            var backend = ResolveBackend(request.Player);
            if (backend is null)
            {
                await WriteResponseAsync(output, request.Id, false, null, $"unknown player: {request.Player}", ct);
                return;
            }

            // 唯一激活语义：换平台时先 deactivate 旧的，再 activate 新的。
            // 审计 T4-3：此前 ActivateAsync 从未被调用（Folia 的 token 校验/建连等
            // 激活逻辑是死代码，未配置 token 时拿到 Connected=false 而非明确错误）。
            if (!ReferenceEquals(backend, _active))
            {
                if (_active is not null)
                {
                    await _active.DeactivateAsync();
                }

                _active = backend;
                await backend.ActivateAsync(ct);
            }

            switch (request.Action)
            {
                case "ping":
                {
                    await WriteResponseAsync(output, request.Id, true, BuildPingResult(backend), null, ct);
                    break;
                }
                case "probe":
                    await HandleWithTimeoutAsync(output, request, () => backend.ProbeAsync(ct), TimeSpan.FromSeconds(6), ct);
                    break;
                case "search":
                    await HandleWithTimeoutAsync(output, request,
                        () => backend.SearchAsync(request.Query ?? "", ct), TimeSpan.FromSeconds(15), ct);
                    break;
                case "execute":
                    var command = ConnectorProtocol.ParseCommand(request.Command);
                    if (command is null)
                    {
                        await WriteResponseAsync(output, request.Id, false, null,
                            $"unsupported command: {request.Command}", ct);
                        break;
                    }

                    var track = SnapshotJson.DeserializeTrack(request.Track is null ? null : ToElement(request.Track));
                    await HandleWithTimeoutAsync(output, request,
                        () => backend.ExecuteAsync(command.Value, track, ct), TimeSpan.FromSeconds(20), ct);
                    break;
                case "subscribe":
                    await StartSubscriptionAsync(backend, output, ct);
                    await WriteResponseAsync(output, request.Id, true, null, null, ct);
                    break;
                default:
                    await WriteResponseAsync(output, request.Id, false, null, $"unknown action: {request.Action}", ct);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(output, request.Id, false, null, ex.Message, ct);
        }
    }

    /// <summary>本连接器宿主版本（ping 元数据展示用）。</summary>
    private static readonly string ConnectorVersion =
        typeof(ConnectorHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// ping 元数据（docs/04 §1.4）。形状对齐上游 awoo 连接器实测响应：
    /// <c>connectorId</c> = 平台键（上游健康检查按它比对连接器身份）、
    /// <c>capabilities</c> = 命令级布尔对象、<c>features</c> = 能力名数组。
    /// 响应顶层的 <c>protocolCapabilities</c> 字符串数组（旧形状）保持不变，
    /// 供旧客户端继续读取——见 ConnectorProtocol.ParsePing 的兼容合并。
    /// </summary>
    private static JsonElement BuildPingResult(IConnectorBackend backend)
    {
        var features = backend.ProtocolCapabilities;
        var capabilities = ConnectorCapabilitySet.FromFeatures(features);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", 1);
            writer.WriteNumber("eventProtocolVersion", 1);
            writer.WriteString("connectorId", backend.Key);
            writer.WriteString("connectorVersion", ConnectorVersion);
            writer.WriteStartObject("capabilities");
            writer.WriteBoolean("search", capabilities.Search);
            writer.WriteBoolean("playSelected", capabilities.PlaySelected);
            writer.WriteBoolean("previous", capabilities.Previous);
            writer.WriteBoolean("pause", capabilities.Pause);
            writer.WriteBoolean("resume", capabilities.Resume);
            writer.WriteBoolean("toggle", capabilities.Toggle);
            writer.WriteBoolean("next", capabilities.Next);
            writer.WriteBoolean("insertNext", capabilities.InsertNext);
            if (!string.IsNullOrEmpty(capabilities.InsertNextLevel))
            {
                writer.WriteString("insertNextLevel", capabilities.InsertNextLevel);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("features");
            foreach (var feature in features)
            {
                writer.WriteStringValue(feature);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private IConnectorBackend? ResolveBackend(string? player) =>
        string.IsNullOrEmpty(player)
            ? _active
            : _backends.GetValueOrDefault(player);

    private async Task HandleWithTimeoutAsync(
        StreamWriter output,
        ConnectorRequest request,
        Func<Task<JsonElement>> operation,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            var result = await operation().WaitAsync(timeout, ct);
            await WriteResponseAsync(output, request.Id, true, result, null, ct);
        }
        catch (TimeoutException)
        {
            await WriteResponseAsync(output, request.Id, false, null, $"timeout after {timeout.TotalSeconds}s", ct);
        }
    }

    private Task StartSubscriptionAsync(IConnectorBackend backend, StreamWriter output, CancellationToken ct)
    {
        var events = backend.WatchSnapshotsAsync(ct);
        if (events is null)
        {
            return Task.CompletedTask; // 无事件源：subscribe ok 但不推送，客户端回退轮询
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var snapshot in events)
                {
                    var envelope = new ConnectorEvent
                    {
                        Player = backend.Key,
                        Sequence = Interlocked.Increment(ref _sequence),
                        Snapshot = snapshot,
                    };
                    var line = JsonSerializer.Serialize(envelope, ConnectorProtocol.Json);
                    await WriteStdoutLineAsync(output, line, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // 推送任务异常不影响请求循环
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task WriteResponseAsync(
        StreamWriter output,
        string id,
        bool ok,
        JsonElement? result,
        string? error,
        CancellationToken ct)
    {
        var response = new ConnectorResponse
        {
            Id = id,
            Ok = ok,
            Result = result,
            Error = error,
            ProtocolCapabilities = _active?.ProtocolCapabilities ?? [],
        };
        var line = JsonSerializer.Serialize(response, ConnectorProtocol.Json);
        await WriteStdoutLineAsync(output, line, ct);
    }

    /// <summary>stdout 写锁：响应与订阅推送双写串行化，避免行交错（NDJSON 逐行协议）。</summary>
    private readonly SemaphoreSlim _stdoutLock = new(1, 1);

    private async Task WriteStdoutLineAsync(StreamWriter output, string line, CancellationToken ct)
    {
        await _stdoutLock.WaitAsync(ct);
        try
        {
            await output.WriteLineAsync(line);
            await output.FlushAsync(ct);
        }
        finally
        {
            _stdoutLock.Release();
        }
    }

    private static JsonElement? ToElement(PlayerTrack track) => SnapshotJson.SerializeTrack(track);
}
