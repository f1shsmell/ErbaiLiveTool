using Erbai.Contracts.Plugins;

namespace Erbai.Modules.SongRequest;

/// <summary>
/// 点歌模块（阶段 2）：提交流水线 / 权限 / 弹幕命令 / worker 派发 /
/// 播放状态机 / 搜索三源。组合根装配依赖后 StartAsync。
/// </summary>
public sealed class SongRequestModule : IFeatureModule
{
    private readonly Services.SongQueueService _queue;
    private readonly Services.SongRequestService _commandService;
    private readonly Services.SongBlacklist _blacklist;

    public SongRequestModule(
        Services.SongQueueService queue,
        Services.SongRequestService commandService,
        Services.SongBlacklist blacklist)
    {
        _queue = queue;
        _commandService = commandService;
        _blacklist = blacklist;
    }

    public string Key => "songrequest";

    public string DisplayName => "点歌";

    public Services.SongQueueService QueueService => _queue;

    public Services.SongRequestService CommandService => _commandService;

    public Services.SongBlacklist Blacklist => _blacklist;

    public async Task StartAsync(ModuleContext context, CancellationToken ct)
    {
        await _blacklist.LoadAsync(ct);
        await _queue.StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.StopAsync();
    }
}
