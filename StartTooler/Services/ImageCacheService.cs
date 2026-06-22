using System;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();
    private static readonly SemaphoreSlim _semaphore = new(Environment.ProcessorCount * 2);

    public async Task<Bitmap?> LoadImageAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        // 检查缓存
        if (_cache.TryGetValue(path, out var cachedTask))
        {
            return await cachedTask;
        }

        // 限制并发数量
        await _semaphore.WaitAsync(ct);

        try
        {
            // 再次检查缓存（可能其他线程刚添加）
            if (_cache.TryGetValue(path, out cachedTask))
            {
                return await cachedTask;
            }

            // 异步加载图片
            var task = Task.Run(async () =>
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    return await Task.FromResult(new Bitmap(stream));
                }
                catch
                {
                    return null;
                }
            }, ct);

            _cache.TryAdd(path, task);
            return await task;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public static void ClearCache()
    {
        foreach (var kvp in _cache)
        {
            if (kvp.Value.IsCompleted && kvp.Value.Result is Bitmap bitmap)
            {
                bitmap.Dispose();
            }
        }
        _cache.Clear();
    }
}
