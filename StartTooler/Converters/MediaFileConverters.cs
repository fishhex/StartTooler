using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StartTooler.Data;
using StartTooler.Models;

namespace StartTooler.Converters;

public class MediaFileToStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MediaFile file)
            return SyncStatus.NotUploaded;

        if (file.IsUploaded && file.LocalExists)
            return SyncStatus.UploadedAndLocal;
        if (file.IsUploaded && !file.LocalExists)
            return SyncStatus.UploadedButMissingLocal;
        return SyncStatus.NotUploaded;
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
