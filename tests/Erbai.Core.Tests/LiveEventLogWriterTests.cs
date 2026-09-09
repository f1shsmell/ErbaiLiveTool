using Erbai.Contracts.Live;
using Erbai.Core.Logging;

namespace Erbai.Core.Tests;

/// <summary>
/// LiveEventLogWriter 契约：JSON 行序列化 ↔ 反序列化往返一致、坏行跳过、
/// 充电宝级中文/多字段不丢、文件按天命名可回读。
/// </summary>
public class LiveEventLogWriterTests
{
    [Fact]
    public void Serialize_Parse_RoundTrips_AllFields()
    {
        var evt = new LiveEvent
        {
            Platform = "douyin",
            RoomId = "7681046522100108041",
            Kind = LiveEventKind.Gift,
            UserId = 54321,
            Nickname = "白眉神探",
            Text = null,
            GiftName = "小心心",
            GiftCount = 3,
            TotalCoin = 100,
            CoinType = "gold",
            IsAdmin = false,
            IsAnchor = true,
            FanLevel = 7,
            MedalLevel = null,
            Timestamp = new DateTimeOffset(2026, 9, 3, 8, 40, 55, TimeSpan.FromHours(8)),
        };

        var line = LiveEventLogWriter.Serialize(evt);
        var parsed = LiveEventLogWriter.TryParseLine(line);

        Assert.NotNull(parsed);
        Assert.Equal(evt.Platform, parsed.Platform);
        Assert.Equal(evt.RoomId, parsed.RoomId);
        Assert.Equal(evt.Kind, parsed.Kind);
        Assert.Equal(evt.UserId, parsed.UserId);
        Assert.Equal(evt.Nickname, parsed.Nickname);
        Assert.Equal(evt.GiftName, parsed.GiftName);
        Assert.Equal(evt.GiftCount, parsed.GiftCount);
        Assert.Equal(evt.TotalCoin, parsed.TotalCoin);
        Assert.Equal(evt.CoinType, parsed.CoinType);
        Assert.Equal(evt.IsAdmin, parsed.IsAdmin);
        Assert.Equal(evt.IsAnchor, parsed.IsAnchor);
        Assert.Equal(evt.FanLevel, parsed.FanLevel);
        Assert.Equal(evt.MedalLevel, parsed.MedalLevel);
        Assert.Equal(evt.Timestamp, parsed.Timestamp);
    }

    [Fact]
    public void Write_ReadFile_AppendsAndParses()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erbai-live-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new LiveEventLogWriter(dir))
            {
                writer.Write(MakeDanmaku("抖音", "你好呀", 1));
                writer.Write(MakeDanmaku("B站", "hello", 2));
            }

            var date = DateTime.Now.ToString("yyyyMMdd");
            var path = Path.Combine(dir, $"{LiveEventLogWriter.FilePrefix}{date}.log");
            Assert.True(File.Exists(path));

            var events = LiveEventLogWriter.ReadFile(path);
            Assert.Equal(2, events.Count);
            Assert.Equal("你好呀", events[0].Text);
            Assert.Equal("B站", events[1].Platform);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadFile_SkipsCorruptLines_KeepsGoodOnes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erbai-live-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var date = DateTime.Now.ToString("yyyyMMdd");
            var path = Path.Combine(dir, $"{LiveEventLogWriter.FilePrefix}{date}.log");
            File.WriteAllLines(path,
            [
                "not-json",
                LiveEventLogWriter.Serialize(MakeDanmaku("douyin", "幸存的行", 42)),
                "",
                "{broken json",
            ]);

            var events = LiveEventLogWriter.ReadFile(path);
            var single = Assert.Single(events);
            Assert.Equal("幸存的行", single.Text);
            Assert.Equal(42, single.UserId);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void UnknownKindLine_ReturnsNull()
    {
        var line = "{\"t\":\"2026-09-03T00:00:00+08:00\",\"p\":\"douyin\",\"k\":\"NoSuchKind\",\"uid\":1,\"n\":\"x\"}";
        Assert.Null(LiveEventLogWriter.TryParseLine(line));
    }

    private static LiveEvent MakeDanmaku(string platform, string text, long userId) => new()
    {
        Platform = platform,
        RoomId = "room1",
        Kind = LiveEventKind.Danmaku,
        UserId = userId,
        Nickname = $"观众{userId}",
        Text = text,
        Timestamp = DateTimeOffset.Now,
    };
}