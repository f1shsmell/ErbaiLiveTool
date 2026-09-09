using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Erbai.Core.Hosting;

namespace Erbai.Modules.SongRequest.Services;

/// <summary>
/// 规范化直播事件 → 点歌命令桥接（阶段 3）：EventBus 上的 Danmaku 事件
/// 转 <see cref="DanmakuContext"/> 喂给 <see cref="SongRequestService.HandleMessageAsync"/>。
/// 平台无关（B站/抖音共用）；非弹幕事件（礼物/进入/关注…）被忽略。
/// </summary>
public static class LiveEventBridge
{
    /// <summary>LiveEvent(Danmaku) → DanmakuContext 字段映射。</summary>
    public static DanmakuContext ToDanmakuContext(LiveEvent evt) => new()
    {
        Text = evt.Text ?? "",
        Nickname = evt.Nickname,
        Platform = evt.Platform,
        RoomId = evt.RoomId,
        UserId = evt.UserId.ToString(),
        IsAdmin = evt.IsAdmin ?? false,
        IsAnchor = evt.IsAnchor ?? false,
        FanLevel = evt.FanLevel,
        MedalLevel = evt.MedalLevel,
    };

    /// <summary>
    /// 启动桥接循环（组合根调用；ct 取消即停）。handler 异常由 ModuleLoops 隔离记日志，循环不中断。
    /// </summary>
    public static Task RunAsync(IEventBus bus, SongRequestService commands, ILogBus logs, CancellationToken ct) =>
        ModuleLoops.RunConsumerLoopAsync(
            bus.Subscribe<LiveEvent>(capacity: 256),
            (evt, loopCt) =>
            {
                if (evt.Kind != LiveEventKind.Danmaku)
                {
                    return Task.CompletedTask;
                }

                return commands.HandleMessageAsync(ToDanmakuContext(evt), loopCt);
            },
            logs,
            "live.danmaku",
            ct);
}
