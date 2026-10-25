using System.Text;
using Erbai.Contracts.Plugins;
using Erbai.Core.Events;
using Erbai.Core.Hosting;
using Erbai.Core.Plugins;

namespace Erbai.Core.Tests;

/// <summary>
/// 目录式插件加载器测试（Stage A，决策 #4/#16）：
/// fake 插件 DLL 由 Erbai.TestPlugins 三个工程编译并经 csproj 拷到测试输出 plugins\ 布局，
/// 测试按真实「第三方插件」形态加载（每目录 config.ini + dll）。
/// 覆盖：三类插件注册/生命周期、config.ini 解析（自定义配置项）、启停标记（ini + .disabled）、
/// 契约版本校验、按目录错误隔离、共享契约（拒绝插件目录内 Erbai.Contracts 副本）、卸载与 ALC 回收。
/// </summary>
/// <summary>插件 ALC 加载类测试互斥串行（2026-10 长稳轮）：并行时相互的
/// CollectGarbage/ALC 卸载会干扰对方正在进行的程序集加载（间歇性空注册）。</summary>
[Collection("plugin-alc")]
public class PluginLoaderTests : IDisposable
{
    private static string SourcePluginsRoot => Path.Combine(AppContext.BaseDirectory, "plugins");

    private readonly string _root;

    public PluginLoaderTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "tmp", $"plugin-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }


    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不拖垮测试结论
        }
    }

    private PluginLoaderOptions Options() => new() { PluginsRoot = _root };

    /// <summary>把 csproj 预拷贝的插件源目录复制到本次测试根，返回目标目录。</summary>
    /// <summary>
    /// 文件锁容错重试（2026-10 长稳轮）：本机环境实测（Defender 实时扫描/并行测试
    /// 宿主）偶发锁住新构建的插件 DLL，导致热加载类测试间歇性失败（origin/main
    /// 同样复现，非代码回归）。对"文件被占用"类异常重试一次（500ms 后）。
    /// </summary>
    private static async Task WithFileLockRetryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (IsFileLock(ex))
        {
            await Task.Delay(500);
            await action();
        }
    }

    private static bool IsFileLock(Exception ex) =>
        ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("正在使用", StringComparison.Ordinal) ||
        ex.Message.Contains("另一个程序", StringComparison.Ordinal) ||
        ex.InnerException is { } inner && IsFileLock(inner);

    private string SeedPlugin(string sourceName, string? dirName = null)
    {
        var src = Path.Combine(SourcePluginsRoot, sourceName);
        var dst = Path.Combine(_root, dirName ?? sourceName);
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            // 播种必须幂等 + 抗源文件瞬时锁：
            // - overwrite:true——WithFileLockRetryAsync 重跑测试体时同 _root 重播种不得
            //   以"文件已存在"IOException 掩盖首遍的原始错误（2026-10 实测定位）；
            // - 复制重试——bin/plugins 源副本会被上一轮 testhost 残留进程/杀毒扫描
            //   短暂锁住（实测间歇 MSB3021/IOException），退避重试即可穿过。
            CopyWithRetry(file, Path.Combine(dst, Path.GetFileName(file)));
        }

        return dst;
    }

    private static void CopyWithRetry(string src, string dst)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(src, dst, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void WriteIni(string dir, string file, bool enabled = true, string? contractVersion = null,
        string? name = null, string extraSections = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("[General]");
        sb.AppendLine($"Name = {name ?? Path.GetFileName(dir)}");
        sb.AppendLine("Description = 测试插件");
        sb.AppendLine("Developer = erbai-tests");
        sb.AppendLine($"File = {file}");
        sb.AppendLine("Version = 1.0.0");
        if (contractVersion is not null)
        {
            sb.AppendLine($"ContractVersion = {contractVersion}");
        }

        if (!enabled)
        {
            sb.AppendLine("Enabled = false");
        }

        if (extraSections.Length > 0)
        {
            sb.Append(extraSections);
        }

        File.WriteAllText(Path.Combine(dir, "config.ini"), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>三个正例插件目录 + config.ini 就绪，返回 loader 与 host（未 Start）。</summary>
    private (PluginLoader Loader, ModuleHost Host) LoadThree(ModuleHost? host = null)
    {
        SeedPlugin("feature-module");
        SeedPlugin("player-module");
        SeedPlugin("live-platform");
        WriteIni(Path.Combine(_root, "feature-module"), "FakeFeatureModule.dll");
        WriteIni(Path.Combine(_root, "player-module"), "FakePlayerModule.dll");
        WriteIni(Path.Combine(_root, "live-platform"), "FakeLivePlatform.dll");

        var loader = new PluginLoader(Options());
        var h = host ?? new ModuleHost();
        loader.LoadAll(h);
        return (loader, h);
    }

    [Fact]
    public void LoadAll_ThreeKinds_RegisterIntoHost()
    {
        var (loader, host) = LoadThree();

        var byKey = loader.Loaded.ToDictionary(i => i.Key);
        Assert.Equal(3, byKey.Count);
        Assert.Equal(ModuleKind.FeatureModule, byKey["fake-feature"].Kind);
        Assert.Equal(ModuleKind.Player, byKey["fake-player"].Kind);
        Assert.Equal(ModuleKind.LivePlatform, byKey["fake-live"].Kind);
        Assert.Equal("Fake 功能模块", byKey["fake-feature"].DisplayName);

        Assert.Contains(host.Modules, m => m.Key == "fake-feature");
        Assert.Contains(host.Modules, m => m.Key == "fake-player");
        Assert.Contains(host.Modules, m => m.Key == "fake-live");
    }

    [Fact]
    public async Task LoadAll_ThenStartAll_LifecycleWorks()
    {
        var (loader, host) = LoadThree();
        await host.StartAllAsync(CreateContext());

        Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == "fake-feature").State);
        Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == "fake-player").State);
        Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == "fake-live").State);

        await host.StopAllAsync();
        Assert.Equal(ModuleState.Stopped, host.Modules.Single(m => m.Key == "fake-live").State);
    }

    [Fact]
    public void CustomConfigItems_AreParsedIntoManifest()
    {
        SeedPlugin("feature-module");
        WriteIni(Path.Combine(_root, "feature-module"), "FakeFeatureModule.dll",
            extraSections: """
                [ToggleKey]
                Name = 重载快捷键
                Type = key
                Value = 118

                [ShowFPS]
                Name = 帧数开关
                Type = bool
                Value = 1
                """);

        var loader = new PluginLoader(Options());
        var host = new ModuleHost();
        var result = loader.LoadAll(host);

        var info = Assert.Single(result.Loaded);
        Assert.Collection(
            info.Manifest.Config,
            item =>
            {
                Assert.Equal("ToggleKey", item.Key);
                Assert.Equal("重载快捷键", item.Name);
                Assert.Equal("key", item.Type);
                Assert.Equal("118", item.Value);
            },
            item =>
            {
                Assert.Equal("ShowFPS", item.Key);
                Assert.Equal("bool", item.Type);
                Assert.Equal("1", item.Value);
            });
    }

    [Fact]
    public void Disabled_ByIniFlag_And_ByDllRename_AreSkipped()
    {
        var feature = SeedPlugin("feature-module");
        var player = SeedPlugin("player-module");
        SeedPlugin("live-platform");
        WriteIni(feature, "FakeFeatureModule.dll", enabled: false);
        WriteIni(player, "FakePlayerModule.dll");
        WriteIni(Path.Combine(_root, "live-platform"), "FakeLivePlatform.dll");

        // FufuLauncher 形态：手改扩展名禁用（config.ini 里的 File 对应文件不存在，但 .disabled 变体存在）
        File.Move(Path.Combine(player, "FakePlayerModule.dll"), Path.Combine(player, "FakePlayerModule.dll.disabled"));

        var loader = new PluginLoader(Options());
        var host = new ModuleHost();
        var result = loader.LoadAll(host);

        Assert.Equal(2, result.Disabled.Count);
        Assert.Contains(result.Disabled, m => m.DirectoryKey == "feature-module");
        Assert.Contains(result.Disabled, m => m.DirectoryKey == "player-module");
        Assert.Empty(result.Errors);
        var loaded = Assert.Single(result.Loaded);
        Assert.Equal("fake-live", loaded.Key);
    }

    [Fact]
    public void ContractVersionMismatch_IsRejected_WithReason()
    {
        SeedPlugin("feature-module");
        SeedPlugin("player-module");
        var live = SeedPlugin("live-platform");
        WriteIni(Path.Combine(_root, "feature-module"), "FakeFeatureModule.dll");
        WriteIni(Path.Combine(_root, "player-module"), "FakePlayerModule.dll");
        WriteIni(live, "FakeLivePlatform.dll", contractVersion: "9.0.0");

        var loader = new PluginLoader(Options());
        var host = new ModuleHost();
        var result = loader.LoadAll(host);

        var error = Assert.Single(result.Errors, e => e.DirectoryKey == "live-platform");
        Assert.Contains("契约版本", error.Reason);
        Assert.Equal(2, result.Loaded.Count);
    }

    [Fact]
    public void BrokenDirectories_AreIsolated_OthersStillLoad()
    {
        // 无 config.ini 的目录
        var noIni = Path.Combine(_root, "broken-no-ini");
        Directory.CreateDirectory(noIni);
        File.WriteAllText(Path.Combine(noIni, "FakeFeatureModule.dll"), "not a real dll");

        // config.ini 声明了不存在的 DLL
        var missingDll = SeedPlugin("player-module");
        WriteIni(missingDll, "NoSuchDll.dll");

        SeedPlugin("feature-module");
        SeedPlugin("live-platform");
        WriteIni(Path.Combine(_root, "feature-module"), "FakeFeatureModule.dll");
        WriteIni(Path.Combine(_root, "live-platform"), "FakeLivePlatform.dll");

        var loader = new PluginLoader(Options());
        var host = new ModuleHost();
        var result = loader.LoadAll(host);

        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.DirectoryKey == "broken-no-ini");
        Assert.Contains(result.Errors, e => e.DirectoryKey == "player-module");
        Assert.Equal(2, result.Loaded.Count);
        Assert.Contains(result.Loaded, i => i.Key == "fake-feature");
        Assert.Contains(result.Loaded, i => i.Key == "fake-live");
    }

    [Fact]
    public void SharedContract_LocalContractsCopyInPluginDir_IsNotLoaded()
    {
        // 第三方错误地把 Erbai.Contracts.dll 副本打进插件目录：共享契约策略必须仍解析宿主版本
        // （若加载了副本，IFeatureModule 强转会失败 → 注册抛异常 → 测试失败）
        var dir = SeedPlugin("feature-module");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Erbai.Contracts.dll"),
            Path.Combine(dir, "Erbai.Contracts.dll"));
        WriteIni(dir, "FakeFeatureModule.dll");

        var loader = new PluginLoader(Options());
        var host = new ModuleHost();
        var result = loader.LoadAll(host);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Loaded, i => i.Key == "fake-feature");
    }

    [Fact]
    public void DuplicateKey_IsReported_OtherPluginsUnaffected()
    {
        SeedPlugin("feature-module");
        var dup = SeedPlugin("feature-module", "feature-module-dup");
        WriteIni(Path.Combine(_root, "feature-module"), "FakeFeatureModule.dll");
        WriteIni(dup, "FakeFeatureModule.dll");
        SeedPlugin("live-platform");
        WriteIni(Path.Combine(_root, "live-platform"), "FakeLivePlatform.dll");

        var loader = new PluginLoader(Options());
        var host = new ModuleHost();
        var result = loader.LoadAll(host);

        var error = Assert.Single(result.Errors, e => e.DirectoryKey == "feature-module-dup");
        Assert.Contains("already registered", error.Reason);
        Assert.Equal(2, result.Loaded.Count); // feature + live（重复的第二个被隔离）
    }

    [Fact]
    public async Task Unload_StopsAndRemoves_AlcIsCollectible_ReloadWorks()
    {
        await WithFileLockRetryAsync(async () =>
        {
        var (loader, host) = LoadThree();
        await host.StartAllAsync(CreateContext());

        var weakRef = loader.WeakRefOf("fake-feature");
        Assert.NotNull(weakRef);

        Assert.True(await loader.UnloadAsync("fake-feature", host));

        Assert.DoesNotContain(host.Modules, m => m.Key == "fake-feature");
        Assert.DoesNotContain(loader.Loaded, i => i.Key == "fake-feature");
        // 其余插件不受影响
        Assert.Contains(host.Modules, m => m.Key == "fake-player");
        Assert.Contains(host.Modules, m => m.Key == "fake-live");

        CollectGarbage();
        Assert.False(weakRef!.IsAlive);

        // 同目录可重新加载（新 ModuleHost：无残留注册/程序集锁，证明 ALC 已回收）
        var host2 = new ModuleHost();
        var loader2 = new PluginLoader(Options());
        var result2 = loader2.LoadAll(host2);
        Assert.True(result2.Loaded.Any(i => i.Key == "fake-feature"),
            $"重载后无 fake-feature。目录状态：{string.Join("；", loader2.Directories.Select(d => $"{d.DirectoryKey}={d.Status}:{d.Error}"))}；" +
            $"文件：{string.Join("；", Directory.EnumerateFiles(Path.Combine(_root, "feature-module")).Select(Path.GetFileName))}");
        });
    }

    [Fact]
    public void PluginRootMissing_ReturnsEmpty()
    {
        var loader = new PluginLoader(new PluginLoaderOptions { PluginsRoot = Path.Combine(_root, "not-exist") });
        var result = loader.LoadAll(new ModuleHost());
        Assert.Empty(result.Loaded);
        Assert.Empty(result.Disabled);
        Assert.Empty(result.Errors);
        Assert.Empty(loader.Directories);
    }

    // ── 插件管理 UI 面（Stage A 遗留①）：目录视图 + 启停 ────────────────────────

    [Fact]
    public void Directories_View_ContainsLoadedDisabledFailed()
    {
        SeedPlugin("feature-module");
        var disabled = SeedPlugin("player-module", "disabled-module");
        WriteIni(Path.Combine(_root, "feature-module"), "FakeFeatureModule.dll");
        WriteIni(disabled, "FakePlayerModule.dll", enabled: false);
        // 无 config.ini 目录 → Failed
        Directory.CreateDirectory(Path.Combine(_root, "broken-no-ini"));

        var loader = new PluginLoader(Options());
        loader.LoadAll(new ModuleHost());

        var view = loader.Directories.ToDictionary(d => d.DirectoryKey);
        Assert.Equal(3, view.Count);

        var loaded = view["feature-module"];
        Assert.Equal(PluginDirectoryStatus.Loaded, loaded.Status);
        Assert.Equal(["fake-feature"], loaded.RegisteredKeys);
        Assert.NotNull(loaded.Manifest);

        var dis = view["disabled-module"];
        Assert.Equal(PluginDirectoryStatus.Disabled, dis.Status);
        Assert.Equal("disabled-module", dis.Manifest?.Name); // config.ini 的 Name（解析成功仍在 manifest 里）
        Assert.Empty(dis.RegisteredKeys);

        var failed = view["broken-no-ini"];
        Assert.Equal(PluginDirectoryStatus.Failed, failed.Status);
        Assert.Null(failed.Manifest);
        Assert.Contains("config.ini", failed.Error);
    }

    [Fact]
    public async Task SetEnabled_Disable_LoadsIniAndUnloadsPlugin()
    {
        var feature = SeedPlugin("feature-module");
        WriteIni(feature, "FakeFeatureModule.dll");
        var host = new ModuleHost();
        var loader = new PluginLoader(Options());
        loader.LoadAll(host);
        await host.StartAllAsync(CreateContext());

        var message = await loader.SetEnabledAsync("feature-module", enabled: false, host);

        Assert.Contains("已禁用", message);
        Assert.DoesNotContain(host.Modules, m => m.Key == "fake-feature");
        var dirView = loader.Directories.Single(d => d.DirectoryKey == "feature-module");
        Assert.Equal(PluginDirectoryStatus.Disabled, dirView.Status);
        // UI 开关读 manifest.Enabled：必须与磁盘一致（否则显示仍旧值 → 实测「关不掉」）
        Assert.False(dirView.Manifest!.Enabled);
        Assert.Contains("Enabled = false", File.ReadAllText(Path.Combine(feature, "config.ini")));

        // 重启视角：新 loader 不再加载该目录
        var loader2 = new PluginLoader(Options());
        var result2 = loader2.LoadAll(new ModuleHost());
        Assert.DoesNotContain(result2.Loaded, i => i.Key == "fake-feature");
        Assert.Contains(result2.Disabled, m => m.DirectoryKey == "feature-module");
    }

    [Fact]
    public async Task SetEnabled_Enable_HotLoadsAndStarts()
    {
        var disabled = SeedPlugin("feature-module", "disabled-module");
        WriteIni(disabled, "FakeFeatureModule.dll", enabled: false);
        var host = new ModuleHost();
        var loader = new PluginLoader(Options());
        loader.LoadAll(host);
        var context = CreateContext();
        await host.StartAllAsync(context); // 运行中（热启用前提）

        var message = await loader.SetEnabledAsync("disabled-module", enabled: true, host, context);

        Assert.DoesNotContain("重启", message);
        Assert.Contains("已启用并立即生效", message);
        var dirView = loader.Directories.Single(d => d.DirectoryKey == "disabled-module");
        Assert.Equal(PluginDirectoryStatus.Loaded, dirView.Status);
        // UI 开关读 manifest.Enabled：必须与磁盘一致（否则显示仍旧值 → 实测「打不开」）
        Assert.True(dirView.Manifest!.Enabled);
        Assert.Contains("Enabled = true", File.ReadAllText(Path.Combine(disabled, "config.ini")));
        // 热启用核心断言：运行中已注册并启动（不再需要重启）
        var module = Assert.Single(host.Modules, m => m.Key == "fake-feature");
        Assert.Equal(ModuleState.Started, module.State);

        // 热启用后可再禁用卸载（回程对称）
        var message2 = await loader.SetEnabledAsync("disabled-module", enabled: false, host);
        Assert.Contains("已禁用", message2);
        Assert.DoesNotContain(host.Modules, m => m.Key == "fake-feature");
    }

    [Fact]
    public async Task SetEnabled_Enable_HotLoadFailure_IsIsolated()
    {
        var broken = SeedPlugin("feature-module", "broken-module");
        WriteIni(broken, "MissingModule.dll"); // 声明的 DLL 不存在 → 加载失败
        var host = new ModuleHost();
        var loader = new PluginLoader(Options());
        loader.LoadAll(host);
        var context = CreateContext();
        await host.StartAllAsync(context);

        var message = await loader.SetEnabledAsync("broken-module", enabled: true, host, context);

        Assert.Contains("加载失败", message);
        Assert.Contains("未找到插件 DLL", message);
        var dirView = loader.Directories.Single(d => d.DirectoryKey == "broken-module");
        Assert.Equal(PluginDirectoryStatus.Failed, dirView.Status);
        Assert.DoesNotContain(host.Modules, m => m.Key == "fake-feature"); // 失败目录不注册
        // 标记已写：下次启动仍会尝试加载
        Assert.Contains("Enabled = true", File.ReadAllText(Path.Combine(broken, "config.ini")));
    }

    [Fact]
    public async Task SetEnabled_EnableDisableEnable_HotCycleWorks()
    {
        await WithFileLockRetryAsync(async () =>
        {
        var dir = SeedPlugin("feature-module", "cycle-module");
        WriteIni(dir, "FakeFeatureModule.dll", enabled: false);
        var host = new ModuleHost();
        var loader = new PluginLoader(Options());
        loader.LoadAll(host);
        var context = CreateContext();
        await host.StartAllAsync(context);

        // 启用 → 加载注册并启动
        var enableMessage = await loader.SetEnabledAsync("cycle-module", enabled: true, host, context);
        Assert.True(host.Modules.Any(m => m.Key == "fake-feature" && m.State == ModuleState.Started),
            $"启用后模块未注册启动（SetEnabledAsync 消息：{enableMessage}；ini：{File.ReadAllText(Path.Combine(_root, "cycle-module", "config.ini"))}）");

        // 禁用 → 卸载（ALC 释放路径）
        await loader.SetEnabledAsync("cycle-module", enabled: false, host);
        Assert.DoesNotContain(host.Modules, m => m.Key == "fake-feature");
        CollectGarbage();

        // 再启用 → 从干净状态重新加载（_catalogs/_byKey 清理后可重载）
        var reEnableMessage = await loader.SetEnabledAsync("cycle-module", enabled: true, host, context);
        Assert.True(host.Modules.Any(m => m.Key == "fake-feature"),
            $"重启用后模块未注册（消息：{reEnableMessage}）");
        var module = Assert.Single(host.Modules, m => m.Key == "fake-feature");
        Assert.Equal(ModuleState.Started, module.State);
        Assert.NotNull(loader.GetPluginInstance<Erbai.Contracts.Plugins.IFeatureModule>("fake-feature"));
        });
    }

    [Fact]
    public void SetEnabled_WritesIni_PreservesCustomSections()
    {
        var dir = SeedPlugin("feature-module");
        WriteIni(dir, "FakeFeatureModule.dll", extraSections: """
            [ToggleKey]
            Name = 重载快捷键
            Type = key
            Value = 118
            """);

        var updated = PluginIniParser.SetEnabled(
            File.ReadAllText(Path.Combine(dir, "config.ini")), enabled: false);

        Assert.Contains("Enabled = false", updated);
        Assert.Contains("[ToggleKey]", updated);
        Assert.Contains("Type = key", updated);
        Assert.Contains("Name = 重载快捷键", updated);
        // 再次解析不破坏结构：Three kinds 都可解析
        var manifest = PluginIniParser.Parse(updated, "feature-module");
        Assert.False(manifest.Enabled);
        Assert.Equal("ToggleKey", Assert.Single(manifest.Config).Key);
    }

    // ── 内置模块迁移前置（M1）：宿主优先共享解析 + 构造注入 ─────────────────────

    [Fact]
    public async Task ConstructorInjection_ResolvesHostServices()
    {
        var dir = SeedPlugin("injected-feature");
        WriteIni(dir, "FakeInjectedFeatureModule.dll");

        var storage = new Erbai.Core.Storage.SqliteStorageEngine(Path.Combine(_root, "inject.db"));
        var loader = new PluginLoader(new PluginLoaderOptions
        {
            PluginsRoot = _root,
            ServiceResolver = type => type == typeof(Erbai.Contracts.Abstractions.IStorageEngine) ? storage : null,
        });
        var host = new ModuleHost();
        var result = loader.LoadAll(host);
        Assert.Empty(result.Errors);

        var info = Assert.Single(result.Loaded);
        Assert.Equal("fake-injected", info.Key);

        // 主程序经共享契约接口取插件实例（内置模块迁移后 AppServices 的取用方式）
        var instance = loader.GetPluginInstance<Erbai.Contracts.Plugins.IFeatureModule>("fake-injected");
        Assert.NotNull(instance);

        await host.StartAllAsync(CreateContext());
        Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == "fake-injected").State);
    }

    [Fact]
    public void ConstructorInjection_WithoutResolver_FailsIsolated()
    {
        var dir = SeedPlugin("injected-feature");
        WriteIni(dir, "FakeInjectedFeatureModule.dll");
        SeedPlugin("feature-module");
        WriteIni(Path.Combine(_root, "feature-module"), "FakeFeatureModule.dll");

        // 无 ServiceResolver：带参构造插件失败，无参插件不受影响
        var loader = new PluginLoader(Options());
        var host = new ModuleHost();
        var result = loader.LoadAll(host);

        var error = Assert.Single(result.Errors, e => e.DirectoryKey == "injected-feature");
        Assert.Contains("ServiceResolver", error.Reason);
        Assert.Single(result.Loaded, i => i.Key == "fake-feature");
    }

    [Fact]
    public void GetPluginInstance_ReturnsNull_WhenNotLoadedOrWrongKind()
    {
        var loader = new PluginLoader(Options());
        Assert.Null(loader.GetPluginInstance<Erbai.Contracts.Plugins.IFeatureModule>("not-loaded"));
    }

    private ModuleContext CreateContext() => new()
    {
        EventBus = new EventBus(),
        Config = new Erbai.Core.Configuration.ConfigStore(Path.Combine(_root, "config.json")),
        Storage = new Erbai.Core.Storage.SqliteStorageEngine(Path.Combine(_root, "test.db")),
        Logs = new Erbai.Core.Logging.LogBus(),
    };

    private static void CollectGarbage()
    {
        for (var i = 0; i < 5; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}