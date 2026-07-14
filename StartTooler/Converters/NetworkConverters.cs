using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace StartTooler.Converters;

/// <summary>
/// v0.11 spec/09 §3.1: bool IsOnline → 状态栏圆点 brush。
/// true(在线)→ 绿，false(离线)→ 红。
/// </summary>
public class BoolToNetworkColorConverter : IValueConverter
{
    private static readonly IBrush OnlineBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
    private static readonly IBrush OfflineBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isOnline) return isOnline ? OnlineBrush : OfflineBrush;
        return OfflineBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// v0.11 spec/09 §3.1: bool IsOnline → "在线" / "离线" 文字。
/// </summary>
public class BoolToNetworkTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isOnline) return isOnline ? "在线" : "离线";
        return "未知";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
