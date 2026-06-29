using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia;

namespace StartTooler.Services;

/// <summary>
/// NotificationType -> 左侧色条 SolidColorBrush。
/// 颜色取自主题（Accent.Stellar / State.Success / State.Danger）。
/// </summary>
public class NotificationTypeToBrushConverter : IValueConverter
{
    public static readonly NotificationTypeToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not NotificationType type) return null;
        var app = Application.Current;
        if (app == null) return null;

        var colorKey = type switch
        {
            NotificationType.Info => "Accent.Stellar",
            NotificationType.Success => "State.Success",
            NotificationType.Error => "State.Danger",
            _ => "Text.Tertiary",
        };

        if (app.Resources.TryGetValue(colorKey, out var res) && res is Color color)
        {
            return new SolidColorBrush(color);
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
