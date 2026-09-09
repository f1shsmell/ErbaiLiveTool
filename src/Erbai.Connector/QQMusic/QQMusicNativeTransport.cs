using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Erbai.Connector.QQMusic;

/// <summary>原生插队结果（安全拒绝语义 + 校验详情）。</summary>
public sealed record QqNativeNextResult(
    bool Accepted,
    bool OriginalCodeRestored,
    string Verification,
    string? Error,
    string? FailureCode,
    int? ProcessId);

/// <summary>降级单曲播放结果（官方 /playbysongid 命令通道，不写进程）。</summary>
public sealed record QqNativePlayResult(bool Accepted, string Message);

/// <summary>
/// QQ音乐原生插队传输（机制 docs/04 §1.5.3，代码表达沿用上游）：
/// ① 分析目标进程模块（QQMusic.dll/QQMusicCommon.dll）→ 版本/SHA256/PE 机器码
///    前置校验 → 画像匹配；任何校验不过**拒绝写进程**；
/// ② VirtualAllocEx 分配 PAGE_EXECUTE_READWRITE 远程块 → 写 x86 trampoline +
///    数据区 → 把 SingleSongPlayDispatchRva 处指令改为相对跳转（5 字节 E8）；
/// ③ 启动 `QQMusic.exe /playbysongid cmd_count==1&&id_0=={id}&&songtype_0=={type}`
///    单实例命令（网络查询 + 创建 SongItem）→ 轮询数据区 stage → 恢复原字节。
/// 互斥 Local\QQMusicControlPoc.NativeNextUiTrampoline 串行化。
/// </summary>
public sealed class QqNativeNextTransport
{
    private const int DataOffset = 0x300;
    private const int DataSize = 0x100;
    private const int VectorOffset = 0xB8;
    private const int ResolvedSongIdOffset = 0xC4;
    private const int HiddenCategoryIdOffset = 0xC8;
    private const int HiddenCategoryCountOffset = 0xCC;
    private const int HiddenCategoryIndexOffset = 0xD0;
    private const int EmptyWideStringOffset = 0xD4;
    private const int DiagnosticIndexOffset = 0x178;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;
    private const string OperationMutexName = @"Local\QQMusicControlPoc.NativeNextUiTrampoline";

    public static async Task<QqNativeNextResult> InsertAsync(long songId, int songType, TimeSpan? responseWindow = null)
    {
        if (songId <= 0 || songId > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(songId), "底层下一首播放只接受 32 位正 songID。");
        }

        await _operationGate.WaitAsync();
        try
        {
            return await Task.Run(() => Insert(songId, songType, responseWindow ?? TimeSpan.FromSeconds(8)));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <summary>注入被拒是否为"未知版本/画像缺失"——可安全降级官方命令通道（不写进程）。</summary>
    public static bool IsUnsupportedVersionFailure(QqNativeNextResult result) =>
        result.Error?.Contains("未知 QQ 音乐版本", StringComparison.Ordinal) == true
        || result.Error?.Contains("profile 未匹配", StringComparison.Ordinal) == true;

    /// <summary>
    /// 降级通道：官方 `QQMusic.exe /playbysongid cmd_count==1&&id_0=={id}&&songtype_0=={type}`
    /// 单曲播放（网络查询 + 创建 SongItem，客户端原生播放），**不写进程、不经画像**。
    /// 22.61 等未知版本在注入被安全拒绝时使用。
    /// 行为差异（与注入插队比）：cmd_count==1 会**替换当前队列为单曲**，播完停止，
    /// 不自动接回原队列；且无法向守卫提供"已插入下一首"的对账信息（守卫跳过）。
    /// </summary>
    public static async Task<QqNativePlayResult> PlayBySongIdAsync(
        long songId, int songType, CancellationToken ct)
    {
        if (songId <= 0 || songId > uint.MaxValue)
        {
            return new QqNativePlayResult(false, $"songId 超出单曲命令支持的 32 位范围：{songId}");
        }

        await _operationGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var target = FindTarget();
                    using var helper = StartSingleSongHelper(target.ExecutablePath, songId, songType);
                    if (!helper.WaitForExit(3500))
                    {
                        TryStopUnexpectedHelper(helper);
                        return new QqNativePlayResult(false, "QQMusic.exe 单实例命令进程未按时退出。");
                    }

                    return new QqNativePlayResult(true,
                        $"官方命令通道播放完成：/playbysongid id_0={songId}&&songtype_0={songType}（未注入）。");
                }
                catch (Exception ex)
                {
                    return new QqNativePlayResult(false, $"官方命令通道失败：{ex.Message}");
                }
            }, ct);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static QqNativeNextResult Insert(long songId, int songType, TimeSpan responseWindow)
    {
        if (responseWindow < TimeSpan.FromSeconds(2) || responseWindow > TimeSpan.FromSeconds(12))
        {
            throw new ArgumentOutOfRangeException(nameof(responseWindow), "响应等待窗口必须在 2 到 12 秒之间。");
        }

        var stopwatch = Stopwatch.StartNew();
        var processId = 0;
        var clientModulePath = string.Empty;
        var fileVersion = string.Empty;
        var sha256 = string.Empty;
        var patchApplied = false;
        var originalCodeRestored = false;
        var patchAddress = nint.Zero;
        nint remoteBlock = 0;
        nint processHandle = nint.Zero;
        QQMusicProfile? activeProfile = null;
        string? error = null;
        string? failureCode = null;
        Mutex? operationMutex = null;
        var mutexAcquired = false;

        try
        {
            operationMutex = new Mutex(false, OperationMutexName);
            try
            {
                mutexAcquired = operationMutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }

            if (!mutexAcquired)
            {
                throw new TimeoutException("另一个原生下一首播放操作仍在进行。");
            }

            var target = FindTarget();
            processId = target.ProcessId;
            clientModulePath = target.ClientModulePath;

            var analysis = Analyze(target, out var profile);
            activeProfile = profile;
            fileVersion = analysis.FileVersion;
            sha256 = analysis.ClientSha256;
            if (!analysis.Allowed || profile is null)
            {
                throw new InvalidOperationException($"{analysis.Summary}");
            }

            processHandle = OpenProcess(
                ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
                false,
                target.ProcessId);
            if (processHandle == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess");
            }

            patchAddress = nint.Add(target.ClientModuleBase, profile.SingleSongPlayDispatchRva);
            var currentBytes = ReadBytes(processHandle, patchAddress, profile.ExpectedPlayDispatchBytes.Length);
            if (!currentBytes.SequenceEqual(profile.ExpectedPlayDispatchBytes))
            {
                throw new InvalidOperationException(
                    "单曲播放分发指令不匹配，已拒绝写入。"
                    + $" 实际={FormatBytes(currentBytes)}，预期={FormatBytes(profile.ExpectedPlayDispatchBytes)}");
            }

            remoteBlock = VirtualAllocEx(processHandle, 0, 0x1000, MemCommit | MemReserve, PageExecuteReadWrite);
            if (remoteBlock == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx");
            }

            var dataAddress = nint.Add(remoteBlock, DataOffset);
            var trampoline = BuildUiTrampoline(dataAddress, target.ClientModuleBase, target.CommonModuleBase, profile);
            WriteBytes(processHandle, remoteBlock, trampoline);
            WriteBytes(processHandle, dataAddress, new byte[DataSize]);

            var redirectBytes = CreateRelativeCall(patchAddress, remoteBlock);
            WriteCodeBytes(processHandle, patchAddress, redirectBytes);
            patchApplied = true;

            using var helper = StartSingleSongHelper(target.ExecutablePath, songId, songType);
            if (!helper.WaitForExit(3500))
            {
                TryStopUnexpectedHelper(helper);
                throw new TimeoutException("QQMusic.exe 单实例命令进程未按时退出。");
            }

            var deadline = DateTime.UtcNow + responseWindow;
            while (DateTime.UtcNow < deadline)
            {
                var stageBytes = ReadBytes(processHandle, dataAddress, 4);
                var stage = stageBytes.Length == 4 ? BitConverter.ToInt32(stageBytes, 0) : 0;
                if (stage >= 5)
                {
                    break;
                }

                Thread.Sleep(50);
            }

            originalCodeRestored = WriteCodeBytes(processHandle, patchAddress, profile.ExpectedPlayDispatchBytes);
            var finalStage = ReadBytes(processHandle, dataAddress, 4).Length == 4
                ? BitConverter.ToInt32(ReadBytes(processHandle, dataAddress, 4), 0)
                : 0;

            var resolvedSongId = ReadBytes(processHandle, nint.Add(dataAddress, ResolvedSongIdOffset), 4).Length == 4
                ? BitConverter.ToUInt32(ReadBytes(processHandle, nint.Add(dataAddress, ResolvedSongIdOffset), 4), 0)
                : 0u;

            if (finalStage < 3)
            {
                failureCode = "NativeNextTrampolineNotReached";
                error = $"trampoline 未在窗口内解析出歌曲（stage={finalStage}）";
            }
            else if (resolvedSongId != songId)
            {
                var categoryCount = ReadBytes(processHandle, nint.Add(dataAddress, HiddenCategoryCountOffset), 4).Length == 4
                    ? BitConverter.ToInt32(ReadBytes(processHandle, nint.Add(dataAddress, HiddenCategoryCountOffset), 4), 0)
                    : -1;
                var actualIndex = ReadBytes(processHandle, nint.Add(dataAddress, DiagnosticIndexOffset), 4).Length == 4
                    ? BitConverter.ToInt32(ReadBytes(processHandle, nint.Add(dataAddress, DiagnosticIndexOffset), 4), 0)
                    : -1;
                failureCode = "NativeNextResolvedSongMismatch";
                error = $"trampoline 解析出的 songId 与目标不符（{resolvedSongId} != {songId}；分类歌曲数={categoryCount}，真实index={actualIndex}）";
            }
            else
            {
                error = finalStage >= 4
                    ? null
                    : $"AddSongs 未在窗口内执行（stage={finalStage}）";
            }

            return new QqNativeNextResult(
                error is null && finalStage >= 4,
                originalCodeRestored,
                $"fileVersion={fileVersion}; sha256={sha256}; stage={finalStage}; elapsed={stopwatch.ElapsedMilliseconds}ms; patchApplied={patchApplied}; restored={originalCodeRestored}",
                error,
                failureCode,
                processId);
        }
        catch (Exception ex)
        {
            // 尽力恢复补丁点，避免留下重定向
            if (processHandle != nint.Zero && patchAddress != nint.Zero && activeProfile is not null)
            {
                originalCodeRestored = WriteCodeBytes(processHandle, patchAddress, activeProfile.ExpectedPlayDispatchBytes);
            }

            failureCode ??= "NativeNextException";
            return new QqNativeNextResult(false, originalCodeRestored, $"fileVersion={fileVersion}; sha256={sha256}; elapsed={stopwatch.ElapsedMilliseconds}ms", ex.Message, failureCode, processId == 0 ? null : processId);
        }
        finally
        {
            if (remoteBlock != 0 && processHandle != nint.Zero)
            {
                _ = VirtualFreeEx(processHandle, remoteBlock, 0, MemRelease);
            }

            if (processHandle != nint.Zero)
            {
                _ = CloseHandle(processHandle);
            }

            if (operationMutex is not null)
            {
                if (mutexAcquired)
                {
                    try
                    {
                        operationMutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                }

                operationMutex.Dispose();
            }
        }
    }

    private sealed record TargetInfo(int ProcessId, string ExecutablePath, string ClientModulePath, nint ClientModuleBase, nint CommonModuleBase);

    private sealed record AnalysisResult(bool Allowed, string FileVersion, string ClientSha256, string Summary);

    private static TargetInfo FindTarget()
    {
        var matches = new List<TargetInfo>();
        foreach (var process in Process.GetProcessesByName("QQMusic"))
        {
            try
            {
                nint clientBase = 0;
                nint commonBase = 0;
                string? clientPath = null;
                string? commonPath = null;
                foreach (ProcessModule module in process.Modules)
                {
                    if (module.ModuleName.Equals("QQMusic.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        clientBase = module.BaseAddress;
                        clientPath = module.FileName;
                    }
                    else if (module.ModuleName.Equals("QQMusicCommon.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        commonBase = module.BaseAddress;
                        commonPath = module.FileName;
                    }
                }

                if (process.MainModule is { } executable && clientBase != 0 && commonBase != 0)
                {
                    matches.Add(new TargetInfo(process.Id, executable.FileName, clientPath!, clientBase, commonBase));
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // 忽略陈旧 helper 进程
            }
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException("没有找到同时加载 QQMusic.dll 和 QQMusicCommon.dll 的 QQ 音乐主进程。");
        }

        return matches.OrderByDescending(m => WorkingSetOf(m.ProcessId)).First();
    }

    private static long WorkingSetOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>分析 + 校验：版本/双 SHA256/PE x86/补丁点机器码/画像匹配。</summary>
    private static AnalysisResult Analyze(TargetInfo target, out QQMusicProfile? profile)
    {
        profile = null;
        var clientPath = Path.GetFullPath(target.ClientModulePath);
        var version = FileVersionInfo.GetVersionInfo(clientPath).FileVersion ?? string.Empty;
        var clientSha = QQMusicPeImage.Sha256(clientPath);
        var isX86 = QQMusicPeImage.IsX86(clientPath);
        var commonPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(clientPath) ?? "", "QQMusicCommon.dll"));
        var commonSha = File.Exists(commonPath) ? QQMusicPeImage.Sha256(commonPath) : string.Empty;
        profile = QQMusicProfiles.Find(version, clientSha, commonSha);
        if (profile is null)
        {
            // 失配详情必须可见：版本/双哈希用于校准新画像（docs/00 修复记录 #17）
            var known = string.Join(" / ", QQMusicProfiles.All.Select(p => p.FileVersion));
            return new AnalysisResult(false, version, clientSha,
                $"未知 QQ 音乐版本（{version}），已拒绝写进程（profile 未匹配）。"
                + $"检测到 clientSha256={clientSha}; commonSha256={commonSha}; 已知画像版本={known}");
        }

        if (!isX86)
        {
            return new AnalysisResult(false, version, clientSha,
                $"QQMusic.dll 不是 x86 映像（版本 {version}），已拒绝写进程。");
        }

        if (profile.Disabled)
        {
            return new AnalysisResult(false, version, clientSha,
                $"QQ 音乐版本 {version} 的画像已标记为「已知崩溃，拒绝注入」，已拒绝写进程。{profile.Evidence}");
        }

        var actual = QQMusicPeImage.ReadMachineCode(clientPath, profile.SingleSongPlayDispatchRva, profile.ExpectedPlayDispatchBytes.Length);
        if (!actual.SequenceEqual(profile.ExpectedPlayDispatchBytes))
        {
            return new AnalysisResult(false, version, clientSha,
                $"补丁点机器码不匹配（版本 {version}）：实际={FormatBytes(actual)}，预期={FormatBytes(profile.ExpectedPlayDispatchBytes)}，已拒绝写进程。");
        }

        return new AnalysisResult(true, version, clientSha, $"profile {version} 全部校验通过");
    }

    /// <summary>trampoline 机器码（机制参照上游，序列为功能性事实）。</summary>
    private static byte[] BuildUiTrampoline(nint dataAddress, nint clientModuleBase, nint commonModuleBase, QQMusicProfile profile)
    {
        var emitter = new X86Emitter();
        var data = checked((uint)dataAddress.ToInt64());
        var getCatManager = Address(commonModuleBase, profile.GetCatManagerRva);
        var getQqUinEx = Address(commonModuleBase, profile.GetQqUinExRva);
        var songItemConstructor = Address(clientModuleBase, profile.SongItemConstructorRva);
        var songItemDestructor = Address(clientModuleBase, profile.SongItemDestructorRva);
        var addSongs = Address(clientModuleBase, profile.AddSongsRva);
        var hiddenCategoryIdAddress = Address(clientModuleBase, profile.HiddenCategoryIdRva);
        var getListRoot = Address(clientModuleBase, profile.GetListRootRva);
        var getListHelper = Address(clientModuleBase, profile.GetListHelperRva);
        var getCategoryCount = Address(clientModuleBase, profile.GetCategoryCountRva);

        // 保存完整寄存器/标志状态；data.stage=1
        emitter.Bytes(0x9C, 0x60, 0xBF);
        emitter.UInt32(data);
        emitter.Bytes(0x33, 0xF6);
        emitter.MovDwordAtEdi(0x00, 1);

        // GetICatMgr(&data.catManager)
        emitter.Bytes(0x8D, 0x47, 0x08, 0x50, 0xB8);
        emitter.UInt32(getCatManager);
        emitter.Bytes(0xFF, 0xD0, 0x83, 0xC4, 0x04);
        emitter.Bytes(0x89, 0x47, 0x04, 0x85, 0xC0);
        emitter.Jump32(0x0F, 0x88, "cleanup");
        emitter.Bytes(0x8B, 0x77, 0x08, 0x85, 0xF6);
        emitter.Jump32(0x0F, 0x84, "cleanup");
        emitter.MovDwordAtEdi(0x00, 2);

        // SongItem songItem（构造于 data+0x18）
        emitter.Bytes(0x8D, 0x4F, 0x18, 0xB8);
        emitter.UInt32(songItemConstructor);
        emitter.Bytes(0xFF, 0xD0);
        emitter.MovDwordAtEdi(0x14, 1);

        // 解析 /playbysongid 刚添加的歌曲（私有命令分类）。
        // index = max(count-1, 0)（尾部追加）。
        // 为什么 22:00 实测"index=0 读到新歌"是假象：X86Emitter 跳转位移 bug（2026-09-03 修复）
        // 使 jns 落到 xor 兜底上、index 恒 0；当分类仅 1 首（count=1）时 index=0 恰等于 count-1=0，
        // 掩盖了 bug 且误判为「头部插入」；22:13 起分类累积到 3/4 首后暴露——index=0 永远解析
        // 最老的一首（462188），新歌 97773/213459344 不在 index 0。恢复尾部语义后 index=count-1。
        emitter.Byte(0xA1);
        emitter.UInt32(hiddenCategoryIdAddress);
        emitter.Bytes(0x89, 0x87);
        emitter.UInt32(HiddenCategoryIdOffset);
        emitter.Bytes(0x8B, 0xD8);
        emitter.Bytes(0x6A, 0x00, 0x6A, 0x00, 0xB8);
        emitter.UInt32(getListRoot);
        emitter.Bytes(0xFF, 0xD0, 0x8B, 0xC8, 0xB8);
        emitter.UInt32(getListHelper);
        emitter.Bytes(0xFF, 0xD0, 0x8B, 0xC8, 0x53, 0xB8);
        emitter.UInt32(getCategoryCount);
        emitter.Bytes(0xFF, 0xD0);
        emitter.Bytes(0x89, 0x87);
        emitter.UInt32(HiddenCategoryCountOffset);
        emitter.Bytes(0x48);
        emitter.Jump32(0x0F, 0x89, "indexReady");
        emitter.Bytes(0x33, 0xC0);
        emitter.Label("indexReady");
        emitter.Bytes(0x89, 0x87);
        emitter.UInt32(HiddenCategoryIndexOffset);
        // 备份真实 index 到诊断字段（0x178），避免被后续 use count hack 覆盖 0xD0 后诊断失真。
        emitter.Bytes(0x89, 0x87);
        emitter.UInt32(DiagnosticIndexOffset);

        // 解析 songId
        emitter.Byte(0xB8);
        emitter.UInt32(getQqUinEx);
        emitter.Bytes(0xFF, 0xD0, 0x6A, 0x00);
        emitter.Bytes(0x8D, 0x4F, 0x18, 0x51);
        emitter.Bytes(0xFF, 0xB7);
        emitter.UInt32(HiddenCategoryIndexOffset);
        emitter.Bytes(0xFF, 0xB7);
        emitter.UInt32(HiddenCategoryIdOffset);
        emitter.Bytes(0x52, 0x50, 0x56, 0x8B, 0x0E);
        emitter.Bytes(0xFF, 0x51, 0x34);
        emitter.Bytes(0x89, 0x47, 0x0C, 0x85, 0xC0);
        emitter.Jump32(0x0F, 0x88, "cleanup");
        emitter.MovDwordAtEdi(0x00, 3);
        emitter.Bytes(0x8B, 0x47, 0x18);
        emitter.Bytes(0x89, 0x87);
        emitter.UInt32(ResolvedSongIdOffset);

        // std::vector<SongItem>（一个已构造元素）
        emitter.Bytes(0x8D, 0x47, 0x18);
        emitter.Bytes(0x89, 0x87);
        emitter.UInt32(VectorOffset);
        emitter.Bytes(0x05);
        emitter.UInt32(checked((uint)profile.SongItemSize));
        emitter.Bytes(0x89, 0x87);
        emitter.UInt32(VectorOffset + 4);
        emitter.Bytes(0x89, 0x87);
        emitter.UInt32(VectorOffset + 8);

        // 22.52+ 的 AddSongs 第4参数（context）已由裸宽字符串变为引用计数智能指针：
        // 返回时无条件析构 [ebp+0xc]-0x10，控制块 +0=vtable、+0xC=use count，减1 后 <=0
        // 走虚析构 → 解引用被当 vtable 的 resolvedSongId（data+0xC4）触发 0xc0000005
        // （崩溃转储实证：读地址 = 被点歌曲 ID 0x17ded）。GetSongInfo 已用完 index 后，
        // 这里把 data+0xD0（HiddenCategoryIndexOffset，即 context-0x4 = use count 字段）
        // 覆盖为 2，使返回析构时 use count 减1 后仍 >0，走 jg 跳过虚析构——不崩，
        // 也不改变 AddSongs 主体插队行为（context 非空仍走 SysAllocString 复制空串主路径）。
        // 对旧版本（裸串 context）无副作用：index 在 GetSongInfo 后不再使用。
        // 注意：0xD0 超出 disp8 有符号范围，必须用 disp32 编码（C7 87 disp32 imm32）。
        emitter.Bytes(0xC7, 0x87);
        emitter.UInt32(checked((uint)HiddenCategoryIndexOffset));
        emitter.UInt32(2);

        // AddSongs(mode=0)：非空 L"" 上下文串指针（null 会走阻塞路径）
        emitter.Bytes(0x8B, 0xCE, 0x8D, 0x97);
        emitter.UInt32(VectorOffset);
        emitter.Byte(0x68);
        emitter.UInt32(checked(data + EmptyWideStringOffset));
        emitter.Bytes(0x6A, 0x00, 0xB8);
        emitter.UInt32(addSongs);
        emitter.Bytes(0xFF, 0xD0, 0x83, 0xC4, 0x08);
        emitter.Bytes(0x89, 0x47, 0x10);
        emitter.MovDwordAtEdi(0x00, 4);

        // cleanup：销毁 SongItem
        emitter.Label("cleanup");
        emitter.Bytes(0x83, 0x7F, 0x14, 0x00);
        emitter.Jump32(0x0F, 0x84, "release");
        emitter.Bytes(0x8D, 0x4F, 0x18, 0xB8);
        emitter.UInt32(songItemDestructor);
        emitter.Bytes(0xFF, 0xD0);

        // release：释放 catManager
        emitter.Label("release");
        emitter.Bytes(0x85, 0xF6);
        emitter.Jump32(0x0F, 0x84, "done");
        emitter.Bytes(0x8B, 0x06, 0x56, 0xFF, 0x50, 0x08);

        // done：stage=5，恢复寄存器并返回
        emitter.Label("done");
        emitter.MovDwordAtEdi(0x00, 5);
        emitter.Bytes(0x61, 0x9D, 0xC3);
        return emitter.Build();
    }

    private static byte[] CreateRelativeCall(nint instructionAddress, nint targetAddress)
    {
        var nextInstruction = instructionAddress.ToInt64() + 5;
        var displacement = checked((int)(targetAddress.ToInt64() - nextInstruction));
        return [0xE8, .. BitConverter.GetBytes(displacement)];
    }

    private static uint Address(nint moduleBase, int rva) => checked((uint)nint.Add(moduleBase, rva).ToInt64());

    private static Process StartSingleSongHelper(string executablePath, long songId, int songType)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("/playbysongid");
        startInfo.ArgumentList.Add($"cmd_count==1&&id_0=={songId}&&songtype_0=={songType}");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("QQMusic.exe 单实例命令进程未启动。");
    }

    private static void TryStopUnexpectedHelper(Process helper)
    {
        try
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static byte[] ReadBytes(nint processHandle, nint address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(processHandle, address, buffer, (nuint)length, out var read) || read != (nuint)length)
        {
            return [];
        }

        return buffer;
    }

    private static bool WriteBytes(nint processHandle, nint address, byte[] bytes)
    {
        if (!WriteProcessMemory(processHandle, address, bytes, (nuint)bytes.Length, out var written))
        {
            return false;
        }

        return written == (nuint)bytes.Length;
    }

    /// <summary>写机器码并回读验证（对齐上游 WriteCodeBytes 语义）。</summary>
    private static bool WriteCodeBytes(nint processHandle, nint address, byte[] bytes)
    {
        if (!WriteBytes(processHandle, address, bytes))
        {
            return false;
        }

        var verified = ReadBytes(processHandle, address, bytes.Length);
        return verified.SequenceEqual(bytes);
    }

    private static string FormatBytes(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint bytesWritten);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
