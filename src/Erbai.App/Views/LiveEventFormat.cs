using Erbai.Contracts.Live;
using Erbai.Contracts.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Erbai.App.Views;

/// <summary>
/// LiveEvent 展示格式化（日志页与概览主控台「实时事件」共用；原 DanmakuLogPage 私有实现上移）。
/// Format 输出完整带前缀文本（概览预览用）；Body/PlatformText 供日志页结构化列分解。
/// </summary>
internal static class LiveEventFormat
{
    /// <summary>平台中文名（bilibili → B站，douyin → 抖音，其余原样）。</summary>
    public static string PlatformText(string platform) => platform switch
    {
        "bilibili" => "B站",
        "douyin" => "抖音",
        _ => platform,
    };

    /// <summary>事件正文（无时间/平台/类型前缀；结构化日志内容列使用）。</summary>
    public static string Body(LiveEvent evt) => evt.Kind switch
    {
        LiveEventKind.Danmaku => $"{evt.Nickname}: {evt.Text}",
        LiveEventKind.Gift => $"{evt.Nickname} 送出 {evt.GiftName}×{evt.GiftCount}{FormatCoin(evt)}",
        LiveEventKind.GuardBuy => $"{evt.Nickname} 开通 {evt.GiftName}",
        LiveEventKind.Enter => evt.Nickname,
        LiveEventKind.Follow => evt.Nickname,
        // 抖音粉丝团（Type=7）与 B站关注语义不同：日志页同样区分
        LiveEventKind.Subscribe => evt.Nickname,
        LiveEventKind.Share => evt.Nickname,
        LiveEventKind.Like => evt.Nickname,
        LiveEventKind.LiveState => evt.Text ?? "",
        _ => evt.Nickname,
    };

    public static string Format(LiveEvent evt)
    {
        var ts = evt.Timestamp.ToLocalTime(); // 页面统一本地时间（文件日志/历史为 +08:00）
        var typeSuffix = evt.Kind switch
        {
            LiveEventKind.Danmaku => "弹幕",
            LiveEventKind.Gift => "礼物",
            LiveEventKind.GuardBuy => "上舰",
            LiveEventKind.Enter => "进入",
            LiveEventKind.Follow => "关注",
            LiveEventKind.Subscribe => evt.Platform == "douyin" ? "粉丝团" : "关注",
            LiveEventKind.Share => "分享",
            LiveEventKind.Like => "点赞",
            _ => "",
        };
        var middle = typeSuffix.Length > 0 ? $"{PlatformText(evt.Platform)}{typeSuffix}" : PlatformText(evt.Platform);
        return $"[{ts:HH:mm:ss}] [{middle}] {Body(evt)}";
    }

    /// <summary>礼物价值后缀：抖音电池 / B站金瓜子（gold）/ 银瓜子（silver）；无价值不显示。</summary>
    private static string FormatCoin(LiveEvent evt)
    {
        if (evt.TotalCoin <= 0)
        {
            return "";
        }

        var unit = evt.Platform == "douyin" ? "电池" : evt.CoinType == "silver" ? "银瓜子" : "金瓜子";
        return $"（{evt.TotalCoin} {unit}）";
    }

    public static DanmakuLogCategory CategoryFor(LiveEventKind kind) => kind switch
    {
        LiveEventKind.Danmaku => DanmakuLogCategory.Danmaku,
        LiveEventKind.Gift => DanmakuLogCategory.Gift,
        LiveEventKind.Follow => DanmakuLogCategory.Follow,
        LiveEventKind.Like => DanmakuLogCategory.Like,
        _ => DanmakuLogCategory.Other,
    };

    /// <summary>
    /// 主题感知事件色板（2026-10 UI 轮）：按页面当前主题取色——旧 <c>LightColorFor</c>
    /// 固定浅色系深色文字，系统深色主题下深灰/深藏青文字在深底上几乎不可见
    /// （弹幕=#37474F 是重灾区）。深色系用 Material 200/300 亮色调（深底可读），
    /// 浅色系保持原 800/900 深色调（白底可读）。行对象随重建刷新，切主题
    /// （跟随系统重启应用）后下一次渲染即取到新色。
    /// </summary>
    public static Brush EventColorFor(LiveEvent evt, ElementTheme theme) => theme == ElementTheme.Dark
        ? evt.Kind switch
        {
            LiveEventKind.Danmaku => Solid("#C3CFD9"),
            LiveEventKind.Gift => Solid("#FF8A80"),
            LiveEventKind.GuardBuy => Solid("#FFB74D"),
            LiveEventKind.Enter => Solid("#A5D6A7"),
            LiveEventKind.Follow => Solid("#90CAF9"),
            LiveEventKind.Subscribe => evt.Platform == "douyin" ? Solid("#CE93D8") : Solid("#90CAF9"),
            LiveEventKind.Share => Solid("#B2DFDB"),
            LiveEventKind.Like => Solid("#F48FB1"),
            LiveEventKind.LiveState => Solid("#B39DDB"),
            _ => Solid("#C3CFD9"),
        }
        : evt.Kind switch
        {
            LiveEventKind.Danmaku => Solid("#37474F"),
            LiveEventKind.Gift => Solid("#B71C1C"),
            LiveEventKind.GuardBuy => Solid("#B4530A"),
            LiveEventKind.Enter => Solid("#1B5E20"),
            LiveEventKind.Follow => Solid("#0D47A1"),
            LiveEventKind.Subscribe => evt.Platform == "douyin" ? Solid("#4A148C") : Solid("#0D47A1"),
            LiveEventKind.Share => Solid("#2E7D32"),
            LiveEventKind.Like => Solid("#AD1457"),
            LiveEventKind.LiveState => Solid("#6A1B9A"),
            _ => Solid("#37474F"),
        };

    /// <summary>系统/工具行文字色（主题感知：错误红、警告琥珀、普通次级灰）。</summary>
    public static Brush LogLevelColorFor(LogLevel level, ElementTheme theme) => theme == ElementTheme.Dark
        ? level switch
        {
            LogLevel.Error or LogLevel.Fatal => Solid("#FF8A80"),
            LogLevel.Warning => Solid("#FFB74D"),
            _ => Solid("#B8C4CC"),
        }
        : level switch
        {
            LogLevel.Error or LogLevel.Fatal => Solid("#C62828"),
            LogLevel.Warning => Solid("#B26A00"),
            _ => Solid("#616161"),
        };

    private static Brush Solid(string hex) =>
        new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)));
}
