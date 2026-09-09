using System.Diagnostics;
using Erbai.App.Services;
using Erbai.Contracts.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Erbai.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        var settings = App.Services.Config.Settings;
        MinimizeToTrayToggle.IsOn = settings.Runtime.MinimizeToTrayOnClose;
        IdleEnabledToggle.IsOn = settings.IdlePlaylist.Enabled;
        IdleIntervalBox.Value = settings.IdlePlaylist.PlayInterval;
        LiveEventLogToggle.IsOn = settings.Runtime.LiveEventLog;
        InitQueueSection(settings);
        InitPlayerSection(settings);
        RefreshLoginStatus();
        RefreshBackupList();
    }

    // ---- 点歌队列（docs/00 修复记录 #16：数量限制可配置）----

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

    private void InitPlayerSection(AppConfig settings)
    {
        var services = App.Services;
        foreach (var item in PlayerKeyCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == settings.Player.Key)
            {
                PlayerKeyCombo.SelectedItem = item;
                break;
            }
        }

        FoliaTokenBox.Text = settings.Player.FoliaToken;
        RefreshPlayerStatus();
        _playerComboReady = true;
    }

    private void RefreshPlayerStatus()
    {
        var services = App.Services;
        var exePath = Path.Combine(AppPaths.HostDir, "Erbai.Connector.exe");
        var running = services.Player is null
            ? "未接入（连接器缺失或激活失败，点歌将按估算时长播放）"
            : $"当前运行：{services.Player.DisplayName}（{services.Player.Key}）";
        PlayerStatusText.Text = File.Exists(exePath)
            ? $"{running}"
            : $"{running}；未找到 Erbai.Connector.exe";
    }

    private void OnPlayerKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_playerComboReady)
        {
            return;
        }

        var key = (PlayerKeyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        FoliaTokenCard.Visibility = key == "folia" ? Visibility.Visible : Visibility.Collapsed;
    }

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

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var services = App.Services;
            var current = services.Config.Settings;
            var playerKey = (PlayerKeyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? current.Player.Key;
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
            // 先写盘成功再更新内存（ConfigStore 语义）；队列/权限策略与空闲歌单配置热生效
            await services.Config.PersistAsync(candidate);
            services.Queue.ReloadPolicy(candidate);
            services.Queue.ReloadIdleConfig(candidate.IdlePlaylist);
            await services.Queue.ReloadIdleSongsAsync(await services.Storage.LoadIdleSongsAsync());

            // 播放器/Folia token 变更热切换连接器（token 经环境变量注入子进程，
            // 只改配置不重建连接器不生效）；失败保持原连接器继续点歌
            if (playerKey != current.Player.Key ||
                candidate.Player.FoliaToken != current.Player.FoliaToken)
            {
                SaveStatus.Text = await services.SwitchPlayerAsync(playerKey);
                RefreshPlayerStatus();
            }
            else
            {
                SaveStatus.Text = "已保存（空闲歌单配置热生效）";
            }
        }
        catch (Exception ex)
        {
            SaveStatus.Text = $"保存失败：{ex.Message}";
        }
    }
}
