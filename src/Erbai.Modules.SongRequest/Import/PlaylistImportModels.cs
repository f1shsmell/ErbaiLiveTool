using Erbai.Contracts.Requests;
using Erbai.Modules.SongRequest.Search;
using Erbai.Contracts.Storage;

namespace Erbai.Modules.SongRequest.Import;

/// <summary>分享链接指向的目标类型：歌单 / 用户主页（拉取其公开歌单）。</summary>
public enum PlaylistLinkKind
{
    Playlist,
    UserHome,
}

/// <summary>
/// 从分享文本解析出的歌单/用户链接。PlaylistId/UserId 为空表示短链（需在客户端
/// 侧解析重定向取真实 id）。复用契约的 PlaylistSource 枚举（local 不会出现）。
/// </summary>
public sealed record PlaylistShareLink(
    PlaylistSource Platform,
    PlaylistLinkKind Kind,
    string Url,
    string? PlaylistId = null,
    string? UserId = null);

/// <summary>导入产物单曲：名称 + 歌手 + 可选的存量搜索候选（落库 search_json，播放免搜索）。</summary>
public sealed record ImportedSong(string Name, string Singer, SongSearchResult? Candidate);

/// <summary>导入产物歌单：平台 + 名称 + 平台侧 id（对账更新用）+ 全部曲目。</summary>
public sealed record ImportedPlaylist(
    PlaylistSource Platform,
    string Name,
    string SourceId,
    IReadOnlyList<ImportedSong> Songs);

/// <summary>导入流程的可读错误（UI 直接展示 message；区别于意外异常）。</summary>
public sealed class PlaylistImportException : Exception
{
    public PlaylistImportException(string message) : base(message)
    {
    }
}

/// <summary>平台歌单客户端契约：由 ShareLinkParser 的产物驱动，短链自解析。</summary>
public interface IPlaylistImportClient
{
    PlaylistSource Platform { get; }

    Task<ImportedPlaylist> FetchAsync(PlaylistShareLink link, CancellationToken ct);
}
