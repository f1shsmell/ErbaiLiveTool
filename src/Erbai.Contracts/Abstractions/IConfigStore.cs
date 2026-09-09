using Erbai.Contracts.Configuration;

namespace Erbai.Contracts.Abstractions;

/// <summary>
/// 配置单一事实源：内存快照 + 原子写盘，先写盘成功再更新内存。
/// </summary>
public interface IConfigStore
{
    /// <summary>当前生效配置（不可变快照；修改请走 <see cref="PersistAsync"/>）。</summary>
    AppConfig Settings { get; }

    /// <summary>从磁盘加载并校验，成为新的内存快照（仅启动期使用）。</summary>
    Task<AppConfig> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// 候选配置先校验 + 原子落盘成功，再提交为内存快照；失败抛
    /// <see cref="Configuration.ConfigException"/> 且内存/磁盘都不变。
    /// </summary>
    Task PersistAsync(AppConfig candidate, CancellationToken ct = default);
}
