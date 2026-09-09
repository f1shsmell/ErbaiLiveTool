using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Tests;

public class WbiSignerTests
{
    // 官方示例向量（bilibili-API-collect）：已知 img/sub key → mixin key
    private const string ImgKey = "7cd084941338484aae1ad9425b84077c";
    private const string SubKey = "4932caff0ff746eab6f01bf08b70ac45";
    private const string ExpectedMixinKey = "ea1db124af3c7062474693fa704f4ff8";

    [Fact]
    public void ImgKey_ExtractsFileNamePart()
    {
        Assert.Equal(
            "7cd084941338484aae1ad9425b84077c",
            WbiSigner.ImgKey("https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png"));

        // 文件名含第二个点：取第一部分
        Assert.Equal("abc", WbiSigner.ImgKey("https://example.com/abc.def.png"));
    }

    [Fact]
    public void GetMixinKey_KnownVector()
    {
        var raw = WbiSigner.ExtractRawKey(
            $"https://i0.hdslb.com/bfs/wbi/{ImgKey}.png",
            $"https://i0.hdslb.com/bfs/wbi/{SubKey}.png");

        Assert.Equal(64, raw.Length);
        Assert.Equal(ExpectedMixinKey, WbiSigner.GetMixinKey(raw));
    }

    [Fact]
    public void SignQuery_KnownVector_ProducesExpectedWRid()
    {
        // 固定 wts=1700000000：query "id=5050&wts=1700000000" + mixin → MD5（预先手工计算）
        var query = WbiSigner.SignQuery(
            new Dictionary<string, string> { ["id"] = "5050", ["wts"] = "1700000000" },
            ExpectedMixinKey);

        Assert.Equal("id=5050&wts=1700000000&w_rid=99da1a2014991bb2d33fea61ba4b6938", query);
    }

    [Fact]
    public void SignQuery_SortsKeysAndFiltersForbiddenChars()
    {
        var query = WbiSigner.SignQuery(
            new Dictionary<string, string>
            {
                ["z"] = "last",
                ["a"] = "a!b'c(d)e*f", // 剔除 ! ' ( ) *
                ["wts"] = "100",
            },
            "01234567890123456789012345678901");

        Assert.StartsWith("a=abcdef&wts=100&z=last&w_rid=", query, StringComparison.Ordinal);
        Assert.Equal(32, query.Split("w_rid=")[^1].Length);
    }

    [Fact]
    public void SignQuery_AddsWtsWhenMissing()
    {
        var query = WbiSigner.SignQuery(
            new Dictionary<string, string> { ["id"] = "5050" },
            ExpectedMixinKey);

        Assert.Contains("&wts=", query, StringComparison.Ordinal);
        Assert.Contains("&w_rid=", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_RemovesOnlyForbiddenChars()
    {
        Assert.Equal("abcdef", WbiSigner.Filter("a!b'c(d)e*f"));
        Assert.Equal("", WbiSigner.Filter("!'()*"));
        Assert.Equal("中文 正常", WbiSigner.Filter("中文 正常"));
    }
}
