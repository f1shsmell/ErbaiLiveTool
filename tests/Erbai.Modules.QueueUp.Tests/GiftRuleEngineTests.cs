using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;

namespace Erbai.Modules.QueueUp.Tests;

/// <summary>
/// 礼物插队规则引擎（docs/01 §3.7 规则表）：礼物名精确匹配 / gold 电池数阈值 /
/// 按配置顺序第一条命中 / 非 Gift 事件不匹配。
/// </summary>
public class GiftRuleEngineTests
{
    private static LiveEvent Gift(
        string giftName = "火箭",
        long totalCoin = 1000,
        string coinType = "gold",
        LiveEventKind kind = LiveEventKind.Gift) =>
        new()
        {
            Platform = "douyin",
            RoomId = "1",
            Kind = kind,
            UserId = 1,
            Nickname = "观众",
            GiftName = giftName,
            GiftCount = 1,
            TotalCoin = totalCoin,
            CoinType = coinType,
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static QueueUpRuleConfig Rule(string matchKind, string matchValue, string action = "insert_at", int position = 1) =>
        new() { MatchKind = matchKind, MatchValue = matchValue, Action = action, Position = position };

    [Fact]
    public void GiftName_ExactMatch_TrimsBothSides()
    {
        var rules = new[] { Rule("gift_name", " 火箭 ") };
        Assert.NotNull(GiftRuleEngine.Match(rules, Gift(giftName: " 火箭 ")));
        Assert.Null(GiftRuleEngine.Match(rules, Gift(giftName: "大火箭"))); // 前缀不算命中
        Assert.Null(GiftRuleEngine.Match(rules, Gift(giftName: "火箭x")));
        Assert.Null(GiftRuleEngine.Match(rules, Gift(giftName: "")));
    }

    [Fact]
    public void CoinThreshold_MatchesGoldOnly()
    {
        var rules = new[] { Rule("coin_threshold", "1000") };

        Assert.NotNull(GiftRuleEngine.Match(rules, Gift(totalCoin: 1000, coinType: "gold")));
        Assert.NotNull(GiftRuleEngine.Match(rules, Gift(totalCoin: 5000, coinType: "gold")));
        Assert.Null(GiftRuleEngine.Match(rules, Gift(totalCoin: 999, coinType: "gold")));
        // silver（B站银瓜子/抖音非电池）不匹配电池阈值规则
        Assert.Null(GiftRuleEngine.Match(rules, Gift(totalCoin: 5000, coinType: "silver")));
        Assert.Null(GiftRuleEngine.Match(rules, Gift(totalCoin: 5000, coinType: "")));
    }

    [Fact]
    public void FirstMatchingRule_Wins_ByConfigOrder()
    {
        var rules = new[]
        {
            Rule("gift_name", "小心心"),
            Rule("gift_name", "火箭", action: "grant_eligibility"),
        };

        var hit = GiftRuleEngine.Match(rules, Gift(giftName: "火箭"));
        Assert.NotNull(hit);
        Assert.Equal("grant_eligibility", hit.Action); // 第二条命中

        hit = GiftRuleEngine.Match(rules, Gift(giftName: "小心心"));
        Assert.Equal("insert_at", hit!.Action);
    }

    [Fact]
    public void NonGiftEvent_ReturnsNull()
    {
        var rules = new[] { Rule("gift_name", "火箭") };
        Assert.Null(GiftRuleEngine.Match(rules, Gift(kind: LiveEventKind.Danmaku)));
        Assert.Null(GiftRuleEngine.Match(rules, Gift(kind: LiveEventKind.GuardBuy)));
    }

    [Fact]
    public void EmptyRules_ReturnsNull()
    {
        Assert.Null(GiftRuleEngine.Match([], Gift()));
    }
}
