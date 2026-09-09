using Erbai.Contracts.Plugins;

namespace Erbai.Core.Hosting;

/// <summary>模块类别（三类插件）。</summary>
public enum ModuleKind
{
    LivePlatform,
    Player,
    FeatureModule,
}

/// <summary>单个模块的生命周期状态。</summary>
public enum ModuleState
{
    Created,
    Starting,
    Started,
    StartFailed,
    Stopped,
}

/// <summary>单个模块的注册信息与运行状态（供 UI 启停/监督者查询）。</summary>
public sealed record ModuleStatus
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public required ModuleKind Kind { get; init; }

    public ModuleState State { get; internal set; } = ModuleState.Created;

    /// <summary>启动失败原因（State == StartFailed 时有值；启动失败不拖垮宿主）。</summary>
    public string? StartError { get; internal set; }
}

/// <summary>
/// 模块宿主：装载三类插件并驱动生命周期。
/// - 启动顺序：功能模块（先就绪订阅）→ 播放器 → 直播平台（后生产事件）；停止逆序；
/// - 异常隔离：单个模块启动失败记录 StartError 后继续，宿主不崩溃
///   （对齐旧 PlayerManager：激活失败记录 start_error 后端继续运行）；
/// - Stop 幂等；DisposeAsync 等价 Stop。
/// </summary>
public sealed class ModuleHost : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly List<Entry> _entries = [];
    private bool _started;
    private bool _disposed;

    /// <summary>最近一次 StartAllAsync 的运行时上下文（热启用新模块复用；未启动过为 null）。</summary>
    private ModuleContext? _runtimeContext;

    public IReadOnlyList<ModuleStatus> Modules
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries.Select(e => e.Status)];
            }
        }
    }

    public void RegisterLivePlatform(ILivePlatformPlugin plugin) =>
        Register(new Entry(plugin.Key, plugin.DisplayName, ModuleKind.LivePlatform, plugin));

    public void RegisterPlayer(IMusicPlayerPlugin plugin) =>
        Register(new Entry(plugin.Key, plugin.DisplayName, ModuleKind.Player, plugin));

    public void RegisterFeatureModule(IFeatureModule module) =>
        Register(new Entry(module.Key, module.DisplayName, ModuleKind.FeatureModule, module));

    private void Register(Entry entry)
    {
        lock (_sync)
        {
            // 运行中允许注册（热启用：目录式插件启用后立即加载注册，新模块保持 Created，
            // 由调用方经 StartModuleAsync 显式启动；不自动启动，不影响已运行模块）。
            // key 唯一性仍强制：重复注册抛异常，防止同名插件顶替/双写。
            if (_entries.Any(e => e.Status.Key == entry.Status.Key))
            {
                throw new ArgumentException($"module key '{entry.Status.Key}' is already registered");
            }

            _entries.Add(entry);
        }
    }

    /// <summary>按注册序启动所有模块：功能模块 → 播放器 → 直播平台。</summary>
    public async Task StartAllAsync(ModuleContext context, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _runtimeContext = context; // 供热启用/后续单独启动复用
        }

        var ordered = _entries
            .OrderBy(e => e.KindOrder)
            .ThenBy(e => e.Status.Key, StringComparer.Ordinal);

        foreach (var entry in ordered)
        {
            ct.ThrowIfCancellationRequested();

            // 已启动/启动中的模块不重复启动（审计 T5-1：StartModuleAsync 单独启动后
            // 再 StartAllAsync，此前会二次 Start——第三方插件无自兜底时会双重初始化）
            if (entry.Status.State is ModuleState.Started or ModuleState.Starting)
            {
                continue;
            }

            entry.Status.State = ModuleState.Starting;
            try
            {
                await entry.StartAsync(context, ct);
                entry.Status.State = ModuleState.Started;
            }
            catch (Exception ex)
            {
                entry.Status.State = ModuleState.StartFailed;
                entry.Status.StartError = ex.Message;
            }
        }
    }

    /// <summary>按逆序停止所有模块（幂等）。</summary>
    public async Task StopAllAsync()
    {
        Entry[] snapshot;
        lock (_sync)
        {
            snapshot = [.. _entries];
        }

        foreach (var entry in snapshot.OrderByDescending(e => e.KindOrder))
        {
            await entry.StopAsync();
        }
    }

    /// <summary>单独启动一个模块（UI 启停 / 平台监督者用）；未注册的键抛 ArgumentException。</summary>
    public async Task StartModuleAsync(string key, ModuleContext context, CancellationToken ct = default)
    {
        var entry = Find(key);
        if (entry.Status.State is ModuleState.Started or ModuleState.Starting)
        {
            return;
        }

        entry.Status.State = ModuleState.Starting;
        try
        {
            await entry.StartAsync(context, ct);
            entry.Status.State = ModuleState.Started;
        }
        catch (Exception ex)
        {
            entry.Status.State = ModuleState.StartFailed;
            entry.Status.StartError = ex.Message;
        }
    }

    /// <summary>
    /// 用最近一次 StartAllAsync 保存的上下文单独启动一个模块（热启用路径）；
    /// 宿主从未 StartAllAsync（无运行时上下文）时抛 InvalidOperationException。
    /// </summary>
    public async Task StartModuleAsync(string key, CancellationToken ct = default)
    {
        var context = _runtimeContext
            ?? throw new InvalidOperationException("host has not been started (no runtime context)");
        await StartModuleAsync(key, context, ct);
    }

    /// <summary>单独停止一个模块（幂等）。</summary>
    public async Task StopModuleAsync(string key)
    {
        var entry = Find(key);
        await entry.StopAsync();
    }

    /// <summary>
    /// 移除一个已停止的模块（目录式插件卸载用：先 StopModuleAsync 再 RemoveModule，
    /// 实例引用随之释放，PluginLoader 才能 Unload 对应 ALC）。未停止的模块抛
    /// <see cref="InvalidOperationException"/>（防止运行中模块被静默移出监督）。
    /// </summary>
    public void RemoveModule(string key)
    {
        lock (_sync)
        {
            var index = _entries.FindIndex(e => e.Status.Key == key);
            if (index < 0)
            {
                throw new ArgumentException($"module '{key}' is not registered");
            }

            var state = _entries[index].Status.State;
            if (state is not (ModuleState.Created or ModuleState.Stopped))
            {
                throw new InvalidOperationException(
                    $"module '{key}' is in state {state} and must be stopped before removal");
            }

            _entries.RemoveAt(index);
        }
    }

    private Entry Find(string key)
    {
        lock (_sync)
        {
            return _entries.FirstOrDefault(e => e.Status.Key == key)
                ?? throw new ArgumentException($"module '{key}' is not registered");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAllAsync();
    }

    private sealed class Entry
    {
        private readonly ILivePlatformPlugin? _platform;
        private readonly IMusicPlayerPlugin? _player;
        private readonly IFeatureModule? _module;

        /// <summary>平台事件转发生命周期（仅 LivePlatform 使用）。</summary>
        private CancellationTokenSource? LifecycleCts;

        private Task? ForwardTask;

        public Entry(string key, string displayName, ModuleKind kind, object plugin)
        {
            Status = new ModuleStatus { Key = key, DisplayName = displayName, Kind = kind };
            switch (kind)
            {
                case ModuleKind.LivePlatform:
                    _platform = (ILivePlatformPlugin)plugin;
                    break;
                case ModuleKind.Player:
                    _player = (IMusicPlayerPlugin)plugin;
                    break;
                case ModuleKind.FeatureModule:
                    _module = (IFeatureModule)plugin;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public ModuleStatus Status { get; }

        /// <summary>启动顺序权重：功能模块先就绪，直播平台最后生产。</summary>
        public int KindOrder => Status.Kind switch
        {
            ModuleKind.FeatureModule => 0,
            ModuleKind.Player => 1,
            ModuleKind.LivePlatform => 2,
            _ => 3,
        };

        public Task StartAsync(ModuleContext context, CancellationToken ct)
        {
            if (Status.Kind == ModuleKind.LivePlatform)
            {
                LifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                return StartPlatformAsync(context, LifecycleCts.Token);
            }

            return Status.Kind switch
            {
                ModuleKind.Player => _player!.ActivateAsync(context.Config.Settings, ct),
                ModuleKind.FeatureModule => _module!.StartAsync(context, ct),
                _ => Task.CompletedTask,
            };
        }

        private async Task StartPlatformAsync(ModuleContext context, CancellationToken ct)
        {
            await _platform!.StartAsync(context.Config.Settings, ct);
            // 平台事件流 → EventBus 桥接（规范化事件被所有模块消费）
            ForwardTask = ModuleLoops.RunConsumerLoopAsync(
                _platform.Events,
                (evt, _) =>
                {
                    context.EventBus.Publish(evt);
                    return Task.CompletedTask;
                },
                context.Logs,
                $"{Status.Key}.events",
                ct);
        }

        public async Task StopAsync()
        {
            if (Status.State is ModuleState.Created or ModuleState.Stopped)
            {
                return;
            }

            Status.State = ModuleState.Stopped;
            try
            {
                switch (Status.Kind)
                {
                    case ModuleKind.LivePlatform:
                        LifecycleCts?.Cancel();
                        if (ForwardTask is not null)
                        {
                            try
                            {
                                await ForwardTask;
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }

                        await _platform!.StopAsync();
                        break;
                    case ModuleKind.Player:
                        await _player!.DeactivateAsync();
                        break;
                    case ModuleKind.FeatureModule:
                        await _module!.DisposeAsync().AsTask();
                        break;
                }
            }
            finally
            {
                LifecycleCts?.Dispose();
                LifecycleCts = null;
                ForwardTask = null;
            }
        }
    }
}
