using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace StartTooler.Converters;

public class ThumbnailPathToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            try
            {
                // 检查文件是否存在
                if (System.IO.File.Exists(path))
                {
                    // 尝试从文件加载图片
                    return new Bitmap(path);
                }
                else
                {
                    // 如果文件不存在，返回 null
                    return null;
                }
            }
            catch (Exception ex)
            {
                // 记录异常信息以便调试
                System.Diagnostics.Debug.WriteLine($"Failed to load image from path: {path}, Error: {ex.Message}");
                // 如果加载失败，返回 null
                return null;
            }
        }
        
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
