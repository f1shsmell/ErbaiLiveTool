using System.Collections.ObjectModel;
using Erbai.App.Services;
using Erbai.Contracts.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Erbai.App.Views;

/// <summary>歌单列表展示投影（来源/曲数/模式摘要 + 空闲播放徽标）。</summary>
public sealed record PlaylistDisplay(Playlist Playlist)
{
    public long Id => Playlist.Id;

    public string Name => Playlist.Name;

    public string Summary => $"{SourceLabel} · {Playlist.SongCount} 首 · {(Playlist.PlayMode == PlaylistPlayMode.Shuffle ? "随机" : "顺序")}";

    public Visibility IdleBadgeVisibility => Playlist.IsIdle ? Visibility.Visible : Visibility.Collapsed;

    private string SourceLabel => Playlist.Source switch
    {
        PlaylistSource.Netease => "网易云",
        PlaylistSource.Qqmusic => "QQ 音乐",
        PlaylistSource.Kugou => "酷狗",
        _ => "本机",
    };
}

/// <summary>歌单曲目展示投影（1 起始序号）。</summary>
public sealed record PlaylistSongDisplay(PlaylistSong Song)
{
    public long Id => Song.Id;

    public int Index => Song.Position + 1;

    public string Name => Song.Name;

    public string Singer => Song.Singer;
}

/// <summary>
/// 歌单页（左列表 / 右详情）：歌单 CRUD、曲目增删排序、空闲播放源标记与
/// 播放模式。所有改动即写盘并经 ReloadIdlePlaylistAsync 热生效（无需重启）；
/// 导入平台分享链接见 ImportPlaylist（P1）。
/// </summary>
public sealed partial class PlaylistPage : Page
{
    private readonly ObservableCollection<PlaylistDisplay> _playlists = [];
    private readonly ObservableCollection<PlaylistSongDisplay> _songs = [];
    private PlaylistDisplay? _selected;

    /// <summary>刷新详情控件（IsOn/SelectedIndex 变更）时抑制事件处理器回写存储。</summary>
    private bool _suppressDetailEvents;

    public PlaylistPage()
    {
        InitializeComponent();
        PlaylistList.ItemsSource = _playlists;
        SongList.ItemsSource = _songs;
        // 数据在 Loaded 拉取（NavigationCacheMode=Enabled：每次进入仍触发 Loaded
        // → 重拉数据，其他页面的改动不会陈旧）
        Loaded += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await ReloadPlaylistsAsync(keepSelectionId: _selected?.Id, autoSelectFirst: _selected is null);
        }
        catch (Exception ex)
        {
            ListStatus.Text = $"加载失败：{ex.Message}";
        }
    }

    // ---- 歌单列表 ----

    /// <summary>重拉歌单列表并尽量恢复选中项；keepSelectionId 不存在时按需自动选中第一个。</summary>
    private async Task ReloadPlaylistsAsync(long? keepSelectionId, bool autoSelectFirst)
    {
        var playlists = await App.Services.Storage.ListPlaylistsAsync();
        var restore = keepSelectionId is { } id ? playlists.FirstOrDefault(p => p.Id == id) : null;

        _playlists.Clear();
        foreach (var playlist in playlists)
        {
            _playlists.Add(new PlaylistDisplay(playlist));
        }

        PlaylistEmptyText.Visibility = _playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (restore is not null)
        {
            PlaylistList.SelectedItem = _playlists.First(x => x.Id == restore.Id); // 触发 OnPlaylistSelected
        }
        else if (autoSelectFirst && _playlists.Count > 0)
        {
            PlaylistList.SelectedIndex = 0;
        }
        else
        {
            ShowNoSelection();
        }
    }

    /// <summary>改动落库后的统一刷新：重拉列表/详情 + 队列侧热重载空闲播放源。</summary>
    private async Task ReloadAllAsync(long? keepSelectionId)
    {
        await ReloadPlaylistsAsync(keepSelectionId, autoSelectFirst: keepSelectionId is null);
        // 任何歌单变化都可能影响空闲播放源（标记切换/被删/内容变化）——
        // 统一热重载（未标记时为清空空闲列表，幂等且廉价）
        await App.Services.Queue.ReloadIdlePlaylistAsync();
    }

    private void OnPlaylistSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not PlaylistDisplay display)
        {
            ShowNoSelection();
            return;
        }

        _selected = display;
        RefreshDetail(display);
        _ = LoadSongsAsync(display.Id);
    }

    private void ShowNoSelection()
    {
        _selected = null;
        _songs.Clear();
        DetailTitle.Text = "";
        // StackPanel 非 Control（无 IsEnabled）：禁用态 = 半透明 + 关闭命中测试
        DetailPanel.Opacity = 0.35;
        DetailPanel.IsHitTestVisible = false;
        SongEmptyState.Visibility = Visibility.Collapsed;
    }

    private void RefreshDetail(PlaylistDisplay display)
    {
        _suppressDetailEvents = true;
        try
        {
            DetailTitle.Text = display.Name;
            IdleToggle.IsOn = display.Playlist.IsIdle;
            PlayModeBox.SelectedIndex = display.Playlist.PlayMode == PlaylistPlayMode.Shuffle ? 1 : 0;
        }
        finally
        {
            _suppressDetailEvents = false;
        }

        DetailPanel.Opacity = 1.0;
        DetailPanel.IsHitTestVisible = true;
        DetailStatus.Text = "";
    }

    // ---- 歌单 CRUD ----

    private async void OnCreatePlaylist(object sender, RoutedEventArgs e)
    {
        var name = await ShowTextInputDialogAsync("新建歌单", "歌单名称", "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var created = await App.Services.Storage.CreatePlaylistAsync(name);
            ListStatus.Text = $"已创建「{created.Name}」";
            await ReloadAllAsync(created.Id);
        }
        catch (Exception ex)
        {
            ListStatus.Text = $"创建失败：{ex.Message}";
        }
    }

    private async void OnRenamePlaylist(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var name = await ShowTextInputDialogAsync("重命名歌单", "歌单名称", _selected.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await App.Services.Storage.RenamePlaylistAsync(_selected.Id, name);
            DetailStatus.Text = "已重命名";
            await ReloadAllAsync(_selected.Id);
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"重命名失败：{ex.Message}";
        }
    }

    private async void OnDeletePlaylist(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除歌单",
            Content = $"确定删除歌单「{_selected.Name}」及其全部 {_selected.Playlist.SongCount} 首歌？删除后不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await App.Services.Storage.DeletePlaylistAsync(_selected.Id);
            ListStatus.Text = $"已删除「{_selected.Name}」";
            // 被删的可能是空闲播放源——ReloadAllAsync 统一热重载兜底
            _selected = null;
            await ReloadAllAsync(keepSelectionId: null);
        }
        catch (Exception ex)
        {
            ListStatus.Text = $"删除失败：{ex.Message}";
        }
    }

    // ---- 空闲播放源 / 播放模式 ----

    private async void OnIdleToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressDetailEvents || _selected is null)
        {
            return;
        }

        try
        {
            var marked = await App.Services.Storage.SetIdlePlaylistAsync(IdleToggle.IsOn ? _selected.Id : null);
            DetailStatus.Text = IdleToggle.IsOn
                ? (marked is null ? "设置失败：歌单不存在" : "已标记为空闲播放源")
                : "已取消空闲播放源";
            await ReloadAllAsync(_selected.Id);
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"操作失败：{ex.Message}";
            await ReloadAllAsync(_selected.Id);
        }
    }

    private async void OnPlayModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDetailEvents || _selected is null)
        {
            return;
        }

        var mode = PlayModeBox.SelectedIndex == 1 ? PlaylistPlayMode.Shuffle : PlaylistPlayMode.Sequential;
        try
        {
            await App.Services.Storage.SetPlayModeAsync(_selected.Id, mode);
            DetailStatus.Text = mode == PlaylistPlayMode.Shuffle ? "已切换为随机播放" : "已切换为顺序播放";
            await ReloadAllAsync(_selected.Id);
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"操作失败：{ex.Message}";
        }
    }

    // ---- 曲目管理 ----

    private async Task LoadSongsAsync(long playlistId)
    {
        try
        {
            var songs = await App.Services.Storage.ListPlaylistSongsAsync(playlistId);
            if (_selected?.Id != playlistId)
            {
                return; // 期间切换了选中项，丢弃过期结果
            }

            _songs.Clear();
            foreach (var song in songs)
            {
                _songs.Add(new PlaylistSongDisplay(song));
            }

            SongEmptyState.Visibility = _songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"加载曲目失败：{ex.Message}";
        }
    }

    private void OnAddSongKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _ = AddSongFromInputsAsync();
            e.Handled = true;
        }
    }

    private async void OnAddSong(object sender, RoutedEventArgs e) => await AddSongFromInputsAsync();

    private async Task AddSongFromInputsAsync()
    {
        if (_selected is null)
        {
            return;
        }

        var name = SongNameBox.Text.Trim();
        if (name.Length == 0)
        {
            DetailStatus.Text = "请输入歌名";
            return;
        }

        try
        {
            await App.Services.Storage.AddPlaylistSongAsync(_selected.Id, name, SongSingerBox.Text.Trim());
            SongNameBox.Text = "";
            SongSingerBox.Text = "";
            DetailStatus.Text = "已添加";
            await ReloadAllAsync(_selected.Id);
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"添加失败：{ex.Message}";
        }
    }

    private async void OnMoveSongUp(object sender, RoutedEventArgs e) => await MoveSongAsync(sender, -1);

    private async void OnMoveSongDown(object sender, RoutedEventArgs e) => await MoveSongAsync(sender, +1);

    private async Task MoveSongAsync(object sender, int offset)
    {
        if (_selected is null || (sender as FrameworkElement)?.Tag is not long songId)
        {
            return;
        }

        try
        {
            await App.Services.Storage.MovePlaylistSongAsync(_selected.Id, songId, offset);
            await ReloadAllAsync(_selected.Id);
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"移动失败：{ex.Message}";
        }
    }

    private async void OnRemoveSong(object sender, RoutedEventArgs e)
    {
        if (_selected is null || (sender as FrameworkElement)?.Tag is not long songId)
        {
            return;
        }

        try
        {
            await App.Services.Storage.RemovePlaylistSongAsync(songId);
            DetailStatus.Text = "已删除";
            await ReloadAllAsync(_selected.Id);
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"删除失败：{ex.Message}";
        }
    }

    // ---- 导入（网易云/QQ/酷狗分享链接；流程见 ImportPlaylist）----

    private async void OnImportPlaylist(object sender, RoutedEventArgs e)
    {
        try
        {
            await ImportPlaylist.ImportFlowAsync(this, () => _ = ReloadAllAsync(keepSelectionId: null));
        }
        catch (Exception ex)
        {
            ListStatus.Text = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>导入流程状态文案（左侧列表区）。</summary>
    internal void SetListStatus(string message) => ListStatus.Text = message;

    /// <summary>导入完成后选中新歌单。</summary>
    internal void SelectPlaylist(long playlistId)
    {
        var display = _playlists.FirstOrDefault(x => x.Id == playlistId);
        if (display is not null)
        {
            PlaylistList.SelectedItem = display; // 触发 OnPlaylistSelected 刷新详情
        }
    }

    /// <summary>多行粘贴框（分享文本常带标题 + 链接 + 尾巴）。</summary>
    internal async Task<string?> ShowImportInputDialogAsync()
    {
        var hint = new TextBlock
        {
            Text = "支持网易云 / QQ 音乐 / 酷狗「分享 → 复制链接」的完整文本；也支持网易云用户主页链接（拉取其公开歌单）。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var box = new TextBox
        {
            PlaceholderText = "粘贴分享文本（只要包含链接即可）",
            AcceptsReturn = true,
            Height = 130,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(hint);
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = "导入歌单",
            Content = panel,
            PrimaryButtonText = "获取歌单",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>导入预览确认（拉取成功后、落库前）。</summary>
    internal async Task<bool> ShowImportConfirmAsync(string name, string platform, int songCount)
    {
        var dialog = new ContentDialog
        {
            Title = "确认导入",
            Content = $"{platform}歌单「{name}」共 {songCount} 首。\n\n同名单歌单重复导入会覆盖更新；导入后可在右上角标记为空闲播放源。",
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>网易云用户公开歌单挑选（用户主页链接分支）。</summary>
    internal async Task<Erbai.Modules.SongRequest.Import.UserPlaylistSummary?> ShowUserPlaylistPickerAsync(
        IReadOnlyList<Erbai.Modules.SongRequest.Import.UserPlaylistSummary> summaries)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300 };
        foreach (var summary in summaries)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = summary.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 });
            row.Children.Add(new TextBlock { Text = $"{summary.TrackCount} 首", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            list.Items.Add(new ListViewItem { Tag = summary, Content = row, MinHeight = 36, Padding = new Thickness(8, 4, 8, 4) });
        }

        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        var dialog = new ContentDialog
        {
            Title = $"该用户有 {summaries.Count} 个公开歌单",
            Content = list,
            PrimaryButtonText = "导入选中歌单",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return (list.SelectedItem as ListViewItem)?.Tag as Erbai.Modules.SongRequest.Import.UserPlaylistSummary;
    }

    // ---- 对话框 ----

    /// <summary>单行文本输入对话框；取消/空文本返回 null。</summary>
    private async Task<string?> ShowTextInputDialogAsync(string title, string placeholder, string initial)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text : null;
    }
}
