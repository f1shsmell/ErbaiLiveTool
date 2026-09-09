namespace Erbai.Contracts.Storage;

/// <summary>空闲歌单条目（对应 idle_playlist_songs 表，按 position 顺序）。</summary>
public sealed record IdleSong
{
    public required string Name { get; init; }

    public string Singer { get; init; } = "";
}
