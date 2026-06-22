using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace StartTooler.Converters;

public static class Int32Converters
{
    public static readonly IValueConverter IsGreaterThanZero = new IsGreaterThanZeroConverter();

    private class IsGreaterThanZeroConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int intValue)
            {
                return intValue > 0;
            }
            if (value is long longValue)
            {
                return longValue > 0;
            }
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
