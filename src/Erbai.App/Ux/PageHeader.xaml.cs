using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Erbai.App.Ux;

/// <summary>
/// 统一页头（2026-10 UI 美化轮）：品牌橙图标章 + 页标题 + 副标题。
/// 图标复用导航项的 Symbol 枚举（与左侧导航一一对应）；DP 赋值即生效。
/// </summary>
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata("", OnTextChanged));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata("", OnTextChanged));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Symbol), typeof(PageHeader), new PropertyMetadata(Symbol.Home, OnIconChanged));

    public PageHeader()
    {
        InitializeComponent();
        ApplyText();
        ApplyIcon();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>页图标（与 MainWindow 导航项的 SymbolIcon 保持一致）。</summary>
    public Symbol Icon
    {
        get => (Symbol)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PageHeader header)
        {
            header.ApplyText();
        }
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PageHeader header)
        {
            header.ApplyIcon();
        }
    }

    private void ApplyText()
    {
        TitleText.Text = Title ?? "";
        SubtitleText.Text = Subtitle ?? "";
        // 空副标题不占位，避免页头下方多出一条空行
        SubtitleText.Visibility = string.IsNullOrEmpty(SubtitleText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyIcon()
    {
        // Symbol 枚举值与 Segoe Fluent 图标码位一一对应；字体族走主题资源
        //（Win11 Segoe Fluent Icons / Win10 Segoe MDL2 Assets，导航同款）
        GlyphIcon.Glyph = ((char)Icon).ToString();
        if (Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var value)
            && value is FontFamily family)
        {
            GlyphIcon.FontFamily = family;
        }
    }
}
