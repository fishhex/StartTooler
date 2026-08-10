using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StartTooler.Data;

namespace StartTooler.Converters;

public class MediaTypeToVideoConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MediaType mediaType)
        {
            return mediaType == MediaType.Video;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class MediaTypeToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MediaType mediaType)
        {
            return mediaType == MediaType.Image;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 媒体类型 → 是否为"采集序列"（.ser）。给 Gallery photo tile 左上角徽章用。
/// v0.12：让 .ser 跟视频一样有自己的角标，避免与普通图片混淆。
/// </summary>
public class MediaTypeToCaptureConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MediaType mediaType)
        {
            return mediaType == MediaType.CaptureSequence;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
