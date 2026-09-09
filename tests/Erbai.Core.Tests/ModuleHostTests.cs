using System.Threading.Channels;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Players;
using Erbai.Contracts.Plugins;
using Erbai.Core.Configuration;
using Erbai.Core.Events;
using Erbai.Core.Hosting;
using Erbai.Core.Logging;
using Erbai.Core.Storage;

namespace Erbai.Core.Tests;

/// <summary>
/// 模块宿主生命周期（阶段 1 退出标准：模块宿主装载 dummy 模块跑通生命周期）。
/// 同时钉住异常隔离（单模块启动失败不拖垮宿主）与 Stop 幂等。
/// </summary>
public class ModuleHostTests
{
    private sealed class DummyLivePlatform : ILivePlatformPlugin
    {
        private readonly Channel<LiveEvent> _events = Channel.CreateUnbounded<LiveEvent>();
        public string Key => "dummy-live";
        public string DisplayName => "Dummy 直播";
        public LiveCapabilities Capabilities => LiveCapabilities.Danmaku | LiveCapabilities.Gift;
        public ChannelReader<LiveEvent> Events => _events.Reader;
        public int StartCount { get; private set; }

        public Task StartAsync(AppConfig config, CancellationToken ct)
        {
            StartCount++;
            _events.Writer.TryWrite(new LiveEvent
            {
                Platform = Key,
                RoomId = "1",
                Kind = LiveEventKind.Danmaku,
                UserId = 1,
                Nickname = "tester",
                Text = "点歌 测试",
                Timestamp = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyPlayer : IMusicPlayerPlugin
    {
        public string Key => "dummy-player";
        public string DisplayName => "Dummy 播放器";
        public PlayerCapabilities Capabilities => PlayerCapabilities.None;
        public int ActivateCount { get; private set; }
        public int DeactivateCount { get; private set; }

        public Task<PlayerSnapshot> ActivateAsync(AppConfig config, CancellationToken ct)
        {
            ActivateCount++;
            return Task.FromResult(new PlayerSnapshot
            {
                Connected = true,
                Version = "dummy",
                NextObservation = NextObservation.Empty,
            });
        }

        public Task DeactivateAsync()
        {
            DeactivateCount++;
            return Task.CompletedTask;
        }

        public Task<PlayerSnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new PlayerSnapshot { Connected = true, NextObservation = NextObservation.Empty });

        public Task<IReadOnlyList<PlayerTrack>> SearchAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PlayerTrack>>([]);

        public Task<PlayerOperationResult> ExecuteAsync(PlayerCommand command, PlayerTrack? track, CancellationToken ct) =>
            Task.FromResult(PlayerOperationResult.Unsupported());

        public IAsyncEnumerable<PlayerSnapshot>? WatchSnapshotsAsync(CancellationToken ct) => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyFeatureModule : IFeatureModule
    {
        private readonly List<LiveEvent> _received = [];
        private Task? _loop;
        private CancellationTokenSource? _cts;

        public string Key => "dummy-module";
        public string DisplayName => "Dummy 模块";
        public IReadOnlyList<LiveEvent> Received => _received;
        public int StartCount { get; private set; }

        public Task StartAsync(ModuleContext context, CancellationToken ct)
        {
            StartCount++;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var subscription = context.EventBus.Subscribe<LiveEvent>();
            _loop = ModuleLoops.RunConsumerLoopAsync(
                subscription,
                (evt, _) =>
                {
                    lock (_received)
                    {
                        _received.Add(evt);
                    }

                    return Task.CompletedTask;
                },
                context.Logs,
                Key,
                _cts.Token);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            _cts?.Cancel();
            if (_loop is not null)
            {
                try
                {
                    await _loop;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private sealed class FailingModule : IFeatureModule
    {
        public string Key => "failing-module";
        public string DisplayName => "Failing";
        public int StartCount { get; private set; }

        public Task StartAsync(ModuleContext context, CancellationToken ct)
        {
            StartCount++;
            throw new InvalidOperationException("boom");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ModuleContext CreateContext(string tempDir) => new()
    {
        EventBus = new EventBus(),
        Config = new ConfigStore(Path.Combine(tempDir, "config.json")),
        Storage = new SqliteStorageEngine(Path.Combine(tempDir, "test.db")),
        Logs = new LogBus(),
    };

    private static string TempDir() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task FullLifecycle_DummyModules_StartAndStop()
    {
        var tempDir = TempDir();
        try
        {
            var host = new ModuleHost();
            var platform = new DummyLivePlatform();
            var player = new DummyPlayer();
            var module = new DummyFeatureModule();
            host.RegisterLivePlatform(platform);
            host.RegisterPlayer(player);
            host.RegisterFeatureModule(module);

            await host.StartAllAsync(CreateContext(tempDir));

            Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == platform.Key).State);
            Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == player.Key).State);
            Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == module.Key).State);
            Assert.Equal(1, platform.StartCount);
            Assert.Equal(1, player.ActivateCount);

            // 平台 StartAsync 已发一条弹幕；等模块消费者收到
            await Task.Delay(200);
            Assert.Single(module.Received);

            await host.StopAllAsync();
            Assert.All(host.Modules, m => Assert.Equal(ModuleState.Stopped, m.State));
            Assert.Equal(1, player.DeactivateCount);

            // Stop 幂等
            await host.StopAllAsync();
            Assert.Equal(1, player.DeactivateCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task StartFailure_IsIsolated_HostContinues()
    {
        var tempDir = TempDir();
        try
        {
            var host = new ModuleHost();
            var failing = new FailingModule();
            var platform = new DummyLivePlatform();
            host.RegisterFeatureModule(failing);
            host.RegisterLivePlatform(platform);

            await host.StartAllAsync(CreateContext(tempDir));

            var failingStatus = host.Modules.Single(m => m.Key == failing.Key);
            Assert.Equal(ModuleState.StartFailed, failingStatus.State);
            Assert.Equal("boom", failingStatus.StartError);

            // 其他模块照常启动，宿主不抛异常
            Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == platform.Key).State);

            await host.StopAllAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task StartAll_IsIdempotent()
    {
        var tempDir = TempDir();
        try
        {
            var host = new ModuleHost();
            var platform = new DummyLivePlatform();
            host.RegisterLivePlatform(platform);

            await host.StartAllAsync(CreateContext(tempDir));
            await host.StartAllAsync(CreateContext(tempDir));

            Assert.Equal(1, platform.StartCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task SingleModuleStartStop_Works()
    {
        var tempDir = TempDir();
        try
        {
            var host = new ModuleHost();
            var platform = new DummyLivePlatform();
            host.RegisterLivePlatform(platform);

            await host.StartModuleAsync(platform.Key, CreateContext(tempDir));
            Assert.Equal(ModuleState.Started, host.Modules.Single().State);

            await host.StopModuleAsync(platform.Key);
            Assert.Equal(ModuleState.Stopped, host.Modules.Single().State);

            // 单独再启动
            await host.StartModuleAsync(platform.Key, CreateContext(tempDir));
            Assert.Equal(ModuleState.Started, host.Modules.Single().State);
            Assert.Equal(2, platform.StartCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public void Register_DuplicateKey_Throws()
    {
        var host = new ModuleHost();
        host.RegisterLivePlatform(new DummyLivePlatform());
        Assert.Throws<ArgumentException>(() => host.RegisterLivePlatform(new DummyLivePlatform()));
    }

    [Fact]
    public async Task Register_AfterStart_AllowsHotStart()
    {
        var tempDir = TempDir();
        try
        {
            var host = new ModuleHost();
            host.RegisterLivePlatform(new DummyLivePlatform());
            await host.StartAllAsync(CreateContext(tempDir));

            // 热启用：运行中允许注册（新模块保持 Created，不自动启动）
            var hot = new DummyFeatureModule();
            host.RegisterFeatureModule(hot);
            Assert.Equal(ModuleState.Created, host.Modules.Single(m => m.Key == hot.Key).State);
            Assert.Equal(0, hot.StartCount);

            // 显式启动：用 StartAllAsync 保存的运行时上下文（无 context 重载）
            await host.StartModuleAsync(hot.Key);
            Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == hot.Key).State);
            Assert.Equal(1, hot.StartCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task StartModule_WithoutRuntimeContext_Throws()
    {
        var host = new ModuleHost();
        host.RegisterFeatureModule(new DummyFeatureModule());

        // 从未 StartAllAsync（无保存的运行时上下文）：无 context 重载应明确报错
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartModuleAsync("dummy-module"));
    }

    [Fact]
    public async Task DisposeAsync_StopsModules()
    {
        var tempDir = TempDir();
        try
        {
            var host = new ModuleHost();
            var player = new DummyPlayer();
            host.RegisterPlayer(player);
            await host.StartAllAsync(CreateContext(tempDir));

            await host.DisposeAsync();
            Assert.Equal(1, player.DeactivateCount);
            Assert.Equal(ModuleState.Stopped, host.Modules.Single().State);
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task RemoveModule_RemovesStoppedModule()
    {
        var tempDir = TempDir();
        try
        {
            var host = new ModuleHost();
            var module = new DummyFeatureModule();
            host.RegisterFeatureModule(module);
            await host.StartAllAsync(CreateContext(tempDir));

            await host.StopModuleAsync(module.Key);
            host.RemoveModule(module.Key);

            Assert.DoesNotContain(host.Modules, m => m.Key == module.Key);
            Assert.Throws<ArgumentException>(() => host.RemoveModule(module.Key));
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task RemoveModule_ThrowsWhileRunning()
    {
        var tempDir = TempDir();
        try
        {
            var host = new ModuleHost();
            var module = new DummyFeatureModule();
            host.RegisterFeatureModule(module);
            await host.StartAllAsync(CreateContext(tempDir));

            var ex = Assert.Throws<InvalidOperationException>(() => host.RemoveModule(module.Key));
            Assert.Contains("stopped", ex.Message);

            // 运行中模块仍在监督内
            Assert.Contains(host.Modules, m => m.Key == module.Key);
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }
}
