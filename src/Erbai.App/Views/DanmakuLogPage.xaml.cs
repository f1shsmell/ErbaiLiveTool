using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Queue;
using Erbai.Core.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Erbai.App.Views;

/// <summary>日志行类别（弹幕 / 礼物 / 关注 / 点赞 / 工具 / 其他）。</summary>
public enum DanmakuLogCategory
{
    Danmaku,
    Gift,
    Follow,
    Like,
    Tool,
    Other,
}

/// <summary>
/// 日志页（评审第一轮 #7 改版）：
/// 结构化列（时间｜类型｜来源｜事件/内容）+ 类型多选筛选 + 来源单选筛选 +
/// 搜索 + 自动滚动开关。Line 保留完整带前缀文本（概览预览列沿用）。
/// </summary>
public sealed partial class DanmakuLogPage : Page
{
    private const int MaxEntries = 1000;

    /// <summary>全量日志行（筛选依据；超限裁剪）。</summary>
    private readonly List<DanmakuLogLine> _allLines = [];

    private readonly ObservableCollection<DanmakuLogLine> _lines = [];
    private CancellationTokenSource? _cts;
    private Subscription<LiveEvent>? _liveSub;
    private Subscription<LogEntry>? _logSub;
    private Subscription<QueueEventEnvelope>? _queueSub;

    /// <summary>历史加载版本号：每次 Reload 自增；后台历史完成后回 UI 时只认最新版本。
    /// 页面重入会再次 Reload，旧的一批正在加载的历史必须作废——否则快速切页反复
    /// Reload 会把多批相同当日历史叠加追加进 `_allLines`，日志重复显示且无谓卡顿。</summary>
    private int _historyVersion;

    public DanmakuLogPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _lines;

        // 生命周期配对：Loaded 每次进入统一 重载历史 + 重新订阅 + 定位最新；
        // Unloaded 退订并取消循环（阶段 6 防僵尸循环；QueueUpPage 同款模式）。
        // 原来在构造函数里一次性订阅——切到其他导航页回来时（Frame 新建/复用页面）
        // 订阅已取消且不重读落盘历史，弹幕事件"又消失了"。
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => StopSubscriptions();
    }

    /// <summary>进入页面统一入口：先停旧订阅（幂等）→ 后台读历史 → 订阅 → 滚到底。
    /// 历史文件读取+解析放到后台线程：日志文件可能很大，在 Loaded（UI 线程）里
    /// 同步全量载入+JSON 解析会导致打开页面卡顿（用户实测"打开日志会卡很久"）。</summary>
    private void Reload()
    {
        StopSubscriptions();
        // 每次进入页面指定新的加载版本：这次的历史加载结果才被采纳，更早的（尚未完成的）作废。
        var version = ++_historyVersion;
        _ = LoadHistoryAsync(version);
        StartSubscriptions();

        // 定位到最新（用户诉求：每次点进都要拖到底部很麻烦）。构造/重载时 ListView
        // 尚未布局，ScrollIntoView 无效——DispatcherQueue 延后一帧确保 ItemContainer 已生成。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (AutoScrollToggle.IsOn && LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        });
    }

    /// <summary>
    /// 后台读当日直播事件历史 + 系统日志历史（各自取尾部 ≤MaxEntries 行），
    /// 完成后切回 UI 批量追加。**后台只做文件读取与 JSON 解析（纯数据），
    /// DanmakuLogLine 含 SolidColorBrush（WinUI 线程亲和）一律在 UI 线程构造。**
    /// 退出页面前后台任务仍可能完成，追加到 `_allLines` 无害（页面复用时数据保留）；
    /// 页面重入时再次 Reload 会产生更大版本号，本次（旧版本）结果在回 UI 时被丢弃，
    /// 只有最新一次加载真正落地（见 <see cref="_historyVersion"/>）。
    /// </summary>
    private async Task LoadHistoryAsync(int version)
    {
        var (events, fileLines, errors) = await Task.Run(() =>
        {
            var events = new List<LiveEvent>();
            var errors = new List<(string Text, LogLevel Level)>();
            LoadLiveHistoryData(events, errors);
            var fileLines = LoadFileHistoryData(errors);
            return (events, fileLines, errors);
        });

        DispatcherQueue.TryEnqueue(() =>
        {
            // 已被更新的 Reload 取代：本次结果作废，避免多批相同历史重复追加
            if (version != _historyVersion)
            {
                return;
            }

            var batch = new List<DanmakuLogLine>();
            foreach (var evt in events)
            {
                batch.Add(ToLine(evt));
            }

            foreach (var raw in fileLines)
            {
                batch.Add(BuildFileLogLine(raw));
            }

            foreach (var (text, level) in errors)
            {
                batch.Add(BuildErrorLogLine(text, level));
            }

            AppendBatch(batch);
        });
    }

    /// <summary>构造系统日志文件行（UI 线程；颜色按行内级别标记）。</summary>
    private static DanmakuLogLine BuildFileLogLine(string line)
    {
        var color = line.Contains("[WRN]", StringComparison.Ordinal) ? LogColorFor(LogLevel.Warning)
            : line.Contains("[ERR]", StringComparison.Ordinal) ? LogColorFor(LogLevel.Error)
            : LogColorFor(LogLevel.Information);
        return new DanmakuLogLine(line, color, DanmakuLogCategory.Tool)
        {
            Time = ExtractTime(line),
            TypeText = "工具",
            SourceText = "系统",
            Detail = line,
        };
    }

    /// <summary>构造错误/提示行（UI 线程；读取失败、未找到文件等）。</summary>
    private static DanmakuLogLine BuildErrorLogLine(string text, LogLevel level) =>
        new($"[错误] {text}", LogColorFor(level), DanmakuLogCategory.Tool)
        {
            TypeText = "工具",
            SourceText = "系统",
            Detail = text,
        };

    private void StartSubscriptions()
    {
        _cts = new CancellationTokenSource();
        var services = App.Services;

        _liveSub = services.Bus.Subscribe<LiveEvent>(capacity: 1024);
        var live = _liveSub;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in live.Reader.ReadAllAsync(_cts!.Token))
                {
                    DispatcherQueue.TryEnqueue(() => Append(ToLine(evt)));
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        _logSub = services.Logs.Subscribe(capacity: 1024);
        var logs = _logSub;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in logs.Reader.ReadAllAsync(_cts!.Token))
                {
                    var ts = entry.Timestamp.ToLocalTime();
                    DispatcherQueue.TryEnqueue(() => Append(new DanmakuLogLine(
                        $"[{ts:HH:mm:ss}] [系统] {entry.Message}",
                        LogColorFor(entry.Level),
                        DanmakuLogCategory.Tool)
                    {
                        Time = $"{ts:HH:mm:ss}",
                        TypeText = "工具",
                        SourceText = "系统",
                        Detail = entry.Message,
                    }));
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // 工具事件：点歌流水（入队/播放/完成/跳过/失败原因等）——docs/00 修复记录 #18
        _queueSub = services.Bus.Subscribe<QueueEventEnvelope>(capacity: 256);
        var queue = _queueSub;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in queue.Reader.ReadAllAsync(_cts!.Token))
                {
                    var ts = envelope.Timestamp.ToLocalTime();
                    DispatcherQueue.TryEnqueue(() => Append(new DanmakuLogLine(
                        $"[{ts:HH:mm:ss}] [队列] {FormatQueue(envelope)}",
                        LogColorFor(LogLevel.Information),
                        DanmakuLogCategory.Tool)
                    {
                        Time = $"{ts:HH:mm:ss}",
                        TypeText = "工具",
                        SourceText = "队列",
                        Detail = FormatQueue(envelope),
                    }));
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>在资源管理器中打开日志目录（logs/，随附文件语义见 AppPaths；不存在则先创建）。</summary>
    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(AppPaths.HostDir, "logs");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Services.Logs.Log(Erbai.Contracts.Logging.LogLevel.Error, $"打开日志目录失败：{ex.Message}");
        }
    }

    private void StopSubscriptions()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _liveSub?.Dispose();
        _liveSub = null;
        _logSub?.Dispose();
        _logSub = null;
        _queueSub?.Dispose();
        _queueSub = null;
    }

    /// <summary>颜色用浅色背景色板（<see cref="LiveEventFormat.LightColorFor"/>，深色文字白底可读）。</summary>
    private static DanmakuLogLine ToLine(LiveEvent evt)
    {
        var ts = evt.Timestamp.ToLocalTime();
        var full = LiveEventFormat.Format(evt);
        return new DanmakuLogLine(full, LiveEventFormat.LightColorFor(evt), LiveEventFormat.CategoryFor(evt.Kind))
        {
            Time = $"{ts:HH:mm:ss}",
            TypeText = CategoryTextFor(LiveEventFormat.CategoryFor(evt.Kind)),
            SourceText = LiveEventFormat.PlatformText(evt.Platform),
            Detail = LiveEventFormat.Body(evt),
        };
    }

    /// <summary>
    /// 读取当日直播事件落盘文件（erbai-live-YYYYMMDD.log，LiveEventLogWriter 写入；JSON 行）
    /// 尾部作为回看——直播事件原本只走内存广播，事后打开日志页永远看不到；
    /// 落盘后这里把当天事件还原为纯数据（LiveEvent），供 UI 线程转结构化行
    /// （docs/00 修复记录）。**后台线程调用：只读文件+JSON 解析，不碰任何 UI 对象**
    /// （DanmakuLogLine 含 SolidColorBrush，WinUI 线程亲和，一律留在 UI 线程构造）。
    /// </summary>
    private static void LoadLiveHistoryData(List<LiveEvent> events, List<(string Text, LogLevel Level)> errors)
    {
        try
        {
            var directory = Path.Combine(AppPaths.HostDir, "logs");
            var path = Path.Combine(directory, $"{LiveEventLogWriter.FilePrefix}{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var raw in ReadTailLinesShared(path, MaxEntries))
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var evt = LiveEventLogWriter.TryParseLine(raw);
                if (evt is not null)
                {
                    events.Add(evt);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(($"读取直播事件历史失败：{ex.Message}", LogLevel.Error));
        }
    }

    /// <summary>
    /// 读取当日系统日志文件尾部（按天滚动 logs/erbai-YYYYMMDD.log）作为初始内容。
    /// **后台线程调用：只返回原始行（纯 string），颜色/时间/DanmakuLogLine 由
    /// UI 线程 <see cref="BuildFileLogLine"/> 构造（WinUI 线程亲和）。**
    /// 缺失或出错时把原因写入 <paramref name="errors"/>（页面上必须可见，否则
    /// 又是"日志是空的"无从排查）。
    /// </summary>
    private static List<string> LoadFileHistoryData(List<(string Text, LogLevel Level)> errors)
    {
        var fileLines = new List<string>();
        try
        {
            var directory = Path.Combine(AppPaths.HostDir, "logs");
            var path = Path.Combine(directory, $"erbai-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(path))
            {
                errors.Add(($"未找到日志历史文件：{path}", LogLevel.Warning));
                return fileLines;
            }

            foreach (var line in ReadTailLinesShared(path, MaxEntries))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    fileLines.Add(line);
                }
            }
        }
        catch (Exception ex)
        {
            // 历史加载失败不能静默：页面上直接显示原因（否则又是"日志是空的"无从排查）
            errors.Add(($"读取日志历史失败：{ex.Message}", LogLevel.Error));
        }

        return fileLines;
    }

    /// <summary>
    /// 从 Serilog 行提取时间。Serilog 默认模板行首为
    /// "yyyy-MM-dd HH:mm:ss.fff zzz"（如 2026-09-03 11:22:10.299 +08:00），时间戳
    /// 不在方括号内——旧实现用 IndexOf('[') 会把紧邻的 [WRN] 级别误当"时间"（用户
    /// 实测 2026-09-03："时间栏显示 WRN"）。这里优先按行首时间戳解析，取 HH:mm:ss；
    /// 兼容个别手写 [HH:mm:ss] 前缀格式（回退旧逻辑）。
    /// </summary>
    private static string ExtractTime(string line)
    {
        var serilog = Regex.Match(line, @"^\d{4}-\d{2}-\d{2}\s+(\d{2}:\d{2}:\d{2})");
        if (serilog.Success)
        {
            return serilog.Groups[1].Value;
        }

        var start = line.IndexOf('[');
        if (start >= 0)
        {
            var end = line.IndexOf(']', start + 1);
            if (end > start && end - start <= 12)
            {
                return line[(start + 1)..end];
            }
        }

        return "";
    }

    /// <summary>
    /// 只读日志文件尾部 ≤maxLines 行（共享句柄，兼容 Serilog 写入）。
    /// 旧实现 <c>ReadAllLinesShared</c> 先全量读入再裁剪：当日日志可能很大
    /// （直播事件/系统日志一天可积累数十万行），打开日志页时全文件 I/O +
    /// 逐行处理是"打开卡很久"的主因之一（docs/00 修复记录 #20 基础上再优化）。
    /// 这里委托 <see cref="LogFileTail.ReadTailLinesShared"/>：从文件尾部向前
    /// 分块扫换行，攒够 maxLines 即停，只解码尾部窗口（纯逻辑已抽离可单测）。
    /// </summary>
    private static List<string> ReadTailLinesShared(string path, int maxLines) =>
        LogFileTail.ReadTailLinesShared(path, maxLines);

    private void Append(DanmakuLogLine line)
    {
        _allLines.Add(line);
        while (_allLines.Count > MaxEntries)
        {
            _allLines.RemoveAt(0);
        }

        ApplyFilter();
        CountText.Text = $"{_lines.Count} 条";
        if (AutoScrollToggle.IsOn && LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    /// <summary>
    /// 批量追加（历史加载用）：一次入列 + 一次裁剪 + 一次过滤 + 一次滚动。
    /// 旧实现逐条调 <see cref="Append"/>——打开日志页时数千行历史会触发数千次
    /// ApplyFilter（每次遍历全量）与滚动，"打开卡很久"的另一主因。
    /// </summary>
    private void AppendBatch(IReadOnlyList<DanmakuLogLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        _allLines.AddRange(lines);
        while (_allLines.Count > MaxEntries)
        {
            _allLines.RemoveAt(0);
        }

        ApplyFilter();
        CountText.Text = $"{_lines.Count} 条";
        if (AutoScrollToggle.IsOn && LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    // ── 筛选（评审第一轮 #7：类型多选、来源单选、搜索；全选为动作不再与多选冲突） ──

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        CountText.Text = $"{_lines.Count} 条";
    }

    private void OnFilterToggled(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
        CountText.Text = $"{_lines.Count} 条";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var name in new[] { "FilterDanmakuBtn", "FilterGiftBtn", "FilterFollowBtn", "FilterLikeBtn", "FilterToolBtn", "FilterOtherBtn" })
        {
            (FindName(name) as ToggleButton)!.IsChecked = true;
        }

        ApplyFilter();
        CountText.Text = $"{_lines.Count} 条";
    }

    /// <summary>来源单选组：点击项置为选中，其余取消（互斥）。</summary>
    private void OnSourceSingleSelect(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked)
        {
            return;
        }

        clicked.IsChecked = true;
        foreach (var name in new[] { "SourceAllBtn", "SourceBiliBtn", "SourceDouyinBtn", "SourceSystemBtn" })
        {
            if (!ReferenceEquals(FindName(name), clicked) && FindName(name) is ToggleButton other)
            {
                other.IsChecked = false;
            }
        }

        ApplyFilter();
        CountText.Text = $"{_lines.Count} 条";
    }

    /// <summary>按当前筛选重建可见列表（类型多选 + 来源单选 + 搜索词）。</summary>
    private void ApplyFilter()
    {
        var keyword = SearchBox.Text?.Trim() ?? "";
        var hasKeyword = keyword.Length > 0;
        var visible = new List<DanmakuLogLine>(_allLines.Count);
        foreach (var line in _allLines)
        {
            if (IsCategoryVisible(line.Category) && IsSourceVisible(line.SourceText) &&
                (!hasKeyword || line.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                 line.Line.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                visible.Add(line);
            }
        }

        _lines.Clear();
        foreach (var line in visible)
        {
            _lines.Add(line);
        }
    }

    private bool IsCategoryVisible(DanmakuLogCategory category) => category switch
    {
        DanmakuLogCategory.Danmaku => FilterDanmakuBtn.IsChecked == true,
        DanmakuLogCategory.Gift => FilterGiftBtn.IsChecked == true,
        DanmakuLogCategory.Follow => FilterFollowBtn.IsChecked == true,
        DanmakuLogCategory.Like => FilterLikeBtn.IsChecked == true,
        DanmakuLogCategory.Tool => FilterToolBtn.IsChecked == true,
        _ => FilterOtherBtn.IsChecked == true,
    };

    private bool IsSourceVisible(string source)
    {
        if (SourceBiliBtn.IsChecked == true)
        {
            return source == "B站";
        }

        if (SourceDouyinBtn.IsChecked == true)
        {
            return source == "抖音";
        }

        if (SourceSystemBtn.IsChecked == true)
        {
            return source is "系统" or "队列";
        }

        return true; // 全部
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _allLines.Clear();
        _lines.Clear();
        CountText.Text = "0 条";
    }

    private static string CategoryTextFor(DanmakuLogCategory category) => category switch
    {
        DanmakuLogCategory.Danmaku => "弹幕",
        DanmakuLogCategory.Gift => "礼物",
        DanmakuLogCategory.Follow => "关注",
        DanmakuLogCategory.Like => "点赞",
        DanmakuLogCategory.Tool => "工具",
        _ => "其他",
    };

    /// <summary>点歌流水格式化（queue.* 事件 → 工具日志行；docs/00 修复记录 #18）。</summary>
    private static string FormatQueue(QueueEventEnvelope envelope)
    {
        var data = envelope.Data;
        var request = data.Request;
        var song = request is null
            ? ""
            : $"{request.SongName}{(string.IsNullOrWhiteSpace(request.Singer) ? "" : $" - {request.Singer}")}";
        var action = envelope.Event switch
        {
            "queue.added" => "点歌入队",
            "queue.searching" => "点歌搜索中",
            "queue.dispatched" => "点歌开始播放",
            "queue.completed" => "点歌完成",
            "queue.skipped" => "点歌已跳过",
            "queue.cancelled" => "点歌已取消",
            "queue.failed" => "点歌失败",
            "queue.rejected" => "点歌被拒",
            "queue.started" => "队列启动",
            _ => envelope.Event,
        };
        var detail = song.Length > 0 ? $"：{song}" : "";
        if (data.Decision is { } decision && decision.Message.Length > 0)
        {
            detail += $"（{decision.Message}）";
        }

        if (!string.IsNullOrWhiteSpace(data.Reason))
        {
            detail += $"（{data.Reason}）";
        }

        return $"{action}{detail}";
    }

    /// <summary>系统/工具行文字色（浅色背景可读：错误深红、警告深琥珀、普通深灰）。</summary>
    private static Brush LogColorFor(LogLevel level) => level switch
    {
        LogLevel.Error or LogLevel.Fatal => Solid("#C62828"),
        LogLevel.Warning => Solid("#B26A00"),
        _ => Solid("#616161"),
    };

    private static Brush Solid(string hex) =>
        new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)));
}

/// <summary>
/// 日志展示行（XAML x:DataType 绑定需公开顶级类型）。
/// Line 为完整带前缀文本（概览事件预览沿用）；Time/TypeText/SourceText/Detail 供结构化列绑定。
/// </summary>
public sealed record DanmakuLogLine(string Line, Brush Color, DanmakuLogCategory Category)
{
    public string Time { get; init; } = "";

    public string TypeText { get; init; } = "";

    public string SourceText { get; init; } = "";

    public string Detail { get; init; } = "";
}