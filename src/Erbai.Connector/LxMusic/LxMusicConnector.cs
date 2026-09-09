using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connector.LxMusic;

/// <summary>lxmusic 连接器配置（宿主从环境变量/默认值注入，MVP 面）。</summary>
public sealed record LxMusicOptions
{
    public string HttpUrl { get; init; } = "http://127.0.0.1:23330";

    public bool HttpEnabled { get; init; } = true;

    public bool SseEnabled { get; init; } = true;

    public bool UseHttpControl { get; init; } = true;

    public static LxMusicOptions FromEnvironment() => new()
    {
        HttpUrl = Environment.GetEnvironmentVariable("LX_HTTP_URL") ?? "http://127.0.0.1:23330",
        HttpEnabled = ParseBool(Environment.GetEnvironmentVariable("LX_HTTP_ENABLED"), true),
        SseEnabled = ParseBool(Environment.GetEnvironmentVariable("LX_SSE_ENABLED"), true),
        UseHttpControl = ParseBool(Environment.GetEnvironmentVariable("LX_USE_HTTP_CONTROL"), true),
    };

    private static bool ParseBool(string? value, bool fallback) =>
        value is null ? fallback : bool.TryParse(value, out var parsed) && parsed;
}

/// <summary>
/// 落雪音乐连接器（决策 #15，官方三通道封装，无注入/无内存操作；能力语义与
/// 旧适配器一致，docs/04 §1.3）：Scheme URL（music/play/music/search/
/// player/skipNext/player/play）+ HTTP OpenAPI（/status、控制 /pause 等）+ SSE
/// 订阅。Pause 无 Scheme 端点必须走 HTTP；InsertNext/ArmNextGuard/
/// InterruptSelected 不支持；search 只打开搜索页返回 []；SSE 事件队列有界 8 丢旧。
/// </summary>
public sealed class LxMusicConnector : IConnectorBackend
{
    private readonly LxMusicOptions _options;
    private readonly LxMusicScheme _scheme;
    private readonly LxMusicHttpClient _http;
    private readonly LxMusicSseClient? _sse;
    private CancellationTokenSource? _sseCts;
    private Task? _sseTask;
    private bool _sseSubscribed;

    public LxMusicConnector(LxMusicOptions? options = null, HttpMessageHandler? handler = null,
        Func<string, Task>? launcher = null)
    {
        _options = options ?? LxMusicOptions.FromEnvironment();
        _scheme = new LxMusicScheme(launcher);
        _http = new LxMusicHttpClient(_options.HttpEnabled ? _options.HttpUrl : "", handler);
        if (_options.SseEnabled)
        {
            _sse = new LxMusicSseClient(_options.HttpUrl, handler);
        }
    }

    public string Key => "lxmusic";

    public string DisplayName => "落雪音乐";

    public string[] ProtocolCapabilities => _sse is null ? [] : ["snapshot-events-v1"];

    public Task<JsonElement> ActivateAsync(CancellationToken ct) => ProbeAsync(ct);

    public Task DeactivateAsync()
    {
        if (_sseCts is not null)
        {
            _sseCts.Cancel();
            _sseCts.Dispose();
            _sseCts = null;
        }

        return Task.CompletedTask;
    }

    public async Task<JsonElement> ProbeAsync(CancellationToken ct)
    {
        var state = await _http.GetPlayStateAsync(ct);
        if (state is null)
        {
            return SnapshotJson.Serialize(new PlayerSnapshot
            {
                Connected = false,
                Version = "lxmusic",
                NextObservation = NextObservation.Unknown,
            });
        }

        return SnapshotJson.Serialize(ToSnapshot(state.Value));
    }

    public async Task<JsonElement> SearchAsync(string query, CancellationToken ct)
    {
        // 只打开播放器搜索页返回 []（实际搜索由三源承担，语义与旧适配器一致）
        try
        {
            await _scheme.LaunchAsync(_scheme.MusicSearchUrl(query));
        }
        catch (Exception)
        {
        }

        return JsonDocument.Parse("[]").RootElement.Clone();
    }

    public async Task<JsonElement> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct)
    {
        switch (command)
        {
            case PlayerCommand.PlaySelected:
                if (track is null)
                {
                    return DummyConnector.SerializeResult(PlayerOperationResult.Failure("missing track"));
                }

                // 自搜自播：searchPlay 交 lxmusic 内部选第一可播候选（根治外部
                // 硬编码 songmid 在 lxmusic 里不可播/无版权时的逐候选链式失败，
                // 参考 blive-vod-fork）。带 source 的搜索结果 → searchPlay；
                // 缺 source/不可解析（纯手工 track）→ 回退 music/play 兜底。
                var url = BuildMusicSearchPlayUrl(track) ?? BuildMusicPlayUrl(track);
                try
                {
                    await _scheme.LaunchAsync(url);
                    return DummyConnector.SerializeResult(PlayerOperationResult.Success(PlayerOutcome.Applied));
                }
                catch (Exception ex)
                {
                    return DummyConnector.SerializeResult(PlayerOperationResult.Failure($"scheme launch failed: {ex.Message}"));
                }

            case PlayerCommand.Next:
                if (_options.UseHttpControl && _http.Enabled)
                {
                    return DummyConnector.SerializeResult(
                        await _http.ControlAsync("/skip-next", ct)
                            ? PlayerOperationResult.Success(PlayerOutcome.Applied)
                            : PlayerOperationResult.Failure("http /skip-next failed"));
                }

                return await LaunchControlAsync(_scheme.PlayerSkipNextUrl());

            case PlayerCommand.Pause:
                // Pause 无 Scheme 端点，必须走 HTTP（docs/04 §1.3）
                if (!_http.Enabled)
                {
                    return DummyConnector.SerializeResult(
                        PlayerOperationResult.Failure("pause requires http api (http_enabled=false)"));
                }

                return DummyConnector.SerializeResult(
                    await _http.ControlAsync("/pause", ct)
                        ? PlayerOperationResult.Success(PlayerOutcome.Applied)
                        : PlayerOperationResult.Failure("http /pause failed"));

            case PlayerCommand.Resume:
                if (_options.UseHttpControl && _http.Enabled)
                {
                    return DummyConnector.SerializeResult(
                        await _http.ControlAsync("/play", ct)
                            ? PlayerOperationResult.Success(PlayerOutcome.Applied)
                            : PlayerOperationResult.Failure("http /play failed"));
                }

                return await LaunchControlAsync(_scheme.PlayerPlayUrl());

            default:
                // InsertNext / ArmNextGuard / InterruptSelected：队列不可编程
                return DummyConnector.SerializeResult(
                    PlayerOperationResult.Unsupported($"lxmusic does not support {command}"));
        }
    }

    public IAsyncEnumerable<JsonElement>? WatchSnapshotsAsync(CancellationToken ct)
    {
        var sse = _sse;
        if (sse is null)
        {
            return null;
        }

        if (!_sseSubscribed)
        {
            _sseSubscribed = true;
            _sseCts = new CancellationTokenSource();
            _sseTask = Task.Run(() => sse.RunAsync(_sseCts.Token), CancellationToken.None);
        }

        return WatchCoreAsync(ct);
    }

    private async IAsyncEnumerable<JsonElement> WatchCoreAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sse = _sse!;
        while (!ct.IsCancellationRequested)
        {
            PlayerSnapshot snapshot;
            try
            {
                snapshot = await sse.Events.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            yield return SnapshotJson.Serialize(snapshot);
        }
    }

    private async Task<JsonElement> LaunchControlAsync(string uri)
    {
        try
        {
            await _scheme.LaunchAsync(uri);
            return DummyConnector.SerializeResult(PlayerOperationResult.Success(PlayerOutcome.Applied));
        }
        catch (Exception ex)
        {
            return DummyConnector.SerializeResult(PlayerOperationResult.Failure($"scheme launch failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// 构造 lxmusic://music/searchPlay 载荷（NativeData 优先；缺省从 track
    /// 字段兜底）。返回 null 表示无足够信息（无 source/无歌名）→ 调用方
    /// 回退 BuildMusicPlayUrl。
    /// </summary>
    private string? BuildMusicSearchPlayUrl(PlayerTrack track)
    {
        string? source = null;
        string name = "";
        string? singer = null;
        string? albumName = null;
        string? interval = null;

        if (track.NativeData is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(track.NativeData);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var element = doc.RootElement;
                    string Get(params string[] keys)
                    {
                        foreach (var key in keys)
                        {
                            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                            {
                                var s = value.GetString();
                                if (s is not null)
                                {
                                    return s;
                                }
                            }
                        }

                        return "";
                    }

                    var src = Get("source", "Source");
                    if (src.Length > 0)
                    {
                        source = src;
                    }

                    name = Get("name", "Name");
                    singer = Get("singer", "Singer");
                    albumName = Get("albumName", "AlbumName");
                    interval = Get("interval", "Interval");
                }
            }
            catch (JsonException)
            {
            }
        }

        if (source is null)
        {
            source = track.Platform;
        }

        if (name.Length == 0)
        {
            name = track.Title;
        }

        singer ??= track.Artist;
        albumName ??= track.Album;
        interval ??= "";

        if (source.Length == 0 || name.Length == 0)
        {
            return null;
        }

        return _scheme.MusicSearchPlayUrl(source, name, singer, albumName, interval);
    }

    /// <summary>
    /// 构造 lxmusic://music/play 载荷（NativeData 优先；缺省从 track 字段兜底）。
    /// NativeData 键大小写不敏感（SongSearchResult 线格式为小写驼峰，此处同时
    /// 兼容 PascalCase 序列化的旧数据）；types 缺失/为空时回退 128k——LX 客户端
    /// qualityFilter 对无合法音质（128k/320k/flac/flac24bit）的载荷报
    /// "quality no match"。
    /// </summary>
    private string BuildMusicPlayUrl(PlayerTrack track)
    {
        if (track.NativeData is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(track.NativeData);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return BuildFromJson(doc.RootElement);
                }
            }
            catch (JsonException)
            {
            }
        }

        return _scheme.MusicPlayUrl(
            LxMusicScheme.NormalizeSource(track.Platform),
            track.Title,
            track.Artist,
            track.Id,
            track.CoverUrl,
            "",
            "",
            track.Album,
            [new SongTypeWire("128k", "", "")],
            "");
    }

    private string BuildFromJson(JsonElement element)
    {
        string Get(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString();
                    if (s is not null)
                    {
                        return s;
                    }
                }
            }

            return "";
        }

        var types = new List<SongTypeWire>();
        if ((element.TryGetProperty("types", out var typesJson) ||
             element.TryGetProperty("Types", out typesJson)) &&
            typesJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typesJson.EnumerateArray())
            {
                types.Add(new SongTypeWire(
                    GetProperty(item, "type", "Type"),
                    GetProperty(item, "size", "Size"),
                    GetProperty(item, "hash", "Hash")));
            }
        }

        if (types.Count == 0)
        {
            types.Add(new SongTypeWire("128k", "", ""));
        }

        return _scheme.MusicPlayUrl(
            Get("source", "Source"),
            Get("name", "Name"),
            Get("singer", "Singer"),
            Get("songmid", "SongMid"),
            Get("img", "Img"),
            Get("albumId", "AlbumId"),
            Get("interval", "Interval"),
            Get("albumName", "AlbumName"),
            types,
            Get("hash", "Hash"),
            Get("strMediaMid", "StrMediaMid"));

        static string GetProperty(JsonElement element, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString();
                    if (s is not null)
                    {
                        return s;
                    }
                }
            }

            return "";
        }
    }

    /// <summary>
    /// 落雪 /status 的 status 值域（"play"/"pause"/"stop"）→ 播放状态机词表
    /// （"playing"/"paused"/"stoped"）。真实落雪返回的是动词原形，不在
    /// PlayerStatusVocabulary 词表内——不做映射则 DeriveStatus 回退按 Current
    /// 推断，播完后 name 仍非空 → 一直判 "playing"，队列要等到总预算
    /// （时长+缓冲，默认 60s）才移除当前歌、才派发下一首（docs/00 用户实测）。
    /// 未知值（含 "error"）原样透传；null 保持 null（调用方按契约回退推断）。
    /// </summary>
    internal static string? MapStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "play" => "playing",
            "pause" => "paused",
            "stop" => "stoped",
            _ => status,
        };
    }

    /// <summary>/status 平铺字段 → PlayerSnapshot（status→RawStatus、name/singer→current）。</summary>
    private static PlayerSnapshot ToSnapshot(JsonElement state)
    {
        string? GetString(string key) =>
            state.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        double? GetNumber(string key) =>
            state.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.Number
                && value.TryGetDouble(out var d)
                ? d
                : null;

        var name = GetString("name");
        var status = GetString("status");
        return new PlayerSnapshot
        {
            Connected = true,
            Version = "lxmusic",
            Current = string.IsNullOrEmpty(name)
                ? null
                : new PlayerTrack
                {
                    Platform = "lxmusic",
                    Title = name,
                    Artist = GetString("singer") ?? "",
                    DurationSeconds = GetNumber("duration") is { } d ? (int)d : null,
                },
            NextObservation = NextObservation.Unknown,
            RawStatus = MapStatus(status),
            ProgressSeconds = GetNumber("progress"),
        };
    }
}
