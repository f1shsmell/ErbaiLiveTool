using System.Text.Json;
using Erbai.Contracts.Players;
using Erbai.Connector.Shared;

namespace Erbai.Connector.Netease;

/// <summary>
/// 网易云播放器连接器（机制 docs/04 §1.5.4，代码表达沿用上游）：
/// 状态面 = bridge 事件管道（GET_TRACK_EVENT/WAIT_EVENT）+ 窗口标题兜底；
/// 控制面 = 原生命令（Next/PlayPause，atom+WM_HOTKEY）+ bridge 精确命令
/// （PLAY/ADD_NEXT/PAUSE/RESUME，经注入的 CEF bridge）。能力：
/// Search/PlaySelected/InsertNext/Next/Pause/Resume；PlaySelected =
/// bridge PLAY（立即播放）；InsertNext = bridge ADD_NEXT + 验证 + 守卫。
/// </summary>
public sealed class NeteaseConnector : IConnectorBackend
{
    private readonly NeteaseSearchClient _search;
    private readonly GuardedNextMonitor _nextGuard = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<string, PlayerTrack> _knownTracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateSync = new();
    private Task<NeteaseBridgeInstallResult>? _bridgeInstallTask;
    private JsonElement? _lastSnapshot;

    public NeteaseConnector(NeteaseSearchClient? search = null)
    {
        _search = search ?? new NeteaseSearchClient();
    }

    public string Key => "netease";

    public string DisplayName => "网易云音乐";

    public string[] ProtocolCapabilities => ["snapshot-events-v1", "queue-programmable-v1"];

    public Task<JsonElement> ActivateAsync(CancellationToken ct) => ProbeAsync(ct);

    public Task DeactivateAsync()
    {
        _nextGuard.Cancel("网易云连接器已停用");
        return Task.CompletedTask;
    }

    public Task<JsonElement> ProbeAsync(CancellationToken ct)
    {
        PlayerSnapshot snapshot;
        var endpoint = NeteaseNativeIpc.FindEndpoint();
        if (endpoint is null)
        {
            snapshot = new PlayerSnapshot
            {
                Connected = false,
                Version = "netease",
                NextObservation = NextObservation.Unknown,
                RawStatus = "未连接：没有发现网易云原生播放器窗口",
            };
        }
        else
        {
            var version = NeteaseNativeIpc.TryGetProcessVersion(endpoint.Value.ProcessId);
            var bridgeEvent = NeteaseBridgeClient.ReadLatestTrackEvent(endpoint.Value.ProcessId);
            var current = bridgeEvent.Available && !string.IsNullOrWhiteSpace(bridgeEvent.Name)
                ? new PlayerTrack
                {
                    Platform = "netease",
                    Id = bridgeEvent.TrackId,
                    Title = bridgeEvent.Name,
                    Artist = bridgeEvent.Artist,
                    Album = bridgeEvent.Album,
                    CoverUrl = bridgeEvent.CoverUrl,
                }
                : null;
            var next = bridgeEvent.Available && !string.IsNullOrWhiteSpace(bridgeEvent.NextName)
                ? new PlayerTrack
                {
                    Platform = "netease",
                    Id = bridgeEvent.NextTrackId,
                    Title = bridgeEvent.NextName,
                    Artist = bridgeEvent.NextArtist,
                    Album = bridgeEvent.NextAlbum,
                }
                : null;
            var status = bridgeEvent.Available
                ? $"bridge 已连接（{bridgeEvent.Type}）"
                : $"bridge 事件暂不可用（{bridgeEvent.RawJson}）";
            if (!string.IsNullOrWhiteSpace(_nextGuard.Status))
            {
                status += $"；{_nextGuard.Status}";
            }

            snapshot = new PlayerSnapshot
            {
                Connected = true,
                Version = string.IsNullOrWhiteSpace(version) ? "netease" : version,
                Current = current,
                Next = next,
                NextObservation = next is null ? NextObservation.Unknown : NextObservation.Track,
                RawStatus = status,
            };
        }

        var element = SnapshotJson.Serialize(snapshot);
        lock (_stateSync)
        {
            _lastSnapshot = element;
        }

        return Task.FromResult(element);
    }

    public async Task<JsonElement> SearchAsync(string query, CancellationToken ct)
    {
        var classified = SongQueryPolicy.ParseNetease(query);
        if (classified.Kind == NeteaseSongQueryKind.Keyword)
        {
            return SerializeTracks(await _search.SearchByKeywordAsync(classified.Value, ct));
        }

        var exact = await _search.TryResolveSongIdAsync(classified.Value, ct);
        if (exact is not null)
        {
            return SerializeTracks([exact]);
        }

        return classified.Kind == NeteaseSongQueryKind.ExplicitId
            ? JsonDocument.Parse("[]").RootElement.Clone()
            : SerializeTracks(await _search.SearchByKeywordAsync(classified.Value, ct));
    }

    public async Task<JsonElement> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try
        {
            var before = await ProbeAsync(ct);
            if (!before.GetProperty("connected").GetBoolean())
            {
                return DummyConnector.SerializeResult(PlayerOperationResult.Failure("网易云未连接；没有发现网易云原生播放器窗口。"));
            }

            if (track is not null)
            {
                RememberTrack(track);
            }

            switch (command)
            {
                case PlayerCommand.Pause:
                case PlayerCommand.Resume:
                {
                    var readiness = await EnsureBridgeReadyAsync(ct);
                    if (!readiness.Ready)
                    {
                        return DummyConnector.SerializeResult(PlayerOperationResult.Failure(readiness.Message));
                    }

                    var result = await Task.Run(
                        () => command == PlayerCommand.Pause ? NeteaseBridgeClient.Pause() : NeteaseBridgeClient.Resume(),
                        ct);
                    return DummyConnector.SerializeResult(result.Success
                        ? PlayerOperationResult.Success(PlayerOutcome.Accepted, result.Message)
                        : PlayerOperationResult.Failure(result.Message));
                }

                case PlayerCommand.Next:
                {
                    var sent = await Task.Run(() => NeteaseNativeIpc.SendNativeCommand(NeteaseNativeCommand.Next), ct);
                    if (!sent.Delivered)
                    {
                        return DummyConnector.SerializeResult(PlayerOperationResult.Failure(sent.Message));
                    }

                    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1400);
                    var after = before;
                    while (DateTimeOffset.UtcNow < deadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(80, ct);
                        after = await ProbeAsync(ct);
                        if (TrackChanged(before, after))
                        {
                            return DummyConnector.SerializeResult(
                                PlayerOperationResult.Success(PlayerOutcome.Applied, $"已观察到切歌：{CurrentTitle(after)}"));
                        }
                    }

                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Success(PlayerOutcome.Accepted, $"{sent.Message}；快速验证窗口内未观察到标题变化。"));
                }

                case PlayerCommand.ArmNextGuard:
                {
                    if (track is null)
                    {
                        return DummyConnector.SerializeResult(PlayerOperationResult.Failure("缺少目标曲目。"));
                    }

                    var armed = _nextGuard.Arm(
                        DeserializeCurrent(before),
                        track,
                        ReadCurrentForGuardAsync,
                        TakeOverGuardedNextAsync,
                        CancellationToken.None,
                        out var guardMessage);
                    return DummyConnector.SerializeResult(armed
                        ? PlayerOperationResult.Success(PlayerOutcome.Accepted, $"未重复插入网易云队列；{guardMessage}")
                        : PlayerOperationResult.Failure("当前歌曲不可识别，无法只更新下一首兜底守卫。"));
                }

                case PlayerCommand.PlaySelected:
                case PlayerCommand.InsertNext:
                {
                    if (track is null)
                    {
                        return DummyConnector.SerializeResult(PlayerOperationResult.Failure("缺少目标曲目。"));
                    }

                    var readiness = await EnsureBridgeReadyAsync(ct);
                    if (!readiness.Ready)
                    {
                        return DummyConnector.SerializeResult(PlayerOperationResult.Failure(readiness.Message));
                    }

                    if (command == PlayerCommand.PlaySelected)
                    {
                        _nextGuard.Cancel("下一首守卫已因立即播放其他歌曲而取消");
                        var bridgeResult = await Task.Run(() => NeteaseBridgeClient.PlaySong(track.Id), ct);
                        if (!bridgeResult.Success)
                        {
                            // 桥瞬态（注入后 events=initializing 等）偶发拒命令：幂等重试一次
                            // 再报错（docs/00 修复记录 #19，实测同 id 重试即通过）
                            await Task.Delay(300, ct);
                            bridgeResult = await Task.Run(() => NeteaseBridgeClient.PlaySong(track.Id), ct);
                        }

                        return DummyConnector.SerializeResult(bridgeResult.Success
                            ? PlayerOperationResult.Success(PlayerOutcome.Accepted, bridgeResult.Message)
                            : PlayerOperationResult.Failure(bridgeResult.Message));
                    }

                    var addNext = await Task.Run(() => NeteaseBridgeClient.AddNext(track.Id), ct);
                    if (!addNext.Success)
                    {
                        var armed = _nextGuard.Arm(
                            DeserializeCurrent(before),
                            track,
                            ReadCurrentForGuardAsync,
                            TakeOverGuardedNextAsync,
                            CancellationToken.None,
                            out var guardMessage);
                        if (armed)
                        {
                            return DummyConnector.SerializeResult(
                                PlayerOperationResult.Success(PlayerOutcome.Accepted, $"{addNext.Message} 原生插入失败，但{guardMessage}"));
                        }

                        return DummyConnector.SerializeResult(PlayerOperationResult.Failure(addNext.Message));
                    }

                    // ADD_NEXT 验证：3.5s 轮询 next 是否为目标
                    var verified = false;
                    var verificationDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3.5);
                    while (DateTimeOffset.UtcNow < verificationDeadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(75, ct);
                        var probe = await ProbeAsync(ct);
                        var next = DeserializeNext(probe);
                        if (next is not null && TrackMatches(next, track))
                        {
                            verified = true;
                            break;
                        }
                    }

                    var armedGuard = _nextGuard.Arm(
                        DeserializeCurrent(before),
                        track,
                        ReadCurrentForGuardAsync,
                        TakeOverGuardedNextAsync,
                        CancellationToken.None,
                        out _);
                    return DummyConnector.SerializeResult(armedGuard
                        ? PlayerOperationResult.Success(PlayerOutcome.Accepted, verified
                            ? $"已精确观察到目标歌曲插入队首：{Display(track)}"
                            : $"ADD_NEXT 已投递：{Display(track)}；守卫会确认实际插入结果。")
                        : (verified
                            ? PlayerOperationResult.Success(PlayerOutcome.Accepted, $"已精确观察到目标歌曲插入队首：{Display(track)}")
                            : PlayerOperationResult.Success(PlayerOutcome.Indeterminate, $"ADD_NEXT 已投递但未确认：{Display(track)}")));
                }

                default:
                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Unsupported($"网易云适配器不支持 {command} 指令。"));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public IAsyncEnumerable<JsonElement>? WatchSnapshotsAsync(CancellationToken ct) => WatchCoreAsync(ct);

    private async IAsyncEnumerable<JsonElement> WatchCoreAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? lastTitle = null;
        var lastStatus = string.Empty;
        while (!ct.IsCancellationRequested)
        {
            var snapshot = await ProbeAsync(ct);
            var title = CurrentTitle(snapshot);
            var status = snapshot.TryGetProperty("rawStatus", out var raw) && raw.ValueKind == JsonValueKind.String
                ? raw.GetString() ?? ""
                : "";
            if (title != lastTitle || status != lastStatus)
            {
                lastTitle = title;
                lastStatus = status;
                yield return snapshot;
            }

            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>bridge 注入（单飞）：注入完成后 HELLO 探测就绪。</summary>
    private async Task<(bool Ready, string Message)> EnsureBridgeReadyAsync(CancellationToken ct)
    {
        Task<NeteaseBridgeInstallResult>? installTask;
        lock (_stateSync)
        {
            installTask = _bridgeInstallTask;
            if (installTask is null || installTask.IsCompleted)
            {
                installTask = Task.Run(NeteaseBridgeInstaller.Install, ct);
                _bridgeInstallTask = installTask;
            }
        }

        var result = await installTask;
        if (!result.Success)
        {
            return (false, result.Message);
        }

        var endpoint = NeteaseNativeIpc.FindEndpoint();
        if (endpoint is null)
        {
            return (false, "网易云窗口已消失。");
        }

        var status = NeteaseBridgeClient.Probe(endpoint.Value.ProcessId);
        return (status.Ready, status.Message);
    }

    private Task<PlayerTrack?> ReadCurrentForGuardAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_stateSync)
        {
            return Task.FromResult(DeserializeCurrent(_lastSnapshot ?? default));
        }
    }

    private async Task<string> TakeOverGuardedNextAsync(PlayerTrack target, CancellationToken ct)
    {
        var readiness = await EnsureBridgeReadyAsync(ct);
        if (!readiness.Ready)
        {
            return $"网易云兜底失败：{readiness.Message}";
        }

        var pause = await Task.Run(() => NeteaseBridgeClient.Pause(), ct);
        if (!pause.Success)
        {
            return $"网易云兜底失败：无法暂停错误歌曲（{pause.Message}）";
        }

        var addNext = await Task.Run(() => NeteaseBridgeClient.AddNext(target.Id), ct);
        if (!addNext.Success)
        {
            return $"网易云已暂停错误歌曲，但重新插入目标失败（{addNext.Message}）";
        }

        var next = await Task.Run(() => NeteaseNativeIpc.SendNativeCommand(NeteaseNativeCommand.Next), ct);
        return next.Delivered
            ? $"网易云已暂停错误歌曲并切换目标：{Display(target)}"
            : $"网易云已重新插入目标，但切换失败（{next.Message}）";
    }

    private void RememberTrack(PlayerTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Id))
        {
            _knownTracks[track.Id] = track;
        }
    }

    private static bool TrackChanged(JsonElement before, JsonElement after) =>
        !string.Equals(CurrentTitle(before), CurrentTitle(after), StringComparison.Ordinal);

    private static string CurrentTitle(JsonElement snapshot) =>
        snapshot.TryGetProperty("current", out var current)
        && current.ValueKind == JsonValueKind.Object
        && current.TryGetProperty("title", out var title)
        && title.ValueKind == JsonValueKind.String
            ? title.GetString() ?? ""
            : "";

    private static PlayerTrack? DeserializeCurrent(JsonElement snapshot) =>
        snapshot.ValueKind == JsonValueKind.Object
        && snapshot.TryGetProperty("current", out var current)
            ? SnapshotJson.DeserializeTrack(current)
            : null;

    private static PlayerTrack? DeserializeNext(JsonElement snapshot) =>
        snapshot.ValueKind == JsonValueKind.Object
        && snapshot.TryGetProperty("next", out var next)
            ? SnapshotJson.DeserializeTrack(next)
            : null;

    private static bool TrackMatches(PlayerTrack? left, PlayerTrack? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.Id) && !string.IsNullOrWhiteSpace(right.Id))
        {
            return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static string Display(PlayerTrack track) =>
        string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Title} - {track.Artist}";

    private static JsonElement SerializeTracks(IReadOnlyList<PlayerTrack> tracks)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var track in tracks)
            {
                var element = SnapshotJson.SerializeTrack(track);
                if (element is { } e)
                {
                    e.WriteTo(writer);
                }
            }

            writer.WriteEndArray();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
