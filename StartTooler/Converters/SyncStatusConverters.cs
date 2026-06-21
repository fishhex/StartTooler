using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StartTooler.Models;

namespace StartTooler.Converters;

public class SyncStatusToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SyncStatus status)
        {
            return status switch
            {
                SyncStatus.UploadedAndLocal => new SolidColorBrush(Color.Parse("#4DD0E1")),
                SyncStatus.UploadedButMissingLocal => new SolidColorBrush(Color.Parse("#FFA726")),
                SyncStatus.NotUploaded => new SolidColorBrush(Color.Parse("#4A5273")),
                _ => new SolidColorBrush(Color.Parse("#4A5273"))
            };
        }
        return new SolidColorBrush(Color.Parse("#4A5273"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SyncStatusToTooltipConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SyncStatus status)
        {
            return status switch
            {
                SyncStatus.UploadedAndLocal => "已上传且本地存在",
                SyncStatus.UploadedButMissingLocal => "已上传但本地不存在",
                SyncStatus.NotUploaded => "未上传",
                _ => "未知状态"
            };
        }
        return "未知状态";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
