using Erbai.Contracts.Live;
using Erbai.OverlayWpf;

namespace Erbai.App.Tests;

/// <summary>
/// 弹幕行文案/配色决策测试（bililive_dm AddDMText 语义移植 + 抖音适配，2026-09）：
/// 纯逻辑 DanmakuLineBuilder，不依赖 WPF 渲染。
/// </summary>
public class DanmakuLineBuilderTests
{
    private static LiveEvent Evt(LiveEventKind kind, string? platform = "bilibili", string? nickname = null,
        string? text = null, string? giftName = null, int giftCount = 0, long totalCoin = 0,
        bool? isAdmin = null, bool? isAnchor = null, int? fanLevel = null) => new()
    {
        Platform = platform ?? "bilibili",
        RoomId = "1",
        Kind = kind,
        UserId = 0,
        Nickname = nickname ?? "",
        Text = text,
        GiftName = giftName,
        GiftCount = giftCount,
        TotalCoin = totalCoin,
        IsAdmin = isAdmin,
        IsAnchor = isAnchor,
        FanLevel = fanLevel,
        Timestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Danmaku_ShowsWhoAndText_NotWarn()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Danmaku, nickname: "观众A", text: "你好世界"))!.Value;
        Assert.Equal("观众A", line.Who);
        Assert.Equal("你好世界", line.Text);
        Assert.False(line.Warn);
        Assert.False(line.IsAdmin);
    }

    [Fact]
    public void Gift_ShowsGiftLine_AsWarn()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Gift, nickname: "土豪B", giftName: "大火箭", giftCount: 3))!.Value;
        Assert.Equal("土豪B", line.Who);
        Assert.Equal("送出 大火箭 × 3", line.Text);
        Assert.True(line.Warn);
    }

    [Fact]
    public void Gift_WithoutCount_OmitsMultiplier()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Gift, nickname: "土豪B", giftName: "小心心"))!.Value;
        Assert.Equal("送出 小心心", line.Text);
    }

    [Fact]
    public void DouyinGift_WithCoin_ShowsBatteryValue()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Gift, platform: "douyin", nickname: "抖音土豪",
            giftName: "大火箭", giftCount: 2, totalCoin: 1200))!.Value;
        Assert.Equal("送出 大火箭 × 2（1200 电池）", line.Text);
    }

    [Fact]
    public void DouyinGift_WithoutCoin_OmitsBattery()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Gift, platform: "douyin", nickname: "抖音土豪",
            giftName: "小心心", giftCount: 1))!.Value;
        Assert.Equal("送出 小心心 × 1", line.Text);
    }

    [Fact]
    public void Enter_ShowsEnterRoom()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Enter, nickname: "路人C"))!.Value;
        Assert.Equal("进入直播间", line.Text);
        Assert.False(line.Warn);
    }

    [Fact]
    public void GuardBuy_IsWarn()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.GuardBuy, nickname: "舰长D"))!.Value;
        Assert.True(line.Warn);
        Assert.Contains("大航海", line.Text);
    }

    [Fact]
    public void DouyinFanClub_ShowsJoinedWithLevel()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Subscribe, platform: "douyin",
            nickname: "团粉E", fanLevel: 3))!.Value;
        Assert.Equal("加入了粉丝团 Lv.3", line.Text);
        Assert.Equal(3, line.FanLevel);
    }

    [Fact]
    public void DouyinFanClub_WithoutLevel_ShowsJoined()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Subscribe, platform: "douyin", nickname: "团粉E"))!.Value;
        Assert.Equal("加入了粉丝团", line.Text);
        Assert.Null(line.FanLevel);
    }

    [Fact]
    public void BilibiliSubscribe_ShowsFollowed()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Subscribe, platform: "bilibili", nickname: "关注F"))!.Value;
        Assert.Equal("关注了主播", line.Text);
    }

    [Fact]
    public void LiveState_ShowsOffline_AsWarn()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.LiveState, platform: "douyin", nickname: "直播间"))!.Value;
        Assert.Equal("直播间已下播", line.Text);
        Assert.True(line.Warn);
    }

    [Fact]
    public void Danmaku_WithFanLevel_PropagatesBadge()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Danmaku, platform: "douyin",
            nickname: "团粉G", text: "好耶", fanLevel: 7))!.Value;
        Assert.Equal(7, line.FanLevel);
        Assert.Equal("好耶", line.Text);
    }

    [Fact]
    public void EmojiNickname_PreservedInLine()
    {
        // 用户实测昵称（含 emoji + 变体选择符）：弹幕行模型必须完整保留昵称与内容
        const string nick = "AAA金雷竹批发韩总🗡️⚡️";
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Danmaku, platform: "douyin",
            nickname: nick, text: "主播唱得好 🎵"))!.Value;
        Assert.Equal(nick, line.Who);
        Assert.Equal("主播唱得好 🎵", line.Text);
        Assert.False(line.Warn);
    }

    [Fact]
    public void Admin_FlagsMarked()
    {
        var line = DanmakuLineBuilder.Build(Evt(LiveEventKind.Danmaku, nickname: "管理E", text: "测试", isAdmin: true))!.Value;
        Assert.True(line.IsAdmin);
    }

    [Fact]
    public void UnknownKind_EmptyText_ReturnsNull()
    {
        Assert.Null(DanmakuLineBuilder.Build(Evt((LiveEventKind)999, nickname: "某人")));
    }
}
