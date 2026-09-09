using Erbai.Contracts.Configuration;
using Erbai.Contracts.Players;
using Erbai.Contracts.Plugins;

namespace Erbai.TestPlugins.Player;

/// <summary>播放器类假插件（PluginLoader 测试用；Capabilities 为空，WatchSnapshots 返回 null）。</summary>
public sealed class FakePlayerModule : IMusicPlayerPlugin
{
    public string Key => "fake-player";

    public string DisplayName => "Fake 播放器";

    public PlayerCapabilities Capabilities => PlayerCapabilities.None;

    public int ActivateCount { get; private set; }

    public Task<PlayerSnapshot> ActivateAsync(AppConfig config, CancellationToken ct)
    {
        ActivateCount++;
        return Task.FromResult(new PlayerSnapshot
        {
            Connected = true,
            NextObservation = NextObservation.Unknown,
        });
    }

    public Task DeactivateAsync() => Task.CompletedTask;

    public Task<PlayerSnapshot> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(new PlayerSnapshot
        {
            Connected = true,
            NextObservation = NextObservation.Unknown,
        });

    public Task<IReadOnlyList<PlayerTrack>> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PlayerTrack>>([]);

    public Task<PlayerOperationResult> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct) =>
        Task.FromResult(PlayerOperationResult.Unsupported());

    public IAsyncEnumerable<PlayerSnapshot>? WatchSnapshotsAsync(CancellationToken ct) => null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}