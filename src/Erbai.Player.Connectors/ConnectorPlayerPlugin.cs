using Erbai.Contracts.Configuration;
using Erbai.Contracts.Players;
using Erbai.Contracts.Plugins;

namespace Erbai.Player.Connectors;

/// <summary>
/// 连接器播放器插件（IMusicPlayerPlugin 实现，决策 #15）：把 ConnectorClient
/// 的 NDJSON 协议面适配为插件契约面。能力从 ping 的能力协商面推导
/// （上游规范形状 result.features / result.capabilities，兼容本仓库旧宿主的
/// 顶层 protocolCapabilities 字符串数组——见 ConnectorProtocol.ParsePing）。
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
        _capabilities = DeriveCapabilities(_client.Ping);
        return snapshot;
    }

    /// <summary>
    /// 加性能力协商：ping 能力面 → PlayerCapabilities。
    /// 缺失 <see cref="PlayerCapabilities.QueueProgrammable"/> 会让插播对账守卫
    /// （GuardNextSong）整体旁路（PlaybackStateMachine 判据），故上游连接器
    /// 以 <c>capabilities.insertNext</c> 声明队列可编程、以
    /// <c>features</c> 里的 <c>snapshot-events-v1</c> 声明快照事件流。
    /// </summary>
    internal static PlayerCapabilities DeriveCapabilities(ConnectorPingInfo ping)
    {
        var caps = PlayerCapabilities.None;

        if (ping.HasFeature(ConnectorProtocol.FeatureSnapshotEvents))
        {
            caps |= PlayerCapabilities.SnapshotEvents;
        }

        if (ping.HasFeature(ConnectorProtocol.FeatureQueueProgrammable) || ping.Capabilities.InsertNext)
        {
            caps |= PlayerCapabilities.QueueProgrammable;
        }

        if (ping.Capabilities.Search)
        {
            caps |= PlayerCapabilities.Search;
        }

        if (ping.Capabilities.Pause && ping.Capabilities.Resume)
        {
            caps |= PlayerCapabilities.PauseResume;
        }

        return caps;
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
