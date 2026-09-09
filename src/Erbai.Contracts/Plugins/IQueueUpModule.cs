namespace Erbai.Contracts.Plugins;

/// <summary>
/// 排队队列模块的宿主协作面（内置 QueueUp 插件迁移为目录式插件后，AppServices/UI
/// 经 <c>IQueueUpModule</c> 接口访问 <see cref="Service"/>，而非强引用模块具体类型）。
/// </summary>
public interface IQueueUpModule : IFeatureModule
{
    /// <summary>队列核心（UI 页面经此操作：完成队首/取消指定条目/读快照）。</summary>
    Erbai.Contracts.QueueUp.IQueueUpService Service { get; }
}