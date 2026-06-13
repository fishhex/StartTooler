using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StartTooler.ViewModels;

namespace StartTooler.Converters;

/// <summary>
/// Converts MainPageType enum value to bool for IsVisible binding.
/// </summary>
public class PageTypeToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MainPageType currentPage || parameter is not string parameterString)
            return false;

        return Enum.TryParse<MainPageType>(parameterString, out var targetPage) 
               && currentPage == targetPage;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
