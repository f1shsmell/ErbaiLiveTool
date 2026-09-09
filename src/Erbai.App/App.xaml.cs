using Erbai.App.Services;
using Microsoft.UI.Dispatching;
using WinUIEx;

namespace Erbai.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    public static Microsoft.UI.Xaml.Window? MainWindow { get; private set; }

    /// <summary>组合根（页面经 App.Services 访问队列/事件总线等）。</summary>
    public static AppServices Services { get; private set; } = null!;

    public App()
    {
        // WinUI 3（自定义 Main + Application.Start 路径）不会自动安装
        // DispatcherQueueSynchronizationContext（与 UWP 的差异）：不装的话所有
        // async void 事件处理器在 await 之后恢复到线程池线程，跨线程访问 UI
        // 控件抛 COMException 0x8001010E 直接闪退（实测：发送测试弹幕崩溃）。
        // 必须在 UI 线程（Application.Start 回调内）安装。
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));

        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // 异常不得逃逸（worker 不变量）：记录并继续，绝不闪退。
            // Handled=true 后异常不再触发进程终止（async void 异常经
            // SynchronizationContext 回到 UI 线程时也会走到这里）。
            try
            {
                Services?.Logs?.Log(Erbai.Contracts.Logging.LogLevel.Error, $"未处理异常（已隔离）：{e.Exception}");
            }
            catch
            {
            }

            e.Handled = true;
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 异步装配：不能同步 GetResult()——组合根里的 await 需要 UI 线程
        // SynchronizationContext 恢复，同步阻塞 UI 线程会死锁
        _ = LaunchAsync();
    }

    // ---- 单实例（2026-09，随托盘常驻引入）：任何二次启动都激活老实例后退出。
    // 托盘常驻前"关窗即退出"天然单实例；常驻后必须显式锁，否则两个实例
    // 抢 Grabber 端口（8888/8827）与系统代理，故障难以排查。----

    private const string SingleInstanceMutexName = "ErbaiLiveTool_SingleInstance";
    private const string ActivateSignalName = "ErbaiLiveTool_ActivateSignal";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateSignal;

    /// <summary>
    /// 尝试成为唯一实例。成功（createdNew）→ 建激活信号通道返回 true；
    /// 失败 → 通知已驻留实例把窗口带到前台，返回 false（调用方应退出本实例）。
    /// 必须在任何服务装配/窗口创建之前调用（无条件生效，与窗口是否可见无关）。
    /// </summary>
    private static bool TryAcquireSingleInstance()
    {
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            // 老实例崩溃/被强杀：互斥体被遗弃，本调用已获得所有权——按唯一实例继续启动
            createdNew = true;
        }

        if (createdNew)
        {
            // 老实例：立即建信号通道（紧随 Mutex，竞态窗口微秒级，可忽略）
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateSignalName, out _);
            return true;
        }

        // 已有实例驻留（托盘后台或前台）：请求它置前，本实例静默退出
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ActivateSignalName);
            signal.Set();
        }
        catch
        {
            // 老实例信号通道未就绪（启动竞态）：忽略，老实例正常启动
        }

        return false;
    }

    /// <summary>后台线程等待二次启动的激活信号，收到后把主窗口带到前台。</summary>
    private void StartActivateSignalListener()
    {
        _ = Task.Run(() =>
        {
            var signal = _activateSignal;
            while (signal is not null)
            {
                try
                {
                    signal.WaitOne();
                }
                catch
                {
                    return; // 句柄已释放（进程退出路径）：随进程终止
                }

                var window = MainWindow;
                window?.DispatcherQueue?.TryEnqueue(() => (window as MainWindow)?.ShowFromTray());
            }
        });
    }

    private async Task LaunchAsync()
    {
        if (!TryAcquireSingleInstance())
        {
            Environment.Exit(0); // 二次启动：激活信号已发出，直接退出
            return;
        }

        StartActivateSignalListener();
        try
        {
            Services = await AppServices.CreateAsync();
        }
        catch (Exception ex)
        {
            // 让窗口框架先起来，再弹错误框（避免消息框在装配完成前无法显示）
            await Task.Yield();
            System.Windows.Forms.MessageBox.Show($"启动装配失败：{ex.Message}\n\n{ex}", "二白直播助手");
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();
        // 提权（requireAdministrator 经 UAC/consent）启动路径的前台激活权常被吞：
        // 窗口正常显示但落在既有窗口之下，必须 Activate 后主动请求前台
        // （WinUIEx 扩展 = WindowNative 取 HWND 调 user32 SetForegroundWindow）。
        MainWindow.SetForegroundWindow();
    }
}
