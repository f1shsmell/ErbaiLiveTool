using System.Diagnostics;
using Erbai.App.Services;
using Erbai.Contracts.Configuration;
using Erbai.Player.Connectors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Erbai.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _autoSaveTimer;
    private bool _autoSaveReady;
    private bool _saveInProgress;
    private int _changeRevision;
    private int _savedRevision;

    public SettingsPage()
    {
        InitializeComponent();
        _autoSaveTimer = DispatcherQueue.CreateTimer();
        _autoSaveTimer.Interval = TimeSpan.FromMilliseconds(650);
        _autoSaveTimer.IsRepeating = false;
        _autoSaveTimer.Tick += async (_, _) => await SaveSettingsAsync();
        Unloaded += async (_, _) =>
        {
            _autoSaveTimer.Stop();
            if (_savedRevision != _changeRevision)
            {
                await SaveSettingsAsync();
            }
        };

        var settings = App.Services.Config.Settings;
        MinimizeToTrayToggle.IsOn = settings.Runtime.MinimizeToTrayOnClose;
        IdleEnabledToggle.IsOn = settings.IdlePlaylist.Enabled;
        IdleIntervalBox.Value = settings.IdlePlaylist.PlayInterval;
        LiveEventLogToggle.IsOn = settings.Runtime.LiveEventLog;
        InitQueueSection(settings);
        InitPlayerSection(settings);
        RefreshLoginStatus();
        RefreshBackupList();
        _autoSaveReady = true;
        _ = RefreshIdleSourceAsync();
    }

    private void OnAutoSaveToggle(object sender, RoutedEventArgs e) => ScheduleAutoSave();

    private void OnAutoSaveText(object sender, TextChangedEventArgs e) => ScheduleAutoSave();

    private void OnAutoSaveNumber(NumberBox sender, NumberBoxValueChangedEventArgs args) => ScheduleAutoSave();

    private void ScheduleAutoSave()
    {
        if (!_autoSaveReady)
        {
            return;
        }

        _changeRevision++;
        SaveStatus.Text = "等待自动保存…";
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    /// <summary>当前空闲播放源显示（异步查库；其他页面改标记后重进设置页能刷新）。</summary>
    private async Task RefreshIdleSourceAsync()
    {
        try
        {
            var idle = await App.Services.Storage.GetIdlePlaylistAsync();
            IdleSourceText.Text = idle is null ? "未指定" : idle.Name;
        }
        catch
        {
            // 纯显示性查询：失败保持占位文案即可
        }
    }

    private void OnGotoPlaylists(object sender, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("playlists");

    // ---- 点歌队列（历史修复记录 #16：数量限制可配置）----

    private void InitQueueSection(AppConfig settings)
    {
        var queue = settings.Queue;
        QueueMaxSizeBox.Value = queue.MaxSize;
        QueueUserLimitToggle.IsOn = queue.UserLimitEnabled;
        QueueMaxPerUserBox.Value = Math.Max(queue.MaxPerUser, 1);
        QueueDedupeToggle.IsOn = queue.DedupeEnabled;
        QueueDisplayLimitBox.Value = Math.Max(queue.DisplayLimit, 1);

        var permissions = settings.Permissions;
        DouyinFanLevelToggle.IsOn = permissions.DouyinMinFanLevel > 0;
        DouyinFanLevelBox.Value = Math.Max(permissions.DouyinMinFanLevel, 1);
        BilibiliMedalLevelToggle.IsOn = permissions.BilibiliMinMedalLevel > 0;
        BilibiliMedalLevelBox.Value = Math.Max(permissions.BilibiliMinMedalLevel, 1);
        AdminBypassToggle.IsOn = permissions.AdminBypass;
    }

    // ---- 音乐播放器（配置层；保存时经 AppServices.SwitchPlayerAsync 热切换）----

    /// <summary>禁止构造期 SelectionChanged 触发状态刷新。</summary>
    private bool _playerComboReady;

    /// <summary>下拉项 → 解析结果，供选中时查"装没装"。</summary>
    private readonly Dictionary<string, PlayerConnectorResolution> _playerOptions = new(StringComparer.Ordinal);

    /// <summary>
    /// 用户是否亲手动过播放器下拉。构造期的程序化选中不算——保存时靠它区分
    /// "用户真的换了播放器"和"下拉因未安装而回落"，后者不该写盘。
    /// </summary>
    private bool _playerTouchedByUser;

    private void InitPlayerSection(AppConfig settings)
    {
        var services = App.Services;
        // 这里只关心"装没装"，不需要内置连接器的 LX_* 环境变量（那是启动时才用的）。
        IReadOnlyList<PlayerConnectorResolution> resolutions =
            services.ConnectorPlayerResolver.ResolveAll(foliaToken: settings.Player.FoliaToken);

        PlayerKeyCombo.Items.Clear();
        _playerOptions.Clear();

        foreach (PlayerConnectorResolution resolution in resolutions)
        {
            _playerOptions[resolution.PlayerKey] = resolution;

            // 未安装的平台照样列出来但灰显：用户需要知道"有这个东西、可以装"，
            // 直接隐藏会让人以为应用不支持该平台（决策 D-D）。
            var item = new ComboBoxItem
            {
                Content = resolution.NeedsInstall
                    ? $"{resolution.DisplayName}（未安装）"
                    : resolution.DisplayName,
                Tag = resolution.PlayerKey,
                IsEnabled = !resolution.NeedsInstall,
            };

            if (resolution.NeedsInstall)
            {
                ToolTipService.SetToolTip(item, resolution.Detail);
            }

            PlayerKeyCombo.Items.Add(item);
        }

        SelectPlayerKey(settings.Player.Key);
        FoliaTokenBox.Text = settings.Player.FoliaToken;

        // 构造期的 SelectionChanged 被 _playerComboReady 挡掉了，这里必须补一次：
        // 否则配置里选的正是 folia 时，token 输入框要等用户重新选一次才出现
        // （只有 folia 连接器读这个 token，其他平台显示它是误导）。
        RefreshFoliaTokenCard(settings.Player.Key);

        RefreshPlayerStatus();
        _playerComboReady = true;
    }

    /// <summary>
    /// 选中指定 key。该 key 未安装（灰显）时<b>也优先选中它</b>，而不是回落到第一个可用项。
    /// </summary>
    /// <remarks>
    /// 回落看起来更"友好"，实际有两个坏处：用户会看到下拉自己跳到了别的播放器（以为配置被改了）；
    /// 而且选中项与配置不一致，一旦顺手保存就可能把播放器换掉。灰显项本身就带着
    /// "未安装"与安装指引（见 <see cref="RefreshPlayerInstallHint"/>），保持选中它语义最清楚。
    /// <para>
    /// 万一某天平台不允许程序化选中灰显项（<c>SelectedItem</c> 被置回 null），
    /// 才回落到第一个可用项——此时保存路径还有 <c>_playerTouchedByUser</c> 兜底。
    /// </para>
    /// </remarks>
    private void SelectPlayerKey(string playerKey)
    {
        ComboBoxItem? target = null;
        ComboBoxItem? firstUsable = null;

        foreach (ComboBoxItem item in PlayerKeyCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.IsEnabled && firstUsable is null)
            {
                firstUsable = item;
            }

            if (item.Tag?.ToString() == playerKey)
            {
                target = item;
            }
        }

        if (target is not null)
        {
            PlayerKeyCombo.SelectedItem = target;
            if (ReferenceEquals(PlayerKeyCombo.SelectedItem, target))
            {
                return;
            }
        }

        PlayerKeyCombo.SelectedItem = firstUsable;
    }

    private void RefreshPlayerStatus()
    {
        var services = App.Services;
        string? selectedKey = (PlayerKeyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        var running = services.Player is null
            ? "未接入（连接器缺失或激活失败，点歌将按估算时长播放）"
            : $"当前运行：{services.Player.DisplayName}（{services.Player.Key}）";

        if (selectedKey is not null
            && _playerOptions.TryGetValue(selectedKey, out PlayerConnectorResolution? resolution)
            && resolution.Availability == PlayerConnectorAvailability.Installed
            && resolution.Version is not null)
        {
            running += $"；{resolution.DisplayName} 已安装 {resolution.Version}";
        }

        PlayerStatusText.Text = running;
        RefreshPlayerInstallHint(selectedKey);
    }

    /// <summary>未安装（或已损坏）时给出原因与去向（决策 D-D）。</summary>
    private void RefreshPlayerInstallHint(string? playerKey)
    {
        if (playerKey is null
            || !_playerOptions.TryGetValue(playerKey, out PlayerConnectorResolution? resolution)
            || !resolution.NeedsInstall)
        {
            PlayerInstallHintText.Visibility = Visibility.Collapsed;
            PlayerInstallHintActions.Visibility = Visibility.Collapsed;
            return;
        }

        string hint = resolution.Availability == PlayerConnectorAvailability.Broken
            ? $"{resolution.Detail}"
            : $"未安装{resolution.DisplayName}连接器。插件页可联网安装，也可从本地 ZIP 安装（离线场景）。";

        PlayerInstallHintText.Text = $"{hint} 未安装的播放器在列表中灰显，不影响其他播放器使用。";
        PlayerInstallHintText.Visibility = Visibility.Visible;
        PlayerInstallHintActions.Visibility = Visibility.Visible;
    }

    private void OnPlayerKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_playerComboReady)
        {
            return;
        }

        // _playerComboReady 之后仍可能发生程序化赋值（例如保存后 RefreshPlayerStatus 触发），
        // 但那种情况下的选中项不会变；真正的"用户改动"一定经过这里且值确实变了。
        _playerTouchedByUser = true;

        var key = (PlayerKeyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        RefreshFoliaTokenCard(key);
        RefreshPlayerStatus();
        ScheduleAutoSave();
    }

    /// <summary>Folia token 卡片只在选中 folia 时有意义——其他连接器不读这个 token。</summary>
    private void RefreshFoliaTokenCard(string? playerKey) =>
        FoliaTokenCard.Visibility = playerKey == "folia" ? Visibility.Visible : Visibility.Collapsed;

    private void OnGoToPluginsForPlayer(object sender, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("plugins");

    /// <summary>在资源管理器中打开备份目录（不存在则先创建）。</summary>
    private void OnOpenBackupFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Backup.BackupService.BackupDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            BackupStatus.Text = $"打开备份目录失败：{ex.Message}";
        }
    }

    private void RefreshBackupList()
    {
        BackupListCombo.Items.Clear();
        foreach (var backup in Backup.BackupService.ListBackups())
        {
            var file = new FileInfo(backup);
            BackupListCombo.Items.Add($"{file.Name}（{file.Length / 1024.0:F0} KB）");
        }

        BackupListCombo.SelectedIndex = BackupListCombo.Items.Count > 0 ? 0 : -1;
    }

    private async void OnCreateBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await Backup.BackupService.CreateBackupAsync(App.Services);
            BackupStatus.Text = $"已创建备份：{path}";
            RefreshBackupList();
        }
        catch (Exception ex)
        {
            BackupStatus.Text = $"备份失败：{ex.Message}";
        }
    }

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (BackupListCombo.SelectedIndex < 0)
        {
            BackupStatus.Text = "请先选择备份文件。";
            return;
        }

        var path = Backup.BackupService.ListBackups()[BackupListCombo.SelectedIndex];
        var confirm = new ContentDialog
        {
            Title = "确认恢复",
            Content = $"将用备份替换当前数据库和配置：{Path.GetFileName(path)}\n恢复后建议重启应用。",
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var message = await Backup.BackupService.RestoreBackupAsync(App.Services, path);
            BackupStatus.Text = message;
            RefreshBackupList();
        }
        catch (Exception ex)
        {
            BackupStatus.Text = $"恢复失败：{ex.Message}";
        }
    }

    private void RefreshLoginStatus()
    {
        var login = App.Services.Login;
        var cookies = login?.CurrentCredentials;
        // 统一连接状态（评审第一轮 #8 + 观感打磨第二轮）：登录态胶囊徽章 + 说明文字
        var state = ConnectionStateMapper.ForLogin(App.Services);
        LoginBadge.Set(state);
        if (cookies is { HasLogin: true })
        {
            LoginStatusText.Text = $"已登录（凭据文件：{Path.GetFileName(login!.CredentialsPath)}）";
            QrLoginButton.IsEnabled = false;
            LogoutButton.IsEnabled = true;
        }
        else
        {
            LoginStatusText.Text = "未登录（登录后可识别主播身份/规避风控；匿名也能收弹幕）";
            QrLoginButton.IsEnabled = true;
            LogoutButton.IsEnabled = false;
        }
    }

    private async void OnQrLogin(object sender, RoutedEventArgs e)
    {
        var login = App.Services.Login;
        if (login is null)
        {
            return;
        }

        try
        {
            var result = await QrLoginDialog.ShowAsync(XamlRoot, login);
            if (result is not null)
            {
                LoginStatusText.Text = $"已登录：{result.AccountName}（直播间 {result.LiveRoomId ?? "无"}）";
                QrLoginButton.IsEnabled = false;
                LogoutButton.IsEnabled = true;
                LoginBadge.Set(ConnectionState.Connected);
            }
            else
            {
                LoginStatusText.Text = "登录未完成（取消/过期/失败）";
            }
        }
        catch (Exception ex)
        {
            LoginStatusText.Text = $"登录出错：{ex.Message}";
        }
    }

    private void OnLogout(object sender, RoutedEventArgs e)
    {
        App.Services.Login?.Logout();
        RefreshLoginStatus();
    }

    private async Task SaveSettingsAsync()
    {
        if (_saveInProgress || _savedRevision == _changeRevision)
        {
            return;
        }

        _saveInProgress = true;
        try
        {
            while (_savedRevision != _changeRevision)
            {
                var revision = _changeRevision;
                var numericValues = new[]
                {
                    QueueMaxSizeBox.Value,
                    QueueMaxPerUserBox.Value,
                    QueueDisplayLimitBox.Value,
                    DouyinFanLevelBox.Value,
                    BilibiliMedalLevelBox.Value,
                    IdleIntervalBox.Value,
                };
                if (numericValues.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                {
                    SaveStatus.Text = "存在无效数值，尚未保存";
                    return;
                }

                SaveStatus.Text = "正在保存…";
                var services = App.Services;
                var current = services.Config.Settings;
                var playerKey = _playerTouchedByUser
                    ? (PlayerKeyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? current.Player.Key
                    : current.Player.Key;
                var candidate = current with
                {
                    Player = current.Player with
                    {
                        Key = playerKey,
                        FoliaToken = FoliaTokenBox.Text.Trim(),
                    },
                    Queue = current.Queue with
                    {
                        MaxSize = (int)QueueMaxSizeBox.Value,
                        UserLimitEnabled = QueueUserLimitToggle.IsOn,
                        MaxPerUser = (int)QueueMaxPerUserBox.Value,
                        DedupeEnabled = QueueDedupeToggle.IsOn,
                        DisplayLimit = (int)QueueDisplayLimitBox.Value,
                    },
                    Permissions = current.Permissions with
                    {
                        DouyinMinFanLevel = DouyinFanLevelToggle.IsOn ? (int)DouyinFanLevelBox.Value : 0,
                        BilibiliMinMedalLevel = BilibiliMedalLevelToggle.IsOn ? (int)BilibiliMedalLevelBox.Value : 0,
                        AdminBypass = AdminBypassToggle.IsOn,
                    },
                    IdlePlaylist = new IdlePlaylistConfig
                    {
                        Enabled = IdleEnabledToggle.IsOn,
                        PlayInterval = (int)IdleIntervalBox.Value,
                    },
                    Runtime = current.Runtime with
                    {
                        LiveEventLog = LiveEventLogToggle.IsOn,
                        MinimizeToTrayOnClose = MinimizeToTrayToggle.IsOn,
                    },
                };

                await services.Config.PersistAsync(candidate);
                services.Queue.ReloadPolicy(candidate);
                services.Queue.ReloadIdleConfig(candidate.IdlePlaylist);
                await services.Queue.ReloadIdlePlaylistAsync();
                await RefreshIdleSourceAsync();

                string? playerStatus = null;
                if (playerKey != current.Player.Key ||
                    candidate.Player.FoliaToken != current.Player.FoliaToken)
                {
                    playerStatus = await services.SwitchPlayerAsync(playerKey);
                    RefreshPlayerStatus();
                }

                _savedRevision = revision;
                if (revision == _changeRevision)
                {
                    SaveStatus.Text = playerStatus ?? $"已自动保存 · {DateTime.Now:HH:mm}";
                }
            }
        }
        catch (Exception ex)
        {
            SaveStatus.Text = $"保存失败：{ex.Message}";
        }
        finally
        {
            _saveInProgress = false;
        }
    }
}
