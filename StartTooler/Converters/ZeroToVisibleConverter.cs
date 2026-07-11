using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace StartTooler.Converters;

/// <summary>
/// int == 0 → Visible（用于 spec §5.2 「（无 diff）」占位文本显隐）。
/// 非 0 → Collapsed。
/// </summary>
public class ZeroToVisibleConverter : IValueConverter
{
    public static readonly ZeroToVisibleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int n) return n == 0;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
