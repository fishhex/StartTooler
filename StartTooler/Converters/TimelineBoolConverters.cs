using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StartTooler.Converters;

public class BoolToAccentOrDividerConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSelected && isSelected)
            return Brush.Parse("#4FC3F7"); // Timeline.Dot.Selected - 蓝色实心圆点
        return Brush.Parse("#2A3050"); // Bg.Divider - 未选中时的灰色
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToFontWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSelected && isSelected)
            return FontWeight.Bold;
        return FontWeight.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToAccentOrSecondaryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSelected && isSelected)
            return Brush.Parse("#FF6B6B"); // Timeline.Selected - 红色文字表示选中
        return Brush.Parse("#8892B0"); // Text.Secondary - 未选中时的灰色
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
