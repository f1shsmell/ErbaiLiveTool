using Erbai.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace Erbai.App.Ux;

/// <summary>
/// 状态胶囊徽章（观感打磨第二轮）：圆点 + 状态文字，外包半透明色底胶囊。
/// 替代此前"裸圆点 + 灰字"的状态行——徽章底色与圆点同色系，信息层级更强，
/// 深浅色主题均由主题画刷保证对比度。
///
/// 用法（代码后置刷新，与既有状态刷新点对齐）：
/// <code>
/// badge.Set(ConnectionStateMapper.ForBilibili(services));
/// </code>
/// 未设置状态前显示"未连接"中性灰，避免初始化闪烁。
/// </summary>
public sealed class StatusBadge : ContentControl
{
    private readonly Border _root = new() { Style = (Style)Application.Current.Resources["StatusBadgeBorderStyle"] };
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new()
    {
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public StatusBadge()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        panel.Children.Add(_dot);
        panel.Children.Add(_text);
        _root.Child = panel;
        Content = _root;
        Set(ConnectionState.Disconnected);
    }

    /// <summary>刷新状态：同步圆点/底色/文字，并播放一次状态变化脉冲（仅 已连接/异常）。</summary>
    public void Set(ConnectionState state)
    {
        var brush = ConnectionStateToBrushConverter.BrushFor(state);
        _root.Background = brush;
        _dot.Fill = brush;
        _text.Text = ConnectionStateToTextConverter.TextFor(state);
        PlayPulse(_dot, state);
    }

    /// <summary>
    /// 任意领域状态的徽章渲染（历史页等非连接状态场景）：文字与语义画刷由调用方给出，
    /// 主题画刷（SystemFillColor*BackgroundBrush）自带深浅色对比度；无脉冲动画
    /// （历史记录是静态终态，不需要"正在变化"的视觉暗示）。
    /// </summary>
    public void SetText(string text, Brush semanticBrush)
    {
        _root.Background = semanticBrush;
        _dot.Fill = semanticBrush;
        _text.Text = text;
    }

    /// <summary>状态变化时的圆点脉冲（从 OverviewPage.PlayStatusPulse 提取复用）。</summary>
    private void PlayPulse(Ellipse dot, ConnectionState state)
    {
        if (state is not (ConnectionState.Connected or ConnectionState.Error))
        {
            return;
        }

        if (dot.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform
            {
                CenterX = dot.Width / 2,
                CenterY = dot.Height / 2,
            };
            dot.RenderTransform = st;
        }

        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 1.35,
            Duration = TimeSpan.FromMilliseconds(160),
            AutoReverse = true,
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut,
            },
        };
        Storyboard.SetTarget(anim, st);
        Storyboard.SetTargetProperty(anim, nameof(ScaleTransform.ScaleX));
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }
}
