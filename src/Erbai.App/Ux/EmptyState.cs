using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Erbai.App.Ux;

/// <summary>
/// 统一空态（观感打磨第二轮）：图标 + 主文案 + 副文案，居中呈现。
/// 替代各页各自手写的"一行灰字 / 居中 StackPanel"两种不一致的空态形态。
///
/// 用法（XAML 声明 + 代码后置切可见性，与既有 EmptyPanel/EmptyText 刷新点对齐）：
/// <code>
/// <ux:EmptyState x:Name="QueueEmpty" Symbol="MusicInfo"
///                Title="暂无待播歌曲" Subtitle="观众发送「点歌 歌名 - 歌手」即可加入。" />
/// </code>
/// 属性均为静态设置（页面声明后不变），故用 PropertyMetadata 回调直接同步,
/// 不走运行时绑定。
/// </summary>
public sealed class EmptyState : ContentControl
{
    public static readonly DependencyProperty SymbolProperty =
        DependencyProperty.Register(nameof(Symbol), typeof(Symbol), typeof(EmptyState),
            new PropertyMetadata(Symbol.Favorite, OnSymbolChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(EmptyState),
            new PropertyMetadata(string.Empty, OnSubtitleChanged));

    public Symbol Symbol
    {
        get => (Symbol)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
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

    private readonly SymbolIcon _icon = new();
    private readonly TextBlock _title = new()
    {
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };
    private readonly TextBlock _subtitle = new()
    {
        FontSize = 12,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 380,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    public EmptyState()
    {
        _icon.SetValue(Control.FontSizeProperty, 28.0);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6,
        };
        stack.Children.Add(_icon);
        stack.Children.Add(_title);
        stack.Children.Add(_subtitle);

        Content = stack;
        IsTabStop = false;
    }

    private static void OnSymbolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EmptyState es && e.NewValue is Symbol s)
        {
            es._icon.Symbol = s;
        }
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EmptyState es)
        {
            es._title.Text = (string)e.NewValue;
        }
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EmptyState es)
        {
            es._subtitle.Text = (string)e.NewValue;
        }
    }
}
