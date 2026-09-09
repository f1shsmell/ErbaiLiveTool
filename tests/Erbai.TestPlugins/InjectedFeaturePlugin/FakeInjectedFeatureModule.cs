using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Plugins;

namespace Erbai.TestPlugins.Injected;

/// <summary>
/// 带参构造假插件：构造函数依赖宿主服务（IStorageEngine，Contracts 接口）。
/// 验证 PluginLoader 的 <c>ServiceResolver</c> 构造注入解析——宿主无法解析参数即加载失败，
/// 注册成功即证明参数类型跨 ALC 强转为同一契约（若出现双版本会 InvalidCast）。
/// </summary>
public sealed class FakeInjectedFeatureModule : IFeatureModule
{
    public FakeInjectedFeatureModule(IStorageEngine storage)
    {
        Storage = storage;
    }

    /// <summary>注入的宿主服务（非空即注入成功）。</summary>
    public IStorageEngine Storage { get; }

    public string Key => "fake-injected";

    public string DisplayName => "Fake 注入模块";

    public Task StartAsync(ModuleContext context, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}