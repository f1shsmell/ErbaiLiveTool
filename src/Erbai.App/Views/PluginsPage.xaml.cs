using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Erbai.App.Services;
using Erbai.Connectors.Management;
using Erbai.Connectors.Management.Catalog;
using Erbai.Connectors.Management.Versioning;
using Erbai.Contracts.Configuration;
using Erbai.Core.Plugins;
using Erbai.Player.Connectors;
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

/// <summary>
/// 播放器连接器行展示投影（插件页"播放器连接器"区）。
/// </summary>
/// <remarks>
/// <para>
/// 两个数据源刻意分开取，因为它们回答的是不同问题、也可能互相矛盾：
/// <list type="bullet">
///   <item><description><see cref="PlayerConnectorResolution"/>（来自 <c>PlayerConnectorResolver</c>）
///   回答"<b>磁盘上现在能不能拉起来</b>"——只读盘、不联网，是本地事实。</description></item>
///   <item><description><see cref="ConnectorUpdateStatus"/>（来自 <c>ConnectorMaintenance</c>）
///   回答"<b>上游有没有更新的版本</b>"——需要清单，可能失败。</description></item>
/// </list>
/// 因此"已安装 / 未安装 / 需重装"的徽章以本地事实为准，版本与可更新性以上游为准；
/// 清单不可达时列表照样能显示本地状态，只是没有版本号与按钮。
/// </para>
/// <para>
/// 本投影是<b>不可变</b>的（只有 init 属性）：刷新时整体重建集合，而不是像
/// <see cref="PluginDirectoryDisplay"/> 那样按项 diff 更新。原因见
/// <see cref="PluginsPage.RefreshConnectorsAsync"/> 的注释。
/// </para>
/// </remarks>
public sealed class ConnectorStatusDisplay
{
    public string PlayerKey { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>徽章文字（已安装 / 未安装 / 需重装）。</summary>
    public string StatusText { get; init; } = string.Empty;

    public Brush StatusBrush { get; init; } = SolidColorBrushHelper.Neutral;

    /// <summary>版本行（已安装 x · 最新 y）。</summary>
    public string VersionText { get; init; } = string.Empty;

    /// <summary>补充说明行（拒绝原因 / 手动更新提示 / 解析告警）。</summary>
    public string DetailText { get; init; } = string.Empty;

    public bool HasDetail { get; init; }

    /// <summary>按钮文字（安装 / 更新 / 更新（需确认））。</summary>
    public string ActionText { get; init; } = string.Empty;

    /// <summary>是否显示动作按钮。清单不可达或没有可安装版本时为 false。</summary>
    public bool CanAct { get; init; }

    /// <summary>本地是否已安装且记录可用（决定按钮是"安装"还是"更新"）。</summary>
    public bool Installed { get; init; }

    /// <summary>该更新跨了播放器版本分支 / 主版本，点击后必须先确认（决策 D5）。</summary>
    public bool IsManual { get; init; }

    /// <summary>手动更新确认对话框的正文。</summary>
    public string ManualWarning { get; init; } = string.Empty;

    public static ConnectorStatusDisplay From(
        ConnectorUpdateStatus status,
        PlayerConnectorResolution resolution)
    {
        bool installed = resolution.Availability == PlayerConnectorAvailability.Installed;

        (string statusText, Brush brush) = resolution.Availability switch
        {
            PlayerConnectorAvailability.Installed => ("已安装", SolidColorBrushHelper.Success),
            PlayerConnectorAvailability.Broken => ("需重装", SolidColorBrushHelper.Critical),
            _ => ("未安装", SolidColorBrushHelper.Neutral),
        };

        bool hasNewerVersion = status.LatestVersion is not null
            && !string.Equals(status.LatestVersion, status.CurrentVersion, StringComparison.Ordinal);

        string versionText = installed
            ? hasNewerVersion
                ? $"已安装 {status.CurrentVersion} · 最新 {status.LatestVersion}"
                : $"已安装 {status.CurrentVersion}"
            : status.LatestVersion is not null
                ? $"可安装 {status.LatestVersion}"
                : "清单中暂无可用版本";

        // 手动更新提示：这是决策 D5 的核心——换播放器分支意味着连接器是为另一个播放器版本
        // 适配的，自动换上去可能让用户原本能用的播放器直接失效。
        string manualWarning = string.Empty;
        if (status.ManualUpdateAvailable && status.LatestVersion is not null)
        {
            string scope = status.TestedPlayerVersion is null
                ? "新的播放器版本分支"
                : $"新的播放器版本分支（适配播放器 {status.TestedPlayerVersion}）";

            manualWarning =
                $"{resolution.DisplayName} 的 {status.LatestVersion} 属于{scope}，不会自动更新。\n\n"
                + "连接器是按特定播放器版本适配的，跨分支替换后可能出现不兼容。确认继续更新？";
        }

        List<string> details = [];
        if (status.RejectionReason is not null)
        {
            details.Add($"清单条目被拒绝：{status.RejectionReason}");
        }

        if (status.ManualUpdateAvailable && status.LatestVersion is not null)
        {
            details.Add($"有跨分支更新 {status.LatestVersion}，需手动确认。");
        }

        if (resolution.Detail is not null)
        {
            details.Add(resolution.Detail);
        }

        if (hasNewerVersion && status.PlayerVersionPolicy is not null)
        {
            details.Add($"上游播放器版本策略：{status.PlayerVersionPolicy}");
        }

        string detailText = string.Join("\n", details);

        // 没有清单条目就无从安装（下载地址 / 签名 / rid 全在条目里）——此时不给按钮，
        // 而不是给一个点了必然失败的按钮。
        bool canAct = status.LatestVersion is not null && (!installed || status.UpdateAvailable);

        return new ConnectorStatusDisplay
        {
            PlayerKey = status.PlayerKey,
            DisplayName = resolution.DisplayName,
            StatusText = statusText,
            StatusBrush = brush,
            VersionText = versionText,
            DetailText = detailText,
            HasDetail = detailText.Length > 0,
            ActionText = !installed ? "安装" : status.ManualUpdateAvailable ? "更新（需确认）" : "更新",
            CanAct = canAct,
            Installed = installed,
            IsManual = status.ManualUpdateAvailable && status.LatestVersion is not null,
            ManualWarning = manualWarning,
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
    private readonly ObservableCollection<ConnectorStatusDisplay> _connectorStatuses = [];

    /// <summary>安装 / 更新进行中：禁用动作按钮并挡住重入（下载可能几十秒，用户会连点）。</summary>
    private bool _connectorActionInFlight;

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

    /// <summary>连接器安装根目录（<c>%LOCALAPPDATA%\ErbaiLiveTool\player-connectors</c>）。</summary>
    public string ConnectorRootText => App.Services.ConnectorLayout.Root;

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
            ? "未接入连接器——点歌将按估算时长播放。若选用的是插件平台，先在下方「播放器连接器」安装；落雪音乐随应用发布，缺失时请重新安装应用。"
            : $"连接器密钥 {player.Key}；在设置页更改播放器（保存后热切换，无需重启）";

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

        // 播放器连接器（插件化改造）：先画本地状态（只读盘、不联网，立刻可见），
        // 再异步补上清单里的版本信息——设置页打开时绝不该卡在一次网络请求上。
        ConnectorList.ItemsSource = _connectorStatuses;
        RenderConnectorStatuses(null);
        _ = RefreshConnectorsAsync(forceRefresh: false);
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

    // ── 播放器连接器管理（插件化改造 2026-09） ──────────────────────────────

    /// <summary>
    /// 采集上游状态并刷新列表。<b>不抛异常</b>：返回失败原因（<see langword="null"/> = 成功）。
    /// </summary>
    /// <remarks>
    /// 清单不可达<b>不是</b>错误状态——本地已装的连接器照常可用，只是拿不到版本信息与更新按钮。
    /// 因此这里降级为"只显示本地状态"，而不是把整个区域变成一条报错。
    /// </remarks>
    private async Task<string?> RefreshConnectorsAsync(bool forceRefresh)
    {
        try
        {
            var statuses = await App.Services.ConnectorMaintenance.GetStatusesAsync(forceRefresh);
            RenderConnectorStatuses(statuses);
            ConnectorEmptyText.Visibility = Visibility.Collapsed;
            return null;
        }
        catch (Exception ex)
        {
            RenderConnectorStatuses(null);
            ConnectorEmptyText.Text = $"连接器清单不可达（{ex.Message}）；下方仅显示本地安装状态。";
            ConnectorEmptyText.Visibility = Visibility.Visible;
            return ex.Message;
        }
    }

    /// <summary>
    /// 渲染连接器列表。<paramref name="statuses"/> 为 <see langword="null"/> 时只画本地状态。
    /// </summary>
    /// <remarks>
    /// <b>整体重建</b>而不是像 <see cref="RefreshPlugins"/> 那样按项 diff：这一区没有
    /// ToggleSwitch，不存在"容器复用时 x:Bind 赋值 IsOn 触发 Toggled"的连锁风险；
    /// 而 <see cref="ConnectorStatusDisplay"/> 是不可变的，重建是让 x:Bind(OneTime) 拿到新值的
    /// 最简方式——不必为一行展示数据再挂一套 INotifyPropertyChanged。
    /// </remarks>
    private void RenderConnectorStatuses(IReadOnlyList<ConnectorUpdateStatus>? statuses)
    {
        var byKey = statuses?.ToDictionary(s => s.PlayerKey, StringComparer.Ordinal)
            ?? new Dictionary<string, ConnectorUpdateStatus>(StringComparer.Ordinal);

        var settings = App.Services.Config.Settings;
        var resolutions = App.Services.ConnectorPlayerResolver.ResolveAll(
            foliaToken: settings.Player.FoliaToken);

        _connectorStatuses.Clear();
        foreach (var resolution in resolutions)
        {
            if (string.Equals(
                    resolution.PlayerKey,
                    PlayerConnectorResolver.BuiltInPlayerKey,
                    StringComparison.Ordinal))
            {
                continue; // 落雪音乐随应用发布，不参与插件安装
            }

            var status = byKey.TryGetValue(resolution.PlayerKey, out var found)
                ? found
                : new ConnectorUpdateStatus { PlayerKey = resolution.PlayerKey };

            _connectorStatuses.Add(ConnectorStatusDisplay.From(status, resolution));
        }
    }

    /// <summary>动作按钮与"从本地 ZIP 安装"的启停（安装期间一律禁用，防连点重复下载）。</summary>
    private void SetConnectorButtonsEnabled(bool enabled)
    {
        CheckConnectorsButton.IsEnabled = enabled;
        InstallFromZipButton.IsEnabled = enabled;
        ConnectorList.IsEnabled = enabled;
    }

    private void ShowConnectorStatus(string message)
    {
        ConnectorStatusText.Text = message;
        ConnectorStatusText.Visibility = Visibility.Visible;
    }

    private async void OnCheckConnectors(object sender, RoutedEventArgs e)
    {
        if (_connectorActionInFlight)
        {
            return;
        }

        _connectorActionInFlight = true;
        SetConnectorButtonsEnabled(false);
        try
        {
            string? error = await RefreshConnectorsAsync(forceRefresh: true);
            ShowConnectorStatus(error is null
                ? $"已检查更新（{DateTime.Now:HH:mm:ss}）。"
                : $"检查更新失败：{error}。下方仅显示本地安装状态。");
        }
        finally
        {
            _connectorActionInFlight = false;
            SetConnectorButtonsEnabled(true);
        }
    }

    private void OnOpenConnectorFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = App.Services.ConnectorLayout.Root;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowConnectorStatus($"打开连接器目录失败：{ex.Message}");
        }
    }

    private async void OnConnectorAction(object sender, RoutedEventArgs e)
    {
        if (_connectorActionInFlight ||
            sender is not Button button ||
            button.Tag is not string playerKey)
        {
            return;
        }

        var display = _connectorStatuses.FirstOrDefault(
            d => string.Equals(d.PlayerKey, playerKey, StringComparison.Ordinal));

        if (display is null || !display.CanAct)
        {
            return;
        }

        // 跨播放器分支 / 主版本推进：必须先让用户明确知道"这会把连接器换成给另一个播放器版本
        // 适配的那一版"。默认焦点落在「取消」（决策 D5：默认不动）。
        if (display.IsManual)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "确认更新连接器",
                Content = display.ManualWarning,
                PrimaryButtonText = "更新",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        _connectorActionInFlight = true;
        SetConnectorButtonsEnabled(false);
        try
        {
            string verb = display.Installed ? "更新" : "安装";
            ShowConnectorStatus($"{display.DisplayName} 正在{verb}…（下载 + 校验 + 健康检查，可能需要一会儿）");

            ConnectorMaintenanceAction action = await App.Services.ConnectorMaintenance
                .UpdateAsync(playerKey, force: display.IsManual);

            ShowConnectorStatus(action.Message);
            await ActivateIfCurrentPlayerAsync(playerKey);
        }
        catch (Exception ex)
        {
            ShowConnectorStatus($"{display.DisplayName} 操作失败：{ex.Message}");
        }
        finally
        {
            _connectorActionInFlight = false;
            SetConnectorButtonsEnabled(true);
        }

        await RefreshConnectorsAsync(forceRefresh: false);
    }

    /// <summary>
    /// 若刚安装的平台正是配置里选用的播放器，顺手激活它——否则用户会以为"装了还是不能用"。
    /// </summary>
    /// <remarks>
    /// 只在"当前没有接入这个连接器"时才切：已经在跑同一个 key 就不必重建子进程。
    /// </remarks>
    private async Task ActivateIfCurrentPlayerAsync(string playerKey)
    {
        if (!string.Equals(App.Services.Config.Settings.Player.Key, playerKey, StringComparison.Ordinal)
            || string.Equals(App.Services.Player?.Key, playerKey, StringComparison.Ordinal))
        {
            return;
        }

        string message = await App.Services.SwitchPlayerAsync(playerKey);
        ShowConnectorStatus($"{ConnectorStatusText.Text} {message}");
    }

    private async void OnInstallFromZip(object sender, RoutedEventArgs e)
    {
        if (_connectorActionInFlight)
        {
            return;
        }

        string? archivePath;
        try
        {
            archivePath = await PickConnectorArchiveAsync();
        }
        catch (Exception ex)
        {
            ShowConnectorStatus($"打开文件选择器失败：{ex.Message}");
            return;
        }

        if (string.IsNullOrEmpty(archivePath))
        {
            return; // 用户取消
        }

        _connectorActionInFlight = true;
        SetConnectorButtonsEnabled(false);
        try
        {
            await InstallFromLocalArchiveAsync(archivePath);
        }
        catch (Exception ex)
        {
            ShowConnectorStatus($"本地安装失败：{ex.Message}");
        }
        finally
        {
            _connectorActionInFlight = false;
            SetConnectorButtonsEnabled(true);
        }

        await RefreshConnectorsAsync(forceRefresh: false);
    }

    /// <summary>
    /// 从本地 ZIP 安装（决策 D6）。两条路径，按能不能拿到清单元数据分：
    /// <list type="number">
    ///   <item><description><b>资产名命中清单</b>（用户从官方 Release 页下载、未改名）→
    ///   与在线安装完全等价的完整校验（size + SHA-256 + Ed25519）。</description></item>
    ///   <item><description><b>命中不了</b>（改名 / 镜像包 / 清单不可达）→ 弹窗让用户明确选择平台与
    ///   发布形态，并明确告知此包<b>不会被校验</b>。绝不猜：猜错部署方式会写出一份
    ///   <c>ConnectorStore</c> 读不回来的 <c>active.json</c>。</description></item>
    /// </list>
    /// </summary>
    private async Task InstallFromLocalArchiveAsync(string archivePath)
    {
        string fileName = Path.GetFileName(archivePath);

        ConnectorCatalogEntry? entry = await FindCatalogEntryByAssetAsync(fileName);

        if (entry is not null)
        {
            // Id / Version 在 DTO 上是可空的（JSON 直绑），但清单校验层已保证它们非空——
            // 这里再断言一次，把"校验层哪天放松了"变成一条可读的错误而不是 NullReference。
            string playerKey = entry.Id
                ?? throw new ConnectorManagementException($"清单条目缺少平台标识（{fileName}）。");

            string version = entry.Version
                ?? throw new ConnectorManagementException($"清单条目 {playerKey} 缺少版本号。");

            string displayName = PlayerConnectorResolver.GetDisplayName(playerKey);
            ShowConnectorStatus($"{displayName} 正在从本地安装 {version}…（校验签名 + 健康检查）");

            ConnectorInstallResult result = await App.Services.ConnectorInstaller
                .InstallFromLocalArchiveAsync(
                    playerKey, archivePath, version, expectedPackage: entry.Package);

            ShowConnectorStatus($"{displayName} 已安装 {result.Version}（签名校验通过）。");
            await ActivateIfCurrentPlayerAsync(playerKey);
            return;
        }

        (string playerKey, string deployment, string? runtimeRid, string version)? choice =
            await AskUnverifiedInstallAsync(fileName);

        if (choice is null)
        {
            return; // 用户取消
        }

        string targetName = PlayerConnectorResolver.GetDisplayName(choice.Value.playerKey);
        ShowConnectorStatus($"{targetName} 正在从本地安装 {choice.Value.version}（未校验）…");

        ConnectorInstallResult unverified = await App.Services.ConnectorInstaller
            .InstallFromLocalArchiveAsync(
                choice.Value.playerKey,
                archivePath,
                choice.Value.version,
                expectedPackage: null,
                allowUnverified: true,
                deployment: choice.Value.deployment,
                runtimeRid: choice.Value.runtimeRid);

        ShowConnectorStatus(
            $"{targetName} 已安装 {unverified.Version}（未校验——该包未经过签名验证，请自行确认来源可信）。");
        await ActivateIfCurrentPlayerAsync(choice.Value.playerKey);
    }

    /// <summary>按资产名在清单里反查条目；清单不可达时返回 <see langword="null"/>（走未校验路径）。</summary>
    private async Task<ConnectorCatalogEntry?> FindCatalogEntryByAssetAsync(string fileName)
    {
        ConnectorCatalogSnapshot snapshot;
        try
        {
            snapshot = await App.Services.ConnectorCatalog.GetSnapshotAsync(forceRefresh: false);
        }
        catch (Exception ex)
        {
            App.Services.Logs.Log(
                Erbai.Contracts.Logging.LogLevel.Warning,
                $"[连接器] 本地 ZIP 安装时清单不可达，将走未校验路径：{ex.Message}");
            return null;
        }

        return snapshot.Entries.FirstOrDefault(e => string.Equals(
            e.Package?.Asset,
            fileName,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 未校验安装的确认弹窗：让用户明确指定平台 / 发布形态 / 版本，并看到风险提示。
    /// 返回 <see langword="null"/> 表示用户取消。
    /// </summary>
    private async Task<(string playerKey, string deployment, string? runtimeRid, string version)?>
        AskUnverifiedInstallAsync(string fileName)
    {
        var platformCombo = new ComboBox
        {
            Header = "这个包属于哪个平台",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (string key in ConnectorPlayers.PluginPlayerKeys)
        {
            platformCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{PlayerConnectorResolver.GetDisplayName(key)}（{key}）",
                Tag = key,
            });
        }

        platformCombo.SelectedIndex = 0;

        var deploymentCombo = new ComboBox
        {
            Header = "发布形态",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        deploymentCombo.Items.Add(new ComboBoxItem
        {
            Content = "自包含（self-contained，包内已含 .NET 运行时）",
            Tag = "self-contained|",
        });
        deploymentCombo.Items.Add(new ComboBoxItem
        {
            Content = "框架依赖 win-x64（使用私有 64 位运行时）",
            Tag = "framework-dependent|win-x64",
        });
        deploymentCombo.Items.Add(new ComboBoxItem
        {
            Content = "框架依赖 win-x86（使用私有 32 位运行时）",
            Tag = "framework-dependent|win-x86",
        });
        deploymentCombo.SelectedIndex = 0;

        var versionBox = new TextBox
        {
            Header = "版本号（用于安装目录命名与后续更新比较）",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // 版本号能从不含平台前缀的文件名里猜到就预填。**必须**填成 3–5 段数字：
        // 它同时是安装目录名，而 ConnectorStore 读回时会用同一条正则自校验
        // （^\d+(?:\.\d+){2,4}$）——放行别的形态会写出一份读不回来的 active.json，
        // 用户装完看到的是"需重装"。
        string? guessed = GuessVersionFromFileName(fileName);
        versionBox.Text = guessed is not null && ConnectorVersionPolicy.TryParse(guessed, out _)
            ? guessed
            : "0.0.0";

        var versionError = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = $"无法在清单中匹配「{fileName}」。\n\n"
                + "继续安装将跳过 size / SHA-256 / Ed25519 校验——请只在确认该文件来源可信时继续。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(platformCombo);
        panel.Children.Add(deploymentCombo);
        panel.Children.Add(versionBox);
        panel.Children.Add(versionError);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "安装未校验的本地包",
            Content = panel,
            PrimaryButtonText = "仍然安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        // 注意 lambda 的第一个形参**不能**叫 _：那样 out _ 会被解析成那个形参而不是弃元。
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            if (ConnectorVersionPolicy.TryParse(versionBox.Text, out _))
            {
                return;
            }

            versionError.Text = "版本号必须是 3–5 段数字（例如 3.1.38.205386.1）。"
                + "填错会让这个连接器之后检测不到更新。";
            versionError.Visibility = Visibility.Visible;
            args.Cancel = true; // 保持对话框打开，让用户改
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        string playerKey = (platformCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "netease";
        string deploymentTag = (deploymentCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "self-contained|";
        string version = versionBox.Text.Trim();

        int separator = deploymentTag.IndexOf('|');
        string deployment = separator < 0 ? deploymentTag : deploymentTag[..separator];
        string? rid = separator < 0 || separator == deploymentTag.Length - 1
            ? null
            : deploymentTag[(separator + 1)..];

        return (playerKey, deployment, rid, version);
    }

    /// <summary>
    /// 从连接器包文件名里提取版本号（形如 <c>awoo-connector-netease-3.1.38.205386.1-win-x64.zip</c>）。
    /// </summary>
    /// <remarks>
    /// 只用于给未校验路径的版本输入框预填——猜不到就返回 <see langword="null"/>，
    /// 让用户自己填，而不是硬塞一个可能错的版本号进安装目录名。
    /// </remarks>
    private static string? GuessVersionFromFileName(string fileName)
    {
        foreach (string key in ConnectorPlayers.PluginPlayerKeys)
        {
            foreach (string prefix in (string[])["awoo-connector-", "bilincm-connector-"])
            {
                string head = $"{prefix}{key}-";
                if (!fileName.StartsWith(head, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string tail = fileName[head.Length..];
                foreach (string suffix in (string[])["-framework-dependent.zip", ".zip"])
                {
                    if (tail.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        tail = tail[..^suffix.Length];
                        break;
                    }
                }

                foreach (string rid in ConnectorPlayers.SupportedRuntimes)
                {
                    string marker = $"-{rid}";
                    if (tail.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return tail[..^marker.Length];
                    }
                }
            }
        }

        return null;
    }

    /// <summary>选择本地连接器 ZIP。返回 <see langword="null"/> 表示用户取消。</summary>
    private static async Task<string?> PickConnectorArchiveAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".zip");

        // WinUI 3 未打包应用必须显式把选择器绑到窗口，否则 PickSingleFileAsync 会直接抛
        // （没有 UI 线程的 HWND 可供模态挂靠）。
        if (App.MainWindow is not null)
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
