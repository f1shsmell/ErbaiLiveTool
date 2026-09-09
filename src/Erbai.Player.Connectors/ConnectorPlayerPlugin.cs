using Erbai.Contracts.Configuration;
using Erbai.Contracts.Players;
using Erbai.Contracts.Plugins;

namespace Erbai.Player.Connectors;

/// <summary>
/// 连接器播放器插件（IMusicPlayerPlugin 实现，决策 #15）：把 ConnectorClient
/// 的 NDJSON 协议面适配为插件契约面。能力从宿主 protocolCapabilities 推导
/// （加性能力协商：snapshot-events-v1 → SnapshotEvents；原生搜索由后端决定）。
/// </summary>
public sealed class ConnectorPlayerPlugin : IMusicPlayerPlugin
{
    private readonly ConnectorClient _client;
    private readonly string _displayName;
    private PlayerCapabilities _capabilities = PlayerCapabilities.None;
    private bool _activated;

    public ConnectorPlayerPlugin(ConnectorClient client, string displayName)
    {
        _client = client;
        _displayName = displayName;
    }

    public string Key => _client.PlayerKey;

    public string DisplayName => _displayName;

    public PlayerCapabilities Capabilities => _capabilities;

    public async Task<PlayerSnapshot> ActivateAsync(AppConfig config, CancellationToken ct)
    {
        var snapshot = await _client.ActivateAsync(ct);
        _activated = true;
        // 加性能力协商：宿主 protocolCapabilities → PlayerCapabilities
        //（snapshot-events-v1 → SnapshotEvents；queue-programmable-v1 →
        // QueueProgrammable——不声明则插播对账守卫（GuardNextSong）整体旁路）
        var caps = PlayerCapabilities.None;
        if (_client.ProtocolCapabilities.Contains("snapshot-events-v1", StringComparer.Ordinal))
        {
            caps |= PlayerCapabilities.SnapshotEvents;
        }

        if (_client.ProtocolCapabilities.Contains("queue-programmable-v1", StringComparer.Ordinal))
        {
            caps |= PlayerCapabilities.QueueProgrammable;
        }

        _capabilities = caps;
        return snapshot;
    }

    public async Task DeactivateAsync()
    {
        if (!_activated)
        {
            return;
        }

        _activated = false;
        await _client.DeactivateAsync();
    }

    public Task<PlayerSnapshot> ProbeAsync(CancellationToken ct) => _client.ProbeAsync(ct);

    public Task<IReadOnlyList<PlayerTrack>> SearchAsync(string query, CancellationToken ct) =>
        _client.SearchAsync(query, ct);

    public Task<PlayerOperationResult> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct) =>
        _client.ExecuteAsync(command, track, ct);

    public IAsyncEnumerable<PlayerSnapshot>? WatchSnapshotsAsync(CancellationToken ct) =>
        _capabilities.HasFlag(PlayerCapabilities.SnapshotEvents)
            ? _client.WatchSnapshotsAsync(ct)
            : null;

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
