using System.Text.Json;

namespace Erbai.Connector.LxMusic;

/// <summary>
/// lxmusic:// Scheme URL 构造与启动（官方文档 lxmusic.toside.cn/desktop/scheme-url，
/// LXMusicScheme。启动动作抽象为委托（测试 fake 记录 URI）。
/// 合法音源白名单 kw/kg/tx/wy/mg，非法 source（如内部 "idle"）归一为 kw。
/// </summary>
public sealed class LxMusicScheme
{
    public static readonly IReadOnlySet<string> ValidSources =
        new HashSet<string> { "kw", "kg", "tx", "wy", "mg" };

    private readonly Func<string, Task> _launcher;

    public LxMusicScheme(Func<string, Task>? launcher = null)
    {
        _launcher = launcher ?? DefaultLaunchAsync;
    }

    public static string NormalizeSource(string? source) => source?.ToLowerInvariant() switch
    {
        "kg" or "kugou" => "kg",
        "wy" or "netease" => "wy",
        "tx" or "qqmusic" => "tx",
        "kw" or "mg" => source.ToLowerInvariant(),
        _ => "kw",
    };

    public string MusicPlayUrl(
        string source,
        string name,
        string singer,
        string songmid,
        string img,
        string albumId,
        string interval,
        string albumName,
        IReadOnlyList<SongTypeWire> types,
        string hash,
        string strMediaMid = "")
    {
        var data = new Dictionary<string, object>
        {
            ["source"] = NormalizeSource(source),
            ["name"] = name,
            ["singer"] = singer,
            ["songmid"] = songmid,
            ["img"] = img,
            ["albumId"] = albumId,
            ["interval"] = interval,
            ["albumName"] = albumName,
            ["types"] = types.Select(t => (object)new { type = t.Type, size = t.Size, hash = t.Hash }).ToList(),
            ["hash"] = hash,
            ["strMediaMid"] = strMediaMid,
            ["copyrightId"] = "",
            ["lrcUrl"] = "",
        };
        return $"lxmusic://music/play?data={Uri.EscapeDataString(JsonSerializer.Serialize(data))}";
    }

    public string MusicSearchUrl(string name) =>
        $"lxmusic://music/search?data={Uri.EscapeDataString(JsonSerializer.Serialize(new { keywords = name }))}";

    /// <summary>
    /// lxmusic://music/searchPlay：搜索并播放（自搜自播）。由搜索结果携带的
    /// source 指定音源，歌名+歌手交给 lxmusic 内部搜索并选中第一可播候选——
    /// 不外部硬编码 songmid（外部选中的 songmid 在 lxmusic 里可能不可播/
    /// 无版权，会触发逐候选换源链式失败）。playLater=false：立即播放，
    /// 与本项目自己的队列派发节奏一致（参考 blive-vod-fork 用 searchPlay
    /// 但 playLater=True 是因它无自有队列）。
    /// </summary>
    public string MusicSearchPlayUrl(
        string source,
        string name,
        string singer,
        string albumName,
        string interval)
    {
        var data = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["singer"] = singer,
            ["source"] = NormalizeSource(source),
            ["albumName"] = albumName,
            ["interval"] = interval,
            ["playLater"] = false,
        };
        return $"lxmusic://music/searchPlay?data={Uri.EscapeDataString(JsonSerializer.Serialize(data))}";
    }

    public string PlayerSkipNextUrl() => "lxmusic://player/skipNext";

    public string PlayerPlayUrl() => "lxmusic://player/play";

    /// <summary>启动一个 lxmusic:// URI（ShellExecute 交给系统协议处理）。</summary>
    public Task LaunchAsync(string uri) => _launcher(uri);

    private static Task DefaultLaunchAsync(string uri)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
        {
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }
}

/// <summary>LX music/play 载荷的 types 条目（wire 形态）。</summary>
public sealed record SongTypeWire(string Type, string Size, string Hash);
