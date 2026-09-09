using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Erbai.Live.Douyin.Hosting;

/// <summary>
/// WinINET 系统代理读写（真实现；docs/04 §3.2）：
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings 的
/// ProxyEnable/ProxyServer/ProxyOverride/AutoConfigURL，写入后 InternetSetOptionW(39/37) 广播。
/// </summary>
public sealed class WinInetProxyStore : ISystemProxyStore
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    /// <summary>
    /// 读当前代理快照。读取失败（注册表键被锁/不存在等）返回 <c>null</c>：
    /// 调用方不得把 null 当"空代理"用于 Restore——空快照会删掉用户自己的
    /// ProxyServer/ProxyOverride（数据损坏）；拿不到快照就放弃本轮代理接管。
    /// </summary>
    public ProxySnapshot? TryRead()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key is null)
            {
                return null;
            }

            return new ProxySnapshot
            {
                ProxyEnable = ReadValue(key, "ProxyEnable"),
                ProxyServer = ReadValue(key, "ProxyServer"),
                ProxyOverride = ReadValue(key, "ProxyOverride"),
                AutoConfigURL = ReadValue(key, "AutoConfigURL"),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>兼容旧调用面：读失败退化为空快照（仅用于"当前值是否指向本程序"的判等，不用于还原）。</summary>
    public ProxySnapshot Read() => TryRead() ?? new ProxySnapshot();

    public void Restore(ProxySnapshot snapshot)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key is null)
            {
                return;
            }

            var enable = snapshot.ProxyEnable ?? "";
            key.SetValue("ProxyEnable", int.TryParse(enable, out var enabled) ? enabled : 0, RegistryValueKind.DWord);

            // ProxyServer/ProxyOverride 是抓包器会改写的键：快照为空时删除残留
            foreach (var name in new[] { "ProxyServer", "ProxyOverride" })
            {
                var value = snapshot.GetType().GetProperty(name)?.GetValue(snapshot) as string ?? "";
                if (value.Length > 0)
                {
                    key.SetValue(name, value, RegistryValueKind.String);
                }
                else
                {
                    try
                    {
                        key.DeleteValue(name, throwOnMissingValue: false);
                    }
                    catch (Exception)
                    {
                        // 删除失败不致命（键可能被其他程序占用）
                    }
                }
            }

            // AutoConfigURL（PAC）：抓包器从不修改它；快照缺失时保留现值，绝不删除
            var pac = snapshot.AutoConfigURL ?? "";
            if (pac.Length > 0)
            {
                key.SetValue("AutoConfigURL", pac, RegistryValueKind.String);
            }
        }
        catch (Exception)
        {
            // 注册表写失败不致命（宿主继续，下次启动的残留清理会再试）
        }

        NotifyChange();
    }

    /// <summary>广播代理设置变更（浏览器与系统立即感知，无需重启）。</summary>
    public static void NotifyChange()
    {
        try
        {
            InternetSetOptionW(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOptionW(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
        }
    }

    private static string ReadValue(RegistryKey key, string name)
    {
        try
        {
            var value = key.GetValue(name);
            return value?.ToString() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InternetSetOptionW(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);
}

/// <summary>TCP 连接探测（真实现；端口就绪探测 30×0.25s 的底层）。</summary>
public sealed class TcpPortProbe : ITcpProbe
{
    public bool IsOpen(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
