using System.Threading.Channels;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Live;
using Erbai.Contracts.Plugins;

namespace Erbai.TestPlugins.Live;

/// <summary>直播平台类假插件（PluginLoader 测试用；StartAsync 后向事件通道写入一条弹幕）。</summary>
public sealed class FakeLivePlatform : ILivePlatformPlugin
{
    private readonly Channel<LiveEvent> _events = Channel.CreateUnbounded<LiveEvent>();

    public string Key => "fake-live";

    public string DisplayName => "Fake 直播";

    public LiveCapabilities Capabilities => LiveCapabilities.Danmaku;

    public ChannelReader<LiveEvent> Events => _events.Reader;

    public int StartCount { get; private set; }

    public Task StartAsync(AppConfig config, CancellationToken ct)
    {
        StartCount++;
        _events.Writer.TryWrite(new LiveEvent
        {
            Platform = Key,
            RoomId = "1",
            Kind = LiveEventKind.Danmaku,
            UserId = 1,
            Nickname = "fake-tester",
            Text = "点歌 测试",
            Timestamp = DateTimeOffset.UtcNow,
        });
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}