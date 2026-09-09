using Erbai.App.Services;
using Erbai.Contracts.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Erbai.App.Views;

/// <summary>
/// 显示与悬浮窗页（评审第一轮 #3/#10）：从概览页迁出的 Overlay/悬浮窗治理。
/// 三类悬浮窗的开关/置顶/穿透/尺寸/样式参数，改动即时持久化并热应用
/// （原有 SaveOverlayWindowsAsync 语义原样保留，仅迁移页面归属）。
/// </summary>
public sealed partial class DisplayPage : Page
{
    /// <summary>悬浮窗字体选项（三类窗共用；WPF/GDI+ 系统字体名，空 = 默认微软雅黑）。
    /// 2026-09 用户需求：点歌/排队原字体硬编码，现与弹幕一致可选。</summary>
    private static readonly (string Label, string Value)[] OverlayFonts =
    [
        ("默认（微软雅黑）", ""),
        ("微软雅黑", "Microsoft YaHei"),
        ("微软雅黑 UI", "Microsoft YaHei UI"),
        ("黑体 SimHei", "SimHei"),
        ("宋体 SimSun", "SimSun"),
        ("楷体 KaiTi", "KaiTi"),
        ("仿宋 FangSong", "FangSong"),
        ("等线 DengXian", "DengXian"),
    ];

    private readonly System.Threading.SemaphoreSlim _overlaySaveGate = new(1, 1);
    private bool _overlayWindowUiReady;

    public DisplayPage()
    {
        InitializeComponent();
        OverlayUrlText.Text = App.Services.OverlayUrl;
        LoadOverlayWindowSettings();
    }

    /// <summary>复制 Overlay URL 到剪贴板（OBS 浏览器源 / 悬浮窗页面共享地址）。</summary>
    private async void OnCopyOverlayUrl(object sender, RoutedEventArgs e)
    {
        var url = OverlayUrlText.Text;
        if (string.IsNullOrWhiteSpace(url) || url.Contains("未启动", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(url);
            Clipboard.SetContent(package);
            CopyOverlayUrlButton.Content = "已复制";
            await Task.Delay(1200);
            CopyOverlayUrlButton.Content = "复制";
        }
        catch
        {
            // 剪贴板被占用等：静默（URL 本身可手选复制）
        }
    }

    /// <summary>悬浮窗设置卡：从配置回填三个开关组与样式参数（初始化时关闭事件防抖）。</summary>
    private void LoadOverlayWindowSettings()
    {
        _overlayWindowUiReady = false;
        var windows = App.Services.Config.Settings.OverlayWindows;
        SongQueueEnabledToggle.IsOn = windows.SongQueue.Enabled;
        SongQueueTopmostToggle.IsOn = windows.SongQueue.Topmost;
        SongQueueThroughToggle.IsOn = windows.SongQueue.ClickThrough;
        QueueUpEnabledToggle.IsOn = windows.QueueUp.Enabled;
        QueueUpTopmostToggle.IsOn = windows.QueueUp.Topmost;
        QueueUpThroughToggle.IsOn = windows.QueueUp.ClickThrough;
        DanmakuEnabledToggle.IsOn = windows.Danmaku.Enabled;
        DanmakuTopmostToggle.IsOn = windows.Danmaku.Topmost;
        DanmakuThroughToggle.IsOn = windows.Danmaku.ClickThrough;

        // 宽高滑块与「自适应高度」开关已移除（2026-09 用户需求：宽高全自适应）：
        // 点歌/排队按内容实测宽高，弹幕窗固定默认宽 + 高度随活跃行数伸缩。

        // 字体下拉（三类窗共用同一份候选；空 = 默认微软雅黑）
        FillFontBox(SongQueueFontFamilyBox);
        FillFontBox(QueueUpFontFamilyBox);
        FillFontBox(DanmakuFontFamilyBox);

        // 样式参数回填（默认强调色：点歌橙 / 排队橙 / 弹幕青；文字色默认白；字体由 LoadStyle 统一回填）
        LoadStyle(SongQueueAccentPicker, SongQueueTextPicker, SongQueueFontScaleSlider, SongQueueOutlineSlider,
            SongQueueGlowSlider, SongQueueOpacitySlider, SongQueueBackgroundSlider, SongQueueFontFamilyBox,
            windows.SongQueue.Style, "#FF7B54");
        LoadStyle(QueueUpAccentPicker, QueueUpTextPicker, QueueUpFontScaleSlider, QueueUpOutlineSlider,
            QueueUpGlowSlider, QueueUpOpacitySlider, QueueUpBackgroundSlider, QueueUpFontFamilyBox,
            windows.QueueUp.Style, "#FF9A3C");
        LoadDanmakuStyle(windows.Danmaku.Style);
        SyncColorChips();
        _overlayWindowUiReady = true;
    }

    /// <summary>填充字体下拉候选（清空后按 OverlayFonts 重建，Tag 存字体名）。</summary>
    private static void FillFontBox(ComboBox box)
    {
        box.Items.Clear();
        foreach (var (label, value) in OverlayFonts)
        {
            box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
    }

    /// <summary>弹幕样式回填：基础参数 + bililive_dm 同款动画四项（拉伸/文字出现/停留/淡出）+ 行数上限 + 字体。</summary>
    private void LoadDanmakuStyle(OverlayWindowStyleConfig style)
    {
        LoadStyle(DanmakuAccentPicker, DanmakuTextPicker, DanmakuFontScaleSlider, DanmakuOutlineSlider,
            DanmakuGlowSlider, DanmakuOpacitySlider, DanmakuBackgroundSlider, DanmakuFontFamilyBox,
            style, "#7FD0FF");
        DanmakuExpandSlider.Value = Math.Clamp(style.EffectExpand, 0.1, 3);
        DanmakuTextInSlider.Value = Math.Clamp(style.EffectTextIn, 0.1, 3);
        DanmakuHoldSlider.Value = Math.Clamp(style.EffectHold, 0, 30);
        DanmakuFadeSlider.Value = Math.Clamp(style.EffectFade, 0.1, 5);
        DanmakuMaxLinesSlider.Value = Math.Clamp(style.MaxLines, 10, 100);
    }

    /// <summary>色块按钮的色板同步当前取色器颜色（Flyout 内色盘收起来后，色块即当前值预览）。</summary>
    private void SyncColorChips()
    {
        SongQueueAccentChip.Background = new SolidColorBrush(SongQueueAccentPicker.Color);
        SongQueueTextChip.Background = new SolidColorBrush(SongQueueTextPicker.Color);
        QueueUpAccentChip.Background = new SolidColorBrush(QueueUpAccentPicker.Color);
        QueueUpTextChip.Background = new SolidColorBrush(QueueUpTextPicker.Color);
        DanmakuAccentChip.Background = new SolidColorBrush(DanmakuAccentPicker.Color);
        DanmakuTextChip.Background = new SolidColorBrush(DanmakuTextPicker.Color);
    }

    private static void SelectFont(ComboBox box, string value)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && Equals(item.Tag as string, value))
            {
                box.SelectedIndex = i;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    /// <summary>样式回填（2026-09 起含字体下拉，三类窗共用）：统一在此重置字体，
    /// 否则「恢复默认」只复位颜色/滑杆、字体下拉残留上次选择，保存后 FontFamily 不变。</summary>
    private static void LoadStyle(ColorPicker accent, ColorPicker text, Slider scale, Slider outline, Slider glow,
        Slider opacity, Slider background, ComboBox fontBox, OverlayWindowStyleConfig style, string fallbackAccent)
    {
        accent.Color = ParseColor(style.AccentColor, fallbackAccent);
        text.Color = ParseColor(style.TextColor, "#FFFFFF");
        scale.Value = style.FontScale;
        outline.Value = style.TextOutline;
        glow.Value = style.TextGlow;
        opacity.Value = style.Opacity;
        background.Value = style.BackgroundOpacity;
        SelectFont(fontBox, style.FontFamily);
    }

    /// <summary>#RRGGBB → Windows.UI.Color（空/非法回退 fallback）。</summary>
    private static Windows.UI.Color ParseColor(string hex, string fallback)
    {
        var h = (string.IsNullOrWhiteSpace(hex) ? fallback : hex).TrimStart('#');
        if (h.Length != 6 || !uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            return ParseColor(fallback, "#FFFFFF");
        }

        return Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // 宽高滑杆联动 UpdateHeightEnabled / OnSizeSliderChanged 已随宽高滑块一并移除
    // （2026-09 用户需求：宽高全自适应，尺寸不再由 UI 控制）。

    /// <summary>悬浮窗开关热切换（决策 #17）：先持久化，再让 manager 即时应用（开/关/置顶/穿透）。
    /// SemaphoreSlim 串行化 + 锁内重读最新配置：并行 Toggled 基于旧快照会互相回滚（review 修复）；
    /// 应用（可能等待窗线程句柄）移出 UI 线程，快速拨开关不卡界面。</summary>
    private async void OnOverlayWindowToggled(object sender, RoutedEventArgs e)
    {
        if (!_overlayWindowUiReady)
        {
            return; // 初始化回填阶段触发的事件，忽略
        }

        await SaveOverlayWindowsAsync();
    }

    /// <summary>样式参数变化（颜色/滑杆/字体）：同 Toggle 路径持久化 + 热应用（2026-08-29 样式参数化）。</summary>
    private async void OnOverlayStyleChanged(object sender, object e)
    {
        // 必须先守卫再同步色块：XAML 解析期 ColorPicker 被赋默认色即触发 ColorChanged，
        // 此时后续元素的 x:Name 字段尚未连接（点歌 AccentPicker 早于排队/弹幕的 Chip），
        // 无条件调 SyncColorChips() 会抛 NullReferenceException，且本方法是 async void——
        // 异常经 DispatcherQueueSynchronizationContext 重新抛出成为「未处理异常（已隔离）」
        // （用户日志实锤：单次进页面 4 条、全天累计 192 条，堆栈恒为此处）。
        // 回填阶段无需同步：LoadOverlayWindowSettings 末尾会显式调一次 SyncColorChips。
        if (!_overlayWindowUiReady)
        {
            return; // 初始化回填阶段触发的事件，忽略
        }

        SyncColorChips(); // 色块预览当前取色器颜色（Flyout 收起后即当前值）
        await SaveOverlayWindowsAsync();
    }

    private async Task SaveOverlayWindowsAsync()
    {
        var services = App.Services;
        await _overlaySaveGate.WaitAsync();
        try
        {
            var current = services.Config.Settings;
            // 宽高/AnchorRight/AnchorBottom 不在这里赋值：宽高已全自适应（配置字段弃用），
            // 位置锚点由 OverlayWindowManager 退出时采集写回——用 with 表达式保留原值，
            // 否则设置页每次保存都会把刚记住的位置抹掉（用户实测需求「记住各自位置」）。
            var candidate = current with
            {
                OverlayWindows = new OverlayWindowsConfig
                {
                    SongQueue = current.OverlayWindows.SongQueue with
                    {
                        Enabled = SongQueueEnabledToggle.IsOn,
                        Topmost = SongQueueTopmostToggle.IsOn,
                        ClickThrough = SongQueueThroughToggle.IsOn,
                        Style = CollectStyle(SongQueueAccentPicker.Color, SongQueueTextPicker.Color,
                            SongQueueFontScaleSlider.Value,
                            SongQueueOutlineSlider.Value, SongQueueGlowSlider.Value, SongQueueOpacitySlider.Value,
                            SongQueueBackgroundSlider.Value, SelectedFont(SongQueueFontFamilyBox)),
                    },
                    QueueUp = current.OverlayWindows.QueueUp with
                    {
                        Enabled = QueueUpEnabledToggle.IsOn,
                        Topmost = QueueUpTopmostToggle.IsOn,
                        ClickThrough = QueueUpThroughToggle.IsOn,
                        Style = CollectStyle(QueueUpAccentPicker.Color, QueueUpTextPicker.Color,
                            QueueUpFontScaleSlider.Value,
                            QueueUpOutlineSlider.Value, QueueUpGlowSlider.Value, QueueUpOpacitySlider.Value,
                            QueueUpBackgroundSlider.Value, SelectedFont(QueueUpFontFamilyBox)),
                    },
                    Danmaku = current.OverlayWindows.Danmaku with
                    {
                        Enabled = DanmakuEnabledToggle.IsOn,
                        Topmost = DanmakuTopmostToggle.IsOn,
                        ClickThrough = DanmakuThroughToggle.IsOn,
                        Style = CollectDanmakuStyle(DanmakuAccentPicker.Color, DanmakuTextPicker.Color,
                            DanmakuFontScaleSlider.Value,
                            DanmakuOutlineSlider.Value, DanmakuGlowSlider.Value, DanmakuOpacitySlider.Value,
                            DanmakuBackgroundSlider.Value, DanmakuExpandSlider.Value, DanmakuTextInSlider.Value,
                            DanmakuHoldSlider.Value, DanmakuFadeSlider.Value, (int)DanmakuMaxLinesSlider.Value,
                            SelectedFont(DanmakuFontFamilyBox)),
                    },
                },
            };

            await services.Config.PersistAsync(candidate);
            await Task.Run(() => services.OverlayWindows?.ApplyConfig(candidate));
        }
        catch (Exception)
        {
            // 保存失败：回填配置原值，放弃本次改动（页面不残留假状态）
            LoadOverlayWindowSettings();
        }
        finally
        {
            _overlaySaveGate.Release();
        }
    }

    /// <summary>样式收集（三类窗共用；2026-09 起含字体——点歌/排队也可选字体）。</summary>
    private static OverlayWindowStyleConfig CollectStyle(Windows.UI.Color accent, Windows.UI.Color text,
        double fontScale, double outline, double glow, double opacity, double background,
        string fontFamily) => new()
    {
        AccentColor = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}",
        TextColor = $"#{text.R:X2}{text.G:X2}{text.B:X2}",
        FontScale = fontScale,
        TextOutline = outline,
        TextGlow = glow,
        Opacity = opacity,
        BackgroundOpacity = background,
        FontFamily = fontFamily,
    };

    /// <summary>弹幕样式收集：基础参数 + 动画四项 / 行数上限 / 字体（bililive_dm MainOverlayEffect1-4 同款）。</summary>
    private static OverlayWindowStyleConfig CollectDanmakuStyle(Windows.UI.Color accent, Windows.UI.Color text,
        double fontScale, double outline, double glow, double opacity, double background, double expand, double textIn,
        double hold, double fade, int maxLines, string fontFamily)
    {
        var style = CollectStyle(accent, text, fontScale, outline, glow, opacity, background, fontFamily);
        return style with
        {
            EffectExpand = expand,
            EffectTextIn = textIn,
            EffectHold = hold,
            EffectFade = fade,
            MaxLines = maxLines,
        };
    }

    /// <summary>字体下拉当前选中值（Tag = 字体名；未选中回退空串 = 默认微软雅黑）。</summary>
    private static string SelectedFont(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>恢复样式默认（bililive_dm OptionDialog 的「默认」按钮）：按 Tag 重置对应样式区并保存。</summary>
    private async void OnResetOverlayStyle(object sender, RoutedEventArgs e)
    {
        if (!_overlayWindowUiReady)
        {
            return;
        }

        var tag = (sender as FrameworkElement)?.Tag as string;
        var defaults = new OverlayWindowStyleConfig();
        _overlayWindowUiReady = false; // 防回填触发的事件触发保存（与 LoadOverlayWindowSettings 同款）
        try
        {
            switch (tag)
            {
                case "danmaku":
                    LoadDanmakuStyle(defaults);
                    break;
                case "songqueue":
                    LoadStyle(SongQueueAccentPicker, SongQueueTextPicker, SongQueueFontScaleSlider, SongQueueOutlineSlider,
                        SongQueueGlowSlider, SongQueueOpacitySlider, SongQueueBackgroundSlider, SongQueueFontFamilyBox,
                        defaults, "#FF7B54");
                    break;
                case "queueup":
                    LoadStyle(QueueUpAccentPicker, QueueUpTextPicker, QueueUpFontScaleSlider, QueueUpOutlineSlider,
                        QueueUpGlowSlider, QueueUpOpacitySlider, QueueUpBackgroundSlider, QueueUpFontFamilyBox,
                        defaults, "#FF9A3C");
                    break;
            }
        }
        finally
        {
            _overlayWindowUiReady = true;
        }

        SyncColorChips();
        await SaveOverlayWindowsAsync();
    }
}