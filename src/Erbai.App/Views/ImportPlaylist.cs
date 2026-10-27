using Erbai.App.Services;
using Erbai.Contracts.Storage;
using Erbai.Modules.SongRequest.Import;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Erbai.App.Views;

/// <summary>
/// 歌单导入 UI 流程（PlaylistPage「导入歌单链接」入口）：
/// 粘贴分享文本 → 解析平台/短链 → 拉取（网易云 v6 / QQ 移动端 / 酷狗 v3，全匿名）
/// → 预览确认 → 去重落库 → 选中新歌单。网易云用户主页链接走「公开歌单列表挑选」
/// 分支（昵称搜索已被平台风控，2026-10 实测）。
/// </summary>
internal static class ImportPlaylist
{
    public static async Task ImportFlowAsync(PlaylistPage page, Func<Task> onChanged)
    {
        var text = await page.ShowImportInputDialogAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var services = App.Services;
        var link = ShareLinkParser.Parse(text);
        if (link is null)
        {
            page.SetListStatus("未识别出歌单分享链接：请粘贴「分享 → 复制链接」的完整文本（网易云 / QQ 音乐 / 酷狗）");
            return;
        }

        try
        {
            if (link.Kind == PlaylistLinkKind.UserHome && link.UserId is { Length: > 0 } userId)
            {
                await ImportFromUserHomeAsync(page, onChanged, userId);
                return;
            }

            page.SetListStatus($"正在获取{PlatformName(link.Platform)}歌单…");
            var imported = await services.PlaylistImport.FetchAsync(link);
            await PreviewAndPersistAsync(page, onChanged, imported);
        }
        catch (PlaylistImportException ex)
        {
            page.SetListStatus(ex.Message);
        }
        catch (Exception ex)
        {
            page.SetListStatus($"导入失败：{ex.Message}");
        }
    }

    /// <summary>网易云用户主页链接/UID → 其公开歌单列表 → 选一个导入。</summary>
    private static async Task ImportFromUserHomeAsync(PlaylistPage page, Func<Task> onChanged, string userId)
    {
        page.SetListStatus("正在拉取该用户的公开歌单…");
        var summaries = await App.Services.NeteasePlaylist.ListUserPlaylistsAsync(userId);
        if (summaries.Count == 0)
        {
            page.SetListStatus("该用户没有公开歌单（或链接/UID 无效）");
            return;
        }

        var picked = await page.ShowUserPlaylistPickerAsync(summaries);
        if (picked is null)
        {
            page.SetListStatus("");
            return;
        }

        page.SetListStatus($"正在获取歌单「{picked.Name}」…");
        var imported = await App.Services.NeteasePlaylist.FetchByIdAsync(picked.Id);
        await PreviewAndPersistAsync(page, onChanged, imported);
    }

    private static async Task PreviewAndPersistAsync(PlaylistPage page, Func<Task> onChanged, ImportedPlaylist imported)
    {
        var confirmed = await page.ShowImportConfirmAsync(imported.Name, PlatformName(imported.Platform), imported.Songs.Count);
        if (!confirmed)
        {
            page.SetListStatus("");
            return;
        }

        page.SetListStatus("正在导入…");
        var (playlist, added, duplicates) = await App.Services.PlaylistImport.UpsertAsync(imported);
        page.SetListStatus(
            $"已导入「{playlist.Name}」：新增 {added} 首" +
            (duplicates > 0 ? $"（跳过重复 {duplicates} 首）" : "") +
            "。可在右侧标记为空闲播放源。");
        await onChanged();
        page.SelectPlaylist(playlist.Id);
    }

    private static string PlatformName(PlaylistSource source) => source switch
    {
        PlaylistSource.Netease => "网易云",
        PlaylistSource.Qqmusic => "QQ 音乐",
        PlaylistSource.Kugou => "酷狗",
        _ => "平台",
    };
}
