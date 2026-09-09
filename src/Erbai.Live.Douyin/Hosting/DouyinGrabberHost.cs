using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Logging;

namespace Erbai.Live.Douyin.Hosting;

/// <summary>Grabber 宿主运行选项（端口/路径/配置全部可注入，docs/04 §3.1 可测性缝）。</summary>
public sealed record DouyinGrabberHostOptions
{
    /// <summary>抓包器 exe 路径（可注入；默认由组合根解析到应用目录 DouyinBarrageGrab/）。</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Grabber 配置（白名单 19 键由 <see cref="GrabberAppSettings"/> 定义）。</summary>
    public required IReadOnlyDictionary<string, object> AppSettings { get; init; }

    public string WsHost { get; init; } = "127.0.0.1";

    public int WsPort { get; init; } = 8888;

    public int ProxyPort { get; init; } = 8827;

    /// <summary>端口就绪探测次数（默认 30×250ms，docs/04 §3.2）。</summary>
    public int ReadyProbeAttempts { get; init; } = 30;

    public int ReadyProbeIntervalMs { get; init; } = 250;

    /// <summary>附加启动参数（测试注入假进程用，如 cmd 的 /c 脚本；生产为空）。</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    public ISystemProxyStore? ProxyStore { get; init; }

    public ITcpProbe? Probe { get; init; }

    public ILogBus? Logs { get; init; }
}

/// <summary>宿主运行状态（概览页/监督者查询）。</summary>
public sealed record GrabberStatus
{
    public bool Running { get; init; }

    /// <summary>本实例持有进程句柄；false = 端口被外部/残留进程服务（adopted）。</summary>
    public bool Managed { get; init; }

    public bool Adopted { get; init; }

    public int? Pid { get; init; }

    public string Error { get; init; } = "";
}

/// <summary>
/// 抖音抓包器子进程宿主（docs/04 §3.2 容错全量继承）：
/// - 配置下发：白名单 19 键 JSON（-config）写入临时文件，启动参数传入（取代改 .config XML）；
/// - 系统代理：启动前清理残留死代理；sysProxy 启用时快照；stop 仅当当前代理仍指向
///   <c>127.0.0.1:proxyPort</c> 才还原快照（绝不碰用户自己的 Clash 等代理）并广播刷新；
/// - 端口就绪探测 30×0.25s，超时回收进程报 port_not_ready；
/// - stdout 逐行读取：utf-8 失败回退 GBK，按中英文 token 分级转发 LogBus；
///   <c>[grabber-state]</c> JSON 行解析为结构化状态；
/// - 子进程树杀（terminate → 5s → kill tree），stdin 关闭。
/// 进程内循环任何异常不逃逸（worker 不变量）。
/// </summary>
public sealed class DouyinGrabberHost : IAsyncDisposable
{
    static DouyinGrabberHost()
    {
        // GBK(936) 回退解码需要 CodePages provider（04 §3.2 _decode_line）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private volatile DouyinGrabberHostOptions _options;
    private readonly ISystemProxyStore _proxy;
    private readonly ITcpProbe _probe;
    private readonly object _sync = new();
    private Process? _process;
    private string? _configFile;
    private ProxySnapshot? _proxySnapshot;
    private Task? _readerTask;
    private Task? _stderrTask;

    public DouyinGrabberHost(DouyinGrabberHostOptions options)
    {
        _options = options;
        _proxy = options.ProxyStore ?? new WinInetProxyStore();
        _probe = options.Probe ?? new TcpPortProbe();
    }

    /// <summary>
    /// 运行时更新抓包器配置（重启平台前由组合根按最新 AppConfig 调用，docs/00 阶段 4 #2 修复）：
    /// 白名单键/WS 端口/代理端口 随配置热刷新，不再依赖 CreateAsync 时的快照。
    /// </summary>
    public void UpdateConfig(
        IReadOnlyDictionary<string, object> appSettings,
        int wsPort,
        int proxyPort)
    {
        _options = _options with { AppSettings = appSettings, WsPort = wsPort, ProxyPort = proxyPort };
    }

    public string WsUrl => $"ws://{_options.WsHost}:{_options.WsPort}";

    /// <summary>抓包器 exe 路径（UI 预检用）。</summary>
    public string ExecutablePath => _options.ExecutablePath;

    /// <summary>是否持有子进程句柄（测试断言启动清理用）。</summary>
    internal bool HasProcess
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    private bool SysProxyEnabled =>
        bool.TryParse(_options.AppSettings.GetValueOrDefault("sysProxy")?.ToString(), out var v) && v;

    private string ProxyHostPort => $"127.0.0.1:{_options.ProxyPort}";

    private ILogBus? Logs => _options.Logs;

    // ── 生命周期 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动抓包器（bundled 语义）：端口已活则 adopt；否则清理残留代理 → 快照代理 →
    /// 下发配置 → 拉起进程 → 端口就绪探测。返回状态（异常返回 error 状态，不抛出）。
    /// </summary>
    public async Task<GrabberStatus> StartAsync(CancellationToken ct = default)
    {
        if (await PortReadyAsync())
        {
            Logs?.Information($"[抖音抓取器] 端口 {_options.WsPort} 已有服务，直接复用（external/adopt）");
            return new GrabberStatus { Running = true, Adopted = true };
        }

        await CleanupLeftoverProxyAsync();

        if (!File.Exists(_options.ExecutablePath))
        {
            return new GrabberStatus { Error = $"executable_not_found:{_options.ExecutablePath}" };
        }

        // 快照系统代理（必须在 WssBarrageServer 写代理之前）：强杀退出后据此还原。
        // TryRead 失败 → 放弃本轮代理接管（sysProxy 照常启动，但停止时不还原）：
        // 空快照会把用户的 ProxyServer/ProxyOverride 当抓包器残留删掉（数据损坏）
        if (SysProxyEnabled)
        {
            _proxySnapshot = await Task.Run(_proxy.TryRead, ct);
            if (_proxySnapshot is null)
            {
                Logs?.Warning("[抖音抓取器] 系统代理快照读取失败，本轮停止时将不还原代理（保护用户配置）");
            }
        }

        try
        {
            _configFile = WriteHostConfig();
        }
        catch (Exception ex)
        {
            _proxySnapshot = null;
            return new GrabberStatus { Error = $"config_write_failed:{ex.Message}" };
        }

        var psi = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath) ?? "",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            // 注意：不设 StandardOutputEncoding——读取循环自己做字节级 UTF-8→GBK 回退解码
            // （04 §3.2 _decode_line 平移；旧 net462 抓包器输出 GBK，新 .NET8 版输出 UTF-8）
        };
        foreach (var extra in _options.ExtraArguments)
        {
            psi.ArgumentList.Add(extra);
        }

        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(_configFile);

        try
        {
            var process = Process.Start(psi);
            if (process is null)
            {
                _proxySnapshot = null;
                DeleteConfigFile();
                return new GrabberStatus { Error = "start_failed:process_start_returned_null" };
            }

            lock (_sync)
            {
                _process = process;
            }

            _readerTask = Task.Run(() => ReadStreamLoopAsync(process.StandardOutput.BaseStream), CancellationToken.None);
            // stderr 必须消费：重定向后无人读会写满管道缓冲（~4KB）导致子进程阻塞
            // （Grabber 初始化失败路径会写 Console.Error；旧版 stderr=STDOUT 合并，这里
            // 用同一行解码/分级语义转发，错误文本同样进 LogBus）
            _stderrTask = Task.Run(() => ReadStreamLoopAsync(process.StandardError.BaseStream), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _proxySnapshot = null;
            DeleteConfigFile();
            return new GrabberStatus { Error = $"start_failed:{ex.Message}" };
        }

        // 端口就绪探测 30×0.25s；超时回收刚启动的进程（避免残留占端口）。
        // 启动被取消（ct）同样必须回收：进程已拉起、代理已快照、配置已写盘，
        // 不清理会留孤儿进程 + 系统代理劫持（docs/00 修复记录 #3）
        try
        {
            for (var i = 0; i < _options.ReadyProbeAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (await PortReadyAsync())
                {
                    return new GrabberStatus { Running = true, Managed = true, Pid = GetPid() };
                }

                await Task.Delay(_options.ReadyProbeIntervalMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Logs?.Warning($"[抖音抓取器] 启动被取消，回收已拉起进程");
            await StopAsync();
            throw;
        }

        Logs?.Warning($"[抖音抓取器] 端口 {_options.WsPort} 就绪超时，回收进程");
        await StopAsync();
        return new GrabberStatus { Error = "port_not_ready" };
    }

    /// <summary>停止抓包器并还原系统代理（幂等；docs/04 §3.2 铁律）。</summary>
    public async Task StopAsync()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            await KillProcessAsync(process);
        }

        // Fallback：端口仍活但本实例不持有进程（adopted 外部服务 / 上轮强杀遗留的
        // 孤儿）：按端口找 PID 并杀——"stop" 绝不静默 no-op（旧契约 docs/04 §3.2）。
        // 身份校验：只有进程名与抓包器 exe 匹配才杀——端口默认 8888 很常用，
        // 无差别强杀会误杀恰好占端口的无关程序（taskkill /T /F 不可逆）
        if (await PortReadyAsync())
        {
            var pid = await FindPidOnPortAsync();
            if (pid is not null)
            {
                if (IsGrabberProcess(pid.Value))
                {
                    Logs?.Warning($"[抖音抓取器] 端口 {_options.WsPort} 仍被 pid={pid} 占用，按端口回收");
                    await KillPidAsync(pid.Value);
                }
                else
                {
                    var name = TryGetProcessName(pid.Value) ?? "?";
                    Logs?.Warning(
                        $"[抖音抓取器] 端口 {_options.WsPort} 被 pid={pid}（{name}，非抓包器进程）占用，已跳过回收——如需释放端口请手动处理");
                }
            }
        }

        await RestoreSystemProxyAsync();
        DeleteConfigFile();
    }

    /// <summary>启动时清理上次强杀残留的系统代理（指向已死的抓包器端口时关闭）。
    /// 不按本次 sysProxy 开关门控：上次崩溃时可能 sysProxy=true 设过代理而本次
    /// 配置已改 false——残留代理仍会劫持系统流量，必须清（docs/00 修复记录 #8）。</summary>
    public async Task CleanupLeftoverProxyAsync()
    {
        var current = await Task.Run(_proxy.Read);
        if (current.ProxyEnable != "1")
        {
            return;
        }

        if (!current.ProxyServer.Contains(ProxyHostPort, StringComparison.OrdinalIgnoreCase))
        {
            return; // 代理指向别处（用户自己的代理），不动
        }

        if (await PortReadyAsync())
        {
            return; // 抓包器还在运行，代理有效
        }

        await Task.Run(() => _proxy.Restore(new ProxySnapshot { ProxyEnable = "0" }));
        Logs?.Warning($"[抖音抓取器] 检测到残留系统代理 {ProxyHostPort}（抓包器未运行），已关闭");
    }

    private async Task RestoreSystemProxyAsync()
    {
        var snapshot = _proxySnapshot;
        _proxySnapshot = null;
        if (snapshot is null || !SysProxyEnabled)
        {
            return;
        }

        var current = await Task.Run(_proxy.Read);
        if (current.ProxyEnable != "1")
        {
            return; // 代理已关闭（用户自己关的），无需处理
        }

        if (!current.ProxyServer.Contains(ProxyHostPort, StringComparison.OrdinalIgnoreCase))
        {
            return; // 代理指向别处（用户自己的代理），绝不还原
        }

        await Task.Run(() => _proxy.Restore(snapshot));
        Logs?.Information($"[抖音抓取器] 已还原系统代理（残留代理 {ProxyHostPort} 已清理）");
    }

    private async Task<bool> PortReadyAsync() =>
        await Task.Run(() => _probe.IsOpen(_options.WsHost, _options.WsPort, 400));

    private int? GetPid()
    {
        lock (_sync)
        {
            return _process is { HasExited: false } ? _process.Id : null;
        }
    }

    private static async Task KillProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 进程已退出/拒绝被杀：不致命
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>按端口找监听 PID（Windows netstat；旧 douyin_grabber_manager 兜底逻辑移植，docs/04 §3.2）。</summary>
    internal async Task<int?> FindPidOnPortAsync()
    {
        var port = _options.WsPort;
        return await Task.Run(() => FindPidOnPortCore(port));
    }

    private int? FindPidOnPortCore(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p TCP",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var needle = $":{port}";
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // TCP    0.0.0.0:8888    0.0.0.0:0    LISTENING    1234
                if (parts.Length >= 5 && parts[1].EndsWith(needle, StringComparison.Ordinal) &&
                    int.TryParse(parts[^1], out var pid))
                {
                    return pid;
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>taskkill 树杀（/T 子进程树 + /F 强杀；App 已整体提权，普通 taskkill 即可，
    /// 旧版的 UAC RunAs 变体在提权环境下不可达，不再移植——见 docs/00 修复记录 #6）。</summary>
    internal async Task<bool> KillPidAsync(int pid)
    {
        return await Task.Run(() => KillPidCore(pid));
    }

    /// <summary>
    /// 端口兜底杀进程前的身份校验：PID 进程名须与配置的抓包器 exe 同名
    /// （bundled 默认 WssBarrageServer）。名字不同 = 无关程序占用了端口
    /// （或用户自起的改名实例），绝不能 taskkill /T /F。
    /// </summary>
    internal bool IsGrabberProcess(int pid)
    {
        var expected = Path.GetFileNameWithoutExtension(_options.ExecutablePath);
        var name = TryGetProcessName(pid);
        return name is not null &&
               string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null; // 进程已退出 / 权限不足
        }
    }

    private bool KillPidCore(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── 配置下发 ────────────────────────────────────────────────────────────

    /// <summary>序列化下发配置（全量 19 键：默认值 + 覆盖 + wsListenPort 同步 + pushFilter 强 7）。</summary>
    internal static string BuildHostConfigJson(
        IReadOnlyDictionary<string, object> appSettings,
        int wsPort,
        bool keepFanClub = true)
    {
        var merged = new Dictionary<string, object>(GrabberAppSettings.CreateDefault(), StringComparer.Ordinal);
        foreach (var (key, value) in appSettings)
        {
            if (!GrabberAppSettings.AllKeys.Contains(key))
            {
                throw new ArgumentException($"未知 grabber 配置键: {key}");
            }

            merged[key] = value;
        }

        merged["wsListenPort"] = wsPort; // 推送端口必须与宿主 WS 连接一致
        if (keepFanClub)
        {
            // 强制保留 Type=7 粉丝团消息（点歌核心；04 §3.2 铁律，双保险）
            var filter = (merged["pushFilter"]?.ToString() ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (!filter.Contains("7"))
            {
                filter.Add("7");
            }

            merged["pushFilter"] = string.Join(",", filter);
        }

        return JsonSerializer.Serialize(
            merged,
            new JsonSerializerOptions { WriteIndented = true });
    }

    private string WriteHostConfig()
    {
        var json = BuildHostConfigJson(_options.AppSettings, _options.WsPort);
        var path = Path.Combine(
            Path.GetTempPath(),
            $"erbai-grabber-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private void DeleteConfigFile()
    {
        var path = _configFile;
        _configFile = null;
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    // ── stdout 读取与日志分级 ───────────────────────────────────────────────

    private static readonly UTF8Encoding Utf8Strict = new(false, throwOnInvalidBytes: false);
    private static Encoding? _gbk;

    /// <summary>UTF-8 解码；含替换符（U+FFFD）时回退 GBK（旧 _decode_line 语义，04 §3.2）。</summary>
    internal static string DecodeLine(byte[] bytes)
    {
        var text = Utf8Strict.GetString(bytes);
        if (!text.Contains('\uFFFD'))
        {
            return text;
        }

        try
        {
            _gbk ??= Encoding.GetEncoding(936);
            return _gbk.GetString(bytes);
        }
        catch (Exception)
        {
            return text; // GBK 不可用时保留 UTF-8 替换结果
        }
    }

    private async Task ReadStreamLoopAsync(Stream stream)
    {
        var buffer = new byte[1 << 15];
        var pending = new MemoryStream();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    // EOF：冲刷剩余行
                    FlushPending(pending);
                    break;
                }

                pending.Write(buffer, 0, read);
                FlushPending(pending);
            }
        }
        catch (Exception)
        {
            // 管道异常/进程消失：读取循环结束（不逃逸）
        }
    }

    private void FlushPending(MemoryStream pending)
    {
        var bytes = pending.GetBuffer();
        var count = (int)pending.Length;
        var start = 0;
        for (var i = 0; i < count; i++)
        {
            if (bytes[i] != (byte)'\n')
            {
                continue;
            }

            var lineBytes = new byte[i - start];
            Array.Copy(bytes, start, lineBytes, 0, lineBytes.Length);
            ForwardLine(TrimLine(lineBytes));
            start = i + 1;
        }

        if (start > 0)
        {
            var rest = new byte[count - start];
            Array.Copy(bytes, start, rest, 0, rest.Length);
            pending.SetLength(0);
            pending.Write(rest, 0, rest.Length);
        }
    }

    private static byte[] TrimLine(byte[] line)
    {
        var end = line.Length;
        while (end > 0 && (line[end - 1] == (byte)'\r' || line[end - 1] == (byte)'\n'))
        {
            end--;
        }

        if (end == line.Length)
        {
            return line;
        }

        var trimmed = new byte[end];
        Array.Copy(line, trimmed, end);
        return trimmed;
    }

    private void ForwardLine(byte[] lineBytes)
    {
        if (lineBytes.Length == 0)
        {
            return;
        }

        var line = DecodeLine(lineBytes);
        if (line.Length == 0)
        {
            return;
        }

        ForwardLine(line);
    }

    private void ForwardLine(string line)
    {
        // 结构化状态行（headless 状态日志，GrabberState.Prefix）
        if (line.StartsWith("[grabber-state]", StringComparison.Ordinal))
        {
            try
            {
                var payload = line["[grabber-state]".Length..].Trim();
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("event", out var evt))
                {
                    var name = evt.GetString();
                    if (name == "ready")
                    {
                        Logs?.Information($"[抖音抓取器] 就绪（{payload}）");
                    }
                    else if (name == "error")
                    {
                        Logs?.Error($"[抖音抓取器] 状态错误: {payload}");
                    }
                }

                return;
            }
            catch (JsonException)
            {
                // 落入普通日志分级
            }
        }

        var level = GrabberLogClassifier.Classify(line);
        switch (level)
        {
            case GrabberLogLevel.Error:
                Logs?.Error($"[抖音抓取器] {line}");
                break;
            case GrabberLogLevel.Warning:
                Logs?.Warning($"[抖音抓取器] {line}");
                break;
            default:
                Logs?.Information($"[抖音抓取器] {line}");
                break;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
