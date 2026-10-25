using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.QueueUp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Erbai.App.Views;

/// <summary>
/// 排队条目展示投影。INPC 类（2026-10 UI 轮）：渲染走差量更新——同一条目原位
/// 更新属性值（x:Bind OneWay 刷新），ListView 选中与滚动位置得以保留；
/// record + Clear/Add 全量重建会在每次事件时丢选中、列表跳回顶部。
/// </summary>
public sealed class QueueUpDisplay : INotifyPropertyChanged
{
    private QueueUpItem _item;

    public QueueUpDisplay(QueueUpItem item) => _item = item;

    public event PropertyChangedEventHandler? PropertyChanged;

    public QueueUpItem Item
    {
        get => _item;
        private set
        {
            if (Equals(_item.Entry.Id, value.Entry.Id) &&
                Equals(_item.Position, value.Position) &&
                string.Equals(_item.Entry.Content, value.Entry.Content, StringComparison.Ordinal))
            {
                return;
            }

            _item = value;
            Raise(nameof(Item));
            Raise(nameof(PositionText));
            Raise(nameof(TitleText));
            Raise(nameof(SubText));
        }
    }

    /// <summary>事件驱动的原位更新入口。</summary>
    public void Update(QueueUpItem item) => Item = item;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string PositionText => $"{Item.Position}.";

    public string TitleText => $"{Item.Entry.Nickname}：{(Item.Entry.Content.Length > 0 ? Item.Entry.Content : "(占位)")}";

    public string SubText => Item.Entry.Source == QueueUpSource.Gift
        ? "礼物插队"
        : $"排队于 {Item.Entry.CreatedAt.ToLocalTime():HH:mm:ss}";
}

/// <summary>排队队列页（阶段 5）：订阅 queueup.* 事件渲染 + 手动完成/取消。</summary>
public sealed partial class QueueUpPage : Page
{
    private readonly ObservableCollection<QueueUpDisplay> _items = [];
    private readonly List<IDisposable> _pumps = [];

    public QueueUpPage()
    {
        InitializeComponent();
        QueueUpList.ItemsSource = _items;
        var services = App.Services;
        // 内置排队插件未加载（被禁用/缺失）时页面显示空队列与 0/0 计数，按钮操作静默忽略
        Render(services.QueueUp?.Service.Snapshot() ?? new QueueUpSnapshot());
        _pumps.Add(new Ux.ViewPump<QueueUpEventEnvelope>(services.Bus, DispatcherQueue, capacity: 64,
            envelope => { if (envelope.Data.Snapshot is { } snapshot) Render(snapshot); }));
        Unloaded += (_, _) => { foreach (var p in _pumps) p.Dispose(); _pumps.Clear(); };
    }

    private void Render(QueueUpSnapshot snapshot)
    {
        // 差量更新（保选中/滚动；算法见 Ux.ObservableDiffer）
        Ux.ObservableDiffer.Apply(_items, snapshot.Items,
            i => i.Entry.Id,
            d => d.Item.Entry.Id,
            i => new QueueUpDisplay(i),
            (existing, desired) => existing.Update(desired));

        CountText.Text = $"{snapshot.Total}";
        CountMaxText.Text = $"/ {snapshot.MaxEntries}";
        QueueUpEmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    /// <summary>评审第一轮 #6：0/50 或未选中时禁用操作按钮（完成队首需要队列有内容，取消选中需要选中行）。</summary>
    private void UpdateButtons()
    {
        CompleteNextButton.IsEnabled = _items.Count > 0;
        CancelSelectedButton.IsEnabled = _items.Count > 0 && QueueUpList.SelectedItem is not null;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private long? SelectedEntryId() =>
        QueueUpList.SelectedItem is QueueUpDisplay display ? display.Item.Entry.Id : null;

    private async void OnCompleteNext(object sender, RoutedEventArgs e)
    {
        if (App.Services.QueueUp is null)
        {
            return; // 排队插件未加载
        }

        await App.Services.QueueUp.Service.CompleteNextAsync();
    }

    private async void OnCancelSelected(object sender, RoutedEventArgs e)
    {
        if (SelectedEntryId() is { } id && App.Services.QueueUp is not null)
        {
            await App.Services.QueueUp.Service.CancelEntryAsync(id);
        }
    }
}
