using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace StartTooler.Services;

/// <summary>
/// 轻量级图片原图宽高探测：基于 SkiaSharp <see cref="SKCodec"/> 只读 header，
/// 不解码像素，~1-5ms/次。
///
/// 灯箱场景用：双击缩略图时需要原图原始分辨率来撑开 ScrollViewer 滚动区域，
/// 让缩放后能正确显示滚动条。原图分辨率 ≠ 缩略图分辨率，必须另读。
///
/// 缓存策略：in-memory ConcurrentDictionary 按路径缓存 MediaFile 全生命周期的探测结果。
/// 进程级缓存够用（一次 Preview 通常在几十秒内翻完一张）；不持久化到磁盘，避免
/// 文件移动 / 删除时缓存失效问题（缓存 miss 时重读，失败则返回 null，UI 优雅降级）。
/// </summary>
public static class ImageDimensionProbe
{
    private record struct Dim(int Width, int Height);

    private static readonly ConcurrentDictionary<string, Dim?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 探测指定图片的原图宽高。
    /// </summary>
    /// <returns>成功返回 (Width, Height)；文件不存在 / 解码失败 / 非图片格式返回 null。</returns>
    public static (int Width, int Height)? Probe(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        // 缓存命中（含 null 负缓存）
        if (_cache.TryGetValue(filePath, out var cached))
        {
            return cached.HasValue ? (cached.Value.Width, cached.Value.Height) : null;
        }

        (int Width, int Height)? result = TryProbeCore(filePath);
        _cache[filePath] = result.HasValue ? new Dim(result.Value.Width, result.Value.Height) : null;
        return result;
    }

    private static (int Width, int Height)? TryProbeCore(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Trace.WriteLine($"[ImageDimensionProbe] file not found: {filePath}");
                return null;
            }

            // SKCodec.Open 只读 header，不解码像素
            using var codec = SKCodec.Create(filePath);
            if (codec == null)
            {
                Trace.WriteLine($"[ImageDimensionProbe] SKCodec.Create returned null: {filePath}");
                return null;
            }

            var info = codec.Info;
            Trace.WriteLine($"[ImageDimensionProbe] {Path.GetFileName(filePath)}: {info.Width}x{info.Height}");
            return (info.Width, info.Height);
        }
        catch (Exception ex)
        {
            // SKCodec 抛 "Could not load image" / "Unknown image format" 等；不抛给上层
            Trace.WriteLine($"[ImageDimensionProbe] probe failed: {filePath} -> {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 清理缓存（用于测试或外部需要强制刷新时）。
    /// </summary>
    public static void ClearCache() => _cache.Clear();
}