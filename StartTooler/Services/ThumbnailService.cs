using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace StartTooler.Services;

public interface IThumbnailService
{
    Task<string?> GenerateThumbnailAsync(string sourcePath, string projectPath, CancellationToken ct = default);
}

public class ThumbnailService : IThumbnailService
{
    private readonly string _thumbnailDir;
    private const int ThumbnailWidth = 320;
    private const int ThumbnailHeight = 240;

    public ThumbnailService()
    {
        _thumbnailDir = AppPaths.ThumbnailDir;
        Trace.WriteLine($"[ThumbnailService] dir={_thumbnailDir}");
    }

    public async Task<string?> GenerateThumbnailAsync(string sourcePath, string projectPath, CancellationToken ct = default)
    {
        Trace.WriteLine($"[ThumbnailService] Generate start: source={sourcePath}");
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            var isVideo = IsVideoFile(ext);
            var isCapture = IsCaptureFile(ext);
            Trace.WriteLine($"[ThumbnailService] ext={ext} isVideo={isVideo} isCapture={isCapture}");

            // 生成缩略图文件名：使用路径的哈希值确保唯一性
            var relativePath = Path.GetRelativePath(projectPath, sourcePath);
            var hash = GetPathHash(relativePath);
            var thumbnailPath = Path.Combine(_thumbnailDir, $"{hash}.jpg");

            // 如果缩略图已存在，直接返回
            if (File.Exists(thumbnailPath))
            {
                Trace.WriteLine($"[ThumbnailService] cache hit: {thumbnailPath}");
                return thumbnailPath;
            }

            if (isVideo)
            {
                await GenerateVideoThumbnailAsync(sourcePath, thumbnailPath, ct);
            }
            else if (isCapture)
            {
                // SER / 采集序列：不生成缩略图。
                // 原因：
                //   1. .ser 单帧是 16-bit Bayer/灰度，未做真正 debayer，拉伸首帧辨识度低；
                //   2. 18 GB 大文件 IO 浪费：读 160 字节头 + 1920×1080×2 字节首帧 + JPG 编码；
                //   3. 用户在 Lightbox 里能看完整元数据面板，Gallery 卡有蓝色相机角标即足够识别。
                // 返回 null → UI FilePathToBitmapConverter 走 fallback → photo tile 显示占位图。
                Trace.WriteLine($"[ThumbnailService] .ser 跳过缩略图生成: {sourcePath}");
                return null;
            }
            else
            {
                await GenerateImageThumbnailAsync(sourcePath, thumbnailPath, ct);
            }

            Trace.WriteLine($"[ThumbnailService] Generate ok: {thumbnailPath}");
            return thumbnailPath;
        }
        catch (Exception ex)
        {
            // 不要再吞异常不写日志了——上次坑就坑在这
            Trace.WriteLine($"[ThumbnailService] Generate FAILED: source={sourcePath} ex={ex}");
            return null;
        }
    }

    private async Task GenerateImageThumbnailAsync(string sourcePath, string thumbnailPath, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var inputStream = File.OpenRead(sourcePath);
            using var original = SKBitmap.Decode(inputStream);
            if (original == null) return;

            // 计算缩放比例，保持宽高比
            var scale = Math.Min(
                (float)ThumbnailWidth / original.Width,
                (float)ThumbnailHeight / original.Height);

            var newWidth = (int)(original.Width * scale);
            var newHeight = (int)(original.Height * scale);

            using var resized = original.Resize(
                new SKImageInfo(newWidth, newHeight),
                SKFilterQuality.High);

            if (resized == null) return;

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            using var outputStream = File.OpenWrite(thumbnailPath);
            data.SaveTo(outputStream);
        }, ct);
    }

    private async Task GenerateVideoThumbnailAsync(string sourcePath, string thumbnailPath, CancellationToken ct)
    {
        Trace.WriteLine($"[ThumbnailService] ============================================");
        Trace.WriteLine($"[ThumbnailService] Video thumbnail generation (direct CLI)");
        Trace.WriteLine($"[ThumbnailService]   input:  {sourcePath}");

        try
        {
            if (File.Exists(sourcePath))
            {
                var size = new FileInfo(sourcePath).Length;
                Trace.WriteLine($"[ThumbnailService]   input exists, size={size} bytes");
            }
            else
            {
                Trace.WriteLine($"[ThumbnailService]   input NOT FOUND on disk!");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ThumbnailService]   input stat FAILED: {ex.Message}");
        }

        Trace.WriteLine($"[ThumbnailService]   ffprobe binary: {FFmpegConfigurator.GetFFprobeBinaryPath()}");
        Trace.WriteLine($"[ThumbnailService]   ffmpeg binary:  {FFmpegConfigurator.GetFFmpegBinaryPath()}");

        // ========== Step 1: ffprobe 解析媒体信息 ==========
        Trace.WriteLine($"[ThumbnailService] step 1/3: FfprobeRunner.ProbeAsync");
        VideoProbeResult? mediaInfo = null;
        try
        {
            mediaInfo = await FfprobeRunner.ProbeAsync(sourcePath, ct);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ThumbnailService] step 1/3 FAILED: {ex.GetType().Name}: {ex.Message}");
            Trace.WriteLine($"[ThumbnailService]   hint: 检查「设置 → 通用 → FFprobe 路径」是否配置正确");
            // ffprobe 失败时尝试用 SkiaSharp 加载（对视频几乎肯定失败，但写日志能看到尝试过）
            await GenerateImageThumbnailAsync(sourcePath, thumbnailPath, ct);
            return;
        }

        if (mediaInfo == null)
        {
            Trace.WriteLine($"[ThumbnailService] step 1/3 FAILED: ffprobe returned no usable info (no video stream?)");
            await GenerateImageThumbnailAsync(sourcePath, thumbnailPath, ct);
            return;
        }

        // ========== Step 2: 解析媒体信息 ==========
        var duration = mediaInfo.Duration.TotalSeconds;
        Trace.WriteLine($"[ThumbnailService] step 2/3: media info parsed");
        Trace.WriteLine($"[ThumbnailService]   duration:  {duration:F2}s");
        Trace.WriteLine($"[ThumbnailService]   video:     {mediaInfo.Width}x{mediaInfo.Height} @ {mediaInfo.FrameRate:F2}fps codec={mediaInfo.Codec}");
        Trace.WriteLine($"[ThumbnailService]   frame selection: thumbnail filter (auto-picks most energetic frame, ignores rawvideo seek issues)");

        // ========== Step 3: 调 ffmpeg 抓快照 ==========
        Trace.WriteLine($"[ThumbnailService] step 3/3: FfmpegSnapshotRunner.SnapshotAsync");
        Trace.WriteLine($"[ThumbnailService]   output:    {thumbnailPath}");
        Trace.WriteLine($"[ThumbnailService]   size:      {ThumbnailWidth}x{ThumbnailHeight}");

        try
        {
            var exists = await FfmpegSnapshotRunner.SnapshotAsync(
                sourcePath,
                thumbnailPath,
                TimeSpan.FromSeconds(duration * 0.05),  // 历史保留参数，未实际使用
                ThumbnailWidth,
                ThumbnailHeight,
                ct);

            if (exists && File.Exists(thumbnailPath))
            {
                var fileSize = new FileInfo(thumbnailPath).Length;
                Trace.WriteLine($"[ThumbnailService]   output file exists, size={fileSize} bytes");
                if (fileSize == 0)
                {
                    Trace.WriteLine($"[ThumbnailService]   WARN: output file is 0 bytes!");
                }
            }
            else
            {
                Trace.WriteLine($"[ThumbnailService]   WARN: runner reported success but output file NOT created");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ThumbnailService] step 3/3 FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;  // 让外层 GenerateThumbnailAsync 记到
        }

        Trace.WriteLine($"[ThumbnailService] Video thumbnail generation done");
        Trace.WriteLine($"[ThumbnailService] ============================================");
    }

    private static bool IsVideoFile(string extension)
    {
        return extension switch
        {
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" or ".m4v" or ".mpg" or ".mpeg" => true,
            _ => false,
        };
    }

    private static bool IsCaptureFile(string extension)
    {
        return extension switch
        {
            ".ser" => true,
            _ => false,
        };
    }

    /// <summary>
    /// SER 缩略图生成：解析 178 字节头 + 读首帧 + SkiaSharp 拉伸到 ThumbnailWidth/Height。
    ///
    /// 实现细节：
    /// - 失败时不抛异常（SerReader 已 try/catch），返回 null 让上层继续。
    /// - 单帧解码失败：写入一个 1×1 占位图避免后续重试。但因为外层缓存命中在文件存在时
    ///   提前 return，所以这里"失败"实际意味着源文件异常，缓存目录不写入。
    /// </summary>
    private async Task GenerateSerThumbnailAsync(string sourcePath, string thumbnailPath, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var header = SerReader.ReadHeader(sourcePath);
            if (header == null)
            {
                Trace.WriteLine($"[ThumbnailService] SER header parse failed: {sourcePath}");
                return;
            }

            var rgb = SerReader.ReadFirstFrameRgb24(sourcePath, header);
            if (rgb == null || rgb.Length != header.Width * header.Height * 3)
            {
                Trace.WriteLine($"[ThumbnailService] SER first frame decode failed: {sourcePath} ({header.Width}x{header.Height} bpp={header.BitsPerPixel} color={header.ColorMode})");
                return;
            }

            // 把 RGB24 字节数组封装成 SKBitmap
            var info = new SKImageInfo(header.Width, header.Height, SKColorType.Rgb888x, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(rgb, 0, bitmap.GetPixels(), rgb.Length);

            // 计算缩放比例，保持宽高比
            var scale = Math.Min(
                (float)ThumbnailWidth / header.Width,
                (float)ThumbnailHeight / header.Height);
            var newWidth = Math.Max(1, (int)(header.Width * scale));
            var newHeight = Math.Max(1, (int)(header.Height * scale));

            using var resized = bitmap.Resize(
                new SKImageInfo(newWidth, newHeight),
                SKFilterQuality.High);
            if (resized == null) return;

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            using var outputStream = File.OpenWrite(thumbnailPath);
            data.SaveTo(outputStream);

            Trace.WriteLine($"[ThumbnailService] SER thumbnail ok: {sourcePath} ({header.Width}x{header.Height} → {newWidth}x{newHeight})");
        }, ct);
    }

    private static string GetPathHash(string path)
    {
        // 使用简单哈希确保文件名合法且唯一。
        // 用 uint 避免 Math.Abs(int.MinValue) 仍返回负数的边界 bug。
        unchecked
        {
            uint hash = 17;
            foreach (var c in path)
            {
                hash = hash * 31 + c;
            }
            return hash.ToString("X8");
        }
    }
}