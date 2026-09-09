using Erbai.Contracts.Configuration;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Erbai.Connector.Netease;

/// <summary>注入结果。</summary>
public sealed record NeteaseBridgeInstallResult(bool Success, bool Loaded, int? ProcessId, string Message, string Details);

/// <summary>
/// 网易云 CEF bridge DLL 注入器（机制 docs/04 §1.5.4，代码表达沿用上游）：
/// 版本 3.1.x + player/libcef.dll SHA256 校验（已实测构建白名单，2026-08-27
/// 实测 3.1.39.205426 CEF 哈希与 3.1.38 完全一致——CEF 未变、仅主程序更新，
/// 桥注入兼容；多版本白名单取代原单版本精确锁定）→ OpenProcess →
/// VirtualAllocEx 写 DLL 路径 → CreateRemoteThread(LoadLibraryW) → 等线程结束。
/// bridge DLL 取自应用目录 bridge/AwooNcmCefBridge.dll（用户已拍板复用
/// vendor 二进制，C# 面照搬上游）。
/// </summary>
public static class NeteaseBridgeInstaller
{
    /// <summary>已实测可注入的网易云构建（版本 + cloudmusic.exe SHA256；libcef SHA 统一校验）。</summary>
    private static readonly (string Version, string PlayerSha256)[] TestedBuilds =
    {
        ("3.1.38.205386", "2AFBDE657C8C090E6209669E1C24979281F87FFD5C7DAC7A489E1F0E900A1D87"),
        // 2026-08-27 用户机器实际版本（CEF 哈希同 3.1.38，实测可注入，见 docs/00 修复记录 #16）
        ("3.1.39.205426", "08219CE25A5ADA092E63E1DE43EB0E9CCBCE5C529F8FF71BBA6ADB7DDED263B1"),
    };

    private const string SupportedCefSha256 = "724B3E35EDB5905540877FA8D7A8583A2503599639D7C79CCEF6FAA8E5A6BC49";

    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0;

    public static NeteaseBridgeInstallResult Install()
    {
        var endpoint = NeteaseNativeIpc.FindEndpoint();
        if (endpoint is null)
        {
            return new NeteaseBridgeInstallResult(false, false, null, "没有发现正在运行的网易云音乐。", string.Empty);
        }

        string playerPath;
        try
        {
            using var process = Process.GetProcessById(endpoint.Value.ProcessId);
            playerPath = process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception exception)
        {
            return new NeteaseBridgeInstallResult(false, false, endpoint.Value.ProcessId, "无法读取网易云主进程路径，已拒绝注入。", exception.Message);
        }

        if (string.IsNullOrWhiteSpace(playerPath) || !File.Exists(playerPath))
        {
            return new NeteaseBridgeInstallResult(false, false, endpoint.Value.ProcessId, "网易云主程序路径无效，已拒绝注入。", playerPath);
        }

        var playerVersion = FileVersionInfo.GetVersionInfo(playerPath).FileVersion ?? string.Empty;
        var cefPath = Path.Combine(Path.GetDirectoryName(playerPath) ?? string.Empty, "libcef.dll");
        if (!playerVersion.StartsWith("3.1.", StringComparison.Ordinal) || !File.Exists(cefPath))
        {
            return new NeteaseBridgeInstallResult(false, false, endpoint.Value.ProcessId, "网易云不属于当前可探测的 3.1 系列，或 CEF 文件不存在，已拒绝注入。", $"player={playerVersion}, cef={cefPath}");
        }

        var playerHash = ComputeSha256(playerPath);
        var cefHash = ComputeSha256(cefPath);
        var exactTestedBuild = cefHash.Equals(SupportedCefSha256, StringComparison.OrdinalIgnoreCase)
            && TestedBuilds.Any(build =>
                playerVersion.Equals(build.Version, StringComparison.Ordinal)
                && playerHash.Equals(build.PlayerSha256, StringComparison.OrdinalIgnoreCase));
        if (!exactTestedBuild)
        {
            return new NeteaseBridgeInstallResult(
                false,
                false,
                endpoint.Value.ProcessId,
                "网易云不是已实测构建（白名单：3.1.38.205386 / 3.1.39.205426 + 双哈希匹配），已拒绝注入。"
                + $"检测到 player={playerVersion} playerSha256={playerHash} cefSha256={cefHash}",
                $"player={playerVersion}; playerSha256={playerHash}; cefSha256={cefHash}");
        }

        var bridgePath = Path.Combine(AppPaths.HostDir, "bridge", "AwooNcmCefBridge.dll");
        if (!File.Exists(bridgePath))
        {
            return new NeteaseBridgeInstallResult(false, false, endpoint.Value.ProcessId, "没有找到本地 CEF 桥 DLL。", bridgePath);
        }

        var processHandle = OpenProcess(
            ProcessCreateThread | ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
            false,
            endpoint.Value.ProcessId);
        if (processHandle == nint.Zero)
        {
            return new NeteaseBridgeInstallResult(false, false, endpoint.Value.ProcessId, "无法打开网易云主进程。", Win32Message());
        }

        nint remotePath = nint.Zero;
        nint remoteThread = nint.Zero;
        try
        {
            var pathBytes = System.Text.Encoding.Unicode.GetBytes(bridgePath + "\0");
            remotePath = VirtualAllocEx(processHandle, nint.Zero, (nuint)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remotePath == nint.Zero)
            {
                return new NeteaseBridgeInstallResult(false, false, endpoint.Value.ProcessId, "无法在网易云进程中分配桥路径内存。", Win32Message());
            }

            if (!WriteProcessMemory(processHandle, remotePath, pathBytes, (nuint)pathBytes.Length, out var bytesWritten)
                || bytesWritten != (nuint)pathBytes.Length)
            {
                return new NeteaseBridgeInstallResult(false, false, endpoint.Value.ProcessId, "无法写入桥 DLL 路径。", Win32Message());
            }

            var loadLibrary = ResolveLoadLibrary(endpoint.Value.ProcessId);
            if (loadLibrary == nint.Zero)
            {
                return new NeteaseBridgeInstallResult(false, false, endpoint.Value.ProcessId, "无法解析网易云进程中的 LoadLibraryW。", Win32Message());
            }

            remoteThread = CreateRemoteThread(processHandle, nint.Zero, 0, loadLibrary, remotePath, 0, out _);
            if (remoteThread == nint.Zero)
            {
                return new NeteaseBridgeInstallResult(false, false, endpoint.Value.ProcessId, "创建桥加载线程失败。", Win32Message());
            }

            if (WaitForSingleObject(remoteThread, 10000) != WaitObject0)
            {
                return new NeteaseBridgeInstallResult(false, true, endpoint.Value.ProcessId, "桥加载线程没有在 10 秒内结束。", string.Empty);
            }

            var status = NeteaseBridgeClient.Probe(endpoint.Value.ProcessId);
            return new NeteaseBridgeInstallResult(
                status.Ready,
                status.Ready,
                endpoint.Value.ProcessId,
                status.Ready ? "进程内 CEF 桥已连接。" : "桥已加载，但尚未取得有效的网易云 CEF 宿主。",
                status.Message);
        }
        finally
        {
            if (remoteThread != nint.Zero)
            {
                _ = CloseHandle(remoteThread);
            }

            if (remotePath != nint.Zero)
            {
                _ = VirtualFreeEx(processHandle, remotePath, 0, MemRelease);
            }

            _ = CloseHandle(processHandle);
        }
    }

    /// <summary>解析目标进程内 kernel32!LoadLibraryW（模块枚举 + GetProcAddress）。</summary>
    private static nint ResolveLoadLibrary(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            foreach (ProcessModule module in process.Modules)
            {
                if (module.ModuleName.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var loadLibrary = GetProcAddress(module.BaseAddress, "LoadLibraryW");
                    if (loadLibrary != nint.Zero)
                    {
                        return loadLibrary;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
        }

        return nint.Zero;
    }

    private static string ComputeSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Win32Message() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(nint process, nint threadAttributes, nuint stackSize, nint startAddress, nint parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string procedureName);
}
