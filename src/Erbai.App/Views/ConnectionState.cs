using Erbai.App.Services;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Erbai.App.Views;

/// <summary>
/// 统一连接状态（评审第一轮 #8）：未配置 / 未连接 / 连接中 / 已连接 / 异常。
/// UI 层映射——当前领域层没有独立的"连接中"信号，映射器只产出真实可判定的状态，
/// Connecting 作为预留成员，待领域层提供中间态信号后再启用。
/// </summary>
public enum ConnectionState
{
    /// <summary>未配置（如未填房间号/未选播放器）。</summary>
    NotConfigured,

    /// <summary>未连接（处于停止/等待状态）。</summary>
    Disconnected,

    /// <summary>连接中（预留：当前无领域信号）。</summary>
    Connecting,

    /// <summary>已连接（运行中/已接入）。</summary>
    Connected,

    /// <summary>异常（启动失败/出错了）。</summary>
    Error,
}

/// <summary>把 AppServices 的布尔+字符串状态映射为统一连接状态（UI 层，不下沉领域）。</summary>
internal static class ConnectionStateMapper
{
    public static ConnectionState ForBilibili(AppServices services)
    {
        if (services.BilibiliRunning)
        {
            return ConnectionState.Connected;
        }

        return services.BilibiliStatus.Contains("失败", StringComparison.Ordinal)
            ? ConnectionState.Error
            : string.IsNullOrWhiteSpace(services.Config.Settings.RoomId)
                ? ConnectionState.NotConfigured
                : ConnectionState.Disconnected;
    }

    public static ConnectionState ForDouyin(AppServices services)
    {
        if (services.DouyinRunning)
        {
            return ConnectionState.Connected;
        }

        return services.DouyinStatus.Contains("失败", StringComparison.Ordinal)
            ? ConnectionState.Error
            : ConnectionState.Disconnected;
    }

    /// <summary>播放器：有连接器实例即"已连接"（现状无法感知子进程断线，语义与旧"已接入"一致）。</summary>
    public static ConnectionState ForPlayer(AppServices services) =>
        services.Player is null ? ConnectionState.NotConfigured : ConnectionState.Connected;

    public static ConnectionState ForLogin(AppServices services) =>
        services.Login?.CurrentCredentials is { HasLogin: true } ? ConnectionState.Connected : ConnectionState.Disconnected;
}

/// <summary>连接状态 → 中文文本。</summary>
public sealed class ConnectionStateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language) =>
        value is ConnectionState s ? TextFor(s) : TextFor(ConnectionState.Disconnected);

    public static string TextFor(ConnectionState state) => state switch
    {
        ConnectionState.NotConfigured => "未配置",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Connected => "已连接",
        ConnectionState.Error => "异常",
        _ => "未连接",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>连接状态 → 圆点/徽章画刷（已连接绿 / 异常红 / 未连接与未配置中性灰 / 连接中强调色）。</summary>
public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language) =>
        value is ConnectionState s ? BrushFor(s) : SolidColorBrushHelper.Neutral;

    public static Brush BrushFor(ConnectionState state) => state switch
    {
        ConnectionState.Connected => SolidColorBrushHelper.Success,
        ConnectionState.Error => SolidColorBrushHelper.Critical,
        ConnectionState.Connecting => SolidColorBrushHelper.Accent,
        _ => SolidColorBrushHelper.Neutral,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}