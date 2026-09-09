using Erbai.Contracts.Configuration;
using Erbai.Core.Configuration;

namespace Erbai.Core.Tests;

/// <summary>
/// 阶段 5 配置段校验：queueup（容量/资格门槛/规则表：match_kind、action、
/// match_value、position）与 giftfx（时长）。规则错误必须抛 ConfigException，
/// 合法配置规范化（trim/枚举小写）。
/// </summary>
public class ModuleConfigValidationTests
{
    private static AppConfig Config(QueueUpConfig? queueUp = null, GiftFxConfig? giftFx = null) =>
        AppConfig.CreateDefault() with
        {
            QueueUp = queueUp ?? new QueueUpConfig(),
            GiftFx = giftFx ?? new GiftFxConfig(),
        };

    private static QueueUpRuleConfig Rule(
        string matchKind = "gift_name",
        string matchValue = "火箭",
        string action = "insert_at",
        int position = 1) =>
        new() { MatchKind = matchKind, MatchValue = matchValue, Action = action, Position = position };

    [Fact]
    public void Defaults_AreValid()
    {
        var result = ConfigValidator.Validate(Config());
        Assert.Equal(50, result.QueueUp.MaxEntries);
        Assert.False(result.QueueUp.EligibilityGate);
        Assert.Empty(result.QueueUp.Rules);
        Assert.Equal(5, result.GiftFx.DurationSeconds);
    }

    [Fact]
    public void MaxEntries_OutOfRange_Throws()
    {
        Assert.Throws<ConfigException>(() => ConfigValidator.Validate(Config(new QueueUpConfig { MaxEntries = 0 })));
        Assert.Throws<ConfigException>(() => ConfigValidator.Validate(Config(new QueueUpConfig { MaxEntries = 501 })));
        Assert.Throws<ConfigException>(() => ConfigValidator.Validate(Config(new QueueUpConfig { MaxEntries = -3 })));
    }

    [Fact]
    public void Rule_GiftNameInsertAt_ValidatedAndNormalized()
    {
        var result = ConfigValidator.Validate(Config(new QueueUpConfig
        {
            Rules = [Rule(matchKind: " GIFT_NAME ", matchValue: " 火箭 ", action: " INSERT_AT ", position: 2)],
        }));

        var rule = Assert.Single(result.QueueUp.Rules);
        Assert.Equal("gift_name", rule.MatchKind);
        Assert.Equal("火箭", rule.MatchValue);
        Assert.Equal("insert_at", rule.Action);
        Assert.Equal(2, rule.Position);
    }

    [Fact]
    public void Rule_CoinThreshold_Validated()
    {
        var result = ConfigValidator.Validate(Config(new QueueUpConfig
        {
            Rules = [Rule(matchKind: "coin_threshold", matchValue: "1000", action: "grant_eligibility")],
        }));

        var rule = Assert.Single(result.QueueUp.Rules);
        Assert.Equal("coin_threshold", rule.MatchKind);
        Assert.Equal("1000", rule.MatchValue);
        Assert.Equal("grant_eligibility", rule.Action);
    }

    [Theory]
    [InlineData("bad_kind", "火箭", "insert_at", 1)]      // match_kind 非法
    [InlineData("gift_name", "", "insert_at", 1)]         // match_value 空
    [InlineData("gift_name", "火箭", "bad_action", 1)]    // action 非法
    [InlineData("gift_name", "火箭", "insert_at", 0)]     // position 越界
    [InlineData("gift_name", "火箭", "insert_at", 501)]   // position 越界
    [InlineData("coin_threshold", "-1", "insert_at", 1)]  // 负阈值
    [InlineData("coin_threshold", "abc", "insert_at", 1)] // 非数字阈值
    public void Rule_Invalid_Throws(string matchKind, string matchValue, string action, int position)
    {
        Assert.Throws<ConfigException>(() => ConfigValidator.Validate(Config(new QueueUpConfig
        {
            Rules = [Rule(matchKind, matchValue, action, position)],
        })));
    }

    [Fact]
    public void MultipleRules_AllValidated()
    {
        Assert.Throws<ConfigException>(() => ConfigValidator.Validate(Config(new QueueUpConfig
        {
            Rules =
            [
                Rule(), // 合法
                Rule(matchKind: "coin_threshold", matchValue: "not-a-number", action: "grant_eligibility"),
            ],
        })));
    }

    [Fact]
    public void GiftFxDuration_OutOfRange_Throws()
    {
        Assert.Throws<ConfigException>(() => ConfigValidator.Validate(Config(giftFx: new GiftFxConfig { DurationSeconds = 0 })));
        Assert.Throws<ConfigException>(() => ConfigValidator.Validate(Config(giftFx: new GiftFxConfig { DurationSeconds = 61 })));
        Assert.Throws<ConfigException>(() => ConfigValidator.Validate(Config(giftFx: new GiftFxConfig { DurationSeconds = -5 })));
    }

    [Fact]
    public void GiftFxDuration_ValidBounds_Accepted()
    {
        var result = ConfigValidator.Validate(Config(giftFx: new GiftFxConfig { DurationSeconds = 60 }));
        Assert.Equal(60, result.GiftFx.DurationSeconds);
    }
}
