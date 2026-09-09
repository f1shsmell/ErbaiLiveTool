using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Erbai.App.Ux;

/// <summary>
/// 卡片悬停反馈（主程序 UI 改进，参考 PCL"一切反馈皆动画"）：
/// 附加到任意 <see cref="FrameworkElement"/> 后，PointerOver 时微缩放（1.0 → 1.02）
/// 离开还原，让卡片有可感知的交互反馈。全局 <c>CardBorderStyle</c> 通过
/// <c>Setter Property="ux:CardHoverEffect.IsEnabled" Value="True"</c> 统一启用，
/// 无需逐页挂事件。
/// </summary>
public static class CardHoverEffect
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(CardHoverEffect),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            fe.PointerEntered += OnPointerEntered;
            fe.PointerExited += OnPointerExited;
            // 缩放围绕中心，避免放大时向右下偏移
            fe.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }
        else
        {
            fe.PointerEntered -= OnPointerEntered;
            fe.PointerExited -= OnPointerExited;
        }
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e) => AnimateScale((FrameworkElement)sender, 1.02);

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e) => AnimateScale((FrameworkElement)sender, 1.0);

    /// <summary>平滑缩放到目标值（150ms EaseOut；阈值内不动，避免反复进出抖动）。</summary>
    private static void AnimateScale(FrameworkElement fe, double target)
    {
        var st = fe.RenderTransform as ScaleTransform;
        if (st is null)
        {
            st = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
            fe.RenderTransform = st;
        }

        if (Math.Abs(st.ScaleX - target) < 0.001)
        {
            return;
        }

        // ScaleX/ScaleY 同步动画（同一 Storyboard）
        var sb = new Storyboard();
        foreach (var axis in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
        {
            var anim = new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut,
                },
            };
            Storyboard.SetTarget(anim, st);
            Storyboard.SetTargetProperty(anim, axis);
            sb.Children.Add(anim);
        }

        sb.Begin();
    }
}