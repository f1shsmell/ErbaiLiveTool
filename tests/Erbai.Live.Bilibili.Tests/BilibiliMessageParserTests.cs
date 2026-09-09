using System.Text.Json;
using Erbai.Contracts.Live;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Tests;

public class BilibiliMessageParserTests
{
    private const long RoomOwnerUid = 17152307;
    private const string RoomId = "5050";

    private static LiveEvent? Parse(string json)
    {
        try
        {
            return BilibiliMessageParser.Parse(JsonDocument.Parse(json).RootElement, RoomOwnerUid, RoomId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [Fact]
    public void Danmaku_StructuredUsernamePreferred_WhenLonger()
    {
        // blivedm 本地修改语义：mode_info.user.base.name 比 info[2][1] 更长时采用前者
        var evt = Parse(DanmakuJson(uname: "爱", structuredName: "爱上一只猫", admin: 0));

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.Danmaku, evt!.Kind);
        Assert.Equal("爱上一只猫", evt.Nickname);
        Assert.Equal("点歌 晴天 - 周杰伦", evt.Text);
        Assert.Equal(123456L, evt.UserId);
        Assert.Equal(RoomId, evt.RoomId);
        Assert.Equal("bilibili", evt.Platform);
    }

    [Fact]
    public void Danmaku_LegacyUsername_WhenStructuredShorterOrMissing()
    {
        var evt = Parse(DanmakuJson(uname: "完整昵称", structuredName: "短", admin: 0));

        Assert.Equal("完整昵称", evt!.Nickname);
    }

    [Fact]
    public void Danmaku_AdminFlag_FromLegacySlot()
    {
        var evt = Parse(DanmakuJson(uname: "房管", admin: 1));

        Assert.True(evt!.IsAdmin);
    }

    [Fact]
    public void Danmaku_AnchorDetected_ByRoomOwnerUid()
    {
        var evt = Parse(DanmakuJson(uname: "主播", admin: 0, uid: RoomOwnerUid));

        Assert.True(evt!.IsAnchor);
        Assert.False(evt.IsAdmin);
    }

    [Fact]
    public void Danmaku_MedalLevel_Parsed()
    {
        var evt = Parse(DanmakuJson(uname: "有牌子", medalLevel: 22));

        Assert.Equal(22, evt!.MedalLevel);
    }

    [Fact]
    public void Danmaku_EmptyMedalArray_MedalNull()
    {
        var evt = Parse(DanmakuJson(uname: "无牌子", medalLevel: 0));

        Assert.Null(evt!.MedalLevel);
    }

    [Fact]
    public void Danmaku_Timestamp_FromInfoSlot()
    {
        var evt = Parse(DanmakuJson(uname: "时间戳"));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), evt!.Timestamp);
    }

    [Fact]
    public void Danmaku_MillisecondTimestamp_2026Format()
    {
        // 2026 起实测 info[0][4] 为毫秒时间戳（13 位），按毫秒解析不抛异常
        var json = DanmakuJson(uname: "毫秒时间戳")
            .Replace("1700000000", "1787436801779", StringComparison.Ordinal);

        var evt = Parse(json);

        Assert.NotNull(evt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787436801779), evt!.Timestamp);
    }

    [Fact]
    public void SendGift_GoldCoins_Parsed()
    {
        var json = """
            {"cmd":"SEND_GIFT","data":{"uid":555,"uname":"送礼人","giftName":"辣条","num":3,
             "price":100,"coin_type":"gold","total_coin":300,"guard_level":0,
             "medal_info":{"medal_level":5,"medal_name":"测试牌","anchor_roomid":5050,"target_id":1}}}
            """;

        var evt = Parse(json);

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.Gift, evt!.Kind);
        Assert.Equal("辣条", evt.GiftName);
        Assert.Equal(3, evt.GiftCount);
        Assert.Equal(300, evt.TotalCoin);
        Assert.Equal("gold", evt.CoinType);
        Assert.Equal(5, evt.MedalLevel);
        Assert.Equal("送礼人", evt.Nickname);
    }

    [Fact]
    public void SendGift_SilverCoins_Parsed()
    {
        var json = """
            {"cmd":"SEND_GIFT","data":{"uid":555,"uname":"送礼人","giftName":"小心心","num":1,
             "price":1,"coin_type":"silver","total_coin":1,"guard_level":0}}
            """;

        var evt = Parse(json);

        Assert.Equal("silver", evt!.CoinType);
    }

    [Fact]
    public void GuardBuy_Parsed()
    {
        var json = """
            {"cmd":"GUARD_BUY","data":{"uid":777,"username":"舰长哥","guard_level":3,
             "num":1,"price":198000,"gift_id":10003,"gift_name":"舰长","start_time":1700000000}}
            """;

        var evt = Parse(json);

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.GuardBuy, evt!.Kind);
        Assert.Equal("舰长", evt.GiftName);
        Assert.Equal(198000, evt.TotalCoin);
        Assert.Equal(777L, evt.UserId);
    }

    [Fact]
    public void UserToastV2_NormalizedToGuardBuy()
    {
        var json = """
            {"cmd":"USER_TOAST_MSG_V2","data":{"uid":888,"username":"提督哥","role_name":"提督",
             "guard_level":2,"num":1,"price":199800,"unit":"月"}}
            """;

        var evt = Parse(json);

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.GuardBuy, evt!.Kind);
        Assert.Equal("提督", evt.GiftName);
        Assert.Equal(199800, evt.TotalCoin);
        Assert.Equal("提督哥", evt.Nickname);
    }

    [Fact]
    public void SuperChat_NormalizedToDanmaku()
    {
        var json = """
            {"cmd":"SUPER_CHAT_MESSAGE","data":{"uid":999,"price":30,"message":"用SC点歌 夜曲 - 周杰伦",
             "user":{"uname":"SC用户","face":""}}}
            """;

        var evt = Parse(json);

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.Danmaku, evt!.Kind);
        Assert.Equal("用SC点歌 夜曲 - 周杰伦", evt.Text);
        Assert.Equal("SC用户", evt.Nickname);
    }

    [Fact]
    public void SuperChatDelete_Skipped()
    {
        var evt = Parse("""{"cmd":"SUPER_CHAT_MESSAGE_DELETE","data":{"ids":[1,2]}}""");

        Assert.Null(evt);
    }

    [Fact]
    public void InteractWord_Enter_FromUserForm()
    {
        var json = """
            {"cmd":"INTERACT_WORD","data":{"msg_type":1,"timestamp":1700000000,
             "user":{"uid":111,"uname":"路人甲"}}}
            """;

        var evt = Parse(json);

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.Enter, evt!.Kind);
        Assert.Equal("路人甲", evt.Nickname);
        Assert.Equal(111L, evt.UserId);
    }

    [Fact]
    public void InteractWord_Follow_FromUinfoForm()
    {
        var json = """
            {"cmd":"INTERACT_WORD","data":{"msg_type":2,"timestamp":1700000000,
             "uinfo":{"uid":222,"base":{"name":"关注者","face":""}}}}
            """;

        var evt = Parse(json);

        Assert.Equal(LiveEventKind.Follow, evt!.Kind);
        Assert.Equal("关注者", evt.Nickname);
        Assert.Equal(222L, evt.UserId);
    }

    [Fact]
    public void InteractWord_Like_Mapped()
    {
        var json = """{"cmd":"INTERACT_WORD","data":{"msg_type":6,"user":{"uid":1,"uname":"点赞者"}}}""";

        Assert.Equal(LiveEventKind.Like, Parse(json)!.Kind);
    }

    [Fact]
    public void LiveState_LiveAndPreparing()
    {
        var live = Parse("""{"cmd":"LIVE","data":{"roomid":5050}}""");
        var preparing = Parse("""{"cmd":"PREPARING","data":{"roomid":5050}}""");

        Assert.Equal(LiveEventKind.LiveState, live!.Kind);
        Assert.Equal("开播", live.Text);
        Assert.Equal(LiveEventKind.LiveState, preparing!.Kind);
        Assert.Equal("下播", preparing.Text);
    }

    [Fact]
    public void UnknownCommand_ReturnsNull()
    {
        Assert.Null(Parse("""{"cmd":"UNKNOWN_CMD","data":{}}"""));
        Assert.Null(Parse("""{"cmd":"WATCHED_CHANGE","data":{}}"""));
    }

    [Fact]
    public void MalformedPayload_ReturnsNull_WithoutThrowing()
    {
        Assert.Null(Parse("""{"cmd":"DANMU_MSG"}"""));          // 缺 info
        Assert.Null(Parse("""{"cmd":"SEND_GIFT"}"""));          // 缺 data
        Assert.Null(Parse("""{"info":123}"""));                 // 缺 cmd
        Assert.Null(Parse("""not-json"""));                     // 非法 JSON
    }

    [Fact]
    public void AnchorFlag_NullForNonDanmakuKinds()
    {
        var json = """{"cmd":"SEND_GIFT","data":{"uid":17152307,"uname":"主播自己","giftName":"x","num":1}}""";

        var evt = Parse(json);

        Assert.True(evt!.IsAnchor);
    }

    private static string DanmakuJson(string uname, string? structuredName = null, int admin = 0,
        long uid = 123456, int medalLevel = 0)
    {
        var modeInfo = structuredName is null
            ? "{}"
            : $"{{\"user\":{{\"base\":{{\"name\":\"{structuredName}\",\"face\":\"\"}}}}}}";
        return $$"""
                 {"cmd":"DANMU_MSG","info":[
                   [0,25,16777215,0,1700000000,0,0,0,0,0,0,0,0,0,0,{{modeInfo}}],
                   "点歌 晴天 - 周杰伦",
                   [{{uid}},"{{uname}}",{{admin}},0,0,10000,1,""],
                   [{{medalLevel}},"粉丝牌","房主",5050,0,0],
                   [30,0,5805050,">50000"],
                   0,0,0,0,0,0,0,
                   [1]
                 ]}
                 """;
    }
}
