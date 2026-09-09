namespace Erbai.Contracts.Live;

/// <summary>直播平台插件能力（加性能力协商：宿主可忽略不认识的位）。</summary>
[Flags]
public enum LiveCapabilities
{
    None = 0,

    /// <summary>弹幕。</summary>
    Danmaku = 1 << 0,

    /// <summary>礼物。</summary>
    Gift = 1 << 1,

    /// <summary>上舰。</summary>
    GuardBuy = 1 << 2,

    /// <summary>进入直播间。</summary>
    Enter = 1 << 3,

    /// <summary>关注。</summary>
    Follow = 1 << 4,

    /// <summary>订阅。</summary>
    Subscribe = 1 << 5,

    /// <summary>点赞。</summary>
    Like = 1 << 6,

    /// <summary>分享。</summary>
    Share = 1 << 7,

    /// <summary>开播/下播状态。</summary>
    LiveState = 1 << 8,
}
