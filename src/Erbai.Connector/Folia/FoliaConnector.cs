using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Erbai.Contracts.Players;
using Erbai.Connector.Shared;

namespace Erbai.Connector.Folia;

/// <summary>
/// Folia 播放器连接器（机制 docs/04 §1.5.1，代码表达沿用上游）：Stage 本地
/// HTTP/WS（32107）+ 网易云曲目详情兜底查询。能力：Search/PlaySelected/
/// Previous/Pause/Resume/Next/InsertNext 全支持；Toggle 不支持。
/// PlaySelected = insert-next + control next；InsertNext = insert-next +
/// 软件兜底守卫（GuardedNextMonitor）；nextSource=stage/next。
/// </summary>
public sealed class FoliaConnector : IConnectorBackend
{
    private readonly FoliaOptions _options;
    private readonly FoliaStageApi _api;
    private readonly HttpClient _neteaseClient;
    private readonly FoliaStageEvents _events;
    private readonly System.Threading.Channels.Channel<PlayerSnapshot> _snapshotEvents =
        System.Threading.Channels.Channel.CreateBounded<PlayerSnapshot>(
            new System.Threading.Channels.BoundedChannelOptions(8)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });
    private readonly GuardedNextMonitor _nextGuard = new();
    private readonly ConcurrentDictionary<string, PlayerTrack> _knownTracks = new(StringComparer.Ordinal);
    private readonly object _stateSync = new();
    private CancellationTokenSource? _eventsCts;
    private Task? _eventsTask;
    private Task? _pumpTask;
    private bool _eventsStarted;
    private PlayerSnapshot _snapshot;

    public FoliaConnector(
        FoliaOptions? options = null,
        HttpMessageHandler? stageHandler = null,
        HttpMessageHandler? neteaseHandler = null,
        Func<string, string, IFoliaStageSocket>? socketFactory = null)
    {
        _options = options ?? FoliaOptions.FromEnvironment();
        _api = new FoliaStageApi(_options.StageUrl, _options.Token, stageHandler);
        _neteaseClient = new HttpClient(neteaseHandler ?? new HttpClientHandler());
        _neteaseClient.Timeout = TimeSpan.FromSeconds(12);
        _neteaseClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BiliNCM-Folia-Connector/1.0");
        _neteaseClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _neteaseClient.DefaultRequestHeaders.Add("Cookie", "os=pc; appver=3.1.37;");
        _neteaseClient.DefaultRequestHeaders.Add("X-Real-IP", "118.88.88.88");
        _neteaseClient.DefaultRequestHeaders.Add("X-Forwarded-For", "118.88.88.88");
        _events = new FoliaStageEvents(_options.StageUrl, _options.Token, socketFactory);
        _snapshot = CreateSnapshot(false, "Folia 连接器尚未连接 Stage API", null);
    }

    public string Key => "folia";

    public string DisplayName => "Folia";

    public string[] ProtocolCapabilities => ["snapshot-events-v1", "queue-programmable-v1"];

    public Task<JsonElement> ActivateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("未配置 Folia Stage Token（环境变量 BILINCM_FOLIA_TOKEN）");
        }

        EnsureEventsStarted();
        return ProbeAsync(ct);
    }

    public Task DeactivateAsync()
    {
        if (_eventsCts is not null)
        {
            _eventsCts.Cancel();
            _eventsCts.Dispose();
            _eventsCts = null;
        }

        _nextGuard.Cancel("Folia 连接器已停用");
        return Task.CompletedTask;
    }

    public Task<JsonElement> ProbeAsync(CancellationToken ct)
    {
        EnsureEventsStarted();
        var snapshot = ReadSnapshot();
        // WS 已断开（事件源不再连接）时快照同步为 disconnected（对齐上游断线语义）
        if (snapshot.Connected && !_events.Connected)
        {
            snapshot = snapshot with
            {
                Connected = false,
                RawStatus = "Folia Stage WebSocket 已断开",
            };
        }

        return Task.FromResult(SnapshotJson.Serialize(
            string.IsNullOrWhiteSpace(_nextGuard.Status)
                ? snapshot
                : snapshot with { RawStatus = $"{snapshot.RawStatus}；{_nextGuard.Status}" }));
    }

    public async Task<JsonElement> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonDocument.Parse("[]").RootElement.Clone();
        }

        var classified = SongQueryPolicy.ParseNetease(query);
        if (classified.Kind == NeteaseSongQueryKind.Keyword)
        {
            return SerializeTracks(await _api.SearchAsync(classified.Value, 20, ct));
        }

        var songId = classified.Value;
        var exactTask = TryLookupNeteaseTrackAsync(songId, ct);
        if (classified.Kind == NeteaseSongQueryKind.ExplicitId)
        {
            var explicitTrack = await exactTask;
            if (explicitTrack is null)
            {
                return JsonDocument.Parse("[]").RootElement.Clone();
            }

            Remember(explicitTrack);
            return SerializeTracks([explicitTrack]);
        }

        var searchTask = _api.SearchAsync(songId, 20, ct);
        var exact = await exactTask;
        if (exact is not null)
        {
            Remember(exact);
            return SerializeTracks([exact]);
        }

        return SerializeTracks(await searchTask);
    }

    public async Task<JsonElement> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct)
    {
        var before = ReadSnapshot();

        switch (command)
        {
            case PlayerCommand.ArmNextGuard:
            {
                if (track is null || !long.TryParse(track.Id, out var guardedSongId) || guardedSongId <= 0)
                {
                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Failure("Folia 需要有效的网易云歌曲 ID。"));
                }

                Remember(track);
                var armed = _nextGuard.Arm(before.Current, track, ReadCurrentForGuardAsync, TakeOverGuardedNextAsync, CancellationToken.None, out var guardMessage);
                return DummyConnector.SerializeResult(armed
                    ? PlayerOperationResult.Success(PlayerOutcome.Accepted, $"未重复插入 Folia 队列；{guardMessage}")
                    : PlayerOperationResult.Failure("当前歌曲不可识别，无法只更新下一首兜底守卫。"));
            }

            case PlayerCommand.InsertNext:
            case PlayerCommand.PlaySelected:
            {
                if (track is null || !long.TryParse(track.Id, out var songId) || songId <= 0)
                {
                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Failure("Folia 需要有效的网易云歌曲 ID。"));
                }

                Remember(track);
                if (command == PlayerCommand.PlaySelected)
                {
                    _nextGuard.Cancel("下一首守卫已因立即播放其他歌曲而取消");
                }

                var insert = await _api.InsertNextAsync(songId, ct);
                if (command == PlayerCommand.InsertNext)
                {
                    var armed = _nextGuard.Arm(before.Current, track, ReadCurrentForGuardAsync, TakeOverGuardedNextAsync, CancellationToken.None, out var guardMessage);
                    return DummyConnector.SerializeResult(insert.Success || armed
                        ? PlayerOperationResult.Success(PlayerOutcome.Accepted,
                            insert.Success
                                ? $"Folia 已接收原生下一首：{Display(track)}。" + (armed ? $" {guardMessage}" : " 当前歌曲不可识别，兜底守卫未启动。")
                                : $"Folia 原生插入失败（HTTP {insert.StatusCode}）。" + (armed ? $" 已回退到软件兜底；{guardMessage}" : " 软件兜底也无法启动。"))
                        : PlayerOperationResult.Failure(
                            $"Folia 原生插入失败（HTTP {insert.StatusCode}），且当前歌曲不可识别，软件兜底无法启动。"));
                }

                if (!insert.Success)
                {
                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Failure($"Folia 拒绝插入下一首（HTTP {insert.StatusCode}）。"));
                }

                var next = await _api.ControlAsync("next", ct);
                return DummyConnector.SerializeResult(next.Success
                    ? PlayerOperationResult.Success(PlayerOutcome.Accepted, $"Folia 已接收立即播放：{Display(track)}")
                    : PlayerOperationResult.Failure($"Folia 拒绝切到目标歌曲（HTTP {next.StatusCode}）。"));
            }

            case PlayerCommand.Pause:
            case PlayerCommand.Resume:
            case PlayerCommand.Next:
            {
                var action = command switch
                {
                    PlayerCommand.Pause => "pause",
                    PlayerCommand.Resume => "play",
                    _ => "next",
                };
                var response = await _api.ControlAsync(action, ct);
                return DummyConnector.SerializeResult(response.Success
                    ? PlayerOperationResult.Success(PlayerOutcome.Accepted, $"Folia 已接收 {action} 指令。")
                    : PlayerOperationResult.Failure($"Folia 拒绝 {action}（HTTP {response.StatusCode}）。"));
            }

            default:
                return DummyConnector.SerializeResult(
                    PlayerOperationResult.Unsupported($"Folia 暂不支持 {command} 指令。"));
        }
    }

    public IAsyncEnumerable<JsonElement>? WatchSnapshotsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            return null;
        }

        EnsureEventsStarted();
        return WatchCoreAsync(ct);
    }

    private async IAsyncEnumerable<JsonElement> WatchCoreAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PlayerSnapshot snapshot;
            try
            {
                snapshot = await _snapshotEvents.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(_nextGuard.Status))
            {
                snapshot = snapshot with { RawStatus = $"{snapshot.RawStatus}；{_nextGuard.Status}" };
            }

            yield return SnapshotJson.Serialize(snapshot);
        }
    }

    private void EnsureEventsStarted()
    {
        if (_eventsStarted)
        {
            return;
        }

        _eventsStarted = true;
        _eventsCts = new CancellationTokenSource();
        var token = _eventsCts.Token;
        _eventsTask = Task.Run(() => _events.RunAsync(token), CancellationToken.None);
        // 快照泵：独立消费事件通道并更新内部快照（对齐上游 ReceiveLoop 语义——
        // 未订阅时事件仍更新快照，Probe 恒为最新）
        _pumpTask = Task.Run(() => PumpSnapshotsAsync(token), CancellationToken.None);
    }

    private async Task PumpSnapshotsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            FoliaStageEvent stageEvent;
            try
            {
                stageEvent = await _events.Events.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            UpdateSnapshot(stageEvent);
            _snapshotEvents.Writer.TryWrite(ReadSnapshot());
        }
    }

    private void UpdateSnapshot(FoliaStageEvent stageEvent)
    {
        var current = stageEvent.Current is null
            ? ReadSnapshot().Current
            : MergeKnown(stageEvent.Current);
        var next = stageEvent.Next is null ? null : MergeKnown(stageEvent.Next);
        lock (_stateSync)
        {
            _snapshot = CreateSnapshot(true, $"Folia {stageEvent.Name}", current, next);
        }
    }

    private PlayerTrack? MergeKnown(PlayerTrack parsed)
    {
        Remember(parsed);
        if (_knownTracks.TryGetValue(parsed.Id, out var known)
            && (string.IsNullOrWhiteSpace(parsed.Title) || parsed.Title.StartsWith("歌曲 ", StringComparison.Ordinal)))
        {
            return parsed with
            {
                Title = string.IsNullOrWhiteSpace(known.Title) ? parsed.Title : known.Title,
                Artist = string.IsNullOrWhiteSpace(parsed.Artist) ? known.Artist : parsed.Artist,
                Album = string.IsNullOrWhiteSpace(parsed.Album) ? known.Album : parsed.Album,
                CoverUrl = string.IsNullOrWhiteSpace(parsed.CoverUrl) ? known.CoverUrl : parsed.CoverUrl,
            };
        }

        return parsed;
    }

    private void Remember(PlayerTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.Id))
        {
            return;
        }

        _knownTracks[track.Id] = track;
        while (_knownTracks.Count > 1024)
        {
            var oldest = _knownTracks.Keys.FirstOrDefault();
            if (string.IsNullOrEmpty(oldest))
            {
                break;
            }

            _knownTracks.TryRemove(oldest, out _);
        }
    }

    /// <summary>网易云曲目详情兜底（id=xxx / 疑似 id 搜索路径）。</summary>
    private async Task<PlayerTrack?> TryLookupNeteaseTrackAsync(string songId, CancellationToken ct)
    {
        try
        {
            var uri = $"{_options.NeteaseApiBase}/api/v3/song/detail?c={Uri.EscapeDataString($"[{{\"id\":{songId}}}]")}";
            using var response = await _neteaseClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (FoliaStageJson.TryGetProperty(doc.RootElement, "code", out var code)
                && code.TryGetInt32(out var codeValue)
                && codeValue != 200)
            {
                return null;
            }

            if (!FoliaStageJson.TryGetProperty(doc.RootElement, "songs", out var songs)
                || songs.ValueKind != JsonValueKind.Array
                || songs.GetArrayLength() == 0)
            {
                return null;
            }

            var song = songs[0];
            var resolvedId = FoliaStageJson.GetScalarString(song, "id");
            if (!string.Equals(resolvedId, songId, StringComparison.Ordinal))
            {
                return null;
            }

            var id = FoliaStageJson.GetScalarString(song, "id") ?? string.Empty;
            var title = FoliaStageJson.GetString(song, "name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return new PlayerTrack
            {
                Platform = "folia",
                Id = id,
                Title = title,
                Artist = FoliaStageJson.ParseArtists(song),
                Album = FoliaStageJson.ParseAlbum(song),
                CoverUrl = FoliaStageJson.ParseCover(song),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private Task<PlayerTrack?> ReadCurrentForGuardAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ReadSnapshot().Current);
    }

    private async Task<string> TakeOverGuardedNextAsync(PlayerTrack target, CancellationToken ct)
    {
        var pause = await _api.ControlAsync("pause", ct);
        if (!pause.Success)
        {
            return $"Folia 兜底失败：无法暂停错误歌曲（HTTP {pause.StatusCode}）。";
        }

        if (!long.TryParse(target.Id, out var songId) || songId <= 0)
        {
            return "Folia 已暂停错误歌曲，但目标歌曲 ID 无效。";
        }

        var insert = await _api.InsertNextAsync(songId, ct);
        if (!insert.Success)
        {
            return $"Folia 已暂停错误歌曲，但重新插入目标失败（HTTP {insert.StatusCode}）。";
        }

        var next = await _api.ControlAsync("next", ct);
        return next.Success
            ? $"Folia 已暂停错误歌曲并切换目标：{Display(target)}"
            : $"Folia 已重新插入目标，但切换失败（HTTP {next.StatusCode}）。";
    }

    private PlayerSnapshot ReadSnapshot()
    {
        lock (_stateSync)
        {
            return _snapshot;
        }
    }

    private static PlayerSnapshot CreateSnapshot(bool connected, string status, PlayerTrack? current, PlayerTrack? next = null) =>
        new()
        {
            Connected = connected,
            Version = "Stage API",
            Current = current,
            Next = next,
            NextObservation = next is null ? NextObservation.Unknown : NextObservation.Track,
            RawStatus = status,
        };

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
