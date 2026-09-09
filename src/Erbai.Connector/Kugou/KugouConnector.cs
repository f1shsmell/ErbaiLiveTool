using System.Diagnostics;
using System.Text.Json;
using Erbai.Contracts.Players;
using Erbai.Connector.Shared;

namespace Erbai.Connector.Kugou;

/// <summary>
/// 酷狗播放器连接器（机制 docs/04 §1.5.2，代码表达沿用上游）：窗口/ini/共享内存
/// 状态面 + WM_APPCOMMAND/COPYDATA 控制面 + HTTP 搜索面。能力：Search/
/// PlaySelected/InsertNext/Next；Pause/Resume 不支持（酷狗只能安全发送
/// Toggle，无法保证明确 Pause/Resume——与上游语义一致）；Previous 不在
/// 七命令协议面。PlaySelected/InsertNext 需要 track.NativeData（搜索结果）。
/// </summary>
public sealed class KugouConnector : IConnectorBackend
{
    private readonly IKugouNativeControl _native;
    private readonly KugouSearchClient _search;
    private readonly GuardedNextMonitor _nextGuard = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _pendingSync = new();
    private PendingKugouNext? _pendingNext;
    private string _anchorResetStatus = string.Empty;
    private string _lastStatus = string.Empty;

    public KugouConnector(IKugouNativeControl? native = null, KugouSearchClient? search = null)
    {
        _native = native ?? new KugouNativeControl();
        _search = search ?? new KugouSearchClient();
    }

    public string Key => "kugou";

    public string DisplayName => "酷狗音乐";

    public string[] ProtocolCapabilities => ["snapshot-events-v1", "queue-programmable-v1"];

    public Task<JsonElement> ActivateAsync(CancellationToken ct) => ProbeAsync(ct);

    public Task DeactivateAsync()
    {
        _nextGuard.Cancel("酷狗连接器已停用");
        return Task.CompletedTask;
    }

    public Task<JsonElement> ProbeAsync(CancellationToken ct)
    {
        var target = _native.FindMainWindow();
        PlayerSnapshot snapshot;
        if (target is null)
        {
            snapshot = new PlayerSnapshot
            {
                Connected = false,
                Version = "kugou",
                NextObservation = NextObservation.Unknown,
                RawStatus = "未连接：没有发现酷狗主窗口",
            };
        }
        else
        {
            var state = _native.ReadPlaybackState();
            var current = string.IsNullOrWhiteSpace(state.Title)
                ? null
                : new PlayerTrack { Platform = "kugou", Title = state.Title, Artist = state.Artist };
            var pendingNext = GetPendingNextTrack();
            ClearPendingNextIfPlaying(current);
            snapshot = new PlayerSnapshot
            {
                Connected = true,
                Version = "kugou",
                Current = current,
                Next = pendingNext,
                NextObservation = pendingNext is null ? NextObservation.Unknown : NextObservation.Track,
                RawStatus = BuildStatus(state.Source),
            };
        }

        _lastStatus = snapshot.RawStatus ?? "";
        var element = SnapshotJson.Serialize(snapshot);
        _lastSnapshotElement = element;
        return Task.FromResult(element);
    }

    public async Task<JsonElement> SearchAsync(string query, CancellationToken ct)
    {
        var classified = SongQueryPolicy.ParseKugou(query);
        var keywordTask = _search.SearchByKeywordAsync(classified.Value, ct);
        Task<IReadOnlyList<PlayerTrack>>? exactTask = classified.Kind switch
        {
            KugouSongQueryKind.Chain => _search.ResolveShareChainAsync(classified.Value, ct),
            KugouSongQueryKind.ShareCode => _search.ResolveShareCodeAsync(classified.Value, ct),
            _ => null,
        };
        if (exactTask is null)
        {
            return SerializeTracks(await keywordTask);
        }

        var exact = await exactTask;
        if (exact.Count > 0)
        {
            _ = keywordTask; // 精确命中即返回
            return SerializeTracks(exact);
        }

        return SerializeTracks(await keywordTask);
    }

    public async Task<JsonElement> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try
        {
            var before = await ProbeAsync(ct);
            if (!before.GetProperty("connected").GetBoolean())
            {
                return DummyConnector.SerializeResult(PlayerOperationResult.Failure("酷狗未连接。"));
            }

            switch (command)
            {
                case PlayerCommand.Next:
                {
                    var result = await Task.Run(() => _native.SendCommand(KugouAppCommand.NextTrack), ct);
                    var after = await ProbeAsync(ct);
                    if (!result.Sent)
                    {
                        return DummyConnector.SerializeResult(PlayerOperationResult.Failure(result.Error ?? "酷狗没有接受内部控制消息。"));
                    }

                    var trackChanged = TrackChanged(before, after);
                    return DummyConnector.SerializeResult(trackChanged
                        ? PlayerOperationResult.Success(PlayerOutcome.Applied, $"已观察到切歌：{CurrentTitle(after)}")
                        : PlayerOperationResult.Success(PlayerOutcome.Indeterminate, "消息已投递，但没有观察到切歌；未执行 Stop 重试。"));
                }

                case PlayerCommand.Pause:
                case PlayerCommand.Resume:
                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Unsupported("酷狗当前只能安全发送 Toggle，无法保证明确 Pause/Resume。"));

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
                        ? PlayerOperationResult.Success(PlayerOutcome.Accepted, $"未重复插入酷狗队列；{guardMessage}")
                        : PlayerOperationResult.Failure("当前歌曲不可识别，无法只更新下一首兜底守卫。"));
                }

                case PlayerCommand.PlaySelected:
                case PlayerCommand.InsertNext:
                {
                    if (track is null || string.IsNullOrWhiteSpace(track.NativeData))
                    {
                        return DummyConnector.SerializeResult(
                            PlayerOperationResult.Unsupported("酷狗适配器不支持该命令，或没有选中有效搜索结果。"));
                    }

                    var endpoint = FindValidatedIpcEndpoint();
                    if (endpoint is null)
                    {
                        return DummyConnector.SerializeResult(
                            PlayerOperationResult.Failure("Local\\KuGouDataExchange 公布的窗口未通过 KuGou/TaskListener 校验。"));
                    }

                    var payload = KugouInsertPayload.Build(track.NativeData);
                    if (payload is null)
                    {
                        return DummyConnector.SerializeResult(PlayerOperationResult.Failure("搜索结果缺少插歌所需字段。"));
                    }

                    var advanceImmediately = command == PlayerCommand.PlaySelected;
                    if (advanceImmediately)
                    {
                        _nextGuard.Cancel("下一首守卫已因立即播放其他歌曲而取消");
                        if (TrackMatches(DeserializeCurrent(before), track))
                        {
                            ClearPendingNext(track);
                            return DummyConnector.SerializeResult(
                                PlayerOperationResult.Success(PlayerOutcome.Verified, $"目标已经是当前歌曲，未重复插入或切歌：{Display(track)}"));
                        }
                    }

                    if (!HasPendingNext(track))
                    {
                        var insert = await Task.Run(() => _native.SendInsertNext(endpoint.Value.Handle, payload.Json), ct);
                        if (!insert.Sent)
                        {
                            return DummyConnector.SerializeResult(
                                PlayerOperationResult.Failure(insert.Error ?? "酷狗没有接受插歌消息。"));
                        }

                        RememberPendingNext(track);
                    }

                    if (!advanceImmediately)
                    {
                        await Task.Delay(60, ct);
                        var armed = _nextGuard.Arm(
                            DeserializeCurrent(before),
                            track,
                            ReadCurrentForGuardAsync,
                            TakeOverGuardedNextAsync,
                            CancellationToken.None,
                            out var guardMessage);
                        var probe = await ProbeAsync(ct);
                        return DummyConnector.SerializeResult(armed
                            ? PlayerOperationResult.Success(PlayerOutcome.Accepted, (HasPendingNext(track) ? "酷狗已将目标歌曲插入当前歌曲之后。" : "酷狗目标已存在于待切换事务中，本次没有重复插入。") + $" {guardMessage}")
                            : PlayerOperationResult.Success(PlayerOutcome.Indeterminate, "酷狗已将目标歌曲插入当前歌曲之后；当前歌曲不可识别，守卫未启动。"));
                    }

                    // PlaySelected：arm 守卫 → 唯一一次 Next → 轮询确认命中
                    var guardArmed = _nextGuard.Arm(
                        DeserializeCurrent(before),
                        track,
                        ReadCurrentForGuardAsync,
                        TakeOverGuardedNextAsync,
                        CancellationToken.None,
                        out _);
                    await Task.Delay(HasPendingNext(track) ? 20 : 60, ct);
                    var advance = await Task.Run(() => _native.SendCommand(KugouAppCommand.NextTrack), ct);
                    if (!advance.Sent)
                    {
                        return DummyConnector.SerializeResult(
                            PlayerOperationResult.Success(PlayerOutcome.Indeterminate,
                                "酷狗已插入目标，但没有接受本次唯一的下一首命令；" + (guardArmed ? "守卫仍在等待自然切歌。" : "当前歌曲不可识别，守卫未启动。")));
                    }

                    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
                    var afterPlay = before;
                    while (DateTimeOffset.UtcNow < deadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(100, ct);
                        afterPlay = await ProbeAsync(ct);
                        if (!TrackMatches(DeserializeCurrent(afterPlay), track))
                        {
                            continue;
                        }

                        ClearPendingNext(track);
                        _nextGuard.Cancel($"立即播放已正确命中：{Display(track)}");
                        return DummyConnector.SerializeResult(
                            PlayerOperationResult.Success(PlayerOutcome.Verified, $"酷狗已插入一次并切换到目标：{Display(track)}"));
                    }

                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Success(PlayerOutcome.Indeterminate,
                            "酷狗已插入目标并只发送一次下一首，但未在等待窗口内确认命中；" + (guardArmed ? "守卫会继续检查实际切歌结果。" : "当前歌曲不可识别，守卫未启动。")));
                }

                default:
                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Unsupported($"酷狗适配器不支持 {command} 指令。"));
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

    private string BuildStatus(string stateSource)
    {
        var parts = new List<string> { $"控制及在线点歌 IPC 已连接（{stateSource}）" };
        if (!string.IsNullOrWhiteSpace(_anchorResetStatus))
        {
            parts.Add(_anchorResetStatus);
        }

        if (!string.IsNullOrWhiteSpace(_nextGuard.Status))
        {
            parts.Add(_nextGuard.Status);
        }

        return string.Join("；", parts);
    }

    private (nint Handle, int ProcessId)? FindValidatedIpcEndpoint()
    {
        // 校验（窗口类 TaskListener + KuGou 进程）已在 native 实现内完成
        return _native.InspectIpcEndpoint();
    }

    private Task<PlayerTrack?> ReadCurrentForGuardAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(DeserializeCurrent(_lastSnapshotElement ?? default));
    }

    private async Task<string> TakeOverGuardedNextAsync(PlayerTrack target, CancellationToken ct)
    {
        // 守卫兜底：目标未成为下一首时，直接播放目标（插入 + Next）
        if (string.IsNullOrWhiteSpace(target.NativeData))
        {
            return "酷狗兜底失败：目标没有插歌载荷。";
        }

        var endpoint = FindValidatedIpcEndpoint();
        if (endpoint is null)
        {
            return "酷狗兜底失败：IPC 端点不可用。";
        }

        var payload = KugouInsertPayload.Build(target.NativeData);
        if (payload is null)
        {
            return "酷狗兜底失败：无法构建插歌载荷。";
        }

        var insert = await Task.Run(() => _native.SendInsertNext(endpoint.Value.Handle, payload.Json), ct);
        if (!insert.Sent)
        {
            return $"酷狗兜底失败：{insert.Error}";
        }

        await Task.Delay(60, ct);
        var advance = await Task.Run(() => _native.SendCommand(KugouAppCommand.NextTrack), ct);
        return advance.Sent
            ? $"酷狗已通过兜底插入并切换到目标：{Display(target)}"
            : "酷狗已重新插入目标，但切换失败。";
    }

    private JsonElement? _lastSnapshotElement;

    private bool TrackChanged(JsonElement before, JsonElement after) =>
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

    private bool HasPendingNext(PlayerTrack target)
    {
        lock (_pendingSync)
        {
            return _pendingNext is not null && TrackMatches(_pendingNext.Target, target);
        }
    }

    private void RememberPendingNext(PlayerTrack target)
    {
        lock (_pendingSync)
        {
            _pendingNext = new PendingKugouNext(target, DateTimeOffset.UtcNow);
        }
    }

    private PlayerTrack? GetPendingNextTrack()
    {
        lock (_pendingSync)
        {
            return _pendingNext?.Target;
        }
    }

    private void ClearPendingNext(PlayerTrack? target)
    {
        lock (_pendingSync)
        {
            if (_pendingNext is null || target is null || TrackMatches(_pendingNext.Target, target))
            {
                _pendingNext = null;
            }
        }
    }

    private void ClearPendingNextIfPlaying(PlayerTrack? current)
    {
        if (current is null)
        {
            return;
        }

        lock (_pendingSync)
        {
            if (_pendingNext is not null && TrackMatches(_pendingNext.Target, current))
            {
                _pendingNext = null;
            }
        }
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
