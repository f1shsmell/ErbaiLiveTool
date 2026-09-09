using System.Threading.Channels;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;

namespace Erbai.Contracts.Plugins;

/// <summary>直播平台弹幕插件（bilibili / douyin）。</summary>
public interface ILivePlatformPlugin : IAsyncDisposable
{
    /// <summary>平台键："bilibili" | "douyin"。</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>能力协商（加性：宿主忽略未知位）。</summary>
    LiveCapabilities Capabilities { get; }

    /// <summary>
    /// 启动监听；配置面由平台实现自行从 <paramref name="config"/> 读取
    /// （如 douyin_ws_url / roomid 等顶层键）。
    /// </summary>
    Task StartAsync(AppConfig config, CancellationToken ct);

    /// <summary>停止监听（幂等）。</summary>
    Task StopAsync();

    /// <summary>规范化事件流（平台无关）。</summary>
    ChannelReader<LiveEvent> Events { get; }
}
