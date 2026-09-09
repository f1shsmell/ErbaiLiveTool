namespace Erbai.Contracts.Players;

/// <summary>平台无关歌曲标识；<see cref="NativeData"/> 为连接器原生不透明载荷。</summary>
public sealed record PlayerTrack
{
    /// <summary>平台键：lxmusic / netease / kugou / qqmusic / folia。</summary>
    public required string Platform { get; init; }

    /// <summary>稳定 ID（可能为空——部分来源无 ID）。</summary>
    public string Id { get; init; } = "";

    public required string Title { get; init; }

    public string Artist { get; init; } = "";

    public string Album { get; init; } = "";

    /// <summary>时长（秒）；未知为 null。</summary>
    public int? DurationSeconds { get; init; }

    public string CoverUrl { get; init; } = "";

    /// <summary>连接器原生不透明载荷（JSON 字符串），透传不解析。</summary>
    public string? NativeData { get; init; }
}
