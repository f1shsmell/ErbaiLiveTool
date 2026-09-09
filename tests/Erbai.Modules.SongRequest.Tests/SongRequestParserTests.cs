using Erbai.Modules.SongRequest.Services;

namespace Erbai.Modules.SongRequest.Tests;

/// <summary>弹幕点歌解析（解析域）。</summary>
public class SongRequestParserTests
{
    [Theory]
    [InlineData("点歌 晴天")]
    [InlineData("点歌:晴天")]
    [InlineData("点歌：晴天")]
    [InlineData(" 点歌   晴天  ")]
    [InlineData("#点歌 晴天")]
    [InlineData("# 点歌 晴天")]
    public void Parse_RequestVariants(string text)
    {
        var command = SongRequestParser.Parse(text);
        Assert.NotNull(command);
        Assert.Equal("request", command.Action);
        Assert.Equal("晴天", command.SongName);
        Assert.Equal("", command.Singer);
    }

    [Theory]
    [InlineData("点歌 晴天 - 周杰伦")]
    [InlineData("点歌 晴天—周杰伦")]
    [InlineData("点歌 晴天－周杰伦")]
    [InlineData("点歌 晴天 | 周杰伦")]
    [InlineData("点歌 晴天｜周杰伦")]
    // 2026-09 扩展：下划线 / 双破折号（连续分隔符）/ macron ˉ / 全角下划线＿ / 连续连字符
    [InlineData("点歌 晴天_周杰伦")]
    [InlineData("点歌 晴天——周杰伦")]
    [InlineData("点歌 晴天ˉ周杰伦")]
    [InlineData("点歌 晴天＿周杰伦")]
    [InlineData("点歌 晴天---周杰伦")]
    [InlineData("点歌 晴天 __ 周杰伦")]
    public void Parse_SingerSeparated(string text)
    {
        var command = SongRequestParser.Parse(text);
        Assert.NotNull(command);
        Assert.Equal("request", command.Action);
        Assert.Equal("晴天", command.SongName);
        Assert.Equal("周杰伦", command.Singer);
    }

    [Fact]
    public void Parse_NoSeparator_WholeQueryIsSongName()
    {
        // R7：带空格标题原样搜索，不按"首词歌名"拆分
        var command = SongRequestParser.Parse("点歌 Hotel California");
        Assert.NotNull(command);
        Assert.Equal("Hotel California", command.SongName);
        Assert.Equal("", command.Singer);
    }

    [Theory]
    [InlineData("点歌 Hotel California - Eagles")]
    [InlineData("点歌 Hotel California-Eagles")]
    [InlineData("点歌 Hotel California_Eagles")]
    public void Parse_SpaceInSongName_WithSeparator_SplitsSinger(string text)
    {
        // 带空格歌名 + 分隔符：分隔符（- / _ 等）拆分歌手，歌名空格原样保留
        var command = SongRequestParser.Parse(text);
        Assert.NotNull(command);
        Assert.Equal("Hotel California", command.SongName);
        Assert.Equal("Eagles", command.Singer);
    }

    [Theory]
    [InlineData("下一首")]
    [InlineData("切歌")]
    [InlineData("跳过")]
    [InlineData(" 切歌 ")]
    public void Parse_SkipCommands(string text)
    {
        var command = SongRequestParser.Parse(text);
        Assert.NotNull(command);
        Assert.Equal("skip", command.Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("你好")]
    [InlineData("点歌")]
    [InlineData("点歌 ")]
    public void Parse_NonCommands_ReturnNull(string text)
    {
        Assert.Null(SongRequestParser.Parse(text));
    }

    [Fact]
    public void Parse_AdminCommands_MatchedByRegexes()
    {
        Assert.Matches(SongRequestParser.AdminSetRegex(), "设置管理员@小明");
        Assert.Matches(SongRequestParser.AdminSetRegex(), "设置管理员 小明");
        Assert.Matches(SongRequestParser.AdminClearRegex(), "取消管理员@小明");
        Assert.Matches(SongRequestParser.BanSetRegex(), "拉黑@小明");
        Assert.Matches(SongRequestParser.BanClearRegex(), "取消拉黑@小明");
        Assert.DoesNotMatch(SongRequestParser.AdminSetRegex(), "设置管理员");
    }

    [Theory]
    [InlineData("拉黑歌曲 晴天")]
    [InlineData("拉黑歌曲 晴天 - 周杰伦")]
    [InlineData("拉黑歌曲 *周杰伦")]
    public void BanSongSetRegex_Matches(string text)
    {
        Assert.Matches(SongRequestParser.BanSongSetRegex(), text);
    }

    [Theory]
    [InlineData("取消拉黑歌曲 晴天")]
    [InlineData("取消拉黑歌曲 *周杰伦")]
    public void BanSongClearRegex_Matches(string text)
    {
        Assert.Matches(SongRequestParser.BanSongClearRegex(), text);
    }

    [Theory]
    [InlineData("拉黑歌曲")]
    [InlineData("取消拉黑歌曲")]
    public void BanSongRegexes_EmptyTarget_NoMatch(string text)
    {
        Assert.DoesNotMatch(SongRequestParser.BanSongSetRegex(), text);
        Assert.DoesNotMatch(SongRequestParser.BanSongClearRegex(), text);
    }

    [Fact]
    public void BanSongRegexes_DoNotOverlapUserBan()
    {
        // 用户拉黑命令不误伤歌曲命令
        Assert.DoesNotMatch(SongRequestParser.BanSongSetRegex(), "拉黑@小明");
        Assert.DoesNotMatch(SongRequestParser.BanSongClearRegex(), "取消拉黑@小明");
    }
}
