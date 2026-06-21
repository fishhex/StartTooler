using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StartTooler.Converters;

public class DeletedStatusConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values != null && values.Count >= 1 && values[0] is bool isDeleted)
        {
            if (isDeleted)
            {
                return new SolidColorBrush(Color.Parse("#666666")); // 灰色表示已删除
            }
        }
        return new SolidColorBrush(Colors.Transparent);
    }
}

public class DeletedOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isDeleted)
        {
            return isDeleted ? 0.5 : 1.0; // 已删除文件降低透明度
        }
        return 1.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
