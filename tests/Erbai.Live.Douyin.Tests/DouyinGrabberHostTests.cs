using System.Diagnostics;
using System.Text;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Logging;
using Erbai.Core.Logging;
using Erbai.Live.Douyin.Hosting;

namespace Erbai.Live.Douyin.Tests;

/// <summary>Fake 注册表（ISystemProxyStore）：绝不打真注册表（docs/04 §3.2 可测性缝）。</summary>
internal sealed class FakeProxyStore : ISystemProxyStore
{
    public ProxySnapshot Current { get; set; } = new();

    public ProxySnapshot? LastRestore { get; private set; }

    public int RestoreCount { get; private set; }

    /// <summary>Read 调用次数（诊断：快照是否已取）。</summary>
    public int ReadCount { get; private set; }

    /// <summary>第 <see cref="SwitchAfterReadCount"/> 次 Read 起返回该值（模拟 Grabber 启动后改写代理）。</summary>
    public ProxySnapshot? SwitchToAfterRead { get; set; }

    public int SwitchAfterReadCount { get; set; } = int.MaxValue;

    /// <summary>TryRead 返回 null（模拟注册表读取失败：杀软锁键等）。</summary>
    public bool FailTryRead { get; set; }

    public ProxySnapshot Read()
    {
        ReadCount++;
        return SwitchToAfterRead is not null && ReadCount >= SwitchAfterReadCount
            ? SwitchToAfterRead
            : Current;
    }

    public ProxySnapshot? TryRead()
    {
        ReadCount++;
        if (FailTryRead)
        {
            return null;
        }

        return SwitchToAfterRead is not null && ReadCount >= SwitchAfterReadCount
            ? SwitchToAfterRead
            : Current;
    }

    public void Restore(ProxySnapshot snapshot)
    {
        LastRestore = snapshot;
        RestoreCount++;
        Current = snapshot;
    }
}

internal sealed class FakeProbe : ITcpProbe
{
    /// <summary>前 <see cref="FailFirst"/> 次探测返回 false（模拟"启动前端口未就绪"），之后返回 <see cref="Result"/>。</summary>
    public int FailFirst { get; set; }

    public bool Result { get; set; }

    private int _calls;

    /// <summary>最近一次探测的端口（UpdateConfig 生效验证用）。</summary>
    public int? LastPort { get; private set; }

    public bool IsOpen(string host, int port, int timeoutMs)
    {
        LastPort = port;
        return ++_calls > FailFirst && Result;
    }
}

/// <summary>DouyinGrabberHost：配置下发 / 端口就绪 / 进程管理 / stdout 分级转发 / 代理铁律。</summary>
public class DouyinGrabberHostTests
{
    private static IReadOnlyDictionary<string, object> SysProxySettings() => new Dictionary<string, object>
    {
        ["sysProxy"] = true,
        ["proxyPort"] = 18827,
        ["pushFilter"] = "1,4,5,7",
    };

    private static string PowerShellExe =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>假进程：输出一行分级样例行 + 一行 [grabber-state] ready，随后保持 20s 存活。</summary>
    private static readonly string[] FakeKeepAliveScript =
    [
        "-NoProfile",
        "-Command",
        "Write-Output 'an error occurred'; Write-Output '[grabber-state] {\"event\":\"ready\",\"wsPort\":18888}'; Start-Sleep 20",
    ];

    // ── 配置 JSON 生成 ──────────────────────────────────────────────────────

    [Fact]
    public void 配置JSON含全量19键且wsListenPort同步()
    {
        var json = DouyinGrabberHost.BuildHostConfigJson(
            SysProxySettings(), wsPort: 18888);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(18888, doc.RootElement.GetProperty("wsListenPort").GetInt32());
        Assert.True(doc.RootElement.GetProperty("sysProxy").GetBoolean());
        Assert.Equal(19, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void 配置JSON强制保留Type7()
    {
        var json = DouyinGrabberHost.BuildHostConfigJson(
            new Dictionary<string, object> { ["pushFilter"] = "1,4,5" }, wsPort: 8888);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("1,4,5,7", doc.RootElement.GetProperty("pushFilter").GetString());
    }

    [Fact]
    public void 未知配置键抛错()
    {
        Assert.Throws<ArgumentException>(() =>
            DouyinGrabberHost.BuildHostConfigJson(
                new Dictionary<string, object> { ["bogus"] = 1 }, wsPort: 8888));
    }

    // ── 代理铁律（fake 注册表） ─────────────────────────────────────────────

    [Fact]
    public async Task 残留代理清理_端口无监听时关闭()
    {
        var store = new FakeProxyStore
        {
            Current = new ProxySnapshot { ProxyEnable = "1", ProxyServer = "127.0.0.1:18827" },
        };
        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = "unused",
            AppSettings = SysProxySettings(),
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = new FakeProbe { Result = false },
        });

        await host.CleanupLeftoverProxyAsync();

        Assert.Equal(1, store.RestoreCount);
        Assert.Equal("0", store.LastRestore!.ProxyEnable); // 只关代理，不动其他键
    }

    [Fact]
    public async Task 残留代理清理不受本次sysProxy开关门控()
    {
        // #8：上次崩溃时 sysProxy=true 设的残留代理，本次配置改 false 也必须清
        var store = new FakeProxyStore
        {
            Current = new ProxySnapshot { ProxyEnable = "1", ProxyServer = "127.0.0.1:18827" },
        };
        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = "unused",
            AppSettings = new Dictionary<string, object>
            {
                ["sysProxy"] = false, // 本次运行不启用代理
                ["proxyPort"] = 18827,
            },
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = new FakeProbe { Result = false },
        });

        await host.CleanupLeftoverProxyAsync();

        Assert.Equal(1, store.RestoreCount);
        Assert.Equal("0", store.LastRestore!.ProxyEnable);
    }

    [Fact]
    public async Task 残留代理清理_端口活着或指向别处时不动()
    {
        // 端口活着（外部抓包器在跑）
        var store = new FakeProxyStore
        {
            Current = new ProxySnapshot { ProxyEnable = "1", ProxyServer = "127.0.0.1:18827" },
        };
        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = "unused",
            AppSettings = SysProxySettings(),
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = new FakeProbe { Result = true },
        });
        await host.CleanupLeftoverProxyAsync();
        Assert.Equal(0, store.RestoreCount);

        // 代理指向用户自己的代理（Clash 等）
        store.Current = new ProxySnapshot { ProxyEnable = "1", ProxyServer = "127.0.0.1:7890" };
        host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = "unused",
            AppSettings = SysProxySettings(),
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = new FakeProbe { Result = false },
        });
        await host.CleanupLeftoverProxyAsync();
        Assert.Equal(0, store.RestoreCount);
    }

    [Fact]
    public async Task Stop还原代理_仅当仍指向抓包器端口()
    {
        var store = new FakeProxyStore
        {
            // 用户原代理=用户自己的 Clash（指向 7890；快照应保留它）
            Current = new ProxySnapshot { ProxyEnable = "1", ProxyServer = "127.0.0.1:7890" },
        };
        var logs = new LogBus();
        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = PowerShellExe,
            AppSettings = SysProxySettings(),
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = new FakeProbe { Result = true, FailFirst = 1 }, // 先未就绪→启动进程→就绪
            Logs = logs,
            ExtraArguments = FakeKeepAliveScript,
        });

        // 启动：快照=用户 Clash；cleanup 不会动它（代理指向 7890，非抓包器端口）
        var status = await host.StartAsync();
        Assert.True(status.Running);
        Assert.Equal(0, store.RestoreCount);

        // 抓包器运行期间代理仍指向用户自己的代理（从未被 Grabber 改写）
        await host.StopAsync();

        // 当前代理指向别处（用户的代理）→ 绝不还原快照
        Assert.Equal(0, store.RestoreCount);
    }

    [Fact]
    public async Task 快照读取失败_停止时绝不还原_保护用户代理()
    {
        // H6 回归：Read 失败曾被吞成空快照——停止时空快照会删掉用户自己的
        // ProxyServer。修复后 TryRead=null → 放弃还原，用户代理原样保留。
        var store = new FakeProxyStore
        {
            Current = new ProxySnapshot { ProxyEnable = "1", ProxyServer = "127.0.0.1:7890" },
            FailTryRead = true, // 快照阶段注册表读取失败
        };
        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = PowerShellExe,
            AppSettings = SysProxySettings(),
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = new FakeProbe { Result = true, FailFirst = 1 },
            ExtraArguments = FakeKeepAliveScript,
        });

        var status = await host.StartAsync();
        Assert.True(status.Running);

        // 抓包器运行期间代理被改写为其端口
        store.Current = new ProxySnapshot { ProxyEnable = "1", ProxyServer = "127.0.0.1:18827" };
        store.FailTryRead = false; // 停止时读当前值正常（但快照为 null）

        await host.StopAsync();

        // 快照缺失 → 不还原（宁留残留代理给下次启动清理，也不冒删用户配置的险）
        Assert.Equal(0, store.RestoreCount);
    }

    [Fact]
    public async Task 启动_快照_停止还原全链路()
    {
        var store = new FakeProxyStore
        {
            // 启动前快照（用户原代理=关闭）
            Current = new ProxySnapshot { ProxyEnable = "0" },
        };
        var logs = new LogBus();
        var probe = new FakeProbe { Result = true, FailFirst = 1 }; // 先未就绪→启动进程→就绪
        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = PowerShellExe,
            AppSettings = SysProxySettings(),
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = probe,
            Logs = logs,
            ExtraArguments = FakeKeepAliveScript,
        });

        var status = await host.StartAsync();
        Assert.True(status.Running);
        Assert.True(status.Managed);
        Assert.NotNull(status.Pid);

        // 抓包器运行期间代理被改写（Grabber 写入 18827）
        store.Current = new ProxySnapshot { ProxyEnable = "1", ProxyServer = "127.0.0.1:18827" };

        await host.StopAsync();

        // 代理还原为快照（ProxyEnable=0）
        Assert.Equal(1, store.RestoreCount);
        Assert.Equal("0", store.LastRestore!.ProxyEnable);
        // 进程已死
        Process? p = null;
        try
        {
            p = Process.GetProcessById(status.Pid!.Value);
        }
        catch (ArgumentException)
        {
        }

        Assert.True(p is null || p.HasExited);
    }

    // ── stdout 分级转发 ────────────────────────────────────────────────────

    [Fact]
    public async Task stdout分级转发到LogBus()
    {
        var logs = new LogBus();
        using var sub = logs.Subscribe(capacity: 512);
        var store = new FakeProxyStore();
        var probe = new FakeProbe { Result = true, FailFirst = 1 };
        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = PowerShellExe,
            AppSettings = new Dictionary<string, object> { ["sysProxy"] = false },
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = probe,
            Logs = logs,
            ExtraArguments = FakeKeepAliveScript,
        });

        await host.StartAsync();

        // 等日志到达（异步读取循环）
        List<LogEntry> entries = [];
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            while (sub.Reader.TryRead(out var entry))
            {
                entries.Add(entry);
            }

            if (entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("error occurred")) &&
                entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("就绪")))
            {
                break;
            }

            await Task.Delay(100);
        }

        await host.StopAsync();

        Assert.Contains(entries, e => e.Level == LogLevel.Error && e.Message.Contains("error occurred"));
        Assert.Contains(entries, e => e.Level == LogLevel.Information && e.Message.Contains("就绪"));
    }

    // ── 阶段 4 修复回归（docs/00 修复记录） ─────────────────────────────────

    [Fact]
    public async Task 启动被取消时回收进程并还原代理()
    {
        // #3：StartAsync 探测循环抛 OCE 时，进程已拉起、代理已快照——必须清理
        var store = new FakeProxyStore
        {
            Current = new ProxySnapshot { ProxyEnable = "0" }, // 用户原代理=关闭（快照）
            // 第 3 次 Read 起模拟 Grabber 已把系统代理改写为抓包器端口（cleanup=1, 快照=2）
            SwitchToAfterRead = new ProxySnapshot { ProxyEnable = "1", ProxyServer = "127.0.0.1:18827" },
            SwitchAfterReadCount = 3,
        };
        var logs = new LogBus();
        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = PowerShellExe,
            AppSettings = SysProxySettings(),
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = new FakeProbe { Result = false }, // 永不就绪 → 探测循环跑满
            Logs = logs,
            ExtraArguments = FakeKeepAliveScript,
        });

        using var cts = new CancellationTokenSource();
        // 足够长：PowerShell 假进程启动（~500ms）后仍处于探测循环（FakeProbe 永不就绪），
        // 取消必然落在探测循环内（进程已拉起、快照已取），验证完整清理路径
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(cts.Token));
        Assert.True(cts.IsCancellationRequested);

        // 进程被回收 + 代理快照被还原（当前代理仍指向抓包器端口→按铁律还原）+ 配置临时文件被删
        Assert.False(host.HasProcess);
        Assert.Equal(1, store.RestoreCount);
        Assert.Equal("0", store.LastRestore!.ProxyEnable);
    }

    [Fact]
    public async Task 端口兜底找到监听PID()
    {
        // #5：FindPidOnPortAsync 用 netstat 按端口找 PID（本进程监听随机端口）
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
            {
                ExecutablePath = "unused",
                AppSettings = SysProxySettings(),
                WsPort = port,
                ProxyPort = 18827,
            });
            var pid = await host.FindPidOnPortAsync();
            Assert.Equal(Environment.ProcessId, pid);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 端口兜底杀掉外部进程()
    {
        // #5：KillPidAsync 用 taskkill /T /F 树杀（杀测试自起的子进程，不碰外部）
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellExe,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("Start-Sleep 30");
        using var child = Process.Start(psi)!;
        await Task.Delay(300);
        Assert.False(child.HasExited);

        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = "unused",
            AppSettings = SysProxySettings(),
            WsPort = 18888,
            ProxyPort = 18827,
        });
        var killed = await host.KillPidAsync(child.Id);
        Assert.True(killed);
        child.WaitForExit(5000);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task StopAsync_端口被无关进程占用_不误杀()
    {
        // H5 回归：此前 StopAsync 的端口兜底对占用者无差别 taskkill /T /F——
        // 默认端口 8888 常被无关程序占用，点"停止"即误杀。现在杀前校验
        // 进程名与抓包器 exe 同名，不匹配只告警。
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            // ExecutablePath 指向不存在的 WssBarrageServer.exe → 占用者（本测试
            // 进程 testhost）名字不匹配 → 必须跳过回收
            var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
            {
                ExecutablePath = @"Z:\nowhere\WssBarrageServer.exe",
                AppSettings = SysProxySettings(),
                WsPort = port,
                ProxyPort = 18827,
            });

            Assert.False(host.IsGrabberProcess(Environment.ProcessId)); // 本进程非抓包器
            await host.StopAsync(); // 端口活、PID=本测试进程、名字不匹配 → 不杀

            Assert.False(Environment.HasShutdownStarted); // 测试进程还活着（没自杀）
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task UpdateConfig后启动使用新端口()
    {
        // #2：重启平台前 UpdateConfig 生效（探测端口 / 下发配置端口同步）
        var store = new FakeProxyStore();
        var probe = new FakeProbe { Result = true, FailFirst = 1 };
        var host = new DouyinGrabberHost(new DouyinGrabberHostOptions
        {
            ExecutablePath = PowerShellExe,
            AppSettings = new Dictionary<string, object> { ["sysProxy"] = false },
            WsPort = 18888,
            ProxyPort = 18827,
            ProxyStore = store,
            Probe = probe,
            ExtraArguments = FakeKeepAliveScript,
        });

        host.UpdateConfig(
            new Dictionary<string, object> { ["sysProxy"] = false, ["wsListenPort"] = 19001 },
            wsPort: 19001,
            proxyPort: 19027);

        var status = await host.StartAsync();
        Assert.True(status.Running);
        Assert.Equal(19001, probe.LastPort); // 探测与配置都用新端口
        await host.StopAsync();
    }
}
