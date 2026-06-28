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
        _thumbnailDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartTooler",
            "thumbnails");

        Directory.CreateDirectory(_thumbnailDir);
        Trace.WriteLine($"[ThumbnailService] dir={_thumbnailDir}");
    }

    public async Task<string?> GenerateThumbnailAsync(string sourcePath, string projectPath, CancellationToken ct = default)
    {
        Trace.WriteLine($"[ThumbnailService] Generate start: source={sourcePath}");
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            var isVideo = IsVideoFile(ext);
            Trace.WriteLine($"[ThumbnailService] ext={ext} isVideo={isVideo}");

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
            _ => false
        };
    }

    private static string GetPathHash(string path)
    {
        // 使用简单哈希确保文件名合法且唯一
        unchecked
        {
            var hash = 17;
            foreach (var c in path)
            {
                hash = hash * 31 + c;
            }
            return Math.Abs(hash).ToString("X8");
        }
    }
}