using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Jint.Runtime;

namespace BarrageGrab
{
    public class Program
    {
        static bool exited = false;
        static WinApi.ControlCtrlDelegate controlCtr = ControlCtrlHandle;
        static Mutex mutex = new Mutex(false, "DyBarrageGrab");

        private const string WatchdogArg = "--watchdog";
        private const string WatchdogPidArg = "--pid";

        static void Main(string[] args)
        {
            // stdout 恒 UTF-8（宿主按 UTF-8 读；避免 Windows 默认 OEM/GBK 控制台代码页造成乱码）
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                Console.InputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // 编码设置失败不致命
            }

            if (IsWatchdogMode(args))
            {
                RunWatchdog(args);
                return;
            }

            if (!mutex.WaitOne(TimeSpan.Zero, true))
            {
                Console.WriteLine("另一个实例已在运行。");
                if (!Console.IsInputRedirected) Console.ReadKey();
                return;
            }

            SetTitle("抖音弹幕监听推送");

            StartWatchdog();

            // 宿主下发配置（阶段 4，docs/04 §3.1）：-config <path> 平铺 19 键白名单 JSON。
            // 失败即报错退出（返回码 3），绝不带残缺配置继续跑——宿主据此判启动失败回收进程。
            var hostConfigPath = TryGetHostConfigPath(args);
            if (hostConfigPath != null)
            {
                try
                {
                    HostConfig.ApplyFromFile(AppSetting.Current, hostConfigPath);
                    Logger.PrintColor($"已从宿主配置加载: {hostConfigPath}");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"宿主配置加载失败: {ex.Message}");
                    Console.Error.WriteLine($"宿主配置加载失败: {ex.Message}");
                    GrabberState.Error("host config load failed: " + ex.Message);
                    Environment.ExitCode = 3; // 非零退出码：宿主据此判启动失败并回收进程
                    return;
                }
            }

            try
            {
                Init();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"程序初始化错误，{ex.Message}");
                Console.Error.WriteLine($"程序初始化错误: {ex.Message}");
                GrabberState.Error("init failed: " + ex.Message);
                // init 失败非零退出（区别于 -config 失败的 3）：宿主/冒烟工具据此
                // 立即识别启动失败，不用等端口探测超时（docs/00 修复记录 #11）
                Environment.ExitCode = 4;
                exited = true;
            }

            while (!exited)
            {
                Thread.Sleep(500);
            }

            if (!AppRuntime.WsServer.IsDisposed)
            {
                AppRuntime.WsServer.Dispose();
            }

            GrabberState.Exit(0);
            Logger.PrintColor("服务器已关闭...");
            WinApi.SetConsoleCtrlHandler(controlCtr, false);//反注册捕获控制台关闭            
        }

        /// <summary>解析 -config 参数（及其值）；重复出现取最后一个。</summary>
        private static string TryGetHostConfigPath(string[] args)
        {
            string found = null;
            if (args == null) return null;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = (args[i] ?? "").Trim();
                if (!arg.Equals("-config", StringComparison.OrdinalIgnoreCase) &&
                    !arg.Equals("--config", StringComparison.OrdinalIgnoreCase) &&
                    !arg.Equals("-jsonconfig", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) &&
                    !args[i + 1].StartsWith("-"))
                {
                    found = args[i + 1];
                    i++;
                }
            }
            return found;
        }

        private static void StartWatchdog()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

                var pid = Process.GetCurrentProcess().Id;
                var args = string.Format("{0} {1} {2}", WatchdogArg, WatchdogPidArg, pid);
                var psi = new ProcessStartInfo(exePath, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
            }
            catch
            {
                // best-effort only
            }
        }

        private static bool IsWatchdogMode(string[] args)
        {
            return args != null && args.Any(a => string.Equals(a, WatchdogArg, StringComparison.OrdinalIgnoreCase));
        }

        private static int? TryGetWatchdogPid(string[] args)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], WatchdogPidArg, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 >= args.Length) return null;
                int pid;
                if (int.TryParse(args[i + 1], out pid)) return pid;
                return null;
            }
            return null;
        }

        private static void RunWatchdog(string[] args)
        {
            try
            {
                var pid = TryGetWatchdogPid(args);
                if (pid == null || pid <= 0) return;

                Process target;
                try
                {
                    target = Process.GetProcessById(pid.Value);
                }
                catch
                {
                    SafeCloseSystemProxy();
                    return;
                }

                for (; ; )
                {
                    if (target.HasExited) break;
                    Thread.Sleep(1000);
                }

                SafeCloseSystemProxy();
            }
            catch
            {
                try { SafeCloseSystemProxy(); } catch { }
            }
        }

        private static void SafeCloseSystemProxy()
        {
            try
            {
                if (!AppSetting.Current.UsedProxy) return;
                WinApi.CloseSystemProxy();
            }
            catch
            {
            }
        }

        private static void Init()
        {
            AppRuntime.Init();
            LiveCompanHelper.SwitchSetup();
            WinApi.SetConsoleCtrlHandler(controlCtr, true);//捕获控制台关闭
            WinApi.DisableQuickEditMode();//禁用控制台快速编辑模式
            AppRuntime.DisplayConsole(!AppSetting.Current.HideConsole);//控制控制台可见
            AppRuntime.WsServer.Grab.Proxy.SetUpstreamProxy(AppSetting.Current.UpstreamProxy);//设置上游代理
            AppRuntime.WsServer.OnClose += (s, e) =>
            {
                exited = true;
            };

            //串口写入服务
            if (!AppSetting.Current.ComPort.IsNullOrWhiteSpace())
            {
                AppRuntime.ComPortServer.OpenStart();
            }

            //显示窗体（.NET 8 迁移：WinForms UI 已移除，纯 headless；原 showWindow 配置忽略）
            if (AppSetting.Current.ShowWindow)
            {
                Logger.LogWarn("showWindow=true 在 .NET 8 迁移版中不受支持（headless 模式）");
            }

            AppRuntime.WsServer.StartListen();//启动WS以及代理服务
            Logger.PrintColor($"{AppRuntime.WsServer.ServerLocation} 弹幕服务已启动，其他端可通过此地址获取到弹幕流信息", ConsoleColor.Green);
            // headless 状态日志：WS 端口就绪 + 代理信息（宿主解析）
            GrabberState.Ready(AppSetting.Current.WsProt, AppSetting.Current.ProxyPort, AppSetting.Current.UsedProxy);

            Version version = System.Reflection.Assembly.GetAssembly(typeof(Program)).GetName().Version;
            SetTitle($"抖音弹幕监听推送 v{version}  [{AppRuntime.WsServer.ServerLocation}]");
        }

        //检测设置控制台标题
        private static void SetTitle(string title)
        {
            if (WinApi.GetConsoleWindow() != IntPtr.Zero)
            {
                Console.Title = title;
            }
        }

        //监听控制台消息事件
        private static bool ControlCtrlHandle(int CtrlType)
        {
            switch (CtrlType)
            {
                case 0:
                    //Logger.PrintColor("0工具被强制关闭"); //Ctrl+C关闭
                    //server.Close();
                    break;
                case 2:
                    Logger.PrintColor("2工具被强制关闭");//按控制台关闭按钮关闭
                    AppRuntime.WsServer.Dispose();
                    break;
            }
            return false;
        }
    }
}
