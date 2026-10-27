using System.Text.Json;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Requests;
using Erbai.Contracts.Storage;

namespace Erbai.Modules.SongRequest.Import;

/// <summary>
/// 歌单导入编排：分享文本 → 解析平台 → 分平台客户端拉取 →（UI 预览）→
/// 归一化去重（SongKey）落库。重复导入同一平台歌单按 (source, source_id)
/// 对账覆盖更新（保留空闲标记与播放模式，曲目全量替换）。
/// </summary>
public sealed class PlaylistImporter
{
    private readonly IStorageEngine _store;
    private readonly IReadOnlyDictionary<PlaylistSource, IPlaylistImportClient> _clients;

    public PlaylistImporter(IStorageEngine store, IEnumerable<IPlaylistImportClient> clients)
    {
        _store = store;
        _clients = clients.ToDictionary(c => c.Platform);
    }

    /// <summary>按平台客户端拉取歌单产物（不含落库）。</summary>
    public async Task<ImportedPlaylist> FetchAsync(PlaylistShareLink link, CancellationToken ct = default)
    {
        if (!_clients.TryGetValue(link.Platform, out var client))
        {
            throw new PlaylistImportException("暂不支持该平台的歌单导入");
        }

        return await client.FetchAsync(link, ct);
    }

    /// <summary>
    /// 一步导入：解析分享文本 → 拉取 → 去重落库。返回落库后的歌单与统计。
    /// </summary>
    public async Task<(Playlist Playlist, int Added, int Duplicates)> ImportAsync(string shareText, CancellationToken ct = default)
    {
        var link = ShareLinkParser.Parse(shareText)
            ?? throw new PlaylistImportException(
                "未识别出歌单分享链接：请粘贴网易云 / QQ 音乐 / 酷狗的「分享 → 复制链接」完整文本");
        var imported = await FetchAsync(link, ct);
        return await UpsertAsync(imported, ct);
    }

    /// <summary>
    /// 去重落库：SongKey 归一化去重（同歌单内）；按 (source, source_id) 对账——
    /// 已存在则清空曲目后全量替换（歌单的空闲标记/播放模式保留），否则新建。
    /// </summary>
    public async Task<(Playlist Playlist, int Added, int Duplicates)> UpsertAsync(ImportedPlaylist imported, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<(string Name, string Singer, string? SearchJson)>();
        var duplicates = 0;
        foreach (var song in imported.Songs)
        {
            var name = song.Name.Trim();
            var singer = song.Singer.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var key = SongKey.Normalize(name, singer);
            if (key.Length > 0 && !seen.Add(key))
            {
                duplicates++;
                continue;
            }

            rows.Add((name, singer, song.Candidate is null
                ? null
                : JsonSerializer.Serialize(new[] { song.Candidate })));
        }

        var existing = imported.SourceId.Length > 0
            ? await _store.FindImportedPlaylistAsync(imported.Platform, imported.SourceId, ct)
            : null;

        Playlist playlist;
        if (existing is not null)
        {
            await _store.ClearPlaylistSongsAsync(existing.Id, ct);
            playlist = await _store.RenamePlaylistAsync(existing.Id, imported.Name, ct) ?? existing;
        }
        else
        {
            playlist = await _store.CreatePlaylistAsync(imported.Name, imported.Platform, imported.SourceId, ct);
        }

        var added = await _store.AddPlaylistSongsAsync(playlist.Id, rows, ct);
        return (playlist, added, duplicates);
    }
}
