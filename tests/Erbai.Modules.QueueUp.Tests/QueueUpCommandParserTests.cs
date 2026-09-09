namespace Erbai.Modules.QueueUp.Tests;

/// <summary>弹幕排队命令解析（docs/01 §3.7 入口面）。</summary>
public class QueueUpCommandParserTests
{
    [Fact]
    public void Enqueue_WithContent()
    {
        var command = QueueUpCommandParser.Parse("排队 上麦唱一首");
        Assert.NotNull(command);
        Assert.Equal(QueueUpCommandKind.Enqueue, command.Kind);
        Assert.Equal("上麦唱一首", command.Content);
    }

    [Fact]
    public void Enqueue_WithFullWidthSpace_AndColon()
    {
        Assert.Equal("内容", QueueUpCommandParser.Parse("排队　内容")!.Content);
        Assert.Equal("内容", QueueUpCommandParser.Parse("排队:内容")!.Content);
        Assert.Equal("内容", QueueUpCommandParser.Parse("排队：内容")!.Content);
        Assert.Equal("内容", QueueUpCommandParser.Parse("  排队  内容  ")!.Content);
    }

    [Fact]
    public void Enqueue_EmptyContent_AllowedAsPlaceholder()
    {
        var command = QueueUpCommandParser.Parse("排队");
        Assert.NotNull(command);
        Assert.Equal(QueueUpCommandKind.Enqueue, command.Kind);
        Assert.Equal("", command.Content);
    }

    [Fact]
    public void CancelAndComplete_ExactMatch()
    {
        Assert.Equal(QueueUpCommandKind.Cancel, QueueUpCommandParser.Parse("取消排队")!.Kind);
        Assert.Equal(QueueUpCommandKind.Complete, QueueUpCommandParser.Parse("完成")!.Kind);
        // 前后空白容忍
        Assert.Equal(QueueUpCommandKind.Cancel, QueueUpCommandParser.Parse("  取消排队  ")!.Kind);
    }

    [Theory]
    [InlineData("排队点歌")]          // 无分隔符：不误吃
    [InlineData("排队中")]            // 前缀词：不误吃
    [InlineData("点歌 晴天")]         // 点歌命令：不冲突
    [InlineData("切歌")]
    [InlineData("普通弹幕")]
    [InlineData("")]
    [InlineData("   ")]
    public void NonQueueUpText_ReturnsNull(string text)
    {
        Assert.Null(QueueUpCommandParser.Parse(text));
    }
}
