using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFMpegCore;
using FFMpegCore.Enums;
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
    }

    public async Task<string?> GenerateThumbnailAsync(string sourcePath, string projectPath, CancellationToken ct = default)
    {
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            var isVideo = IsVideoFile(ext);

            // 生成缩略图文件名：使用路径的哈希值确保唯一性
            var relativePath = Path.GetRelativePath(projectPath, sourcePath);
            var hash = GetPathHash(relativePath);
            var thumbnailPath = Path.Combine(_thumbnailDir, $"{hash}.jpg");

            // 如果缩略图已存在，直接返回
            if (File.Exists(thumbnailPath))
            {
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

            return thumbnailPath;
        }
        catch (Exception)
        {
            // 缩略图生成失败时返回 null
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
        try
        {
            // 使用 FFMpegCore 提取视频第一帧
            var mediaInfo = await FFProbe.AnalyseAsync(sourcePath, cancellationToken: ct);
            var duration = mediaInfo.Duration.TotalSeconds;

            // 从 5% 位置取帧（避免黑屏）
            var grabPosition = Math.Max(1, duration * 0.05);

            await FFMpeg.SnapshotAsync(sourcePath, thumbnailPath,
                new System.Drawing.Size(ThumbnailWidth, ThumbnailHeight),
                captureTime: TimeSpan.FromSeconds(grabPosition));
        }
        catch
        {
            // FFMpeg 失败时尝试使用 SkiaSharp 加载（可能失败）
            await GenerateImageThumbnailAsync(sourcePath, thumbnailPath, ct);
        }
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
