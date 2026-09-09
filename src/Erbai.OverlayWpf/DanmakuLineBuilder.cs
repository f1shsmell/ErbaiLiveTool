using Erbai.Contracts.Live;

namespace Erbai.OverlayWpf;

/// <summary>弹幕行展示模型（bililive_dm AddDMText 语义：用户名 + 内容 + warn 红名；
/// 抖音适配：粉丝团等级徽章）。</summary>
internal readonly record struct DanmakuLineModel(
    string Who, string Text, bool Warn, bool IsAdmin, bool IsAnchor, int? FanLevel = null);

/// <summary>
/// live.* 事件 → 弹幕行文案/配色决策（纯逻辑，可单测）。
/// 语义对齐 bililive_dm MainWindow.ProcDanmaku/AddDMText：礼物/上舰 warn=true（红名），
/// 弹幕/进场等普通消息黄名；管理员/主播保留青/橙标识色。
/// 抖音适配（2026-09）：粉丝团消息区分加入/升级并带等级、礼物显示电池价值、下播提示；
/// 粉丝团等级（FanLevel）供弹幕行渲染 Lv 徽章。
/// </summary>
internal static class DanmakuLineBuilder
{
    /// <summary>事件 → 弹幕行模型；返回 null 表示不显示（无文案事件）。</summary>
    public static DanmakuLineModel? Build(LiveEvent evt)
    {
        var who = string.IsNullOrWhiteSpace(evt.Nickname) ? "匿名" : evt.Nickname;
        var gift = string.IsNullOrWhiteSpace(evt.GiftName) ? "礼物" : evt.GiftName;
        var fanLevel = evt.FanLevel is > 0 ? evt.FanLevel : (int?)null;
        return evt.Kind switch
        {
            LiveEventKind.Danmaku =>
                new DanmakuLineModel(who, (evt.Text ?? "").Trim(), false, evt.IsAdmin == true, evt.IsAnchor == true, fanLevel),
            LiveEventKind.Gift =>
                new DanmakuLineModel(who, FormatGift(evt, gift), true, false, false, fanLevel),
            LiveEventKind.GuardBuy =>
                new DanmakuLineModel(who, "开通大航海（舰长）", true, false, false, fanLevel),
            LiveEventKind.Enter =>
                new DanmakuLineModel(who, "进入直播间", false, false, false, fanLevel),
            // 抖音粉丝团（Type=7）与 B站关注语义不同：粉丝团 → 加团/升级（带等级），关注 → 关注了主播
            LiveEventKind.Follow or LiveEventKind.Subscribe =>
                new DanmakuLineModel(who, FormatFollow(evt, fanLevel), false, false, false, fanLevel),
            LiveEventKind.Like =>
                new DanmakuLineModel(who, "点赞了直播间", false, false, false, fanLevel),
            LiveEventKind.Share =>
                new DanmakuLineModel(who, "分享了直播间", false, false, false, fanLevel),
            LiveEventKind.LiveState =>
                new DanmakuLineModel(who, "直播间已下播", true, false, false, null),
            _ => string.IsNullOrWhiteSpace(evt.Text)
                ? null
                : new DanmakuLineModel(who, evt.Text.Trim(), false, evt.IsAdmin == true, evt.IsAnchor == true, fanLevel),
        };
    }

    /// <summary>礼物行：抖音带电池价值（TotalCoin>0）。</summary>
    private static string FormatGift(LiveEvent evt, string gift)
    {
        var baseLine = evt.GiftCount > 0 ? $"送出 {gift} × {evt.GiftCount}" : $"送出 {gift}";
        return evt.Platform == "douyin" && evt.TotalCoin > 0
            ? $"{baseLine}（{evt.TotalCoin} 电池）"
            : baseLine;
    }

    /// <summary>抖音粉丝团：加入/升级（带等级）；B站关注：关注了主播。</summary>
    private static string FormatFollow(LiveEvent evt, int? fanLevel)
    {
        if (evt.Platform != "douyin")
        {
            return "关注了主播";
        }

        return fanLevel is { } lv ? $"加入了粉丝团 Lv.{lv}" : "加入了粉丝团";
    }
}
