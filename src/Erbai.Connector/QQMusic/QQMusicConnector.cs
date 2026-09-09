using System.Text.Json;
using Erbai.Contracts.Players;
using Erbai.Connector.Shared;

namespace Erbai.Connector.QQMusic;

/// <summary>搜索结果载荷（NativeData 内嵌，原生插队所需）。</summary>
public sealed record QqTrackPayload(long SongId, int SongType, bool IsPlayable);

/// <summary>
/// QQ 音乐播放器连接器（机制 docs/04 §1.5.3，代码表达沿用上游）：
/// 状态面 = 窗口标题解析；控制面 = 单实例命令 /playcontrol + 版本锁定
/// profile 原生插队（QqNativeNextTransport，安全拒绝语义）；搜索 = 目录 API。
/// 能力：Search/PlaySelected/InsertNext/Next/Pause/Resume；InterruptSelected
/// 收敛为 PlaySelected 路径；未知版本插队安全拒绝 → 降级官方 /playbysongid
/// 单曲播放（2026-09，22.61 无画像时点歌仍可用；单曲模式替换当前队列）。
/// </summary>
public sealed class QqMusicConnector : IConnectorBackend
{
    private readonly QqCatalogClient _catalog;
    private readonly GuardedNextMonitor _nextGuard = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<string, PlayerTrack> _knownTracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateSync = new();
    private JsonElement? _lastSnapshot;

    public QqMusicConnector(QqCatalogClient? catalog = null)
    {
        _catalog = catalog ?? new QqCatalogClient();
    }

    public string Key => "qqmusic";

    public string DisplayName => "QQ 音乐";

    public string[] ProtocolCapabilities => ["snapshot-events-v1", "queue-programmable-v1"];

    public Task<JsonElement> ActivateAsync(CancellationToken ct) => ProbeAsync(ct);

    public Task DeactivateAsync()
    {
        _nextGuard.Cancel("QQ 音乐连接器已停用");
        return Task.CompletedTask;
    }

    public Task<JsonElement> ProbeAsync(CancellationToken ct)
    {
        PlayerSnapshot snapshot;
        var state = QqNativeController.ReadPlaybackState();
        if (!state.IsRunning)
        {
            snapshot = new PlayerSnapshot
            {
                Connected = false,
                Version = "qqmusic",
                NextObservation = NextObservation.Unknown,
                RawStatus = "未连接：没有发现可见 QQ 音乐窗口",
            };
        }
        else
        {
            var current = string.IsNullOrWhiteSpace(state.Title)
                ? null
                : new PlayerTrack { Platform = "qqmusic", Id = "", Title = state.Title, Artist = state.Artist ?? "" };
            var status = string.IsNullOrWhiteSpace(_nextGuard.Status)
                ? "QQ 音乐窗口已连接"
                : $"QQ 音乐窗口已连接；{_nextGuard.Status}";
            snapshot = new PlayerSnapshot
            {
                Connected = true,
                Version = "qqmusic",
                Current = current,
                NextObservation = NextObservation.Unknown,
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
        var songs = await _catalog.SearchAsync(query, 12, ct);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var song in songs)
            {
                writer.WriteStartObject();
                writer.WriteString("platform", "qqmusic");
                writer.WriteString("id", song.SongId.ToString());
                writer.WriteString("title", song.Title);
                writer.WriteString("artist", song.Artist);
                writer.WriteString("album", song.Album);
                writer.WriteString("coverUrl", BuildCoverUrl(song.AlbumMid));
                writer.WritePropertyName("nativeData");
                writer.WriteStringValue(JsonSerializer.Serialize(new QqTrackPayload(song.SongId, song.SongType, song.IsPlayable)));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public async Task<JsonElement> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try
        {
            var before = await ProbeAsync(ct);
            if (!before.GetProperty("connected").GetBoolean())
            {
                return DummyConnector.SerializeResult(PlayerOperationResult.Failure("QQ 音乐未连接。"));
            }

            if (track is not null)
            {
                RememberTrack(track);
            }

            switch (command)
            {
                case PlayerCommand.Next:
                    return await ExecutePlayControlAsync("next", before, ct, verifyTrackChange: true);

                case PlayerCommand.Pause:
                    return await ExecutePlayControlAsync("pause", before, ct, verifyTrackChange: false);

                case PlayerCommand.Resume:
                    return await ExecutePlayControlAsync("play", before, ct, verifyTrackChange: false);

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
                        ? PlayerOperationResult.Success(PlayerOutcome.Accepted, $"未重复提交 QQ 原生下一首；{guardMessage}")
                        : PlayerOperationResult.Failure("当前歌曲不可识别，无法只更新下一首兜底守卫。"));
                }

                case PlayerCommand.InsertNext:
                case PlayerCommand.PlaySelected:
                case PlayerCommand.InterruptSelected:
                    return await ExecuteInsertOrPlayAsync(command, track, before, ct);

                default:
                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Unsupported($"QQ 音乐适配器不支持 {command} 指令。"));
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

    private async Task<JsonElement> ExecutePlayControlAsync(
        string argument,
        JsonElement before,
        CancellationToken ct,
        bool verifyTrackChange)
    {
        var executable = QqNativeController.FindExecutablePath();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return DummyConnector.SerializeResult(
                PlayerOperationResult.Failure("无法从运行进程或常见安装目录定位 QQMusic.exe。"));
        }

        var send = await Task.Run(() => QqNativeController.SendPlayControl(executable, argument), ct);
        if (!send.Sent)
        {
            return DummyConnector.SerializeResult(PlayerOperationResult.Failure(send.Message));
        }

        if (!verifyTrackChange)
        {
            return DummyConnector.SerializeResult(
                PlayerOperationResult.Success(PlayerOutcome.Accepted, $"{send.Message}；命令为明确的 {argument}，但标题状态不能验证暂停位。"));
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1400);
        var after = before;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(80, ct);
            after = await ProbeAsync(ct);
            if (!string.Equals(CurrentTitle(before), CurrentTitle(after), StringComparison.Ordinal))
            {
                return DummyConnector.SerializeResult(
                    PlayerOperationResult.Success(PlayerOutcome.Applied, $"已观察到 QQ 音乐切歌：{CurrentTitle(after)}"));
            }
        }

        return DummyConnector.SerializeResult(
            PlayerOperationResult.Success(PlayerOutcome.Accepted, $"{send.Message}；快速验证窗口内未观察到标题变化；后台轮询仍会继续更新状态。"));
    }

    private async Task<JsonElement> ExecuteInsertOrPlayAsync(PlayerCommand command, PlayerTrack? track, JsonElement before, CancellationToken ct)
    {
        if (track is null)
        {
            return DummyConnector.SerializeResult(PlayerOperationResult.Failure("请先选择一条 QQ 搜索结果。"));
        }

        var payload = ParsePayload(track);
        if (payload is null || !payload.IsPlayable)
        {
            return DummyConnector.SerializeResult(
                PlayerOperationResult.Failure(payload is null ? "搜索结果缺少插队载荷。" : "QQ 目录接口把该结果标记为不可播放。"));
        }

        if (command == PlayerCommand.InsertNext)
        {
            var result = await QqNativeNextTransport.InsertAsync(payload.SongId, payload.SongType);
            if (!result.Accepted)
            {
                return DummyConnector.SerializeResult(
                    PlayerOperationResult.Failure($"QQ 原生插入下一首被拒绝；为保护播放器原有队列，没有回退到会重建队列的 /playbysongid。 验证={result.Verification}；{result.Error}"));
            }

            var armed = _nextGuard.Arm(
                DeserializeCurrent(before),
                track,
                ReadCurrentForGuardAsync,
                TakeOverGuardedNextAsync,
                CancellationToken.None,
                out var guardMessage);
            return DummyConnector.SerializeResult(armed
                ? PlayerOperationResult.Success(PlayerOutcome.Accepted, $"QQ 原生下一首已插入：{Display(track)}。{guardMessage}")
                : PlayerOperationResult.Success(PlayerOutcome.Accepted, $"QQ 原生下一首已插入：{Display(track)}。当前歌曲不可识别，守卫未启动。"));
        }

        // PlaySelected / InterruptSelected：pause（防漏音）→ 插入 → next 切换 → 验证
        var executable = QqNativeController.FindExecutablePath();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return DummyConnector.SerializeResult(PlayerOperationResult.Failure("找不到 QQMusic.exe，无法执行暂停—插入—切换事务。"));
        }

        var pause = await Task.Run(() => QqNativeController.SendPlayControl(executable, "pause"), ct);
        if (!pause.Sent)
        {
            return DummyConnector.SerializeResult(
                PlayerOperationResult.Failure($"QQ 立即点歌未能先暂停，已取消插入以避免漏音：{pause.Message}"));
        }

        await Task.Delay(20, ct);
        _nextGuard.Cancel("正在立即播放新的目标歌曲。");
        var guardArmed = _nextGuard.Arm(
            DeserializeCurrent(before),
            track,
            ReadCurrentForGuardAsync,
            TakeOverGuardedNextAsync,
            CancellationToken.None,
            out _);
        var native = await QqNativeNextTransport.InsertAsync(payload.SongId, payload.SongType);
        if (!native.Accepted)
        {
            _nextGuard.Cancel("QQ 原生插入被画像校验拒绝。");
            // 未知版本（无画像，如 22.61）：注入被安全拒绝，降级官方 /playbysongid
            // 单曲播放（不写进程）。行为差异：cmd_count==1 替换当前队列为单曲、播完
            // 停止（不自动接回原队列）；守卫无插队对账信息（跳过守卫）。仅 PlaySelected
            // /InterruptSelected 适用；InsertNext 语义无法用单曲命令模拟，保持拒绝。
            if (QqNativeNextTransport.IsUnsupportedVersionFailure(native))
            {
                var fallback = await QqNativeNextTransport.PlayBySongIdAsync(
                    payload.SongId, payload.SongType, ct);
                if (fallback.Accepted)
                {
                    _ = await Task.Run(() => QqNativeController.SendPlayControl(executable, "play"), ct);
                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Success(PlayerOutcome.Accepted,
                            $"QQ 版本无画像（未知版本），已降级官方命令播放单曲：{Display(track)}。{fallback.Message}（单曲模式会替换当前队列，播完停止）"));
                }
            }

            _ = await Task.Run(() => QqNativeController.SendPlayControl(executable, "play"), ct);
            return DummyConnector.SerializeResult(
                PlayerOperationResult.Failure($"QQ 原生插入下一首被拒绝；为保护播放器原有队列，没有回退到会重建队列的 /playbysongid。 验证={native.Verification}；{native.Error}"));
        }

        var resume = await Task.Run(() => QqNativeController.SendPlayControl(executable, "play"), ct);
        if (!resume.Sent)
        {
            return DummyConnector.SerializeResult(
                PlayerOperationResult.Success(PlayerOutcome.Indeterminate, $"QQ 已插入目标但恢复播放失败：{resume.Message}；守卫仍在检查实际结果。"));
        }

        // next 切换并验证命中
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        var after = before;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
            after = await ProbeAsync(ct);
            if (TrackMatches(DeserializeCurrent(after), track))
            {
                _nextGuard.Cancel($"立即播放已正确命中：{Display(track)}");
                return DummyConnector.SerializeResult(
                    PlayerOperationResult.Success(PlayerOutcome.Verified, $"QQ 已插入一次并切换到目标：{Display(track)}"));
            }
        }

        return DummyConnector.SerializeResult(
            PlayerOperationResult.Success(PlayerOutcome.Indeterminate,
                "QQ 已插入目标，但未在等待窗口内确认命中；" + (guardArmed ? "守卫会继续检查实际切歌结果。" : "当前歌曲不可识别，守卫未启动。")));
    }

    private async Task<string> TakeOverGuardedNextAsync(PlayerTrack target, CancellationToken ct)
    {
        var payload = ParsePayload(target);
        if (payload is null || !payload.IsPlayable)
        {
            return "QQ 兜底失败：目标没有可用的插队载荷。";
        }

        var native = await QqNativeNextTransport.InsertAsync(payload.SongId, payload.SongType);
        if (!native.Accepted)
        {
            return $"QQ 兜底失败：{native.Error}";
        }

        var executable = QqNativeController.FindExecutablePath();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return "QQ 已重新插入目标，但无法定位 QQMusic.exe 执行切换。";
        }

        var next = await Task.Run(() => QqNativeController.SendPlayControl(executable, "next"), ct);
        return next.Sent
            ? $"QQ 已通过兜底插入并切换到目标：{Display(target)}"
            : $"QQ 已重新插入目标，但切换失败：{next.Message}";
    }

    private Task<PlayerTrack?> ReadCurrentForGuardAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_stateSync)
        {
            return Task.FromResult(DeserializeCurrent(_lastSnapshot ?? default));
        }
    }

    private void RememberTrack(PlayerTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Id))
        {
            _knownTracks[track.Id] = track;
        }
    }

    /// <summary>
    /// 解析插队载荷：兼容两种线格式——
    /// ① 连接器原生 search 产物（PascalCase QqTrackPayload：SongId/SongType/IsPlayable）；
    /// ② 三源搜索 SongSearchResult 序列化（小写驼峰 songId/songType，无 isPlayable 键）。
    /// 后者的 songId/songType 由 QqMusicSearchProvider 从 client_search_cp 解析填充；
    /// 缺失 songId（≤0）或无载荷一律返回 null（调用方按"搜索结果缺少插队载荷"拒绝）。
    /// </summary>
    private static QqTrackPayload? ParsePayload(PlayerTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.NativeData))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(track.NativeData);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var songId = ReadNumber(root, "songId", "SongId");
            if (songId is null || songId <= 0)
            {
                return null;
            }

            var songType = (int?)ReadNumber(root, "songType", "SongType") ?? 0;
            var isPlayable = ReadBool(root, "isPlayable", "IsPlayable") ?? true; // 三源线格式无该键 → 默认可播
            return new QqTrackPayload((long)songId, songType, isPlayable);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ReadNumber(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var value) &&
                value.ValueKind is JsonValueKind.Number &&
                value.TryGetDouble(out var d))
            {
                return d;
            }

            if (root.TryGetProperty(key, out value) &&
                value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? ReadBool(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (root.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    private static string BuildCoverUrl(string albumMid) =>
        string.IsNullOrWhiteSpace(albumMid)
            ? ""
            : $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg";

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

    private static string Display(PlayerTrack track) =>
        string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Title} - {track.Artist}";
}
