using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StartTooler.Models;

namespace StartTooler.Converters;

public static class RefreshStateConverters
{
    public static readonly IValueConverter IsScanning = new IsScanningConverter();

    public static readonly IValueConverter IsVisible = new IsVisibleConverter();

    public static readonly IValueConverter RefreshStateToClass = new RefreshStateToClassConverter();

    private class IsScanningConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is RefreshState state)
            {
                return state == RefreshState.Scanning || state == RefreshState.Completed;
            }
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    private class IsVisibleConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is RefreshState state)
            {
                return state != RefreshState.Idle && state != RefreshState.Stopped;
            }
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    private class RefreshStateToClassConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is RefreshState state)
            {
                return state.ToString();
            }
            return "Idle";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
