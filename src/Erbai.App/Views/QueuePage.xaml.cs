using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Erbai.App.Services;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Queue;
using Erbai.Contracts.Requests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Erbai.App.Views;

/// <summary>
/// 队列项展示投影（position + 点歌人/来源/状态 + 行操作可见性）。
/// INPC 类（2026-10 UI 轮）：渲染走差量更新——同一请求原位更新属性值，
/// x:Bind OneWay 刷新，ListView 选中与滚动位置得以保留；record + Clear/Add
/// 全量重建会在每次快照事件时丢选中、列表跳回顶部。
/// </summary>
public sealed class QueueDisplay : INotifyPropertyChanged
{
    private QueueItem _item;

    public QueueDisplay(QueueItem item) => _item = item;

    public event PropertyChangedEventHandler? PropertyChanged;

    public QueueItem Item
    {
        get => _item;
        private set
        {
            if (Equals(_item.Request.RequestId, value.Request.RequestId) &&
                Equals(_item.Position, value.Position) &&
                Equals(_item.IsCurrent, value.IsCurrent) &&
                Equals(_item.Request.Status, value.Request.Status))
            {
                return;
            }

            _item = value;
            // 展示面只投影这几个字段，全部通知（量级 ≤20 行，逐字段比对不值得）
            Raise(nameof(Item));
            Raise(nameof(Position));
            Raise(nameof(SongName));
            Raise(nameof(Singer));
            Raise(nameof(HasSinger));
            Raise(nameof(Requester));
            Raise(nameof(SourceText));
            Raise(nameof(StatusText));
            Raise(nameof(IsCurrent));
            Raise(nameof(NeedsRemoval));
        }
    }

    /// <summary>快照事件驱动的原位更新入口。</summary>
    public void Update(QueueItem item) => Item = item;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public int Position => Item.Position;

    public Erbai.Contracts.Requests.SongRequest Request => Item.Request;

    public string SongName => Item.Request.SongName;

    public string Singer => Item.Request.Singer;

    public bool HasSinger => Item.Request.Singer.Length > 0;

    /// <summary>点歌人（无昵称时退回用户标识）。</summary>
    public string Requester => Item.Request.Nickname.Length > 0
        ? Item.Request.Nickname
        : Item.Request.UserId;

    /// <summary>来源平台（bilibili → B站，douyin → 抖音，其余原样）。</summary>
    public string SourceText => Item.Request.Platform switch
    {
        "bilibili" => "B站",
        "douyin" => "抖音",
        var p => p,
    };

    /// <summary>状态中文（活动态为主）。</summary>
    public string StatusText => Item.Request.Status switch
    {
        RequestStatus.Queued => "等待中",
        RequestStatus.Searching => "搜索中",
        RequestStatus.Ready => "就绪",
        RequestStatus.Dispatched => "播放中",
        RequestStatus.Received => "已接收",
        _ => Item.Request.Status.ToString(),
    };

    public bool IsCurrent => Item.IsCurrent;

    /// <summary>状态点画刷（2026-10 布局样式轮）：播放/搜索中=强调橙，等待=中性。</summary>
    public Microsoft.UI.Xaml.Media.Brush StatusBrush => Item.Request.Status switch
    {
        RequestStatus.Dispatched or RequestStatus.Searching => SolidColorBrushHelper.Accent,
        RequestStatus.Queued => SolidColorBrushHelper.Success,
        _ => SolidColorBrushHelper.Neutral,
    };

    /// <summary>等待中的行显示「移出队列」行内操作（正在播放行显示「标记完成/跳过」）。</summary>
    public bool NeedsRemoval => !Item.IsCurrent;
}

public sealed partial class QueuePage : Page
{
    private readonly ObservableCollection<QueueDisplay> _items = [];
    private readonly List<IDisposable> _pumps = [];

    public QueuePage()
    {
        InitializeComponent();
        QueueList.ItemsSource = _items;
        var services = App.Services;
        // 初始快照：页面是事件驱动的，切走再回来只有新事件才渲染——
        // 没有初始快照时队列是静态的，回来就空白（阶段 6 修复）
        _ = LoadSnapshotAsync(services);
        _pumps.Add(new Ux.ViewPump<QueueEventEnvelope>(services.Bus, DispatcherQueue, capacity: 64,
            envelope => { if (envelope.Data.QueueSnapshot is { } snapshot) Render(snapshot); }));
    }

    private async Task LoadSnapshotAsync(AppServices services)
    {
        try
        {
            var snapshot = await services.Queue.GetSnapshotAsync();
            DispatcherQueue.TryEnqueue(() => Render(snapshot));
        }
        catch (Exception)
        {
            // 初始快照失败不致命（事件流仍会渲染）
        }
    }

    private void Render(QueueSnapshot snapshot)
    {
        // 差量更新（保选中/滚动；算法见 Ux.ObservableDiffer）
        Ux.ObservableDiffer.Apply(_items, snapshot.Items,
            i => i.Request.RequestId!.Value,
            d => d.Item.Request.RequestId!.Value,
            i => new QueueDisplay(i),
            (existing, desired) => existing.Update(desired));

        QueueEmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateOperationButtons();
    }

    // ── 按钮可用性（评审第一轮 #6：空队列/无对象时禁用） ──

    /// <summary>滑动选中变化也刷新操作按钮可用性。</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOperationButtons();

    private void UpdateOperationButtons()
    {
        var hasTarget = TargetId() is not null;
        CompleteButton.IsEnabled = hasTarget;
        SkipButton.IsEnabled = hasTarget;
        RemoveButton.IsEnabled = SelectedId() is not null;
        SelectionHint.Text = hasTarget
            ? "完成 / 跳过 作用于当前播放（或选中的歌曲）"
            : SelectedId() is null
                ? "选中歌曲可使用「移出队列」"
                : "";
    }

    private long? SelectedId() =>
        QueueList.SelectedItem is QueueDisplay display
            ? display.Item.Request.RequestId
            : null;

    /// <summary>操作目标：优先选中项；无选中时回落到当前播放（IsCurrent）。</summary>
    private long? TargetId() =>
        SelectedId()
        ?? _items.FirstOrDefault(x => x.IsCurrent)?.Item.Request.RequestId;

    // ── 底部操作栏（作用于当前播放或选中歌曲） ──

    private async void OnComplete(object sender, RoutedEventArgs e)
    {
        if (TargetId() is { } id)
        {
            await App.Services.Queue.CompleteAsync(id);
        }
    }

    private async void OnSkip(object sender, RoutedEventArgs e)
    {
        if (TargetId() is { } id)
        {
            await App.Services.Queue.SkipAsync(id);
        }
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (SelectedId() is { } id)
        {
            await App.Services.Queue.CancelAsync(id);
        }
    }

    // ── 行内操作（按行 Tag 定位，不依赖选中） ──

    private static long? RowId(object sender)
    {
        var tag = (sender as FrameworkElement)?.Tag;
        return tag is long id ? id : null;
    }

    private async void OnCompleteInline(object sender, RoutedEventArgs e)
    {
        if (RowId(sender) is { } id)
        {
            await App.Services.Queue.CompleteAsync(id);
        }
    }

    private async void OnSkipInline(object sender, RoutedEventArgs e)
    {
        if (RowId(sender) is { } id)
        {
            await App.Services.Queue.SkipAsync(id);
        }
    }

    private async void OnRemoveInline(object sender, RoutedEventArgs e)
    {
        if (RowId(sender) is { } id)
        {
            await App.Services.Queue.CancelAsync(id);
        }
    }

    // ── 手动点歌（评审第一轮 #3/#5：队列页顶部直接点歌，Enter 提交） ──

    private void OnManualSongKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OnManualSongSubmit(sender, e);
            e.Handled = true;
        }
    }

    private async void OnManualSongSubmit(object sender, RoutedEventArgs e)
    {
        var text = ManualSongBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 手动输入无需带「点歌」前缀：未识别为弹幕命令时自动补上再走一遍（兼容旧习惯与切歌/管理命令）
        var consumed = await App.Services.InjectDanmakuAsync(text)
            || await App.Services.InjectDanmakuAsync($"点歌 {text}");
        ManualStatus.Text = consumed ? "已加入队列" : "未识别为点歌/命令";
        if (consumed)
        {
            ManualSongBox.Text = "";
        }
    }
}