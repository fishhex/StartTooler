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
}
