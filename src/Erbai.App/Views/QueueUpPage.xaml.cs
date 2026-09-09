using System.Collections.ObjectModel;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.QueueUp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Erbai.App.Views;

/// <summary>排队条目展示投影。</summary>
public sealed record QueueUpDisplay(QueueUpItem Item)
{
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
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private Subscription<QueueUpEventEnvelope>? _subscription;

    public QueueUpPage()
    {
        InitializeComponent();
        QueueUpList.ItemsSource = _items;
        var services = App.Services;
        // 内置排队插件未加载（被禁用/缺失）时页面显示空队列与 0/0 计数，按钮操作静默忽略
        Render(services.QueueUp?.Service.Snapshot() ?? new QueueUpSnapshot());
        // 页面卸载（导航离开）必须退订并取消循环——否则反复导航累积
        // 僵尸循环渲染已卸载页面（🟡 阶段 5 修复；旧页面同款模式留阶段 6）
        Unloaded += (_, _) =>
        {
            _cts.Cancel();
            _subscription?.Dispose();
            _subscription = null;
        };
        _subscription = services.Bus.Subscribe<QueueUpEventEnvelope>(capacity: 64);
        var sub = _subscription;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in sub.Reader.ReadAllAsync(_cts.Token))
                {
                    if (envelope.Data.Snapshot is { } snapshot)
                    {
                        DispatcherQueue.TryEnqueue(() => Render(snapshot));
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void Render(QueueUpSnapshot snapshot)
    {
        _items.Clear();
        foreach (var item in snapshot.Items)
        {
            _items.Add(new QueueUpDisplay(item));
        }

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

    private static long? SelectedEntryId(Page page) =>
        (page.FindName("QueueUpList") as ListView)?.SelectedItem is QueueUpDisplay display
            ? display.Item.Entry.Id
            : null;

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
        if (SelectedEntryId(this) is { } id && App.Services.QueueUp is not null)
        {
            await App.Services.QueueUp.Service.CancelEntryAsync(id);
        }
    }
}
