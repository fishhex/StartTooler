using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StartTooler.Converters;

public static class ScanProgressConverters
{
    public static readonly IMultiValueConverter ProgressWidth = new ProgressWidthConverter();

    public static readonly IValueConverter FileName = new FileNameConverter();

    private class ProgressWidthConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 2)
            {
                if (values[0] is int processed && values[1] is int total && total > 0)
                {
                    var ratio = (double)processed / total;
                    return new GridLength(ratio, GridUnitType.Star);
                }
            }
            return new GridLength(0, GridUnitType.Star);
        }
    }

    private class FileNameConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrEmpty(path))
            {
                return Path.GetFileName(path);
            }
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
