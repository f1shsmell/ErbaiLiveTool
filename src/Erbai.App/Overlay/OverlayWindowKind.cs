namespace Erbai.App.Overlay;

/// <summary>
/// 悬浮窗类型（决策 #17）：每类一个独立透明悬浮窗，各自开关/置顶/穿透。
/// 与 <see cref="Erbai.Contracts.Configuration.OverlayWindowsConfig"/> 三个条目一一对应。
/// </summary>
public enum OverlayWindowKind
{
    /// <summary>点歌悬浮窗：正在播放 + 点歌队列（/overlay/queue）。</summary>
    SongQueue,

    /// <summary>排队看板悬浮窗（/overlay/queueup）。</summary>
    QueueUp,

    /// <summary>弹幕悬浮窗（/overlay/danmaku；bililive_dm 同款 WPF 方案，2026-09）。</summary>
    Danmaku,
}