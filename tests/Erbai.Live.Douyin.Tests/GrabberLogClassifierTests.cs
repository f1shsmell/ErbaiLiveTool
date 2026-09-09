using Erbai.Live.Douyin.Hosting;

namespace Erbai.Live.Douyin.Tests;

/// <summary>Grabber stdout 行分级（docs/04 §3.2：中文 token + 英文 errors 正则 + "N errors" 误报剔除）。</summary>
public class GrabberLogClassifierTests
{
    [Theory]
    [InlineData("程序初始化错误，配置不正确")]
    [InlineData("检测到异常")]
    [InlineData("启动失败: port_not_ready")]
    [InlineData("无法连接到服务器")]
    [InlineData("exception: bad stuff")]
    public void 中文错误token分级为Error(string line) =>
        Assert.Equal(GrabberLogLevel.Error, GrabberLogClassifier.Classify(line));

    [Theory]
    [InlineData("some error occurred")]
    [InlineData("Error: cannot bind")]
    [InlineData("mitm ERROR on stream")]
    public void 英文errors单词分级为Error(string line) =>
        Assert.Equal(GrabberLogLevel.Error, GrabberLogClassifier.Classify(line));

    [Theory]
    [InlineData("Total 3 errors handled")]      // "N errors" 计数误报剔除
    [InlineData("0 errors, 5 warnings")]        // 计数行不得判 error
    [InlineData("1 error(s)")]                  // 计数带括号
    public void Nerrors计数不判Error(string line) =>
        Assert.NotEqual(GrabberLogLevel.Error, GrabberLogClassifier.Classify(line));

    [Theory]
    [InlineData("警告：配置将重启后生效")]
    [InlineData("this is a warning")]
    [InlineData("warn: deprecated option")]
    [InlineData("WARNING: disk low")]
    public void 警告token分级为Warning(string line) =>
        Assert.Equal(GrabberLogLevel.Warning, GrabberLogClassifier.Classify(line));

    [Theory]
    [InlineData("弹幕服务已启动")]
    [InlineData("已建立与[127.0.0.1:1234]的连接")]
    [InlineData("[grabber-state] {\"event\":\"ready\"}")]
    public void 普通行分级为System(string line) =>
        Assert.Equal(GrabberLogLevel.System, GrabberLogClassifier.Classify(line));
}
