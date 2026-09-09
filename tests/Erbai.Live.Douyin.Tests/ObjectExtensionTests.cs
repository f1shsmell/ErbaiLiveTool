using System.Collections.Generic;
using BarrageGrab;

namespace Erbai.Live.Douyin.Tests;

/// <summary>Grabber ObjectExtension 纯函数单测（字符串判定 / JSON 转换 / 字典取值）。</summary>
public class ObjectExtensionTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("abc", false)]
    public void IsNullOrWhiteSpace_判定(string? value, bool expected)
    {
        Assert.Equal(expected, value.IsNullOrWhiteSpace());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("abc", false)]
    public void IsNullOrEmpty_判定(string? value, bool expected)
    {
        Assert.Equal(expected, value.IsNullOrEmpty());
    }

    [Fact]
    public void ToJson_字符串透传_对象序列化_缩进可选()
    {
        Assert.Equal("abc", "abc".ToJson());
        Assert.Equal("{\"A\":1}", new { A = 1 }.ToJson());
        Assert.Contains("\n", new { A = 1 }.ToJson(format: true));
    }

    [Fact]
    public void AsObject_反序列化()
    {
        var obj = "{\"A\":1}".AsObject<Dictionary<string, int>>();
        Assert.Equal(1, obj["A"]);
    }

    [Fact]
    public void ValueOrDefault_命中取值_未命中返回默认()
    {
        var dic = new Dictionary<string, int> { ["a"] = 1 };
        Assert.Equal(1, dic.ValueOrDefault("a"));
        Assert.Equal(0, dic.ValueOrDefault("missing"));
    }
}
