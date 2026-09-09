using Erbai.Contracts.Configuration;
using Erbai.Contracts.Players;
using Erbai.Contracts.Plugins;
using Erbai.Modules.SongRequest.Search;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>里程碑 3 测试公共设施：FakePlayer + 搜索处理器。</summary>
internal sealed class FakePlayer : IMusicPlayerPlugin
{
    public string Key { get; set; } = "fake";

    public string DisplayName => "Fake 播放器";

    public PlayerCapabilities Capabilities { get; set; } = PlayerCapabilities.None;

    public PlayerOutcome Outcome { get; set; } = PlayerOutcome.Applied;

    /// <summary>按曲目定制 PlaySelected 结果（优先于 Outcome；用于"首候选拒、次候选收"场景）。</summary>
    public Func<PlayerTrack, PlayerOperationResult>? ExecuteResultFunc { get; set; }

    /// <summary>非 null 时 ProbeAsync 抛此异常（模拟探测失败）。</summary>
    public Exception? ProbeFailure { get; set; }

    public List<(PlayerCommand Command, PlayerTrack? Track)> Executed { get; } = [];

    private readonly object _executedLock = new();
    private int _playSelectedCount;

    /// <summary>
    /// PlaySelected 的线程安全计数：供 WaitUntilAsync 轮询等待使用。
    /// 直接对 <see cref="Executed"/> 做 LINQ 枚举与 worker 线程的 Add 并发时
    /// 会抛 InvalidOperationException（集合已修改），造成与业务无关的 flaky。
    /// </summary>
    public int PlaySelectedCount
    {
        get
        {
            lock (_executedLock)
            {
                return _playSelectedCount;
            }
        }
    }

    /// <summary>
    /// 线程安全地获取已执行命令的快照副本：供 worker 后台运行时（如 WaitUntilAsync
    /// 轮询、派发后立即读明细）安全枚举。直接枚举 <see cref="Executed"/> 与 worker
    /// 的 Add 并发会抛 InvalidOperationException（集合已修改）。
    /// </summary>
    public IReadOnlyList<(PlayerCommand Command, PlayerTrack? Track)> ExecutedSnapshot()
    {
        lock (_executedLock)
        {
            return Executed.ToList();
        }
    }

    /// <summary>可编程探测序列：每次 ProbeAsync 出队一个；空时返回 Connected=true 空快照。</summary>
    public Queue<PlayerSnapshot> ProbeResults { get; } = new();

    /// <summary>可编程探测函数（非 null 时优先于 ProbeResults/FixedProbe）。</summary>
    public Func<PlayerSnapshot>? ProbeFunc { get; set; }

    /// <summary>固定探测结果（非 null 时优先于 ProbeResults）。</summary>
    public PlayerSnapshot? FixedProbe { get; set; }

    public static PlayerSnapshot Snapshot(string? status = null, string? title = null, string? artist = null,
        double? progress = null, PlayerTrack? next = null, NextObservation nextObservation = NextObservation.Unknown,
        int? duration = null) =>
        new()
        {
            Connected = true,
            Version = "fake",
            Current = title is null ? null : new PlayerTrack
            {
                Platform = "fake",
                Title = title,
                Artist = artist ?? "",
                DurationSeconds = duration,
            },
            Next = next,
            NextObservation = nextObservation,
            RawStatus = status,
            ProgressSeconds = progress,
        };

    public Task<PlayerSnapshot> ActivateAsync(AppConfig config, CancellationToken ct) =>
        Task.FromResult(new PlayerSnapshot
        {
            Connected = true,
            Version = "fake",
            NextObservation = NextObservation.Unknown,
        });

    public Task DeactivateAsync() => Task.CompletedTask;

    public Task<PlayerSnapshot> ProbeAsync(CancellationToken ct)
    {
        if (ProbeFailure is not null)
        {
            throw ProbeFailure;
        }

        if (ProbeFunc is not null)
        {
            return Task.FromResult(ProbeFunc());
        }

        if (FixedProbe is not null)
        {
            return Task.FromResult(FixedProbe);
        }

        return Task.FromResult(ProbeResults.Count > 0 ? ProbeResults.Dequeue() : Snapshot());
    }

    public Task<IReadOnlyList<PlayerTrack>> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PlayerTrack>>([]);

    public Task<PlayerOperationResult> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct)
    {
        lock (_executedLock)
        {
            Executed.Add((command, track));
            if (command == PlayerCommand.PlaySelected)
            {
                _playSelectedCount++;
            }
        }

        var result = ExecuteResultFunc is not null && command == PlayerCommand.PlaySelected && track is not null
            ? ExecuteResultFunc(track)
            : new PlayerOperationResult(Outcome, "");
        return Task.FromResult(result);
    }

    public IAsyncEnumerable<PlayerSnapshot>? WatchSnapshotsAsync(CancellationToken ct) => null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class SearchHarness
{
    /// <summary>返回固定候选的搜索处理器（首候选即歌名+歌手）。</summary>
    public static Func<Erbai.Contracts.Requests.SongRequest, CancellationToken, Task<IReadOnlyList<SongSearchResult>?>> Fixed(
        string source = "kugou") =>
        (request, _) => Task.FromResult<IReadOnlyList<SongSearchResult>?>(
        [
            new SongSearchResult
            {
                Source = source,
                Name = request.SongName,
                Singer = request.Singer,
                SongMid = $"mid-{request.SongName}",
            },
        ]);

    /// <summary>等待条件成立（轮询，带超时）。</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }
}
