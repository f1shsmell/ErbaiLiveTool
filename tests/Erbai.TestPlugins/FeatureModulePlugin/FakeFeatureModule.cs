using Erbai.Contracts.Plugins;

namespace Erbai.TestPlugins.Feature;

/// <summary>功能模块类假插件（PluginLoader 加载/注册/卸载测试用）。</summary>
public sealed class FakeFeatureModule : IFeatureModule
{
    public string Key => "fake-feature";

    public string DisplayName => "Fake 功能模块";

    public int StartCount { get; private set; }

    public static int DisposedCount;

    public Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        StartCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposedCount++;
        return ValueTask.CompletedTask;
    }
}