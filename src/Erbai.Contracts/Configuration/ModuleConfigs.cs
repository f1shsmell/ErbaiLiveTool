namespace Erbai.Contracts.Configuration;

/// <summary>
/// 排队队列模块配置（docs/01 §3.7 最小版）：命令入口（排队/取消排队/完成）+
/// 礼物插队规则引擎。看板/每日限次/多套预设/无影插等本版不做。
/// </summary>
public sealed record QueueUpConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>队列容量（校验 1-500）。</summary>
    public int MaxEntries { get; init; } = 50;

    /// <summary>
    /// 资格门槛：开启后只有命中 grant_eligibility 规则获得资格的用户才能排队
    /// （默认关 = 所有观众可排队）。
    /// </summary>
    public bool EligibilityGate { get; init; } = false;

    /// <summary>礼物插队规则表（按配置顺序，第一条命中生效）。</summary>
    public IReadOnlyList<QueueUpRuleConfig> Rules { get; init; } = [];
}

/// <summary>
/// 礼物插队规则（docs/01 §3.7：{match, action}；行为参考 bilipdj 送礼插队，
/// 只参考行为描述不抄代码）：match = 礼物名精确匹配 或 gold 电池数阈值；
/// action = 插入名次 N（送礼者自动入队第 N 位，source=gift）或授予入队资格。
/// </summary>
public sealed record QueueUpRuleConfig
{
    /// <summary>匹配维度：gift_name（礼物名精确匹配）| coin_threshold（电池数阈值）。</summary>
    public string MatchKind { get; init; } = "gift_name";

    /// <summary>gift_name 时为礼物名；coin_threshold 时为非负整数阈值字符串。</summary>
    public string MatchValue { get; init; } = "";

    /// <summary>动作：insert_at（自动插队）| grant_eligibility（授予入队资格）。</summary>
    public string Action { get; init; } = "insert_at";

    /// <summary>insert_at 的插入名次（1 = 队首；校验 1-500）。</summary>
    public int Position { get; init; } = 1;
}

/// <summary>礼物特效模块配置（docs/01 §3.8 框架通道）：开关 / 时长。</summary>
public sealed record GiftFxConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>特效展示时长（秒，校验 1-60；随 giftfx.play 载荷下发，overlay 动画时长）。</summary>
    public int DurationSeconds { get; init; } = 5;
}
