using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using StartTooler.Models;

namespace StartTooler.Services;

public class ThumbnailService
{
    private readonly string _thumbnailCacheDir;

    public ThumbnailService()
    {
        // 在用户临时目录中创建缩略图缓存文件夹
        var tempDir = Path.GetTempPath();
        _thumbnailCacheDir = Path.Combine(tempDir, "StartTooler_Thumbnails");
        
        if (!Directory.Exists(_thumbnailCacheDir))
        {
            Directory.CreateDirectory(_thumbnailCacheDir);
        }
    }

    /// <summary>
    /// 为媒体文件生成缩略图
    /// </summary>
    /// <param name="mediaFile">媒体文件对象</param>
    /// <returns>缩略图路径，如果失败则返回 null</returns>
    public async Task<string?> GenerateThumbnailAsync(MediaFile mediaFile)
    {
        if (mediaFile == null || string.IsNullOrEmpty(mediaFile.FilePath))
            return null;

        try
        {
            // 如果是图片，直接返回原路径
            if (mediaFile.FileType == "图片")
            {
                mediaFile.ThumbnailPath = mediaFile.FilePath;
                return mediaFile.FilePath;
            }

            // 如果是视频，使用 FFmpeg 生成首帧缩略图
            if (mediaFile.FileType == "视频")
            {
                return await GenerateVideoThumbnailAsync(mediaFile.FilePath);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 使用 FFmpeg 生成视频首帧缩略图
    /// </summary>
    private async Task<string?> GenerateVideoThumbnailAsync(string videoPath)
    {
        try
        {
            // 生成唯一的缩略图文件名（基于视频路径的哈希）
            var fileName = $"{GetFileHash(videoPath)}.jpg";
            var thumbnailPath = Path.Combine(_thumbnailCacheDir, fileName);

            // 如果缩略图已存在，直接返回
            if (File.Exists(thumbnailPath))
            {
                return thumbnailPath;
            }

            // 检查 FFmpeg 是否可用
            if (!IsFFmpegAvailable())
            {
                Console.WriteLine("FFmpeg 未安装或不在系统 PATH 中");
                return null;
            }

            // 构建 FFmpeg 命令
            // -i: 输入文件
            // -ss 00:00:01: 从第1秒开始提取（避免黑屏）
            // -vframes 1: 只提取1帧
            // -vf scale=320:-1: 缩放宽度为320px，高度自适应
            // -q:v 5: JPEG 质量（1-31，越小质量越高）
            var arguments = $"-i \"{videoPath}\" -ss 00:00:01 -vframes 1 -vf scale=320:-1 -q:v 5 -y \"{thumbnailPath}\"";

            var processInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                Console.WriteLine("无法启动 FFmpeg 进程");
                return null;
            }

            // 等待进程完成，最多等待30秒
            var completed = await Task.Run(() => process.WaitForExit(30000));
            
            if (!completed || process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                Console.WriteLine($"FFmpeg 执行失败: {error}");
                
                // 删除可能生成的不完整文件
                if (File.Exists(thumbnailPath))
                {
                    File.Delete(thumbnailPath);
                }
                
                return null;
            }

            // 验证缩略图是否成功生成
            if (File.Exists(thumbnailPath) && new FileInfo(thumbnailPath).Length > 0)
            {
                return thumbnailPath;
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"生成缩略图时出错: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 检查 FFmpeg 是否可用
    /// </summary>
    private bool IsFFmpegAvailable()
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
                return false;

            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 计算文件路径的哈希值，用于生成唯一的文件名
    /// </summary>
    private string GetFileHash(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(filePath);
        var hash = md5.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// 清理缩略图缓存
    /// </summary>
    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(_thumbnailCacheDir))
            {
                foreach (var file in Directory.GetFiles(_thumbnailCacheDir))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // 忽略清理错误
        }
    }
}
