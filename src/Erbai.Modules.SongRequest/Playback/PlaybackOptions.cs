using Erbai.Contracts.Configuration;

namespace Erbai.Modules.SongRequest.Playback;

/// <summary>
/// 播放状态机全部时间参数（可注入 options record——旧测试靠 patch 常量到
/// 0.05–0.1s 跑完 996 行判定矩阵，C# 不留此缝移植不了，见 docs/00 阶段 2
/// 开工简报「两个可测性缝」）。
/// </summary>
public sealed record PlaybackOptions
{
    /// <summary>起播超时（默认 15s）。</summary>
    public TimeSpan PlaybackStartTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>播放总预算的缓冲部分；null = 用 DEFAULT_PLAYBACK_TIMEOUT_SECONDS(300s)。</summary>
    public TimeSpan? PlaybackTimeout { get; init; }

    /// <summary>无候选可换且未开播时是否自动失败（false = 等手动处理，仍受总预算）。</summary>
    public bool PlaybackFailSkip { get; init; } = true;

    /// <summary>轮询节奏（SSE 断线/无事件时的兜底，1.0s）。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>刚播即停双条件判定阈值（进度与墙钟都 &lt; 5s）。</summary>
    public TimeSpan ShortPlayFail { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>开播后 paused/waiting 且进度始终 &lt;5s 的观察窗（10s）。</summary>
    public TimeSpan PausedStallGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>换源冷却（1s，防残留 error 触发连续跳源）。</summary>
    public TimeSpan SwitchSourceCooldown { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 派发/换源后的起播瞬态宽限窗（默认 5s）：窗内播放器报的 error 不可信，
    /// 不作为换源/失败依据（交给起播超时统一裁决）。lxmusic 的 music/searchPlay
    /// 是"自搜自播"，从 scheme 下发到真正起播之间要搜索+解析音源，期间 /status
    /// 会残留上一个源的 error；对齐空闲歌路径 Worker.IdleTransientGraceSeconds
    /// 的既有防护（用户实测：派发 2.3s 内连续两次 error → 请求被秒杀成
    /// playback_failed，而歌几分钟后正常播起来了）。
    /// </summary>
    public TimeSpan PlaybackStartGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 未开播期 error 的连续确认次数（默认 2）：宽限窗过后仍需连续多帧 error
    /// 才换源，单次抖动/残留不换源。
    /// </summary>
    public int ErrorConfirmThreshold { get; init; } = 2;

    /// <summary>next 对账守卫限频（3s）。</summary>
    public TimeSpan GuardResyncInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>连续对账失败升级 ArmNextGuard 的阈值（2 次）。</summary>
    public int GuardArmThreshold { get; init; } = 2;

    /// <summary>歌名相似度下限（SequenceMatcher 等价物，0.62）。</summary>
    public double SongMatchRatioThreshold { get; init; } = 0.62;

    public static PlaybackOptions FromConfig(AppConfig config) => new()
    {
        PlaybackStartTimeout = TimeSpan.FromSeconds(config.Queue.PlaybackStartTimeout),
        PlaybackTimeout = TimeSpan.FromSeconds(config.Queue.PlaybackTimeout),
        PlaybackFailSkip = config.Queue.PlaybackFailSkip,
    };
}
