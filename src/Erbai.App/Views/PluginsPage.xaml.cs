using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Erbai.App.Services;
using Erbai.Contracts.Configuration;
using Erbai.Core.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Erbai.App.Views;

/// <summary>礼物插队规则展示投影。</summary>
public sealed record QueueUpRuleDisplay(QueueUpRuleConfig Rule)
{
    public string Description => Rule.MatchKind == "gift_name"
        ? $"礼物「{Rule.MatchValue}」→ {(Rule.Action == "insert_at" ? $"插队到第 {Rule.Position} 位" : "授予入队资格")}"
        : $"电池 ≥ {Rule.MatchValue} → {(Rule.Action == "insert_at" ? $"插队到第 {Rule.Position} 位" : "授予入队资格")}";
}

/// <summary>目录式插件配置项展示投影（只读展示；普通 setter 以满足 WinUI x:Bind 生成器）。</summary>
public sealed class PluginConfigItemDisplay
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Summary => $"{Name}（{Key}） [{Type}] = {Value}";
}

/// <summary>目录式插件行展示投影（实现 INotifyPropertyChanged 供 x:Bind OneWay 同步
    /// ——启停后按项更新属性（不重建列表），避免全量重建引发的容器复用/延迟 Toggled
    /// 连锁（用户实测闪退根因，2026-08-28）。</summary>
public sealed class PluginDirectoryDisplay : INotifyPropertyChanged
{
    private string _directoryKey = string.Empty;
    private string _displayName = string.Empty;
    private string _metaText = string.Empty;
    private string _description = string.Empty;
    private string _statusText = string.Empty;
    private Brush _statusBrush = SolidColorBrushHelper.Neutral;
    private bool _isEnabled = true;
    private bool _hasDescription;
    private bool _isFailed;
    private string _error = string.Empty;
    private bool _hasConfig;
    private IReadOnlyList<PluginConfigItemDisplay> _config = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DirectoryKey { get => _directoryKey; set => Set(ref _directoryKey, value); }

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

    /// <summary>版本 · 开发者 · 注册模块键（按可用项拼接）。</summary>
    public string MetaText { get => _metaText; set => Set(ref _metaText, value); }

    public string Description { get => _description; set => Set(ref _description, value); }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    /// <summary>启停开关初值（config.ini Enabled；解析失败的目录默认显示为开但不写入）。</summary>
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

    public bool HasDescription { get => _hasDescription; set => Set(ref _hasDescription, value); }

    public bool IsFailed { get => _isFailed; set => Set(ref _isFailed, value); }

    public string Error { get => _error; set => Set(ref _error, value); }

    public bool HasConfig { get => _hasConfig; set => Set(ref _hasConfig, value); }

    public IReadOnlyList<PluginConfigItemDisplay> Config { get => _config; set => Set(ref _config, value); }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>把另一投影的全部字段同步到本项（供 RefreshPlugins 按项更新, 不打乱集合）。</summary>
    public void ApplyFrom(PluginDirectoryDisplay src)
    {
        DirectoryKey = src.DirectoryKey;
        DisplayName = src.DisplayName;
        MetaText = src.MetaText;
        Description = src.Description;
        StatusText = src.StatusText;
        StatusBrush = src.StatusBrush;
        IsEnabled = src.IsEnabled;
        HasDescription = src.HasDescription;
        IsFailed = src.IsFailed;
        Error = src.Error;
        HasConfig = src.HasConfig;
        Config = src.Config;
    }

    public static PluginDirectoryDisplay From(PluginDirectoryInfo info)
    {
        var manifest = info.Manifest;
        var description = manifest?.Description ?? string.Empty;
        var meta = string.Join(" · ", new[]
        {
            manifest is null ? null : $"v{manifest.Version}",
            manifest?.Developer,
            info.RegisteredKeys.Count == 0 ? null : $"模块：{string.Join("、", info.RegisteredKeys)}",
        }.Where(p => !string.IsNullOrWhiteSpace(p)));

        var (statusText, brush) = info.Status switch
        {
            PluginDirectoryStatus.Loaded => ("运行中", SolidColorBrushHelper.Success),
            PluginDirectoryStatus.Disabled => ("已禁用", SolidColorBrushHelper.Neutral),
            PluginDirectoryStatus.Failed => ("加载失败", SolidColorBrushHelper.Critical),
            _ => ("已启用", SolidColorBrushHelper.Neutral),
        };

        return new PluginDirectoryDisplay
        {
            DirectoryKey = info.DirectoryKey,
            DisplayName = manifest?.Name ?? info.DirectoryKey,
            MetaText = meta,
            Description = description,
            StatusText = statusText,
            StatusBrush = brush,
            IsEnabled = manifest?.Enabled ?? true,
            HasDescription = !string.IsNullOrEmpty(description),
            IsFailed = info.Status == PluginDirectoryStatus.Failed,
            Error = info.Error,
            HasConfig = manifest is { Config.Count: > 0 },
            Config = manifest?.Config.Select(c => new PluginConfigItemDisplay
            {
                Key = c.Key,
                Name = c.Name,
                Type = c.Type,
                Value = c.Value,
            }).ToList() ?? [],
        };
    }
}

/// <summary>插件状态徽章画刷（主题资源取用）。</summary>
internal static class SolidColorBrushHelper
{
    public static Brush Success => Resource("SystemFillColorSuccessBackgroundBrush");

    public static Brush Critical => Resource("SystemFillColorCriticalBackgroundBrush");

    public static Brush Neutral => Resource("ControlFillColorDefaultBrush");

    /// <summary>强调色（连接中/进行中状态；WASDK 主题资源）。</summary>
    public static Brush Accent => Resource("AccentFillColorDefaultBrush");

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>运行态 → 徽章背景色（运行绿 / 未启动或未接入中性灰）。
/// 未启动/未接入是正常状态，不再用红色 critical（视觉语义统一）。</summary>
public sealed class RunningStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language) =>
        value is true
            ? SolidColorBrushHelper.Success
            : SolidColorBrushHelper.Neutral;

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class PluginsPage : Page
{
    private readonly ObservableCollection<QueueUpRuleDisplay> _rules = [];
    private readonly ObservableCollection<PluginDirectoryDisplay> _pluginDirs = [];

    // 插件启停防连锁（用户实测 2026-08-28 暴雷）：
    // RefreshPlugins 重建列表时, x:Bind(OneTime) 把 ToggleSwitch.IsOn 从默认值赋成
    // 绑定值, 值变化即触发 Toggled→误操作其他插件（"关闭 A 却连锁卸载 B"）→ 连锁
    // 重入最终闪退, 回弹的「已启用」还会把 ini 覆盖回 true（重启后"没关闭成功"）。
    // _suppressToggle: 程序化重建期间抑制一切 Toggled;
    // _toggleInFlight: 一次用户操作进行中 (await 期间) 忽略重入/连点。
    private bool _suppressToggle;
    private bool _toggleInFlight;

    // x:Bind 属性（INotifyPropertyChanged 不引入,页面短生命周期,重进页面即刷新）
    public bool BilibiliRunning => App.Services.BilibiliRunning;
    public bool DouyinRunning => App.Services.DouyinRunning;
    public bool PlayerConnected => App.Services.Player is not null;
    public string PluginRootText => App.Services.PluginRoot;
    public ObservableCollection<PluginDirectoryDisplay> PluginDirectories => _pluginDirs;

    // 统一连接状态（评审第一轮 #8）：平台/播放器行圆点+文字由 x:Bind 转换器呈现
    public ConnectionState BiliState => ConnectionStateMapper.ForBilibili(App.Services);

    public ConnectionState DouyinState => ConnectionStateMapper.ForDouyin(App.Services);

    public ConnectionState PlayerState => ConnectionStateMapper.ForPlayer(App.Services);

    public PluginsPage()
    {
        InitializeComponent();
        var settings = App.Services.Config.Settings;

        // 目录式插件（Plugins/）视图
        DirectoryPluginsList.ItemsSource = _pluginDirs;
        RefreshPlugins();

        // 直播平台 / 播放器状态（统一连接状态 #8：圆点+文字由 x:Bind 呈现，此处只填详情）
        BiliDetailText.Text = BiliState == ConnectionState.Connected
            ? "正在监听 B站弹幕"
            : $"去概览页启动 · {App.Services.BilibiliStatus}";
        DouyinDetailText.Text = DouyinState == ConnectionState.Connected
            ? "正在监听抖音弹幕"
            : $"去概览页启动 · {App.Services.DouyinStatus}";
        var player = App.Services.Player;
        PlayerNameText.Text = player is null ? "未接入" : $"{player.DisplayName}（{player.Key}）";
        PlayerDetailText.Text = player is null
            ? "未找到 Erbai.Connector.exe 或激活失败——点歌将按估算时长播放；连接器随应用发布,若持续缺失请重新安装"
            : $"连接器密钥 {player.Key}；在设置页更改播放器（重启后生效）";

        // 功能模块
        QueueUpEnabledToggle.IsOn = settings.QueueUp.Enabled;
        QueueUpMaxBox.Value = settings.QueueUp.MaxEntries;
        QueueUpGateToggle.IsOn = settings.QueueUp.EligibilityGate;
        GiftFxEnabledToggle.IsOn = settings.GiftFx.Enabled;
        GiftFxDurationBox.Value = settings.GiftFx.DurationSeconds;
        RulesList.ItemsSource = _rules;
        foreach (var rule in settings.QueueUp.Rules)
        {
            _rules.Add(new QueueUpRuleDisplay(rule));
        }

        // 搜索源
        KugouProviderCheck.IsChecked = settings.Providers.Enabled.Contains("kugou");
        NeteaseProviderCheck.IsChecked = settings.Providers.Enabled.Contains("netease");
        QQMusicProviderCheck.IsChecked = settings.Providers.Enabled.Contains("qqmusic");
    }

    private void OnModuleToggled(object sender, RoutedEventArgs e)
    {
        // 开关即时写配置（模块侧热生效）;容量/时长/规则仍走"保存模块设置"
        var services = App.Services;
        var candidate = services.Config.Settings with
        {
            QueueUp = services.Config.Settings.QueueUp with
            {
                Enabled = QueueUpEnabledToggle.IsOn,
            },
            GiftFx = services.Config.Settings.GiftFx with
            {
                Enabled = GiftFxEnabledToggle.IsOn,
            },
        };
        _ = PersistQuietlyAsync(candidate, ModuleSaveStatus, "模块开关已保存（热生效）");
    }

    private async Task PersistQuietlyAsync(AppConfig candidate, TextBlock status, string okMessage)
    {
        try
        {
            await App.Services.Config.PersistAsync(candidate);
            status.Text = okMessage;
        }
        catch (Exception ex)
        {
            status.Text = $"保存失败：{ex.Message}";
        }
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var matchKind = (RuleMatchKindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gift_name";
        var action = (RuleActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "insert_at";
        var rule = new QueueUpRuleConfig
        {
            MatchKind = matchKind,
            MatchValue = RuleMatchValueBox.Text.Trim(),
            Action = action,
            Position = (int)RulePositionBox.Value,
        };
        if (rule.MatchValue.Length == 0)
        {
            ModuleSaveStatus.Text = "规则未添加：请填写礼物名或电池阈值";
            return;
        }

        _rules.Add(new QueueUpRuleDisplay(rule));
        RuleMatchValueBox.Text = "";
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (RulesList.SelectedItem is QueueUpRuleDisplay display)
        {
            _rules.Remove(display);
        }
    }

    private async void OnSaveModules(object sender, RoutedEventArgs e)
    {
        try
        {
            var services = App.Services;
            var candidate = services.Config.Settings with
            {
                QueueUp = new QueueUpConfig
                {
                    Enabled = QueueUpEnabledToggle.IsOn,
                    MaxEntries = (int)QueueUpMaxBox.Value,
                    EligibilityGate = QueueUpGateToggle.IsOn,
                    Rules = [.. _rules.Select(r => r.Rule)],
                },
                GiftFx = new GiftFxConfig
                {
                    Enabled = GiftFxEnabledToggle.IsOn,
                    DurationSeconds = (int)GiftFxDurationBox.Value,
                },
            };
            await services.Config.PersistAsync(candidate);
            ModuleSaveStatus.Text = "已保存（排队/礼物特效配置热生效）";
        }
        catch (Exception ex)
        {
            ModuleSaveStatus.Text = $"保存失败：{ex.Message}";
        }
    }

    private async void OnSaveProviders(object sender, RoutedEventArgs e)
    {
        try
        {
            var services = App.Services;
            var enabled = new List<string>();
            if (KugouProviderCheck.IsChecked == true)
            {
                enabled.Add("kugou");
            }

            if (NeteaseProviderCheck.IsChecked == true)
            {
                enabled.Add("netease");
            }

            if (QQMusicProviderCheck.IsChecked == true)
            {
                enabled.Add("qqmusic");
            }

            if (enabled.Count == 0)
            {
                ProviderStatus.Text = "至少启用一个搜索源";
                return;
            }

            var candidate = services.Config.Settings with
            {
                Providers = new ProvidersConfig { Enabled = enabled },
            };
            await services.Config.PersistAsync(candidate);
            ProviderStatus.Text = "已保存（重启后新搜索源生效）";
        }
        catch (Exception ex)
        {
            ProviderStatus.Text = $"保存失败：{ex.Message}";
        }
    }

    // ── 目录式插件管理（Stage A 遗留①） ─────────────────────────────────

    /// <summary>从 AppServices.PluginDirectories 同步插件列表（**按项更新, 不重建集合**）：
    /// 全量 Clear+Add 会让 ListView 容器复用/延迟生成——x:Bind(OneTime) 赋值 IsOn
    /// 触发 Toggled 且发生在抑制窗口之外（用户实测: 启用后自动误触发一次开关 → 连锁
    /// 闪退）。差异更新 + 源 INotifyPropertyChanged + OneWay 绑定后, 状态推送是同步的,
    /// 始终落在 _suppressToggle 抑制窗口内。</summary>
    private void RefreshPlugins()
    {
        _suppressToggle = true;
        try
        {
            var current = App.Services.PluginDirectories;

            // 逆序移除已消失的目录
            for (var i = _pluginDirs.Count - 1; i >= 0; i--)
            {
                if (current.All(d => !string.Equals(d.DirectoryKey, _pluginDirs[i].DirectoryKey, StringComparison.Ordinal)))
                {
                    _pluginDirs.RemoveAt(i);
                }
            }

            // 更新已有项 / 追加新项（保持目录序）
            var insertIndex = 0;
            foreach (var info in current)
            {
                var index = -1;
                for (var i = 0; i < _pluginDirs.Count; i++)
                {
                    if (string.Equals(_pluginDirs[i].DirectoryKey, info.DirectoryKey, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    _pluginDirs.Insert(Math.Min(insertIndex, _pluginDirs.Count), PluginDirectoryDisplay.From(info));
                }
                else if (index != insertIndex && insertIndex < _pluginDirs.Count)
                {
                    // 目录顺序变化才移动（当前只两个内置插件, 顺序稳定, 极少触发）
                    var item = _pluginDirs[index];
                    _pluginDirs.RemoveAt(index);
                    _pluginDirs.Insert(Math.Min(insertIndex, _pluginDirs.Count), item);
                }
                else
                {
                    _pluginDirs[index].ApplyFrom(PluginDirectoryDisplay.From(info));
                }

                insertIndex++;
            }

            DirectoryPluginsEmptyText.Visibility = _pluginDirs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _suppressToggle = false;
        }
    }

    private async void OnDirectoryPluginToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _toggleInFlight ||
            sender is not ToggleSwitch toggler || toggler.Tag is not string directoryKey)
        {
            return; // 程序化重建/操作进行中/非法来源：一律忽略（防连锁与重入）
        }

        _toggleInFlight = true;
        toggler.IsEnabled = false;
        try
        {
            var message = await App.Services.SetPluginEnabledAsync(directoryKey, toggler.IsOn);
            DirectoryPluginsStatusText.Text = message;
            DirectoryPluginsStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            DirectoryPluginsStatusText.Text = $"插件操作失败：{ex.Message}";
            DirectoryPluginsStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            // 重建列表（Toggle 事件期间控件随旧容器销毁，无需再恢复 IsEnabled）；
            _toggleInFlight = false;
            // 刷新异常不得逃逸 async void（UnhandledException 兜底但会显示空白）
            try
            {
                RefreshPlugins();
            }
            catch (Exception ex)
            {
                DirectoryPluginsStatusText.Text = $"刷新插件列表失败：{ex.Message}";
                DirectoryPluginsStatusText.Visibility = Visibility.Visible;
            }
        }
    }

    private void OnOpenPluginsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = App.Services.PluginRoot;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DirectoryPluginsStatusText.Text = $"打开插件目录失败：{ex.Message}";
            DirectoryPluginsStatusText.Visibility = Visibility.Visible;
        }
    }
}
