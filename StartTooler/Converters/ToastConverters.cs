using Avalonia;
using Avalonia.Controls;
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
            var key = type switch
            {
                ToastType.Success => "AlertSuccessBrush",
                ToastType.Error => "AlertErrorBrush",
                _ => "AlertInfoBrush"
            };
            return Application.Current?.FindResource(key) as SolidColorBrush
                ?? new SolidColorBrush(Color.Parse("#DD181B25"));
        }
        return new SolidColorBrush(Color.Parse("#DD181B25"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}