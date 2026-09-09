using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connector;

/// <summary>
/// dummy 连接器（测试/对照用）：内存状态机 + 可注入脚本。
/// 无事件源（客户端回退轮询）；execute 按命令记录并回放预设响应。
/// </summary>
public sealed class DummyConnector : IConnectorBackend
{
    private readonly List<string> _executed = [];
    private PlayerSnapshot _snapshot = new()
    {
        Connected = true,
        Version = "dummy-1",
        NextObservation = NextObservation.Unknown,
    };

    public string Key => "dummy";

    public string DisplayName => "Dummy 连接器";

    public string[] ProtocolCapabilities => ["snapshot-events-v1"];

    public IReadOnlyList<string> Executed => _executed;

    /// <summary>测试注入：下一次 Probe 返回的快照（null = 保持当前）。</summary>
    public PlayerSnapshot? NextProbe { get; set; }

    /// <summary>测试注入：execute 命令统一回放结果（null = 默认 applied）。</summary>
    public PlayerOperationResult? ExecuteResult { get; set; }

    public Task<JsonElement> ActivateAsync(CancellationToken ct) => Task.FromResult(SnapshotJson.Serialize(_snapshot));

    public Task DeactivateAsync() => Task.CompletedTask;

    public Task<JsonElement> ProbeAsync(CancellationToken ct)
    {
        if (NextProbe is not null)
        {
            _snapshot = NextProbe;
            NextProbe = null;
        }

        return Task.FromResult(SnapshotJson.Serialize(_snapshot));
    }

    public Task<JsonElement> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult(JsonDocument.Parse("[]").RootElement.Clone());

    public Task<JsonElement> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct)
    {
        _executed.Add($"{command}|{track?.Title ?? ""}");
        var result = ExecuteResult ?? PlayerOperationResult.Success(PlayerOutcome.Applied);
        return Task.FromResult(SerializeResult(result));
    }

    public IAsyncEnumerable<JsonElement>? WatchSnapshotsAsync(CancellationToken ct) => null;

    internal static JsonElement SerializeResult(PlayerOperationResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("outcome", result.Outcome.ToString().ToLowerInvariant());
            writer.WriteString("message", result.Message);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
