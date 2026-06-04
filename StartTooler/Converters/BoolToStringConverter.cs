using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace StartTooler.Converters;

public class BoolToStringConverter : IValueConverter
{
    public string? TrueValue { get; set; }
    public string? FalseValue { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? TrueValue : FalseValue;
        }
        return FalseValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
