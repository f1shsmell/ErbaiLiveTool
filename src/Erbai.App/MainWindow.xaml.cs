using Erbai.App.Views;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using WinUIEx;

namespace Erbai.App;

public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "二白直播助手";

        // 窗口/任务栏图标（logo 2026-09-08）：exe 已经 ApplicationIcon 内嵌图标，
        // 但 WinUI 3 未打包应用的窗口标题栏图标不随 exe 图标，须显式 SetIcon
        // （Assets\AppLogo.ico 作为 Content 随构建/发布进输出目录）。
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.ico"));
        }
        catch
        {
            // 图标文件缺失（如开发期未拷贝）：保持默认图标，不影响使用
        }

        // 主窗口 UI 改进（2026-08-27）：Mica 系统背景（Win11 生效）+ 初始窗口尺寸。
        // 观感增强（2026-09，修订）：按系统版本显式分派背景，替代"catch 回退"——
        // 实测 MicaBackdrop 在 Win10 上构造不会抛异常（静默不渲染），导致 catch 回退
        // 永不触发、窗口退化成纯白。本方案最低支持 17763，面向两代系统：
        //   · Win11（Build ≥ 22000）：Mica 系统背景（安静低耗）；
        //   · Win10：不设 SystemBackdrop（Windows 10 上该机制不可靠），保持默认背景——
        //     NavigationView 自带主题感知背景（浅色偏白/深色偏深），扁平即干净，不追求亚克力。
        const int Win11Build = 22000;
        var os = Environment.OSVersion.Version;
        if (os.Major >= 10 && os.Build >= Win11Build)
        {
            try
            {
                SystemBackdrop = new MicaBackdrop();
            }
            catch
            {
                // 系统不支持 Mica（远程会话/显卡受限）：保持默认背景，不影响使用
            }
        }

        try
        {
            this.SetWindowSize(1120, 720);
        }
        catch
        {
        }
        // 窗口关闭语义（托盘常驻 2026-09）：点 X 默认隐藏到系统托盘后台运行
        // （主播挂机监听场景，关窗不退出；托盘菜单「退出」走完整清理链）；
        // 系统关机/设置关闭托盘时仍走完整退出路径（释放组合根——停平台监督者
        // （取消 runner → 插件/抓包器随 finally 停止、系统代理按铁律还原）、
        // 停队列/存储/连接器。不释放的话：抖音 Grabber 子进程变孤儿继续占
        // 8888/8827，系统代理被持续劫持（Grabber 自带 watchdog 只在自己
        // 死亡时还原代理，宿主死了不管））。
        //
        // 必须 Closing 拦截而非 Closed+async void：Closed 时窗口已销毁、
        // DispatcherQueue 随之关闭——DisposeAsync 的 await 续延回不到 UI 线程，
        // 清理在首个挂起点后被截断（恰好漏掉停平台监督者）。
        // Closing 事件无 deferral API（WASDK 1.8 只有 Cancel）：先 Cancel 拦下，
        // 异步清理完成后再 Close()，守卫标志放行这第二次关闭。
        AppWindow.Closing += OnClosing;
        Nav.SelectionChanged += (_, _) => Navigate();
        Nav.SelectedItem = Nav.MenuItems[0];
        // 观感打磨第二轮：Footer 显示程序集信息版本（安装包同名，单一事实源）
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        // 系统关机/注销检测（AppWindow.Closing 无 Reason，WASDK 1.8 只有 Cancel）：
        // SessionEnding 在关机/注销流程开始时触发，置标志供 OnClosing 分流（不得隐藏，须放行）
        SystemEvents.SessionEnding += OnSessionEnding;
        InitTrayIcon();
    }

    // SystemEvents.SessionEnding 在 SystemEvents 自己的消息线程触发，UI 线程读取：volatile 保证可见性
    private volatile bool _sessionEnding;

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => _sessionEnding = true;

    // Footer 版本项不可选中（SelectsOnInvoked=False）；点击复制版本信息到剪贴板，
    // 方便用户在报障时提供版本号。
    private async void OnFooterVersionTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var text = $"二白直播助手 {VersionText.Text}";
        try
        {
            var pkg = Windows.ApplicationModel.Package.Current;
            text += $"（{pkg.Id.Architecture}）";
        }
        catch
        {
            // 未打包运行：无架构信息，忽略
        }

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        await new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "版本信息",
            Content = $"{text}\n\n已复制到剪贴板。",
            CloseButtonText = "确定",
        }.ShowAsync();
    }

    private bool _exiting; // 已进入退出流程（清理进行中）
    private bool _exitCleanupDone; // 清理完成，放行真正关闭

    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitCleanupDone)
        {
            return; // 清理完成后的 Close：放行真正关闭
        }

        args.Cancel = true; // 一律先拦下（含清理期间的二次关闭请求——防止清理被截断，
                            // 否则 Grabber 变孤儿占 8888/8827、系统代理不还原）
        if (_exiting)
        {
            return; // 清理进行中：保持拦下，清理完成后再放行
        }

        // 托盘常驻分流：非系统关机且开启「关闭时最小化到托盘」→ 藏窗口不退出。
        // 系统关机/注销（SessionEnding 已置标志）不得隐藏，走完整退出。
        var minimizeToTray = App.Services is not null &&
            CloseToTray.ShouldHideOnClose(_sessionEnding,
                App.Services.Config.Settings.Runtime.MinimizeToTrayOnClose);
        if (minimizeToTray)
        {
            AppWindow.Hide();
            return;
        }

        _exiting = true;
        await CleanupAndCloseAsync();
    }

    /// <summary>完整退出唯一出口：清理组合根（停平台监督者/队列/存储/连接器）→
    /// 放行标志 → Close()。点 X 退出与托盘「退出」共用，保证清理链一致。</summary>
    private async Task CleanupAndCloseAsync()
    {
        try
        {
            await App.Services!.DisposeAsync();
            _trayIcon?.Dispose();
        }
        catch (Exception ex)
        {
            // 退出清理失败不阻断进程退出，但必须留痕
            try
            {
                App.Services?.Logs?.Log(Erbai.Contracts.Logging.LogLevel.Error, $"退出清理失败：{ex}");
            }
            catch
            {
            }
        }

        _exitCleanupDone = true;
        Close(); // OnClosing 见 _exitCleanupDone：放行真正关闭
    }

    // ---- 系统托盘（2026-09：点 X 隐藏到托盘后台运行；托盘菜单「退出」走完整清理链）----
    //
    // H.NotifyIcon 2.2.0 的 WinUI TaskbarIcon 不暴露鼠标事件（编译期确认），
    // 点击行为统一走 LeftClickCommand/RightClickCommand/DoubleClickCommand；
    // 右键菜单用 Win32 PopupMenu + GetCursorPos 锚点（无需事件坐标）。

    private H.NotifyIcon.TaskbarIcon? _trayIcon;

    private void InitTrayIcon()
    {
        var logs = App.Services?.Logs;
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");
            logs?.Log(Erbai.Contracts.Logging.LogLevel.Information,
                $"[托盘] 初始化：图标 {icoPath}，存在={File.Exists(icoPath)}");
            if (!File.Exists(icoPath))
            {
                return; // 图标缺失（开发期未拷贝）：不建托盘，不影响主功能
            }

            var icon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "二白直播助手（后台运行中）",
                // Icon 优先于 IconSource：System.Drawing.Icon 同步加载，绕开 BitmapImage
                // 异步解码→HICON 提取失败导致的透明图标（2026-09 用户实测：图标存在但透明；
                // 窗口标题栏 AppWindow.SetIcon 走同路径一直正常，故换 ico + Icon 构造加载；
                // 注意 .NET Core 的 System.Drawing.Icon 无 FromFile 静态方法，只有构造函数）
                Icon = new System.Drawing.Icon(icoPath),
                LeftClickCommand = new RelayCommand(ShowFromTray),
                DoubleClickCommand = new RelayCommand(ShowFromTray),
                RightClickCommand = new RelayCommand(ShowTrayMenu),
            };
            // ForceCreate(false)：H.NotifyIcon 官方创建姿势（资源内/延迟场景必须显式触发；
            // 传 false 避免其默认开启 Windows 11 Efficiency Mode——后台挂机时可省资源，
            // 但会压低进程调度优先级，弹幕/点歌链路不稳，本应用不启用）
            icon.ForceCreate(false);
            logs?.Log(Erbai.Contracts.Logging.LogLevel.Information,
                $"[托盘] TaskbarIcon 装配完成，IsCreated={icon.IsCreated}");
            _trayIcon = icon;
        }
        catch (Exception ex)
        {
            // 托盘装配失败（图标解码等）：不影响主功能，但必须留痕供排查
            logs?.Log(Erbai.Contracts.Logging.LogLevel.Error, $"[托盘] 装配失败：{ex}");
        }
    }

    /// <summary>从托盘恢复主窗口（单实例激活信号/托盘点击共用；窗口可见时无害，仅置前）。</summary>
    public void ShowFromTray()
    {
        try
        {
            if (AppWindow is null)
            {
                return;
            }

            AppWindow.Show();
            // 前台激活权可能被吞（与启动路径同问题）：显式 user32 SetForegroundWindow
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _ = SetForegroundWindow(hwnd);
        }
        catch
        {
            // 托盘恢复失败（极端窗口状态）不影响后台运行
        }
    }

    private void ShowTrayMenu()
    {
        try
        {
            var menu = new H.NotifyIcon.Core.PopupMenu
            {
                Items =
                {
                    new H.NotifyIcon.Core.PopupMenuItem("打开主界面", (_, _) => ShowFromTray()),
                    new H.NotifyIcon.Core.PopupMenuSeparator(),
                    new H.NotifyIcon.Core.PopupMenuItem("退出", (_, _) => ExitApp()),
                },
            };
            // 右键托盘图标时光标即位于图标处：取光标屏幕坐标作菜单锚点
            if (!GetCursorPos(out var pt))
            {
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            menu.Show(hwnd, pt.X, pt.Y);
        }
        catch
        {
            // 托盘菜单显示失败不影响主功能
        }
    }

    /// <summary>托盘「退出」：走与点 X 相同的完整清理链（CleanupAndCloseAsync 唯一出口）。</summary>
    private async void ExitApp()
    {
        if (_exiting || _exitCleanupDone)
        {
            return;
        }

        _exiting = true;
        await CleanupAndCloseAsync();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point pt);

    /// <summary>TaskbarIcon 命令用极简 ICommand（WinUI 3 无内置 RelayCommand）。</summary>
    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;

        public RelayCommand(Action action) => _action = action;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _action();

        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    private void Navigate()
    {
        // 页面过渡动画（UI 改进，参考 PCL 一切反馈皆动画）：切换页面时左侧滑入 + 淡入，
        // 替代默认无过渡的跳变，提升页面切换的"流动感"。
        var tag = (Nav.SelectedItem as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Tag?.ToString();
        var pageType = tag switch
        {
            "queue" => typeof(QueuePage),
            "queueup" => typeof(QueueUpPage),
            "history" => typeof(HistoryPage),
            "danmakulog" => typeof(DanmakuLogPage),
            "display" => typeof(DisplayPage),
            "plugins" => typeof(PluginsPage),
            "diagnostics" => typeof(DiagnosticsPage),
            "admin" => typeof(AdminPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(OverviewPage),
        };
        // 导航过渡：EntranceNavigationTransitionInfo（左滑入+淡入）。首次导航（SelectedItem
        // 在构造器里设置）也会播放，属可接受的启动动效。
        ContentFrame.Navigate(pageType, null,
            new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
    }
}
