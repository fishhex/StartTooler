using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace StartTooler.Converters;

public class AsyncImageConverter : IValueConverter
{
    private static readonly ImageCache _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
        {
            return null;
        }

        // 先检查缓存
        if (ImageCache.TryGet(path, out var cached))
        {
            return cached;
        }

        // 异步加载图片
        _ = LoadImageAsync(path);
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static async Task LoadImageAsync(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            await using var stream = File.OpenRead(path);
            var bitmap = new Bitmap(stream);
            ImageCache.Set(path, bitmap);
            ImageLoaded?.Invoke(null, new ImageLoadedEventArgs(path, bitmap));
        }
        catch
        {
            // 加载失败，无操作
        }
    }

    public static event EventHandler<ImageLoadedEventArgs>? ImageLoaded;
}

public class ImageLoadedEventArgs : EventArgs
{
    public string Path { get; }
    public Bitmap Bitmap { get; }

    public ImageLoadedEventArgs(string path, Bitmap bitmap)
    {
        Path = path;
        Bitmap = bitmap;
    }
}

// 简单的图片缓存
public class ImageCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap> _cache = new();

    public static bool TryGet(string path, out Bitmap? bitmap)
    {
        return _cache.TryGetValue(path, out bitmap);
    }

    public static void Set(string path, Bitmap bitmap)
    {
        _cache.TryAdd(path, bitmap);
    }

    public static void Clear()
    {
        foreach (var kvp in _cache)
        {
            kvp.Value.Dispose();
        }
        _cache.Clear();
    }
}
