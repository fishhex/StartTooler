using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace StartTooler.Converters;

public class IsUploadedBorderConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isUploaded && isUploaded)
        {
            return new SolidColorBrush(Color.Parse("#2ECC71")); // 绿色边框表示已上传
        }
        return new SolidColorBrush(Color.Parse("#E4E4E7")); // 默认边框颜色
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
