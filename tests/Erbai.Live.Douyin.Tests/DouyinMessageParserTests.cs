using System.Text.Json;
using Erbai.Contracts.Live;
using Erbai.Live.Douyin.Messages;

namespace Erbai.Live.Douyin.Tests;

/// <summary>抖音 Grabber 报文归一测试（docs/04 §3.1 字段解析面）。</summary>
public class DouyinMessageParserTests
{
    private static string Envelope(int type, object data) =>
        JsonSerializer.Serialize(new { Type = type, Data = JsonSerializer.Serialize(data) });

    private static LiveEvent? Parse(int type, object data, IReadOnlySet<string>? allowed = null) =>
        DouyinMessageParser.Parse(Envelope(type, data), allowed,
            error => throw new InvalidOperationException($"parse error: {error}"));

    [Fact]
    public void 弹幕信封解析()
    {
        var evt = Parse(1, new
        {
            Content = "点歌 晴天 - 周杰伦",
            WebRoomId = "123456",
            User = new { Nickname = "测试观众", IsAdmin = true },
        });

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.Danmaku, evt.Kind);
        Assert.Equal("点歌 晴天 - 周杰伦", evt.Text);
        Assert.Equal("测试观众", evt.Nickname);
        Assert.Equal("123456", evt.RoomId);
        Assert.True(evt.IsAdmin);
        Assert.Equal("douyin", evt.Platform);
    }

    [Fact]
    public void 含表情符号昵称与内容完整保留()
    {
        // 用户实测昵称（含 emoji 与变体选择符）与含表情的弹幕文本：
        // 解析必须完整保留（UTF-8 JSON 往返不丢字），否则该用户的点歌/弹幕无法识别
        const string nick = "AAA金雷竹批发韩总🗡️⚡️";
        var evt = Parse(1, new
        {
            Content = "点歌 晴天 - 周杰伦 🎵",
            WebRoomId = "123456",
            User = new { Nickname = nick },
        });

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.Danmaku, evt.Kind);
        Assert.Equal("点歌 晴天 - 周杰伦 🎵", evt.Text);
        Assert.Equal(nick, evt.Nickname);
        Assert.Equal("douyin", evt.Platform);
    }

    [Fact]
    public void 含表情昵称的礼物事件保留发送者()
    {
        const string nick = "AAA金雷竹批发韩总🗡️⚡️";
        var evt = Parse(5, new
        {
            GiftName = "大火箭",
            GiftCount = 3,
            DiamondCount = 100,
            WebRoomId = "123456",
            User = new { Nickname = nick },
        });

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.Gift, evt.Kind);
        Assert.Equal(nick, evt.Nickname);
        Assert.Equal("大火箭", evt.GiftName);
        Assert.Equal(3, evt.GiftCount);
        Assert.Equal(100, evt.TotalCoin);
    }

    [Fact]
    public void 字符串假标志不当真()
    {
        var evt = Parse(1, new
        {
            Content = "点歌 晴天",
            WebRoomId = "123456",
            User = new { Nickname = "普通观众", IsAdmin = "false", IsAnchor = "0" },
        });

        Assert.NotNull(evt);
        Assert.False(evt.IsAdmin);
        Assert.False(evt.IsAnchor);
    }

    [Fact]
    public void 非弹幕与房间白名单过滤()
    {
        Assert.Null(DouyinMessageParser.Parse(JsonSerializer.Serialize(new { Type = 6, Data = "{}" })));
        Assert.Null(Parse(1, new { Content = "点歌 晴天", WebRoomId = "1", User = new { } },
            new HashSet<string> { "2" }));
    }

    [Theory]
    [InlineData("IsAnchor")]
    [InlineData("isanchor")]
    [InlineData("anchor")]
    [InlineData("isowner")]
    [InlineData("owner")]
    public void 主播标志变体(string key)
    {
        var evt = Parse(1, new { Content = "点歌 晴天", WebRoomId = "1", User = new Dictionary<string, object> { [key] = true } });
        Assert.NotNull(evt);
        Assert.True(evt.IsAnchor, key);
    }

    [Fact]
    public void 主播role串与普通弹幕()
    {
        var evt = Parse(1, new { Content = "点歌 晴天", WebRoomId = "1", User = new { role = "主播" } });
        Assert.NotNull(evt);
        Assert.True(evt.IsAnchor);
        // "主播" 是 anchor-only token：不得升格为 admin
        Assert.False(evt.IsAdmin);

        var plain = Parse(1, new { Content = "点歌 晴天", WebRoomId = "1", User = new { Nickname = "观众" } });
        Assert.NotNull(plain);
        Assert.False(plain.IsAnchor);
        Assert.False(plain.IsAdmin);
    }

    [Fact]
    public void Owner匹配识别无标志主播()
    {
        var evt = Parse(1, new
        {
            Content = "点歌 晴天",
            WebRoomId = "1",
            User = new { Nickname = "主播", Id = "12345678901" },
            Owner = new { UserId = "12345678901", Nickname = "主播" },
        });

        Assert.NotNull(evt);
        Assert.True(evt.IsAnchor);
        Assert.Equal(12345678901, evt.UserId);
    }

    [Fact]
    public void Owner匹配不得升格管理员()
    {
        var evt = Parse(1, new
        {
            Content = "点歌 晴天",
            WebRoomId = "1",
            User = new { Nickname = "主播", Id = "owner-1" },
            Owner = new { UserId = "owner-1" },
        });

        Assert.NotNull(evt);
        Assert.True(evt.IsAnchor);
        Assert.False(evt.IsAdmin);
    }

    [Fact]
    public void Owner不匹配保持普通观众()
    {
        var evt = Parse(1, new
        {
            Content = "点歌 晴天",
            WebRoomId = "1",
            User = new { Nickname = "观众", Id = "777" },
            Owner = new { UserId = "owner-1" },
        });

        Assert.NotNull(evt);
        Assert.False(evt.IsAnchor);
        Assert.Equal(777, evt.UserId);
    }

    [Fact]
    public void SecUid匹配识别主播且Id兜底()
    {
        var evt = Parse(1, new
        {
            Content = "点歌 晴天",
            WebRoomId = "1",
            User = new { Nickname = "主播", SecUid = "sec-owner" },
            Owner = new { UserId = "owner-1", SecUid = "sec-owner" },
        });

        Assert.NotNull(evt);
        Assert.True(evt.IsAnchor);
        // 数字 Id 缺失时从 Owner 块兜底（身份可持久化）；非数字 Id 在 long 模型下归零
        Assert.Equal(0, evt.UserId);
    }

    [Fact]
    public void 管理员键变体()
    {
        foreach (var key in new[] { "IsAdmin", "isadmin", "IsModerator", "RoomAdmin", "moderator" })
        {
            var evt = Parse(1, new { Content = "点歌", WebRoomId = "1", User = new Dictionary<string, object> { [key] = true } });
            Assert.NotNull(evt);
            Assert.True(evt.IsAdmin, key);
        }
    }

    [Fact]
    public void 粉丝团等级文档形态()
    {
        var evt = Parse(1, new
        {
            Content = "点歌",
            WebRoomId = "1",
            User = new { Nickname = "粉丝", FansClub = new { Level = 4 } },
        });

        Assert.NotNull(evt);
        Assert.Equal(4, evt.FanLevel);
    }

    [Theory]
    [InlineData("FanLevel", 2)]
    [InlineData("FansLevel", 3)]
    [InlineData("FansClubLevel", 5)]
    [InlineData("fan_level", 6)]
    public void 粉丝团等级平铺键(string key, int level)
    {
        var evt = Parse(1, new { Content = "点歌", WebRoomId = "1", User = new Dictionary<string, object> { [key] = level } });
        Assert.NotNull(evt);
        Assert.Equal(level, evt.FanLevel);
    }

    [Fact]
    public void 粉丝团事件与顶级Level()
    {
        var evt = Parse(7, new { WebRoomId = "1", User = new { Nickname = "粉丝", Id = "88" }, Level = 9 });
        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.Subscribe, evt.Kind);
        Assert.Equal(9, evt.FanLevel);
    }

    [Fact]
    public void 普通弹幕不得误用顶级Level()
    {
        // 普通弹幕的顶级 Level 是用户等级，不是粉丝团等级
        var evt = Parse(1, new { Content = "点歌", WebRoomId = "1", User = new { Nickname = "观众", Id = "88" }, Level = 50 });
        Assert.NotNull(evt);
        Assert.Null(evt.FanLevel);
    }

    [Fact]
    public void 礼物映射()
    {
        var evt = Parse(5, new
        {
            WebRoomId = "1",
            GiftName = "小心心",
            GiftCount = 3,
            DiamondCount = 30,
            User = new { Nickname = "送礼人", Id = "55" },
        });

        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.Gift, evt.Kind);
        Assert.Equal("小心心", evt.GiftName);
        Assert.Equal(3, evt.GiftCount);
        Assert.Equal(30, evt.TotalCoin);
        Assert.Equal("gold", evt.CoinType);
    }

    [Fact]
    public void 下播无Data()
    {
        var evt = DouyinMessageParser.Parse(JsonSerializer.Serialize(new { Type = 9 }));
        Assert.NotNull(evt);
        Assert.Equal(LiveEventKind.LiveState, evt.Kind);
        Assert.Equal("下播", evt.Text);
    }

    [Theory]
    [InlineData(2, LiveEventKind.Like)]
    [InlineData(3, LiveEventKind.Enter)]
    [InlineData(4, LiveEventKind.Follow)]
    [InlineData(8, LiveEventKind.Share)]
    public void 其余类型映射(int type, LiveEventKind kind)
    {
        var evt = Parse(type, new { WebRoomId = "1", User = new { Nickname = "观众", Id = "1" } });
        Assert.NotNull(evt);
        Assert.Equal(kind, evt.Kind);
    }

    [Fact]
    public void 空弹幕忽略()
    {
        Assert.Null(Parse(1, new { WebRoomId = "1", User = new { Nickname = "观众" } }));
    }

    [Fact]
    public void 坏报文返回null不抛出()
    {
        string? error = null;
        var evt = DouyinMessageParser.Parse("{not json", onError: e => error = e);
        Assert.Null(evt);
        Assert.NotNull(error);
    }

    [Fact]
    public void Data为内嵌对象而非字符串()
    {
        // 部分 Grabber 版本 Data 直接是对象
        var evt = DouyinMessageParser.Parse(
            JsonSerializer.Serialize(new
            {
                Type = 1,
                Data = new { Content = "点歌", WebRoomId = "1", User = new { Nickname = "观众" } },
            }));
        Assert.NotNull(evt);
        Assert.Equal("点歌", evt.Text);
    }

    [Fact]
    public void 昵称缺失时兜底抖音观众()
    {
        var evt = Parse(1, new { Content = "点歌", WebRoomId = "1", User = new { } });
        Assert.NotNull(evt);
        Assert.Equal("抖音观众", evt.Nickname);
    }
}
