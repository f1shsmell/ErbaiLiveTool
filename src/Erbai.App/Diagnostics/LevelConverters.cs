using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Erbai.App.Diagnostics;

/// <summary>诊断级别 → 徽标背景色。</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is DiagnosticLevel level
            ? new SolidColorBrush(level switch
            {
                DiagnosticLevel.Ok => Color.FromArgb(0xFF, 0x2E, 0x7D, 0x32),
                DiagnosticLevel.Warn => Color.FromArgb(0xFF, 0xF9, 0xA8, 0x25),
                _ => Color.FromArgb(0xFF, 0xC6, 0x28, 0x28),
            })
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x60, 0x60));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>诊断级别 → 徽标文本。</summary>
public sealed class LevelToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DiagnosticLevel level
            ? level switch
            {
                DiagnosticLevel.Ok => "OK",
                DiagnosticLevel.Warn => "WARN",
                _ => "FAIL",
            }
            : "?";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
