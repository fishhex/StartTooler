using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace StartTooler.Services;

public interface IImageCacheService
{
    Task<Bitmap?> LoadImageAsync(string? path, CancellationToken ct = default);
}

public class ImageCacheService : IImageCacheService
{
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> s_cache = new();
    private readonly SemaphoreSlim _semaphore = new(Environment.ProcessorCount * 2);

    public async Task<Bitmap?> LoadImageAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        if (s_cache.TryGetValue(path, out var cachedTask))
        {
            return await cachedTask;
        }

        try
        {
            await _semaphore.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (s_cache.TryGetValue(path, out cachedTask))
            {
                return await cachedTask;
            }

            var task = Task.Run(() =>
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    return (Bitmap?)new Bitmap(stream);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ImageCacheService] 加载图片失败: {path}, {ex.Message}");
                    return null;
                }
            }, ct);

            s_cache.TryAdd(path, task);
            return await task;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public static void ClearCache()
    {
        foreach (var kvp in s_cache)
        {
            if (kvp.Value.IsCompletedSuccessfully && kvp.Value.Result is Bitmap bitmap)
            {
                bitmap.Dispose();
            }
        }
        s_cache.Clear();
    }

    /// <summary>
    /// v0.11 spec/10 §4.2.1: 统计当前缓存中的图片数和估算内存占用。
    /// 已完成且加载成功的 Bitmap 才有意义;正在加载中的 Task 跳过。
    /// </summary>
    public static CacheStats GetStats()
    {
        var bitmapCount = 0;
        long estimatedMemory = 0;

        foreach (var kvp in s_cache)
        {
            if (kvp.Value.IsCompletedSuccessfully && kvp.Value.Result is Bitmap bitmap)
            {
                bitmapCount++;
                // RGBA 4 bytes per pixel 估算（实际可能有 1/2 字节 alpha 或 RGB565 偏差，但数量级正确）
                estimatedMemory += (long)bitmap.PixelSize.Width
                                  * bitmap.PixelSize.Height
                                  * 4;
            }
        }

        return new CacheStats
        {
            CachedImageCount = bitmapCount,
            EstimatedMemoryBytes = estimatedMemory
        };
    }
}

/// <summary>
/// v0.11 spec/10 §4.2.1: 缩略图缓存统计快照。
/// </summary>
public record CacheStats
{
    public int CachedImageCount { get; init; }
    public long EstimatedMemoryBytes { get; init; }

    public string FormattedSize => EstimatedMemoryBytes switch
    {
        >= 100L << 20 => $"{EstimatedMemoryBytes / (1L << 20)} MB",
        >= 1L << 20   => $"{EstimatedMemoryBytes / (double)(1L << 20):F1} MB",
        _             => $"{EstimatedMemoryBytes / 1024} KB"
    };
}
