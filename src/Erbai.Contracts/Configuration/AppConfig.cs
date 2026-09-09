namespace Erbai.Contracts.Configuration;

/// <summary>
/// 应用配置树（强类型；已删：queue.duplicate_scope（无消费方的 quirk）、api.admin_token（决策 #6）。
/// 校验规则见 docs/03 §4 与 Erbai.Core.Configuration.ConfigValidator。
/// </summary>
public sealed record AppConfig
{
    public int ConfigVersion { get; init; } = 1;

    public string Platform { get; init; } = "douyin";

    public string RoomId { get; init; } = "";

    public string DouyinWsUrl { get; init; } = "ws://127.0.0.1:8888";

    public IReadOnlyList<string> DouyinRoomIds { get; init; } = [];

    public int DouyinReconnectDelay { get; init; } = 5;

    public QueueConfig Queue { get; init; } = new();

    public PlayerConfig Player { get; init; } = new();

    public ProvidersConfig Providers { get; init; } = new();

    public SearchConfig Search { get; init; } = new();

    public RuntimeConfig Runtime { get; init; } = new();

    public PermissionsConfig Permissions { get; init; } = new();

    public StorageConfig Storage { get; init; } = new();

    public UiConfig Ui { get; init; } = new();

    public OverlayConfig Overlay { get; init; } = new();

    /// <summary>独立悬浮窗治理（决策 #17）：每类一个透明悬浮窗，各自开关/置顶/穿透。</summary>
    public OverlayWindowsConfig OverlayWindows { get; init; } = new();

    public IdlePlaylistConfig IdlePlaylist { get; init; } = new();

    public ApiConfig Api { get; init; } = new();

    public DouyinGrabberConfig DouyinGrabber { get; init; } = new();

    /// <summary>排队队列模块（阶段 5：骨架 + 礼物插队规则引擎）。</summary>
    public QueueUpConfig QueueUp { get; init; } = new();

    /// <summary>礼物特效模块（阶段 5：框架通道 → overlay 渲染层）。</summary>
    public GiftFxConfig GiftFx { get; init; } = new();

    public static AppConfig CreateDefault() => new();
}

public sealed record QueueConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>队列容量（校验 1-999）。</summary>
    public int MaxSize { get; init; } = 20;

    /// <summary>展示条数（校验 1-100）。</summary>
    public int DisplayLimit { get; init; } = 5;

    public bool UserLimitEnabled { get; init; } = true;

    /// <summary>单用户上限（校验 1-100）。</summary>
    public int MaxPerUser { get; init; } = 1;

    public bool DedupeEnabled { get; init; } = true;

    public bool PlaybackFailSkip { get; init; } = true;

    /// <summary>展示顺序 asc/desc（派发固定 FIFO）。</summary>
    public string DisplayOrder { get; init; } = "asc";

    /// <summary>起播超时（秒）；状态机运行中不热改。</summary>
    public int PlaybackStartTimeout { get; init; } = 15;

    /// <summary>播放总预算（秒）；状态机运行中不热改。</summary>
    public int PlaybackTimeout { get; init; } = 60;
}

public sealed record PlayerConfig
{
    /// <summary>lxmusic | netease | kugou | qqmusic | folia。</summary>
    public string Key { get; init; } = "lxmusic";

    /// <summary>连接器目录（空 = 默认解析顺序，见 docs/04 §1.4）。</summary>
    public string ConnectorDir { get; init; } = "";

    public bool AutoStart { get; init; } = true;

    public LxMusicConfig Lxmusic { get; init; } = new();

    public string FoliaToken { get; init; } = "";
}

public sealed record LxMusicConfig
{
    public string Path { get; init; } = "";

    public string HttpUrl { get; init; } = "http://127.0.0.1:23330";

    public bool HttpEnabled { get; init; } = true;

    public bool SseEnabled { get; init; } = true;

    public bool UseHttpControl { get; init; } = true;
}

public sealed record ProvidersConfig
{
    /// <summary>启用搜索源（kugou/netease/qqmusic），非空、去重。</summary>
    public IReadOnlyList<string> Enabled { get; init; } = ["kugou", "netease", "qqmusic"];
}

public sealed record SearchConfig
{
    public bool PreferHot { get; init; } = true;
}

public sealed record RuntimeConfig
{
    public bool AutoStartPlatforms { get; init; } = false;

    /// <summary>直播事件落盘（logs/erbai-live-YYYYMMDD.log，按天滚动；日志页回看依据）。
    /// 默认开：直播事件只走内存广播，页面没开就永远错过——落盘后事后打开日志页也能看到。</summary>
    public bool LiveEventLog { get; init; } = true;

    /// <summary>关闭窗口时最小化到系统托盘后台运行（默认开；关 = 点 X 直接退出）。
    /// 托盘常驻（2026-09）：主播挂机监听弹幕场景，关窗不退出；托盘菜单「退出」走完整清理链。</summary>
    public bool MinimizeToTrayOnClose { get; init; } = true;
}

public sealed record PermissionsConfig
{
    /// <summary>抖音粉丝团等级门槛；≤0 即关闭。</summary>
    public int DouyinMinFanLevel { get; init; } = 0;

    /// <summary>B站勋章等级门槛；≤0 即关闭。</summary>
    public int BilibiliMinMedalLevel { get; init; } = 0;

    /// <summary>等级未知/非数字时的策略：deny | allow（默认 deny）。</summary>
    public string LevelUnknownPolicy { get; init; } = "deny";

    /// <summary>admin/anchor 绕过一切点歌限制。</summary>
    public bool AdminBypass { get; init; } = true;
}

public sealed record StorageConfig
{
    /// <summary>数据库路径（相对路径由宿主解析到数据目录）。</summary>
    public string DatabasePath { get; init; } = "song_request.db";
}

public sealed record UiConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>只允许回环地址，归一化为 127.0.0.1（决策 #6：本地 HTTP 只服务 Overlay）。</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>端口（校验 1-65535；被占逐级上探 20 个）。</summary>
    public int Port { get; init; } = 19830;

    public bool OpenBrowserOnStart { get; init; } = true;

    public bool OverlayEnabled { get; init; } = true;
}

public sealed record OverlayConfig
{
    /// <summary>clash | light。</summary>
    public string Theme { get; init; } = "clash";

    public string CustomCss { get; init; } = "";

    public bool ShowCurrent { get; init; } = true;

    public bool ShowQueue { get; init; } = true;

    public bool ShowLyric { get; init; } = true;

    public bool Transparent { get; init; } = false;
}

/// <summary>悬浮窗治理配置（决策 #17）：三类悬浮窗各自独立开关/置顶/穿透。
/// 点歌/排队默认宽 267（用户实测 400 太宽，2026-09 调为 2/3）；弹幕保持 400
/// （bililive_dm 侧边栏风格，宽度语义不同）。</summary>
public sealed record OverlayWindowsConfig
{
    /// <summary>点歌悬浮窗（正在播放 + 点歌队列，页面 /overlay/queue）。</summary>
    public OverlayWindowItemConfig SongQueue { get; init; } = new() { Width = 267 };

    /// <summary>排队看板悬浮窗（页面 /overlay/queueup）。</summary>
    public OverlayWindowItemConfig QueueUp { get; init; } = new() { Width = 267 };

    /// <summary>弹幕悬浮窗（页面 /overlay/danmaku）。</summary>
    public OverlayWindowItemConfig Danmaku { get; init; } = new();
}

/// <summary>单个悬浮窗的可配置项。默认全部关闭（开启走设置页/配置）。</summary>
public sealed record OverlayWindowItemConfig
{
    /// <summary>独立开关：false 时对应悬浮窗不创建（销毁已存在实例）。</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>最前显示（置顶）。</summary>
    public bool Topmost { get; init; } = true;

    /// <summary>点击穿透（鼠标事件透传下层窗口；设置页可随时切换）。</summary>
    public bool ClickThrough { get; init; } = true;

    /// <summary>
    /// 悬浮窗宽度（逻辑像素）。<b>2026-09 起已弃用</b>：点歌/排队宽高全自适应（随内容实测），
    /// 弹幕窗用内置默认宽 + 高度随活跃行数伸缩，均不再读本字段。
    /// 保留仅为兼容既有 config.json 反序列化（旧配置里的 width/height 不报错、被忽略）。
    /// </summary>
    public int Width { get; init; } = 400;

    /// <summary>悬浮窗高度（逻辑像素）。<b>2026-09 起已弃用</b>，理由同 <see cref="Width"/>。</summary>
    public int Height { get; init; } = 260;

    /// <summary>
    /// 高度自适应开关。<b>2026-09 起已弃用</b>：宽高自适应已成为唯一行为（无开关），
    /// 设置页也不再提供该开关。保留仅为兼容既有 config.json。
    /// </summary>
    public bool AutoHeight { get; init; } = true;

    /// <summary>
    /// 悬浮窗<b>右下角</b> X（屏幕坐标）；null = 未设置过 → 按类型默认摆放（屏幕右下错开）。
    /// 关闭程序时采集当前右下角、下次启动复位（2026-09 用户需求「记住各自位置」）。
    /// 存右下角而非左上角：宽高自适应时窗口向左上生长，右下角锚定才不会随内容增减而漂移。
    /// </summary>
    public int? AnchorRight { get; init; }

    /// <summary>悬浮窗<b>右下角</b> Y（屏幕坐标）；语义同 <see cref="AnchorRight"/>。</summary>
    public int? AnchorBottom { get; init; }

    /// <summary>悬浮窗样式参数（预案 B 样式参数化，2026-08-29；参考 AwooMusicBot/bilipdj/blivechat
    /// 的 style.json 模式——强调色/字号/描边/发光/不透明度均可配置，设置页实时生效。
    /// 点歌/排队走 GDI+ 自绘渲染器，弹幕走 WPF（Erbai.OverlayWpf）——映射见 docs/02 #17 修订）。</summary>
    public OverlayWindowStyleConfig Style { get; init; } = new();
}

/// <summary>悬浮窗样式参数（每类一套；渲染器/窗口 ApplyStyle 实时生效，无需重建窗口）。</summary>
public sealed record OverlayWindowStyleConfig
{
    /// <summary>强调色（#RRGGBB）；空 = 按悬浮窗类型默认（点歌/排队橙 #FF7B54，弹幕青 #7FD0FF）。</summary>
    public string AccentColor { get; init; } = "";

    /// <summary>正文文字颜色（#RRGGBB）；空 = 默认白色。与强调色分离：
    /// 强调色只作用于色条/徽章/光晕等点缀，正文颜色独立可调（2026-09 用户需求）。</summary>
    public string TextColor { get; init; } = "";

    /// <summary>字体缩放（0.6–1.6；默认 1.0）。</summary>
    public double FontScale { get; init; } = 1.0;

    /// <summary>文字描边强度（0=关 … 1=强；黑色描边，透明底可读性增强）。</summary>
    public double TextOutline { get; init; } = 0.0;

    /// <summary>文字发光强度（0=关 … 1=强；强调色光晕，bilipdj 霓虹风）。</summary>
    public double TextGlow { get; init; } = 0.0;

    /// <summary>内容不透明度（0.5–1.0；默认 1.0）。</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>背景面板不透明度（0=无底全透明 … 0.95；默认 0.75 深蓝黑圆角面板，
    /// 参考 bilipdj moren.css / AwooMusicBot panelOpacity 的玻璃面板；0 回到纯文字悬浮。
    /// 弹幕 = 弹幕行底条 alpha）。</summary>
    public double BackgroundOpacity { get; init; } = 0.75;

    // ---- 弹幕专用（bililive_dm MainOverlayEffect1-4 / MainOverlayFontsize / 行数上限；点歌/排队忽略）----

    /// <summary>弹幕行高度拉伸时长（秒；默认 0.8 = 弹幕姬 Store.MainOverlayEffect1）。</summary>
    public double EffectExpand { get; init; } = 0.8;

    /// <summary>弹幕行文字淡入时长（秒；默认 0.6 = 弹幕姬 Store.MainOverlayEffect2）。</summary>
    public double EffectTextIn { get; init; } = 0.6;

    /// <summary>弹幕行停留时长（秒；默认 4.6 = 弹幕姬 Store.MainOverlayEffect3）。</summary>
    public double EffectHold { get; init; } = 4.6;

    /// <summary>弹幕行淡出时长（秒；默认 1.0 = 弹幕姬 Store.MainOverlayEffect4）。</summary>
    public double EffectFade { get; init; } = 1.0;

    /// <summary>弹幕行数上限（超限移除最旧行；默认 30）。</summary>
    public int MaxLines { get; init; } = 30;

    /// <summary>弹幕字体（WPF 系统字体名；空 = 微软雅黑）。</summary>
    public string FontFamily { get; init; } = "";
}

public sealed record IdlePlaylistConfig
{
    public bool Enabled { get; init; } = false;

    /// <summary>空闲播放间隔（秒，>0）。</summary>
    public int PlayInterval { get; init; } = 30;
}

public sealed record ApiConfig
{
    // 2026-09 修订（决策 #6 再修订）：移除 overlay token——服务器回环绑定 127.0.0.1 已隔离
    // 外部网络；OBS 浏览器源为同机应用，URL 复制不带 token 导致页面 401 空白（用户实测）；
    // 业界（bililive_dm 等弹幕姬/点歌机）本地 OBS 页面均无鉴权。诊断页随之移除 token 检查项。
}

public sealed record DouyinGrabberConfig
{
    /// <summary>bundled | external | disabled。</summary>
    public string Mode { get; init; } = "bundled";

    public bool AutoStart { get; init; } = true;

    public bool AutoStop { get; init; } = false;

    public bool AdoptExisting { get; init; } = true;

    public bool StartOnLaunch { get; init; } = false;

    public string ExecutablePath { get; init; } = "";

    public string ConfigPath { get; init; } = "";

    /// <summary>抓包器 appSettings 白名单（19 键，docs/03 §4）；未知键报错。</summary>
    public IReadOnlyDictionary<string, object> AppSettings { get; init; } = GrabberAppSettings.CreateDefault();
}

/// <summary>Grabber appSettings 白名单默认值。</summary>
public static class GrabberAppSettings
{
    public const string PushFilter = "1,4,5,7"; // 强制保留 Type=7 粉丝团消息（点歌核心）

    public static IReadOnlyDictionary<string, object> CreateDefault() =>
        new Dictionary<string, object>
        {
            ["hideConsole"] = true,
            ["printBarrage"] = false,
            ["printFilter"] = "",
            ["sysProxy"] = true,
            ["proxyPort"] = 8827,
            ["upstreamProxy"] = "",
            ["forcePolling"] = false,
            ["pollingInterval"] = 3000,
            ["autoPause"] = true,
            ["filterHostName"] = true,
            ["processFilter"] = "直播伴侣,douyin,chrome,msedge,QQBrowser,360se,firefox,2345explorer,iexplore",
            ["wsListenPort"] = 8888,
            ["listenAny"] = false,
            ["webRoomIds"] = "",
            ["hostNameFilter"] = "",
            ["disableLivePageScriptCache"] = false,
            ["barrageFileLog"] = false,
            ["showWindow"] = false,
            ["pushFilter"] = PushFilter,
        };

    public static readonly IReadOnlySet<string> BoolKeys = new HashSet<string>
    {
        "hideConsole", "printBarrage", "sysProxy", "forcePolling", "autoPause",
        "filterHostName", "listenAny", "disableLivePageScriptCache",
        "barrageFileLog", "showWindow",
    };

    public static readonly IReadOnlySet<string> IntKeys = new HashSet<string>
    {
        "proxyPort", "pollingInterval", "wsListenPort",
    };

    public static readonly IReadOnlySet<string> PortKeys = new HashSet<string>
    {
        "proxyPort", "wsListenPort",
    };

    /// <summary>全部白名单键（bool + int + 字符串键）。</summary>
    public static readonly IReadOnlySet<string> AllKeys =
        new HashSet<string>(
            BoolKeys.Concat(IntKeys).Concat(new[]
            {
                "printFilter", "upstreamProxy", "processFilter",
                "webRoomIds", "hostNameFilter", "pushFilter",
            }),
            StringComparer.Ordinal);
}
