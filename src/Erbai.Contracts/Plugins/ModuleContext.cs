using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Logging;

namespace Erbai.Contracts.Plugins;

/// <summary>
/// 功能模块运行上下文：事件总线 / 配置 / 存储 / 日志 / Overlay 通道
/// （阶段 2 起由 App 组合根装配注入）。
/// </summary>
public sealed class ModuleContext
{
    public required IEventBus EventBus { get; init; }

    public required IConfigStore Config { get; init; }

    public required IStorageEngine Storage { get; init; }

    public required ILogBus Logs { get; init; }

    /// <summary>Overlay WS 推送通道；阶段 2（Erbai.Web）之前为 null。</summary>
    public IOverlayHub? Overlay { get; init; }
}
