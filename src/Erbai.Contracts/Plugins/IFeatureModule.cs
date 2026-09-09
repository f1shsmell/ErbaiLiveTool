namespace Erbai.Contracts.Plugins;

/// <summary>
/// 功能模块（点歌/排队/特效同构）：StartAsync 内自行订阅 EventBus 并驱动后台循环。
/// 不变量：模块循环内任何异常不得逃逸（结算当前请求并继续，否则队列永久停摆）；宿主负责异常隔离。
/// </summary>
public interface IFeatureModule : IAsyncDisposable
{
    string Key { get; }

    string DisplayName { get; }

    Task StartAsync(ModuleContext context, CancellationToken ct);
}
