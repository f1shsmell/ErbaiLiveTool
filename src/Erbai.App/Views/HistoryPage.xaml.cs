using System.Collections.ObjectModel;
using Erbai.App.Ux;
using Erbai.Contracts.Requests;
using Erbai.Contracts.QueueUp;
using Erbai.Contracts.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Erbai.App.Views;

/// <summary>
/// 历史页（两个分区）：点歌历史（song_requests 全量，含 rejected）与排队历史（queueup 终态）。
/// 数据来自 SQL 分页查询（ListRequestsPageAsync / ListQueueUpHistoryPageAsync），不整表载入。
/// 快捷操作：加入空闲歌单 / 拉黑歌曲 / 设为管理员——全部复用 AdminPage 同款服务，
/// 写盘即热生效；解除类操作（移出歌单/解除拉黑/取消管理员）仍留在管理页。
/// 行 UI 在 code-behind 构建并直接作为列表项（代码行模式）：本页两个列表的 DataTemplate
/// 若用 x:DataType + x:Bind 会触发 XamlCompiler（WASDK 1.8）WMC9999 codegen 崩溃；
/// 按钮 Click 闭包直接捕获领域对象。
/// </summary>
public sealed partial class HistoryPage : Page
{
    private const int PageSize = 50;

    private readonly ObservableCollection<UIElement> _songItems = [];
    private readonly ObservableCollection<UIElement> _queueUpItems = [];

    private bool _songSection = true;
    private RequestStatus? _songStatusFilter;
    private int _page;

    public HistoryPage()
    {
        InitializeComponent();
        SongList.ItemsSource = _songItems;
        QueueUpList.ItemsSource = _queueUpItems;
        _ = LoadCurrentSectionAsync();
    }

    // ---- 状态文案 ----

    private static string SongStatusText(RequestStatus status) => status switch
    {
        RequestStatus.Received => "已接收",
        RequestStatus.Queued => "排队中",
        RequestStatus.Searching => "搜索中",
        RequestStatus.Ready => "待播放",
        RequestStatus.Dispatched => "播放中",
        RequestStatus.Completed => "已播",
        RequestStatus.Skipped => "已跳过",
        RequestStatus.Cancelled => "已取消",
        RequestStatus.Failed => "播放失败",
        RequestStatus.Rejected => "已拒绝",
        RequestStatus.RecoveryRequired => "待恢复",
        _ => status.ToString(),
    };

    private static string QueueUpStatusText(QueueUpStatus status) => status switch
    {
        QueueUpStatus.Completed => "已完成",
        QueueUpStatus.Cancelled => "已取消",
        _ => status.ToString(),
    };

    // ---- 状态徽章语义色（绿=顺利终态 / 红=异常终态 / 中性=主动放弃；取主题画刷，
    //      SystemFillColor*BackgroundBrush 自带深浅色对比度，与概览/插件页徽章同一视觉语言）----

    private static Brush SongStatusBrush(RequestStatus status) => status switch
    {
        RequestStatus.Completed => SolidColorBrushHelper.Success,
        RequestStatus.Failed or RequestStatus.Rejected or RequestStatus.RecoveryRequired
            => SolidColorBrushHelper.Critical,
        _ => SolidColorBrushHelper.Neutral,
    };

    private static Brush QueueUpStatusBrush(QueueUpStatus status) => status switch
    {
        QueueUpStatus.Completed => SolidColorBrushHelper.Success,
        _ => SolidColorBrushHelper.Neutral,
    };

    /// <summary>状态胶囊徽章（圆点 + 文字 + 半透明色底）；历史记录是静态终态，无脉冲动画。</summary>
    private static ContentControl StatusBadgeFor(string text, Brush brush)
    {
        var badge = new StatusBadge();
        badge.SetText(text, brush);
        return badge;
    }

    // ---- 行 UI 构建（列宽与 HistoryPage.xaml 表头严格一致：150 / * / 110 / 100 / Auto） ----

    private static TextBlock Cell(string text, bool caption = false)
    {
        var block = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (caption)
        {
            block.Style = (Style)Application.Current.Resources["CaptionTextStyle"];
        }

        return block;
    }

    /// <summary>行根 Grid：统一列宽 + 行内边距（悬停高亮与选中态由 ListViewItem 容器负责）。</summary>
    private static Grid RowGrid()
    {
        var root = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 8, 0, 8) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return root;
    }

    /// <summary>行内图标操作按钮（ToolTip 说明语义），比文字按钮更省横向空间、不再把列挤出视口。</summary>
    private static Button IconButton(string glyph, string tooltip, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["InlineActionButtonStyle"],
            Padding = new Thickness(7, 5, 7, 5),
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += onClick;
        return button;
    }

    /// <summary>行内文本按钮（空闲歌单的"+"用文本而非字形，直观且无歧义）。</summary>
    private static Button TextButton(string text, string tooltip, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["InlineActionButtonStyle"],
            Padding = new Thickness(7, 5, 7, 5),
            Content = new TextBlock { Text = text, FontSize = 14 },
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += onClick;
        return button;
    }

    private Grid BuildSongRow(SongRequest song)
    {
        var root = RowGrid();

        var time = Cell(song.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"), caption: true);
        time.FontFamily = new FontFamily("Consolas");
        root.Children.Add(time);
        Grid.SetColumn(time, 0);

        var songStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        songStack.Children.Add(Cell(song.Singer.Length > 0 ? $"{song.SongName} - {song.Singer}" : song.SongName));
        if (song.FailureReason.Length > 0)
        {
            // 失败原因：⚠ 前缀 + 半透明强调色小字（保留原始 reason 文本不翻译）
            songStack.Children.Add(new TextBlock
            {
                Text = "⚠ " + song.FailureReason,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        root.Children.Add(songStack);
        Grid.SetColumn(songStack, 1);

        var requester = Cell(song.Nickname);
        root.Children.Add(requester);
        Grid.SetColumn(requester, 2);

        var status = StatusBadgeFor(SongStatusText(song.Status), SongStatusBrush(song.Status));
        root.Children.Add(status);
        Grid.SetColumn(status, 3);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(IconButton("\uE768", "加入播放队列", (_, _) => _ = QueueFromHistoryAsync(song)));
        actions.Children.Add(TextButton("+", "加入空闲歌单", (_, _) => _ = AddIdleFromHistoryAsync(song)));
        actions.Children.Add(IconButton("\uE783", "拉黑歌曲", (_, _) => _ = BanSongFromHistoryAsync(song)));
        root.Children.Add(actions);
        Grid.SetColumn(actions, 4);

        return root;
    }

    private Grid BuildQueueUpRow(QueueUpEntry entry)
    {
        var root = RowGrid();

        var time = Cell(entry.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"), caption: true);
        time.FontFamily = new FontFamily("Consolas");
        root.Children.Add(time);
        Grid.SetColumn(time, 0);

        var content = Cell(entry.Content.Length > 0
            ? entry.Content
            : entry.Source == QueueUpSource.Gift ? "(礼物插队)" : "(占位)");
        root.Children.Add(content);
        Grid.SetColumn(content, 1);

        var viewer = Cell(entry.Nickname);
        root.Children.Add(viewer);
        Grid.SetColumn(viewer, 2);

        var status = StatusBadgeFor(QueueUpStatusText(entry.Status), QueueUpStatusBrush(entry.Status));
        root.Children.Add(status);
        Grid.SetColumn(status, 3);

        var adminButton = IconButton("\uE7EF", "设为管理员", (_, _) => _ = SetAdminFromHistoryAsync(entry));
        root.Children.Add(adminButton);
        Grid.SetColumn(adminButton, 4);

        return root;
    }

    // ---- 分区/筛选/分页 ----

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SongSection is null)
        {
            return; // XAML 初始化期间的 SelectionChanged 尚未完成 InitializeComponent
        }

        _songSection = (SectionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "song";
        SongSection.Visibility = _songSection ? Visibility.Visible : Visibility.Collapsed;
        QueueUpSection.Visibility = _songSection ? Visibility.Collapsed : Visibility.Visible;
        SongStatusBox.Visibility = _songSection ? Visibility.Visible : Visibility.Collapsed;
        _page = 0;
        _ = LoadCurrentSectionAsync();
    }

    private void OnSongStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SongSection is null)
        {
            return;
        }

        _songStatusFilter = (SongStatusBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "completed" => RequestStatus.Completed,
            "skipped" => RequestStatus.Skipped,
            "cancelled" => RequestStatus.Cancelled,
            "failed" => RequestStatus.Failed,
            "rejected" => RequestStatus.Rejected,
            _ => null,
        };
        _page = 0;
        if (_songSection)
        {
            _ = LoadSongHistoryAsync();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = LoadCurrentSectionAsync();

    private void OnPrevPage(object sender, RoutedEventArgs e)
    {
        if (_page > 0)
        {
            _page--;
            _ = LoadCurrentSectionAsync();
        }
    }

    private void OnNextPage(object sender, RoutedEventArgs e)
    {
        _page++;
        _ = LoadCurrentSectionAsync();
    }

    private Task LoadCurrentSectionAsync() =>
        _songSection ? LoadSongHistoryAsync() : LoadQueueUpHistoryAsync();

    private async Task LoadSongHistoryAsync()
    {
        try
        {
            var (rows, total) = await App.Services.Storage.ListRequestsPageAsync(
                status: _songStatusFilter, descending: true, limit: PageSize, offset: _page * PageSize);
            _songItems.Clear();
            foreach (var row in rows)
            {
                _songItems.Add(BuildSongRow(row));
            }

            SongEmptyState.Visibility = _songItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePaging(total);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载失败：{ex.Message}";
        }
    }

    private async Task LoadQueueUpHistoryAsync()
    {
        try
        {
            var (rows, total) = await App.Services.Storage.ListQueueUpHistoryPageAsync(
                descending: true, limit: PageSize, offset: _page * PageSize);
            _queueUpItems.Clear();
            foreach (var row in rows)
            {
                _queueUpItems.Add(BuildQueueUpRow(row));
            }

            QueueUpEmptyState.Visibility = _queueUpItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePaging(total);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>页码展示 + 上下页按钮可用性；空列表视为 1 页。</summary>
    private void UpdatePaging(int total)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        if (_page >= totalPages)
        {
            _page = totalPages - 1; // 数据被清空后回退页码
        }

        PageText.Text = $"第 {_page + 1} / {totalPages} 页（共 {total} 条）";
        PrevPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = _page < totalPages - 1;
    }

    // ---- 点歌历史快捷操作 ----

    /// <summary>把该点歌记录作为新点歌提交（走完整点歌流水线：搜索→入队→播放），
    /// 复用 OverviewPage 同款注入方式（先「点歌 」前缀，失败再回退原文）。</summary>
    private async Task QueueFromHistoryAsync(SongRequest song)
    {
        var text = string.IsNullOrWhiteSpace(song.Singer) ? song.SongName : $"{song.SongName} - {song.Singer}";
        try
        {
            var consumed = await App.Services.InjectDanmakuAsync($"点歌 {text}")
                || await App.Services.InjectDanmakuAsync(text);
            StatusText.Text = consumed ? $"已把「{text}」加入队列" : $"加入队列失败（未识别）：{text}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加入队列失败：{ex.Message}";
        }
    }

    /// <summary>把该点歌记录追加到空闲歌单末尾并热生效（AdminPage 同款 ReloadIdleSongsAsync 流程）。</summary>
    private async Task AddIdleFromHistoryAsync(SongRequest song)
    {
        try
        {
            var idle = (await App.Services.Storage.LoadIdleSongsAsync()).ToList();
            idle.Add(new IdleSong { Name = song.SongName, Singer = song.Singer });
            await App.Services.Queue.ReloadIdleSongsAsync(idle);
            StatusText.Text = $"已把「{song.SongName}」加入空闲歌单";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加入空闲歌单失败：{ex.Message}";
        }
    }

    /// <summary>按歌名加入歌曲黑名单（精确规则，弹幕侧立即生效；重复规则由 AddRuleAsync 幂等返回）。</summary>
    private async Task BanSongFromHistoryAsync(SongRequest song)
    {
        try
        {
            var changed = await App.Services.Blacklist.AddRuleAsync(song.SongName);
            StatusText.Text = changed ? $"已拉黑歌曲「{song.SongName}」" : "规则已存在";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"拉黑失败：{ex.Message}";
        }
    }

    // ---- 排队历史快捷操作 ----

    /// <summary>
    /// 把排队复合键 "{Platform}:{RoomId}:{UserId}" 拆分后置为管理员
    /// （QueueUpModule.cs 同款复合键格式；SetUserAdminAsync 只写 is_admin，用户不存在返回 null）。
    /// </summary>
    private async Task SetAdminFromHistoryAsync(QueueUpEntry entry)
    {
        var parts = entry.UserId.Split(':');
        if (parts.Length != 3)
        {
            StatusText.Text = $"无法识别用户标识：{entry.UserId}";
            return;
        }

        try
        {
            var user = await App.Services.Storage.SetUserAdminAsync(parts[0], parts[1], parts[2], admin: true);
            StatusText.Text = user is null
                ? "用户尚无记录，先等其点歌或出现在用户表后再设置"
                : $"已把 {entry.Nickname} 设为管理员";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"设置管理员失败：{ex.Message}";
        }
    }
}
