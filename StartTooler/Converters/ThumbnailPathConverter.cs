using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using StartTooler.Helpers;

namespace StartTooler.Converters;

/// <summary>
/// 缩略图路径转换器，将相对路径转换为完整的 BitmapImage
/// </summary>
public class ThumbnailPathConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string relativePath && !string.IsNullOrEmpty(relativePath))
        {
            var fullPath = PathHelper.GetThumbnailFullPath(relativePath);
            
            if (File.Exists(fullPath))
            {
                try
                {
                    return new Bitmap(fullPath);
                }
                catch
                {
                    return null;
                }
            }
        }
        
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 不支持反向转换
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
/// 字节转 MB 转换器
/// </summary>
public class BytesToMBConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return bytes / (1024.0 * 1024.0);
        }
        return 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 不支持反向转换
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
