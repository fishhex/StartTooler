using Avalonia.Data.Converters;
using Avalonia.Media;
using StartTooler.Services;
using System;
using System.Globalization;

namespace StartTooler.Converters;

public class ToastTypeToBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ToastType type)
        {
            return type switch
            {
                ToastType.Success => new SolidColorBrush(Color.Parse("#2ECC71")),
                ToastType.Error => new SolidColorBrush(Color.Parse("#E74C3C")),
                _ => new SolidColorBrush(Color.Parse("#DD181B25"))
            };
        }
        return new SolidColorBrush(Color.Parse("#DD181B25"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}