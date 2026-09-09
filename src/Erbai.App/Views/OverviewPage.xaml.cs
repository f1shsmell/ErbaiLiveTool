using System.Collections.ObjectModel;
using Erbai.App.Services;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Live;
using Erbai.Contracts.Queue;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Erbai.App.Views;

public sealed partial class OverviewPage : Page
{
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private readonly Subscription<QueueEventEnvelope> _queueSub;
    private readonly Subscription<Erbai.Contracts.QueueUp.QueueUpEventEnvelope> _queueUpSub;

    // 实时事件预览（LiveEventFormat 与日志页共用）
    private const int PreviewMax = 100;
    private readonly ObservableCollection<DanmakuLogLine> _previewLines = [];
    private Subscription<LiveEvent>? _livePreviewSub;

    // 概览主区列表（队列/排队前几条，与 QueuePage/QueueUpPage 共用展示投影）
    private readonly ObservableCollection<QueueDisplay> _queueItems = [];
    private readonly ObservableCollection<QueueUpDisplay> _queueUpItems = [];

    public OverviewPage()
    {
        InitializeComponent();
        var services = App.Services;
        RoomIdBox.Text = services.Config.Settings.RoomId;
        DouyinRoomBox.Text = string.Join(",", services.Config.Settings.DouyinRoomIds);
        UpdateBiliStatus();
        UpdateDouyinStatus();
        PlayerText.Text = services.Player is null
            ? "未接入播放器"
            : $"{services.Player.DisplayName}（{services.Player.Key}）";
        QueueList.ItemsSource = _queueItems;
        QueueUpList.ItemsSource = _queueUpItems;

        // 审计 T6-1：反复进入概览页会累积订阅表项与常驻任务（页面被订阅/闭包强引用无法回收）。
        Unloaded += (_, _) =>
        {
            _cts.Cancel();
            _queueSub?.Dispose();
            _queueUpSub?.Dispose();
            _livePreviewSub?.Dispose();
            _cts.Dispose();
        };

        _queueSub = services.Bus.Subscribe<QueueEventEnvelope>(capacity: 64);
        var events = _queueSub.Reader;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in events.ReadAllAsync(_cts.Token))
                {
                    if (envelope.Data.QueueSnapshot is { } snapshot)
                    {
                        DispatcherQueue.TryEnqueue(() => Render(snapshot));
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // 排队人数（阶段 5）：订阅 queueup.* 事件刷新
        _queueUpSub = services.Bus.Subscribe<Erbai.Contracts.QueueUp.QueueUpEventEnvelope>(capacity: 64);
        var queueUpEvents = _queueUpSub.Reader;
        var initialUp = services.QueueUp?.Service.Snapshot();
        QueueUpStatsText.Text = initialUp is null ? "0 / 0" : $"{initialUp.Total} / {initialUp.MaxEntries}";
        RenderQueueUp(initialUp);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in queueUpEvents.ReadAllAsync(_cts.Token))
                {
                    if (envelope.Data.Snapshot is { } snapshot)
                    {
                        DispatcherQueue.TryEnqueue(() => RenderQueueUp(snapshot));
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // 初始快照：事件驱动页面切走再回来只有新事件才渲染——首次进入/往返
        // 时若有队列内容, 概览应直接显示（QueuePage 同款修复）
        _ = LoadInitialSnapshotAsync(services);

        // 实时事件预览：订阅 live.* 事件流，滚动显示最近 N 条
        _livePreviewSub = services.Bus.Subscribe<LiveEvent>(capacity: 512);
        var livePreview = _livePreviewSub;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in livePreview.Reader.ReadAllAsync(_cts.Token))
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _previewLines.Add(new DanmakuLogLine(
                            LiveEventFormat.Format(evt), LiveEventFormat.LightColorFor(evt), LiveEventFormat.CategoryFor(evt.Kind)));
                        while (_previewLines.Count > PreviewMax)
                        {
                            _previewLines.RemoveAt(0);
                        }

                        PreviewCountText.Text = $"{_previewLines.Count} 条";
                        if (PreviewList.Items.Count > 0)
                        {
                            PreviewList.ScrollIntoView(PreviewList.Items[^1]);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    // ── 平台状态（评审第一轮 #8：统一连接状态：文字 + 圆点颜色 + 详情 + 启停按钮） ──

    private void UpdateBiliStatus()
    {
        var services = App.Services;
        var state = ConnectionStateMapper.ForBilibili(services);
        BiliBadge.Set(state);
        BiliDetailText.Text = state switch
        {
            ConnectionState.Connected => "监听中",
            ConnectionState.Error => services.BilibiliStatus,
            ConnectionState.NotConfigured => "请先填写房间号",
            _ => "未启动",
        };
        var running = services.BilibiliRunning;
        BiliStartButton.IsEnabled = !running;
        BiliStopButton.IsEnabled = running;
    }

    private void UpdateDouyinStatus()
    {
        var services = App.Services;
        var state = ConnectionStateMapper.ForDouyin(services);
        DouyinBadge.Set(state);
        DouyinDetailText.Text = state switch
        {
            ConnectionState.Connected => "监听中",
            ConnectionState.Error => services.DouyinStatus,
            _ => "未启动",
        };
        var running = services.DouyinRunning;
        DouyinStartButton.IsEnabled = !running;
        DouyinStopButton.IsEnabled = running;
    }

    private async void OnBiliStart(object sender, RoutedEventArgs e)
    {
        var services = App.Services;
        try
        {
            // 先把房间号/平台写入配置（B站平台），再启动
            var candidate = services.Config.Settings with
            {
                Platform = "bilibili",
                RoomId = RoomIdBox.Text.Trim(),
            };
            await services.Config.PersistAsync(candidate);
            await services.StartBilibiliAsync();
        }
        catch (Exception ex)
        {
            LogText.Text = $"启动失败：{ex.Message}";
        }

        UpdateBiliStatus();
    }

    private async void OnBiliStop(object sender, RoutedEventArgs e)
    {
        await App.Services.StopBilibiliAsync();
        UpdateBiliStatus();
    }

    private async void OnDouyinStart(object sender, RoutedEventArgs e)
    {
        var services = App.Services;
        try
        {
            // 抖音房间白名单（逗号分隔，可选）：写入配置再启动
            var roomIds = DouyinRoomBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var candidate = services.Config.Settings with
            {
                Platform = "douyin",
                DouyinRoomIds = roomIds,
            };
            await services.Config.PersistAsync(candidate);
            await services.StartDouyinAsync();
        }
        catch (Exception ex)
        {
            LogText.Text = $"启动失败：{ex.Message}";
        }

        UpdateDouyinStatus();
    }

    private async void OnDouyinStop(object sender, RoutedEventArgs e)
    {
        await App.Services.StopDouyinAsync();
        UpdateDouyinStatus();
    }

    // ── 队列/排队渲染 ──

    /// <summary>拉一次当前队列快照渲染（失败不致命, 事件流仍会刷新）。</summary>
    private async Task LoadInitialSnapshotAsync(AppServices services)
    {
        try
        {
            var snapshot = await services.Queue.GetSnapshotAsync();
            DispatcherQueue.TryEnqueue(() => Render(snapshot));
        }
        catch (Exception)
        {
        }
    }

    private void Render(QueueSnapshot snapshot)
    {
        QueueStatsText.Text = $"{snapshot.QueueTotal}/{snapshot.QueueLimit ?? 0}";
        var current = snapshot.Items.FirstOrDefault(i => i.IsCurrent);
        CurrentSongText.Text = current is null
            ? snapshot.Player.SongName ?? "暂无"
            : $"{current.Request.SongName}{(current.Request.Singer?.Length > 0 ? " - " + current.Request.Singer : "")}";

        // 队列预览：最多展示 DisplayLimit 条（默认 5；快照 Items 已按 FIFO 排序）
        var preview = snapshot.Items.Where(i => !i.IsCurrent).Take(snapshot.DisplayLimit ?? 5).ToList();
        _queueItems.Clear();
        foreach (var item in preview)
        {
            _queueItems.Add(new QueueDisplay(item));
        }

        QueueEmptyText.Visibility = snapshot.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderQueueUp(Erbai.Contracts.QueueUp.QueueUpSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        QueueUpStatsText.Text = $"{snapshot.Total} / {snapshot.MaxEntries}";
        _queueUpItems.Clear();
        foreach (var item in snapshot.Items.Take(8))
        {
            _queueUpItems.Add(new QueueUpDisplay(item));
        }

        QueueUpEmptyText.Visibility = snapshot.Total == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 手动点歌（提升到首屏；Enter 提交） ──

    private void OnDanmakuInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OnSendDanmaku(sender, e);
            e.Handled = true;
        }
    }

    private async void OnSendDanmaku(object sender, RoutedEventArgs e)
    {
        var text = DanmakuInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 手动输入无需带「点歌」前缀：未识别为弹幕命令时自动补上再走一遍（兼容旧习惯与切歌/管理命令）
        var consumed = await App.Services.InjectDanmakuAsync(text)
            || await App.Services.InjectDanmakuAsync($"点歌 {text}");
        LogText.Text = consumed
            ? $"已处理：{text}"
            : $"（未识别为点歌/命令）：{text}";
        DanmakuInput.Text = "";
    }

    private async void OnInjectTest(object sender, RoutedEventArgs e)
    {
        // 端到端冒烟：注入"点歌"（自动走提交流水线→搜索→派发→播放等待）
        var consumed = await App.Services.InjectDanmakuAsync("点歌 晴天 - 周杰伦");
        LogText.Text = consumed ? "测试弹幕已注入（点歌 晴天 - 周杰伦）" : "注入失败";
    }

    // ── 连接状态脉冲反馈已迁入 Ux.StatusBadge（观感打磨第二轮）：徽章内部
    // 在状态变化时自行播放圆点脉冲，页面后置代码只调用 badge.Set(state)。
}