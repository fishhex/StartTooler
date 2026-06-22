using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StartTooler.Data;
using StartTooler.Models;

namespace StartTooler.Converters;

public class MediaFileToStatusConverter : IValueConverter
{
    private static readonly SyncStatusToColorConverter _colorConverter = new();
    private static readonly SyncStatusToTooltipConverter _tooltipConverter = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MediaFile file)
            return _colorConverter.Convert(SyncStatus.NotUploaded, targetType, parameter, culture);

        SyncStatus status;
        if (file.IsUploaded && file.LocalExists)
            status = SyncStatus.UploadedAndLocal;
        else if (file.IsUploaded && !file.LocalExists)
            status = SyncStatus.UploadedButMissingLocal;
        else
            status = SyncStatus.NotUploaded;

        // 根据 parameter 决定返回什么
        if (parameter is string p && p.Equals("Color", StringComparison.OrdinalIgnoreCase))
        {
            return _colorConverter.Convert(status, targetType, parameter, culture);
        }
        if (parameter is string p2 && p2.Equals("Tooltip", StringComparison.OrdinalIgnoreCase))
        {
            return _tooltipConverter.Convert(status, targetType, parameter, culture);
        }

        return status;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class MediaFileToTooltipConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MediaFile file)
            return "未知状态";

        var status = file.IsUploaded && file.LocalExists
            ? "已上传且本地存在"
            : file.IsUploaded && !file.LocalExists
                ? "已上传但本地不存在"
                : "未上传";

        return $"{file.FileName}\n{status}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
