using BarrageGrab;
using Newtonsoft.Json.Linq;

namespace Erbai.Live.Douyin.Tests;

/// <summary>Grabber 宿主下发配置解析（HostConfig，docs/04 §3.1 + §3.2）：白名单 19 键、未知键报错、
/// 类型校验、端口/间隔范围、pushFilter 强制保留 Type=7。</summary>
public class HostConfigTests
{
    private static AppSetting NewSetting() => new();

    private static void Apply(AppSetting setting, string json) =>
        HostConfig.Apply(setting, JObject.Parse(json));

    [Fact]
    public void 全量默认值可应用且字段映射正确()
    {
        var setting = NewSetting();
        Apply(setting, """
            {
              "hideConsole": true, "printBarrage": true, "sysProxy": false,
              "forcePolling": true, "autoPause": false, "filterHostName": false,
              "listenAny": true, "disableLivePageScriptCache": true,
              "barrageFileLog": true, "showWindow": true,
              "proxyPort": 18827, "pollingInterval": 4000, "wsListenPort": 18888,
              "printFilter": "1,2", "upstreamProxy": "127.0.0.1:7890",
              "processFilter": "douyin,chrome", "webRoomIds": "123,456",
              "hostNameFilter": "webcast.amemv.com", "pushFilter": "1,4,5,7"
            }
            """);

        Assert.True(setting.HideConsole);
        Assert.True(setting.PrintBarrage);
        Assert.False(setting.UsedProxy);
        Assert.True(setting.ForcePolling);
        Assert.False(setting.AutoPause);
        Assert.False(setting.FilterHostName);
        Assert.True(setting.ListenAny);
        Assert.True(setting.BarrageLog);
        Assert.True(setting.ShowWindow);
        Assert.Equal(18827, setting.ProxyPort);
        Assert.Equal(4000, setting.PollingInterval);
        Assert.Equal(18888, setting.WsProt);
        Assert.Equal(new[] { 1, 2 }, setting.PrintFilter);
        Assert.Equal("127.0.0.1:7890", setting.UpstreamProxy);
        Assert.Equal(new[] { "douyin", "chrome" }, setting.ProcessFilter);
        Assert.Equal(new[] { "123", "456" }, setting.WebRoomIds);
        Assert.Equal(new[] { "webcast.amemv.com" }, setting.HostNameFilter);
        Assert.Equal(new[] { 1, 4, 5, 7 }, setting.PushFilter);
    }

    [Fact]
    public void 未知键报错()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Apply(NewSetting(), """{"bogusKey": true}"""));
        Assert.Contains("bogusKey", ex.Message);
    }

    [Fact]
    public void 布尔键给整数报错()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Apply(NewSetting(), """{"sysProxy": 1}"""));
        Assert.Contains("sysProxy", ex.Message);
    }

    [Fact]
    public void 整数键给布尔报错()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Apply(NewSetting(), """{"wsListenPort": true}"""));
        Assert.Contains("wsListenPort", ex.Message);
    }

    [Theory]
    [InlineData("proxyPort", 0)]
    [InlineData("proxyPort", 65536)]
    [InlineData("wsListenPort", -1)]
    [InlineData("pollingInterval", 999)]
    [InlineData("pollingInterval", 60001)]
    public void 端口与间隔范围校验(string key, int value)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Apply(NewSetting(), "{\"" + key + "\": " + value + "}"));
        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void pushFilter缺7时强制追加()
    {
        var setting = NewSetting();
        Apply(setting, """{"pushFilter": "1,4,5"}""");
        Assert.Equal(new[] { 1, 4, 5, 7 }, setting.PushFilter);
    }

    [Fact]
    public void pushFilter含7时保持不变()
    {
        var setting = NewSetting();
        Apply(setting, """{"pushFilter": "7,1"}""");
        Assert.Equal(new[] { 7, 1 }, setting.PushFilter);
    }

    [Fact]
    public void 逗号分隔列表忽略空白项()
    {
        var setting = NewSetting();
        Apply(setting, """{"webRoomIds": " 123 ,  ,456 "}""");
        Assert.Equal(new[] { "123", "456" }, setting.WebRoomIds);
    }

    [Fact]
    public void 非法整数列表报错()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Apply(NewSetting(), """{"pushFilter": "1,abc"}"""));
        Assert.Contains("pushFilter", ex.Message);
    }

    [Fact]
    public void 缺省键保持出厂默认()
    {
        var setting = NewSetting();
        Apply(setting, """{"wsListenPort": 18888}""");
        Assert.Equal(18888, setting.WsProt);
        Assert.False(setting.UsedProxy); // 出厂默认 false（无宿主配置时不误动系统代理；宿主总下发全量键覆盖）
        Assert.Equal(8827, setting.ProxyPort);
    }
}
