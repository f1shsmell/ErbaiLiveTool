using Erbai.Contracts.Live;
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
    /// 浅色背景事件色板（日志页与概览卡片共用；深色文字在白底上可读）。
    /// 旧 <see cref="ColorFor"/> 面向深色悬浮窗底色（弹幕=白字），概览卡片是浅底，
    /// 白字在浅底上看不见——"概览实时事件空白"的根因，改用浅色板。
    /// </summary>
    public static Brush LightColorFor(LiveEvent evt) => evt.Kind switch
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

    private static Brush Solid(string hex) =>
        new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)));
}