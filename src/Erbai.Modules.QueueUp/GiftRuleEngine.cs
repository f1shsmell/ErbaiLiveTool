using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;

namespace Erbai.Modules.QueueUp;

/// <summary>
/// 礼物插队规则引擎（docs/01 §3.7 规则表 + 05 §5 bilipdj 行为参考，只参考行为不抄代码）：
/// 规则 [{match: 礼物名 或 gold 电池数阈值, action: 插入名次 N / 授予入队资格}]。
/// 匹配语义：
/// - gift_name：GiftName 精确匹配（Ordinal，两侧 trim）；
/// - coin_threshold：CoinType == "gold" 且 TotalCoin ≥ 阈值（B站金瓜子/抖音电池均归一 gold）；
/// - 按配置顺序第一条命中生效（规则优先级 = 顺序）。
/// </summary>
public static class GiftRuleEngine
{
    /// <summary>返回第一条命中的规则；无命中返回 null。</summary>
    public static QueueUpRuleConfig? Match(IReadOnlyList<QueueUpRuleConfig> rules, LiveEvent gift)
    {
        if (gift.Kind != LiveEventKind.Gift)
        {
            return null;
        }

        foreach (var rule in rules)
        {
            if (rule.Action is not ("insert_at" or "grant_eligibility"))
            {
                continue; // 配置校验兜底（正常情况不会出现）
            }

            if (Matches(rule, gift))
            {
                return rule;
            }
        }

        return null;
    }

    private static bool Matches(QueueUpRuleConfig rule, LiveEvent gift)
    {
        if (rule.MatchKind == "gift_name")
        {
            var giftName = (gift.GiftName ?? "").Trim();
            return giftName.Length > 0 &&
                   string.Equals(giftName, rule.MatchValue.Trim(), StringComparison.Ordinal);
        }

        if (rule.MatchKind == "coin_threshold")
        {
            // gold = B站金瓜子 / 抖音电池（LiveEvent.CoinType 归一语义，docs/01 §2）
            if (!string.Equals(gift.CoinType, "gold", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 与 gift_name 分支一致地 Trim（审计 T2-3：带空格阈值此前永不命中）
            return long.TryParse(rule.MatchValue.Trim(), out var threshold) && gift.TotalCoin >= threshold;
        }

        return false;
    }
}
