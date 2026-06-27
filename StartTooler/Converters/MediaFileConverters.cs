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

        // 优先展示瞬时 UploadStatus（Failed 时显示原因）
        string statusText = file.UploadStatus switch
        {
            UploadStatus.Uploading => "上传中…",
            UploadStatus.Failed => string.IsNullOrEmpty(file.UploadError) ? "上传失败" : $"上传失败：{file.UploadError}",
            UploadStatus.Paused => "上次未完成，可继续上传",
            UploadStatus.Uploaded => file.IsUploaded && file.LocalExists
                ? "已上传且本地存在"
                : file.IsUploaded && !file.LocalExists
                    ? "已上传但本地不存在"
                    : "已上传",
            _ => file.IsUploaded && file.LocalExists
                ? "已上传且本地存在"
                : file.IsUploaded && !file.LocalExists
                    ? "已上传但本地不存在"
                    : "未上传",
        };

        return $"{file.FileName}\n{statusText}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 把 UploadStatus 和 parameter（字符串枚举名）比，相等返回 true（→ IsVisible 可见）。
/// AXAML 用法：IsVisible="{Binding UploadStatus, Converter={x:Static c}, ConverterParameter=Uploading}"
/// </summary>
public class UploadStatusToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not UploadStatus status || parameter is not string p)
            return false;
        return Enum.TryParse<UploadStatus>(p, ignoreCase: true, out var target) && status == target;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 把 MediaFile 派生的 SyncStatus 和 parameter 比，相等返回 true。
/// 当 UploadStatus 处于 Uploading / Failed / Paused 时**强制返回 false**——这三个是瞬时进度态，
/// 由原 UploadStatusToVisibilityConverter 系列的徽章负责显示；同步态徽章不与之重叠。
///
/// AXAML 用法：IsVisible="{Binding ., Converter={StaticResource ...}, ConverterParameter=UploadedAndLocal}"
/// </summary>
public class MediaFileToSyncVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MediaFile file || parameter is not string p)
            return false;

        // 进度态优先：Uploading / Failed / Paused 时让对应徽章显示
        if (file.UploadStatus is UploadStatus.Uploading
            or UploadStatus.Failed
            or UploadStatus.Paused)
            return false;

        SyncStatus status;
        if (file.IsUploaded && file.LocalExists)
            status = SyncStatus.UploadedAndLocal;
        else if (file.IsUploaded && !file.LocalExists)
            status = SyncStatus.UploadedButMissingLocal;
        else
            status = SyncStatus.NotUploaded;

        return Enum.TryParse<SyncStatus>(p, ignoreCase: true, out var target) && status == target;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
