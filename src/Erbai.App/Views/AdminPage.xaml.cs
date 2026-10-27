using System.Collections.ObjectModel;
using Erbai.App.Services;
using Erbai.Contracts.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Erbai.App.Views;

/// <summary>管理员列表展示投影（角色文案）。</summary>
public sealed record UserDisplay(User User)
{
    public string Nickname => User.Nickname;

    public string Platform => User.Platform;

    public string RoomId => User.RoomId;

    public string Role => User.IsAnchor ? "主播" : User.IsAdmin ? "管理员" : "普通";
}

/// <summary>
/// 管理页（三分区）：歌曲黑名单 / 用户黑名单 / 管理员。
/// 全部复用现有领域服务（SongBlacklist / IStorageEngine），改动即写盘并热生效，
/// 无需重启。空闲播放源已迁至「歌单」页（PlaylistPage）。
/// </summary>
public sealed partial class AdminPage : Page
{
    private readonly ObservableCollection<string> _songBan = [];
    private readonly ObservableCollection<BannedUser> _userBan = [];
    private readonly ObservableCollection<User> _banCandidates = [];
    private readonly ObservableCollection<UserDisplay> _admins = [];

    public AdminPage()
    {
        InitializeComponent();
        SongBanList.ItemsSource = _songBan;
        UserBanList.ItemsSource = _userBan;
        UserBanCandidates.ItemsSource = _banCandidates;
        AdminList.ItemsSource = _admins;
        // 数据在 Loaded 拉取（NavigationCacheMode=Enabled：页面实例缓存，
        // 每次进入仍触发 Loaded → 重拉数据，弹幕路径的拉黑/授权不会陈旧）
        Loaded += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            RefreshSongBanList();
            await RefreshUserBanListAsync();
            await RefreshAdminListAsync();
        }
        catch (Exception ex)
        {
            SongBanStatus.Text = $"加载失败：{ex.Message}";
        }
    }

    // ---- 歌曲黑名单 ----

    private async void OnAddSongBan(object sender, RoutedEventArgs e)
    {
        var rule = SongBanBox.Text.Trim();
        if (rule.Length == 0)
        {
            SongBanStatus.Text = "请输入规则";
            return;
        }

        try
        {
            var changed = await App.Services.Blacklist.AddRuleAsync(rule);
            SongBanBox.Text = "";
            SongBanStatus.Text = changed ? "已添加" : "规则已存在";
            RefreshSongBanList();
        }
        catch (Exception ex)
        {
            SongBanStatus.Text = $"添加失败：{ex.Message}";
        }
    }

    private async void OnRemoveSongBan(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not string rule)
        {
            return;
        }

        try
        {
            await App.Services.Blacklist.RemoveRuleAsync(rule);
            RefreshSongBanList();
            SongBanStatus.Text = "已删除";
        }
        catch (Exception ex)
        {
            SongBanStatus.Text = $"删除失败：{ex.Message}";
        }
    }

    private void RefreshSongBanList()
    {
        _songBan.Clear();
        foreach (var rule in App.Services.Blacklist.Items())
        {
            _songBan.Add(rule);
        }

        SongBanEmptyText.Visibility = _songBan.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 用户黑名单 ----

    private void OnSearchUsersForBanKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OnSearchUsersForBan(sender, e);
            e.Handled = true;
        }
    }

    private async void OnSearchUsersForBan(object sender, RoutedEventArgs e)
    {
        var keyword = UserBanSearchBox.Text.Trim();
        try
        {
            var users = await App.Services.Storage.ListUsersAsync(
                nickname: keyword.Length == 0 ? null : keyword, limit: 50);
            _banCandidates.Clear();
            foreach (var user in users)
            {
                _banCandidates.Add(user);
            }

            UserBanStatus.Text = $"找到 {users.Count} 个用户";
            UserBanCandidatesEmptyText.Visibility = _banCandidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            UserBanStatus.Text = $"搜索失败：{ex.Message}";
        }
    }

    private async void OnBanSelectedUser(object sender, RoutedEventArgs e)
    {
        if (UserBanCandidates.SelectedItem is not User user)
        {
            UserBanStatus.Text = "请先在候选列表选中用户";
            return;
        }

        try
        {
            await App.Services.Storage.BanUserAsync(
                user.Platform, user.RoomId, user.UserId, user.Nickname,
                reason: "管理员在界面拉黑", bannedBy: "管理员");
            UserBanStatus.Text = $"已拉黑：{user.Nickname}";
            await RefreshUserBanListAsync();
        }
        catch (Exception ex)
        {
            UserBanStatus.Text = $"拉黑失败：{ex.Message}";
        }
    }

    private async void OnUnbanUser(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not BannedUser banned)
        {
            return;
        }

        try
        {
            await App.Services.Storage.UnbanUserAsync(banned.Platform, banned.RoomId, banned.UserId);
            await RefreshUserBanListAsync();
            UserBanStatus.Text = $"已解除拉黑：{banned.Nickname}";
        }
        catch (Exception ex)
        {
            UserBanStatus.Text = $"解除失败：{ex.Message}";
        }
    }

    private async Task RefreshUserBanListAsync()
    {
        var list = await App.Services.Storage.ListBannedUsersAsync(limit: 200);
        _userBan.Clear();
        foreach (var banned in list)
        {
            _userBan.Add(banned);
        }

        UserBanEmptyText.Visibility = _userBan.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 管理员 ----

    private void OnSearchAdminsKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OnSearchAdmins(sender, e);
            e.Handled = true;
        }
    }

    private async void OnSearchAdmins(object sender, RoutedEventArgs e)
    {
        await RefreshAdminListAsync();
    }

    private async void OnToggleAdmin(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not UserDisplay display)
        {
            return;
        }

        try
        {
            var user = display.User;
            var updated = await App.Services.Storage.SetUserAdminAsync(
                user.Platform, user.RoomId, user.UserId, !user.IsAdmin);
            AdminStatus.Text = updated is null
                ? "用户不存在"
                : $"已{(updated.IsAdmin ? "设为" : "取消")}管理员：{updated.Nickname}";
            await RefreshAdminListAsync();
        }
        catch (Exception ex)
        {
            AdminStatus.Text = $"操作失败：{ex.Message}";
        }
    }

    private async Task RefreshAdminListAsync()
    {
        var keyword = AdminSearchBox.Text.Trim();
        var users = await App.Services.Storage.ListUsersAsync(
            nickname: keyword.Length == 0 ? null : keyword, limit: 200);
        _admins.Clear();
        foreach (var user in users)
        {
            _admins.Add(new UserDisplay(user));
        }

        AdminEmptyText.Visibility = _admins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
