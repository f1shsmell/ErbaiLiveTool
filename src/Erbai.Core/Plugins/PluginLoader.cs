using System.Runtime.Loader;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Plugins;
using Erbai.Core.Hosting;

namespace Erbai.Core.Plugins;

/// <summary>插件目录的当前状态（插件管理 UI 视图面）。</summary>
public enum PluginDirectoryStatus
{
    /// <summary>已加载并注册（ModuleHost 管理生命周期）。</summary>
    Loaded,

    /// <summary>启停标记为禁用（Enabled=false 或 DLL 改 .disabled），未加载。</summary>
    Disabled,

    /// <summary>加载/解析失败（<see cref="PluginDirectoryInfo.Error"/> 有原因；不影响其他插件）。</summary>
    Failed,

    /// <summary>已启用但未加载（曾加载后被卸载 / 启动后新启用待重启生效——ModuleHost 运行中不可注册）。</summary>
    Unloaded,
}

/// <summary>单个插件目录的视图条目（UI 展示/启停操作）。</summary>
public sealed record PluginDirectoryInfo
{
    public required string DirectoryKey { get; init; }

    /// <summary>解析成功的元数据；解析失败（config.ini 缺失/格式错）为 null。</summary>
    public PluginManifest? Manifest { get; init; }

    public required PluginDirectoryStatus Status { get; init; }

    /// <summary>Failed 时的原因（人类可读，UI 展示）。</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>已注册的模块键（Status == Loaded 时有值）。</summary>
    public IReadOnlyList<string> RegisteredKeys { get; init; } = [];
}

/// <summary>加载成功的一个插件条目（UI/诊断视图面，不含 ALC 等运行时细节）。</summary>
public sealed record LoadedPluginInfo(string Key, string DisplayName, ModuleKind Kind, PluginManifest Manifest);

/// <summary>单个插件目录加载/发现的失败原因（不拖垮其他插件）。</summary>
public sealed record PluginLoadError(string DirectoryKey, string Reason, PluginManifest? Manifest = null);

/// <summary>一次 LoadAll 的结果汇总。</summary>
public sealed record PluginLoadResult
{
    public IReadOnlyList<LoadedPluginInfo> Loaded { get; init; } = [];

    /// <summary>禁用（Enabled=false 或 DLL 改 .disabled）的插件清单，不参与加载。</summary>
    public IReadOnlyList<PluginManifest> Disabled { get; init; } = [];

    public IReadOnlyList<PluginLoadError> Errors { get; init; } = [];
}

/// <summary>PluginLoader 配置。</summary>
public sealed class PluginLoaderOptions
{
    /// <summary>插件根目录（App 侧 = AppPaths.HostDir\Plugins；测试侧 = 测试输出 plugins\）。</summary>
    public required string PluginsRoot { get; init; }

    public ILogBus? Logs { get; init; }

    /// <summary>
    /// 共享契约程序集名（Stage A M5 起 ALC 解析改为「宿主已加载优先 → 插件目录 fallback」，
    /// 本名单不再参与解析；保留仅为兼容既有构造，新代码请勿依赖）。
    /// </summary>
    [Obsolete("宿主优先共享解析已取代名单模式，保留仅为兼容")]
    public IReadOnlySet<string> SharedAssemblyNames { get; init; } =
        new HashSet<string>(["Erbai.Contracts"], StringComparer.Ordinal);

    /// <summary>
    /// 宿主服务解析器（内置插件/需依赖注入的第三方插件用）：插件类型没有 public 无参构造时，
    /// 按构造函数参数类型逐个回调，返回对应服务实例；返回 null 视为该参数不可解析 → 实例化失败。
    /// 参数类型必须是 Erbai.Contracts 的共享契约类型（跨 ALC 强转成立的前提）。
    /// </summary>
    public Func<Type, object?>? ServiceResolver { get; init; }

    /// <summary>宿主契约版本（默认取 Erbai.Contracts 的 AssemblyVersion）。</summary>
    public System.Version? HostContractVersion { get; init; }
}

/// <summary>
/// 目录式插件加载器（决策 #4/#16 落地，docs/07）：
/// 枚举 <c>Plugins/</c> 下每插件一子目录 → 解析 config.ini → 校验契约版本与启停标记 →
/// 按插件建可回收 ALC（共享契约 + 目录依赖解析）→ 发现插件类型并实例化 → 注册进 ModuleHost。
/// 加载/发现异常按目录隔离（单个坏插件不拖垮其余）；卸载 = 停止 + 移除注册 + Dispose + ALC.Unload。
/// 内置模块（组合根硬编码装配）不受影响，过渡期并行保留。
/// </summary>
public sealed class PluginLoader : IAsyncDisposable
{
    private readonly PluginLoaderOptions _options;
    private readonly System.Version _hostContractVersion;
    private readonly List<CatalogEntry> _catalogs = [];
    private readonly Dictionary<string, CatalogEntry> _byKey = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private List<PluginDirectoryInfo> _directories = [];
    private bool _disposed;

    public PluginLoader(PluginLoaderOptions options)
    {
        _options = options;
        _hostContractVersion = options.HostContractVersion
            ?? typeof(PluginManifest).Assembly.GetName().Version
            ?? new System.Version(1, 0, 0);
    }

    /// <summary>已加载插件（诊断/UI 显示；按注册键序）。</summary>
    public IReadOnlyList<LoadedPluginInfo> Loaded
    {
        get
        {
            lock (_sync)
            {
                return [.. _byKey.Values.OrderBy(e => e.RegisteredKeys.Min(), StringComparer.Ordinal)
                    .SelectMany(e => e.ToInfos())];
            }
        }
    }

    /// <summary>全部插件目录的视图（含禁用/失败；按目录名序，LoadAll 时重建，启停/卸载后更新）。</summary>
    public IReadOnlyList<PluginDirectoryInfo> Directories
    {
        get
        {
            lock (_sync)
            {
                return [.. _directories];
            }
        }
    }

    /// <summary>插件 ALC 的弱引用（测试断言卸载后可被 GC 回收）；未加载返回 null。</summary>
    internal WeakReference? WeakRefOf(string key)
    {
        lock (_sync)
        {
            return _byKey.TryGetValue(key, out var entry) ? entry.WeakRef : null;
        }
    }

    /// <summary>
    /// 发现 + 加载 + 注册进 <paramref name="host"/>。
    /// 要求在 <c>host.StartAllAsync</c> 之前调用（ModuleHost 启动后禁止注册）。
    /// 插件根目录不存在 → 空结果（不上报错误）。按目录隔离失败。
    /// </summary>
    public PluginLoadResult LoadAll(ModuleHost host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _directories = [];
        }

        if (!Directory.Exists(_options.PluginsRoot))
        {
            return new PluginLoadResult();
        }

        var loaded = new List<LoadedPluginInfo>();
        var disabled = new List<PluginManifest>();
        var errors = new List<PluginLoadError>();

        foreach (var pluginDir in Directory.EnumerateDirectories(_options.PluginsRoot).OrderBy(p => p, StringComparer.Ordinal))
        {
            var directoryKey = Path.GetFileName(pluginDir);
            var outcome = TryLoadDirectory(pluginDir, directoryKey, host);
            switch (outcome.Status)
            {
                case PluginDirectoryStatus.Loaded:
                    loaded.AddRange(outcome.Infos);
                    break;
                case PluginDirectoryStatus.Disabled:
                    disabled.Add(outcome.Manifest!);
                    break;
                default:
                    errors.Add(new PluginLoadError(directoryKey, outcome.Message ?? "加载失败"));
                    break;
            }
        }

        _options.Logs?.Log(LogLevel.Information,
            $"插件加载完成：加载 {loaded.Count} 个 / 禁用 {disabled.Count} 个 / 失败 {errors.Count} 个（目录 {_options.PluginsRoot}）");
        return new PluginLoadResult { Loaded = loaded, Disabled = disabled, Errors = errors };
    }

    /// <summary>
    /// 单目录加载核心（LoadAll 全量枚举与热启用共用）：解析 config.ini → 启停标记/契约校验
    /// → LoadCatalog → 登记 _catalogs/_byKey → 记录目录视图状态。失败按目录隔离
    /// （单个坏目录不影响其余，错误原因经 <see cref="PluginLoadError.Reason"/> 返回）。
    /// </summary>
    private DirectoryLoadResult TryLoadDirectory(string pluginDir, string directoryKey, ModuleHost host)
    {
        var iniPath = Path.Combine(pluginDir, "config.ini");
        if (!File.Exists(iniPath))
        {
            const string reason = "缺少 config.ini（每插件目录必须含 config.ini 元数据，见 docs/07）";
            RecordDirectory(directoryKey, null, PluginDirectoryStatus.Failed, reason);
            return new(PluginDirectoryStatus.Failed, reason);
        }

        PluginManifest manifest;
        try
        {
            manifest = PluginIniParser.Parse(File.ReadAllText(iniPath), directoryKey);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            var reason = $"config.ini 解析失败：{ex.Message}";
            RecordDirectory(directoryKey, null, PluginDirectoryStatus.Failed, reason);
            return new(PluginDirectoryStatus.Failed, reason);
        }

        // 启停标记：config.ini Enabled=false，或 DLL 已改扩展名禁用手工（FufuLauncher 形态）
        var dllPath = Path.Combine(pluginDir, manifest.File);
        var disabledByMarker = !File.Exists(dllPath) && File.Exists(dllPath + ".disabled");
        if (!manifest.Enabled || disabledByMarker)
        {
            RecordDirectory(directoryKey, manifest, PluginDirectoryStatus.Disabled);
            return new(PluginDirectoryStatus.Disabled, null, Manifest: manifest);
        }

        if (!File.Exists(dllPath))
        {
            var reason = $"未找到插件 DLL {manifest.File}（若已禁用手工请改回扩展名；若为 .disabled 变体将按禁用跳过）";
            RecordDirectory(directoryKey, manifest, PluginDirectoryStatus.Failed, reason);
            return new(PluginDirectoryStatus.Failed, reason);
        }

        if (!IsContractCompatible(manifest.ContractVersion, out var contractReason))
        {
            RecordDirectory(directoryKey, manifest, PluginDirectoryStatus.Failed, contractReason);
            return new(PluginDirectoryStatus.Failed, contractReason);
        }

        try
        {
            var catalog = LoadCatalog(pluginDir, manifest, host);
            var infos = catalog.ToInfos().ToList();
            lock (_sync)
            {
                _catalogs.Add(catalog);
                foreach (var info in infos)
                {
                    _byKey.Add(info.Key, catalog);
                }
            }

            RecordDirectory(directoryKey, manifest, PluginDirectoryStatus.Loaded,
                registeredKeys: [.. infos.Select(i => i.Key)]);
            return new(PluginDirectoryStatus.Loaded, null, infos);
        }
        catch (Exception ex)
        {
            var reason = $"加载失败：{ex.Message}";
            RecordDirectory(directoryKey, manifest, PluginDirectoryStatus.Failed, reason);
            return new(PluginDirectoryStatus.Failed, reason);
        }
    }

    private sealed record DirectoryLoadResult(
        PluginDirectoryStatus Status,
        string? Message,
        IReadOnlyList<LoadedPluginInfo> Infos = null!,
        PluginManifest? Manifest = null);

    /// <summary>卸载一个已加载插件（按注册键）：停止模块 → 移除注册 → Dispose 实例 → ALC.Unload。幂等。</summary>
    public async Task<bool> UnloadAsync(string key, ModuleHost host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CatalogEntry? entry;
        lock (_sync)
        {
            if (!_byKey.TryGetValue(key, out entry))
            {
                return false;
            }
        }

        _options.Logs?.Log(LogLevel.Information, $"[插件] 卸载 {key}：停止模块…");
        await host.StopModuleAsync(key);
        host.RemoveModule(key);
        _options.Logs?.Log(LogLevel.Information, $"[插件] 卸载 {key}：Dispose 实例并回收 ALC…");
        var removed = await ReleaseIfEmptyAsync(key, entry);
        if (removed)
        {
            UpdateDirectoryAfterUnload(entry);
            _options.Logs?.Log(LogLevel.Information, $"[插件] 卸载 {key} 完成");
        }

        return removed;
    }

    /// <summary>
    /// 插件启停（插件管理 UI）：改写 <c>[General] Enabled</c> 到 config.ini。
    /// 关闭 = 写标记 + 立即卸载已注册实例（热生效）；
    /// 开启 = 写标记 + 传入 <paramref name="hotContext"/> 时立即加载注册启动（热启用，
    /// 失败按目录隔离并记录 Failed，标记已写下次启动仍会尝试）；未传 context（宿主未启动）
    /// 保持「重启后生效」旧行为。
    /// </summary>
    public async Task<string> SetEnabledAsync(string directoryKey, bool enabled, ModuleHost host,
        ModuleContext? hotContext = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryGetDirectory(directoryKey, out var info))
        {
            return $"插件目录 {directoryKey} 不存在（可能已被删除，重启后生效）。";
        }

        var dir = Path.Combine(_options.PluginsRoot, directoryKey);
        var iniPath = Path.Combine(dir, "config.ini");
        if (!File.Exists(iniPath))
        {
            return $"缺少 {iniPath}，无法改写启停标记。";
        }

        string updated;
        try
        {
            updated = PluginIniParser.SetEnabled(File.ReadAllText(iniPath), enabled);
            File.WriteAllText(iniPath, updated);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            return $"改写 config.ini 失败：{ex.Message}";
        }

        // manifest 可能为 null（config.ini 解析失败的目录）：仅更新状态，不投影字段
        var updatedManifest = info.Manifest is { } m ? m with { Enabled = enabled } : null;

        if (enabled)
        {
            // 用磁盘一致的新 Enabled 记录（manifest 是 record，with 保留其余字段）；
            // 不刷新的话 UI 开关（IsEnabled = manifest.Enabled）会显示旧值（实测「关不掉/打不开」）
            RecordDirectory(directoryKey, updatedManifest, PluginDirectoryStatus.Unloaded);
            if (hotContext is null)
            {
                _options.Logs?.Log(LogLevel.Information, $"[插件] {directoryKey} 已启用（重启后生效）");
                return $"{directoryKey} 已启用：重启应用后生效（宿主未启动/无运行时上下文）。";
            }

            // 热启用：立即加载并注册启动（失败按目录隔离，不拖垮宿主）
            _options.Logs?.Log(LogLevel.Information, $"[插件] {directoryKey} 已启用，热加载…");
            var outcome = TryLoadDirectory(Path.Combine(_options.PluginsRoot, directoryKey), directoryKey, host);
            switch (outcome.Status)
            {
                case PluginDirectoryStatus.Loaded:
                {
                    foreach (var key in outcome.Infos.Select(i => i.Key))
                    {
                        await host.StartModuleAsync(key, hotContext);
                    }

                    var startFailed = outcome.Infos
                        .Select(i => i.Key)
                        .Where(k => host.Modules.First(m => m.Key == k).State == ModuleState.StartFailed)
                        .ToList();
                    var loaded = outcome.Infos.Count - startFailed.Count;
                    _options.Logs?.Log(LogLevel.Information,
                        $"[插件] {directoryKey} 热启用完成：{loaded} 个模块已启动" +
                        (startFailed.Count > 0 ? $"，{startFailed.Count} 个启动失败（见状态/日志）" : ""));
                    return startFailed.Count > 0
                        ? $"{directoryKey} 已启用：{loaded} 个模块生效，{startFailed.Count} 个启动失败（见插件状态/日志）。"
                        : $"{directoryKey} 已启用并立即生效（{loaded} 个模块已启动）。";
                }
                case PluginDirectoryStatus.Disabled:
                    return $"{directoryKey} 已启用，但目录仍为禁用标记（DLL 为 .disabled 变体）——改回扩展名后重试。";
                default:
                    return $"{directoryKey} 已启用，但加载失败：{outcome.Message}";
            }
        }

        foreach (var key in info.RegisteredKeys)
        {
            try
            {
                await UnloadAsync(key, host);
            }
            catch (Exception ex)
            {
                // 按目录错误隔离：单键卸载失败不阻断其余键/状态记录，插件错误不得拖垮宿主
                _options.Logs?.Log(LogLevel.Warning, $"[插件] {directoryKey} 卸载 {key} 异常（继续）：{ex.Message}");
            }
        }

        RecordDirectory(directoryKey, updatedManifest, PluginDirectoryStatus.Disabled);
        _options.Logs?.Log(LogLevel.Information, $"[插件] {directoryKey} 已禁用并卸载（下次启动不再加载）");
        return $"{directoryKey} 已禁用并卸载：下次启动不再加载。";
    }

    /// <summary>卸载全部已加载插件（App 关闭/重置时调用）。</summary>
    public async Task UnloadAllAsync(ModuleHost host)
    {
        string[] keys;
        lock (_sync)
        {
            keys = [.. _byKey.Keys.OrderByDescending(k => k, StringComparer.Ordinal)];
        }

        foreach (var key in keys)
        {
            try
            {
                await UnloadAsync(key, host);
            }
            catch (Exception ex)
            {
                _options.Logs?.Log(LogLevel.Warning, $"插件 {key} 卸载异常（继续）：{ex.Message}");
            }
        }
    }

    private async Task<bool> ReleaseIfEmptyAsync(string key, CatalogEntry entry)
    {
        lock (_sync)
        {
            if (!_byKey.Remove(key))
            {
                return false;
            }

            entry.RegisteredKeys.Remove(key);
            if (entry.RegisteredKeys.Count > 0)
            {
                return true; // 同目录其他类型仍在线，ALC 保留
            }

            _catalogs.Remove(entry);
        }

        // 实例引用已随 RemoveModule 释放，此处仅做显式 Dispose（幂等）；
        // 等实例释放完再 Unload（Unload 只是标记，实际回收由 GC 完成）
        foreach (var instance in entry.Instances)
        {
            await ((IAsyncDisposable)instance).DisposeAsync().AsTask();
        }

        entry.Alc?.Unload();
        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        // 卸载 ALC 由 UnloadAsync/UnloadAllAsync 负责（先停/移除再回收）；此处仅释放目录引用
        lock (_sync)
        {
            _catalogs.Clear();
            _byKey.Clear();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>按注册键在目录视图中记录/覆盖一个目录条目（LoadAll/启停更新用）。</summary>
    private void RecordDirectory(string directoryKey, PluginManifest? manifest, PluginDirectoryStatus status,
        string error = "", IReadOnlyList<string>? registeredKeys = null)
    {
        var entry = new PluginDirectoryInfo
        {
            DirectoryKey = directoryKey,
            Manifest = manifest,
            Status = status,
            Error = error,
            RegisteredKeys = registeredKeys ?? [],
        };
        lock (_sync)
        {
            _directories.RemoveAll(d => d.DirectoryKey == directoryKey);
            _directories.Add(entry);
        }
    }

    /// <summary>卸载后同步目录视图：最后一个键卸载 → Unloaded；多键插件部分卸载 → 更新剩余键。</summary>
    private void UpdateDirectoryAfterUnload(CatalogEntry entry)
    {
        var directoryKey = entry.Manifest.DirectoryKey;
        lock (_sync)
        {
            var index = _directories.FindIndex(d => d.DirectoryKey == directoryKey);
            if (index < 0)
            {
                return;
            }

            var current = _directories[index];
            _directories[index] = entry.RegisteredKeys.Count == 0
                ? current with { Status = PluginDirectoryStatus.Unloaded, RegisteredKeys = [] }
                : current with { RegisteredKeys = [.. entry.RegisteredKeys] };
        }
    }

    private bool TryGetDirectory(string directoryKey, out PluginDirectoryInfo info)
    {
        lock (_sync)
        {
            foreach (var d in _directories)
            {
                if (string.Equals(d.DirectoryKey, directoryKey, StringComparison.Ordinal))
                {
                    info = d;
                    return true;
                }
            }
        }

        info = null!;
        return false;
    }

    /// <summary>契约版本策略：未声明 = 兼容；声明后主版本必须与宿主一致且不得高于宿主。</summary>
    private bool IsContractCompatible(System.Version? declared, out string reason)
    {
        if (declared is null)
        {
            reason = string.Empty;
            return true;
        }

        if (declared.Major != _hostContractVersion.Major || declared > _hostContractVersion)
        {
            reason = $"契约版本 {declared} 与宿主契约（{_hostContractVersion}）不兼容：" +
                     "主版本必须一致且不得高于宿主（docs/07 §3）";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 建 ALC 加载主 DLL → 发现实现三类接口的 public 非抽象类型 → 逐类型实例化并注册。
    /// 同目录多个类型均可注册（共享同一 ALC；最后一个类型卸载时回收 ALC）。
    /// 任一步失败：Dispose 已实例化插件并 Unload ALC 后 rethrow（不留泄漏）。
    /// </summary>
    private CatalogEntry LoadCatalog(string pluginDir, PluginManifest manifest, ModuleHost host)
    {
        var dllPath = Path.Combine(pluginDir, manifest.File);
        var alc = new PluginLoadContext(pluginDir);
        var entry = new CatalogEntry(manifest) { Alc = alc, WeakRef = new WeakReference(alc) };
        var assembly = alc.LoadFromAssemblyPath(Path.Combine(dllPath));

        var discovered = 0;
        try
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || !type.IsClass || type.ContainsGenericParameters)
                {
                    continue;
                }

                if (typeof(ILivePlatformPlugin).IsAssignableFrom(type))
                {
                    Register(entry, type, ModuleKind.LivePlatform, host);
                    discovered++;
                }
                else if (typeof(IMusicPlayerPlugin).IsAssignableFrom(type))
                {
                    Register(entry, type, ModuleKind.Player, host);
                    discovered++;
                }
                else if (typeof(IFeatureModule).IsAssignableFrom(type))
                {
                    Register(entry, type, ModuleKind.FeatureModule, host);
                    discovered++;
                }
            }
        }
        catch (Exception)
        {
            DisposeInstances(entry);
            try
            {
                alc.Unload();
            }
            catch (Exception)
            {
                // Unload 只是标记，失败不吞原始异常
            }

            throw;
        }

        if (discovered == 0)
        {
            // 无可用插件类型 → 立即回收该 ALC（避免残留已加载程序集）
            alc.Unload();
            throw new InvalidOperationException(
                $"程序集 {manifest.File} 未发现任何实现 ILivePlatformPlugin / IMusicPlayerPlugin / IFeatureModule 的 public 类型（docs/07 §4）");
        }

        return entry;
    }

    private static void DisposeInstances(CatalogEntry entry)
    {
        foreach (var instance in entry.Instances)
        {
            try
            {
                ((IAsyncDisposable)instance).DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 失败清理路径：忽略单实例释放异常
            }
        }
    }

    private void Register(CatalogEntry entry, Type type, ModuleKind kind, ModuleHost host)
    {
        object instance;
        try
        {
            instance = CreateInstance(type);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"插件类型 {type.FullName} 实例化失败（需 public 无参构造，或由宿主 ServiceResolver 按构造参数类型解析）：{ex.Message}", ex);
        }

        string key;
        string displayName;
        switch (kind)
        {
            case ModuleKind.LivePlatform:
            {
                var plugin = (Erbai.Contracts.Plugins.ILivePlatformPlugin)instance;
                key = RequireKey(plugin.Key, entry.Manifest);
                displayName = plugin.DisplayName;
                host.RegisterLivePlatform(plugin);
                break;
            }

            case ModuleKind.Player:
            {
                var plugin = (Erbai.Contracts.Plugins.IMusicPlayerPlugin)instance;
                key = RequireKey(plugin.Key, entry.Manifest);
                displayName = plugin.DisplayName;
                host.RegisterPlayer(plugin);
                break;
            }

            case ModuleKind.FeatureModule:
            {
                var module = (Erbai.Contracts.Plugins.IFeatureModule)instance;
                key = RequireKey(module.Key, entry.Manifest);
                displayName = module.DisplayName;
                host.RegisterFeatureModule(module);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        entry.Instances.Add(instance);
        entry.RegisteredKeys.Add(key);
        entry.Registrations.Add(new LoadedPluginInfo(key, displayName, kind, entry.Manifest));
    }

    private static string RequireKey(string? key, PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"{manifest.Name} 的插件 Key 为空（必须返回非空 Key，且不能与内置模块及其他插件重复）");
        }

        return key;
    }

    /// <summary>
    /// 创建插件类型实例：优先 public 无参构造（第三方插件默认形态）；
    /// 无无参构造时按构造参数类型经 <see cref="PluginLoaderOptions.ServiceResolver"/> 解析
    /// （内置插件依赖注入形态），任一参数解析失败 → 抛错（按目录隔离）。
    /// </summary>
    private object CreateInstance(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) is not null)
        {
            return Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"{type.FullName} 实例化返回 null");
        }

        var resolver = _options.ServiceResolver;
        if (resolver is null)
        {
            throw new InvalidOperationException("类型没有 public 无参构造，且未配置宿主 ServiceResolver");
        }

        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("类型没有 public 构造函数");

        var arguments = new object?[ctor.GetParameters().Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            var parameter = ctor.GetParameters()[i];
            arguments[i] = resolver.Invoke(parameter.ParameterType)
                ?? throw new InvalidOperationException(
                    $"构造参数 {parameter.Name}（{parameter.ParameterType.FullName}）未被宿主 ServiceResolver 解析");
        }

        return ctor.Invoke(arguments)
            ?? throw new InvalidOperationException($"{type.FullName} 实例化返回 null");
    }

    /// <summary>
    /// 按注册键取插件实例（主程序经共享契约接口访问内置/第三方插件：
    /// 如 AppServices 拿 queueup 插件实例强转 <c>IQueueUpService</c>）。
    /// 未加载/类型不匹配返回 null。
    /// </summary>
    public T? GetPluginInstance<T>(string key)
        where T : class
    {
        lock (_sync)
        {
            return _byKey.TryGetValue(key, out var entry)
                ? entry.Instances.OfType<T>().FirstOrDefault()
                : null;
        }
    }

    /// <summary>一个插件目录的运行条目：共享一个 ALC，可含多个注册键（多类型插件）。</summary>
    private sealed class CatalogEntry
    {
        public CatalogEntry(PluginManifest manifest) => Manifest = manifest;

        public PluginManifest Manifest { get; }

        public AssemblyLoadContext? Alc { get; set; }

        public WeakReference? WeakRef { get; set; }

        public List<object> Instances { get; } = [];

        public List<string> RegisteredKeys { get; } = [];

        public List<LoadedPluginInfo> Registrations { get; } = [];

        public IEnumerable<LoadedPluginInfo> ToInfos() => Registrations;
    }
}