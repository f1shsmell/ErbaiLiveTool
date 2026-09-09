using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Logging;
using Erbai.Contracts.Plugins;
using Erbai.Core.Configuration;
using Erbai.Core.Events;
using Erbai.Core.Hosting;
using Erbai.Core.Logging;
using Erbai.Core.Plugins;
using Erbai.Core.Storage;
using Erbai.Modules.QueueUp;

namespace Erbai.Core.Tests;

/// <summary>
/// 内置模块迁移为目录式插件（Stage A 遗留②）的装配契约测试：
/// queueup 内置模块以「Plugins/queueup + config.ini + DLL」形态经 PluginLoader 加载，
/// 依赖经 ServiceResolver 构造注入（IStorageEngine/IEventBus/ILogBus/Func&lt;AppConfig&gt;），
/// 主程序经 <c>IQueueUpModule</c> 接口访问——复刻 AppServices 迁移后行为。
/// </summary>
public class BuiltinPluginMigrationTests
{
    [Fact]
    public async Task BuiltinQueueUp_LoadsAsDirectoryPlugin_WithServiceInjection()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "tmp", $"builtin-plugin-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "queueup");
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(typeof(QueueUpModule).Assembly.Location, Path.Combine(dir, "Erbai.Modules.QueueUp.dll"));
            File.WriteAllText(Path.Combine(dir, "config.ini"), """
                [General]
                Name = 排队队列
                Developer = ErbaiLiveTool
                File = Erbai.Modules.QueueUp.dll
                Version = 1.0.0
                """);

            var store = new SqliteStorageEngine(Path.Combine(root, "test.db"));
            await store.OpenAsync();
            var bus = new EventBus();
            var logs = new LogBus();
            var config = new ConfigStore(Path.Combine(root, "config.json"));

            var loader = new PluginLoader(new PluginLoaderOptions
            {
                PluginsRoot = root,
                ServiceResolver = type =>
                    type == typeof(IStorageEngine) ? store
                    : type == typeof(IEventBus) ? bus
                    : type == typeof(ILogBus) ? logs
                    : type == typeof(Func<AppConfig>) ? (object)(() => AppConfig.CreateDefault())
                    : null,
            });
            var host = new ModuleHost();
            var result = loader.LoadAll(host);
            Assert.Empty(result.Errors);

            // 主程序经共享契约接口取内置插件实例（AppServices.QueueUp 的取用方式）
            var module = loader.GetPluginInstance<IQueueUpModule>("queueup");
            Assert.NotNull(module);
            Assert.Equal("queueup", module.Key);

            await host.StartAllAsync(new ModuleContext
            {
                EventBus = bus,
                Config = config,
                Storage = store,
                Logs = logs,
            });
            Assert.Equal(ModuleState.Started, host.Modules.Single(m => m.Key == "queueup").State);

            // UI 面服务可用（快照可读）
            Assert.NotNull(module.Service);
            _ = module.Service.Snapshot();

            await host.StopAllAsync();
            await loader.UnloadAllAsync(host);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录清理失败不拖垮测试结论
            }
        }
    }
}