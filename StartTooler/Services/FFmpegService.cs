using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StartTooler.Helpers;

namespace StartTooler.Services;

/// <summary>
/// FFmpeg 服务类，用于提取视频文件的第一帧作为缩略图
/// </summary>
public class FFmpegService
{
    private readonly string _ffmpegPath;

    public FFmpegService()
    {
        // 尝试查找 FFmpeg 可执行文件
        _ffmpegPath = FindFFmpegPath();
    }

    /// <summary>
    /// 查找 FFmpeg 可执行文件路径
    /// </summary>
    private static string FindFFmpegPath()
    {
        // 在不同操作系统上查找 ffmpeg
        if (OperatingSystem.IsWindows())
        {
            // Windows: 检查常见路径和环境变量
            var paths = new[]
            {
                "ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin", "ffmpeg.exe")
            };

            foreach (var path in paths)
            {
                if (File.Exists(path))
                    return path;
            }

            // 检查 PATH 环境变量
            var envPath = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(envPath))
            {
                foreach (var dir in envPath.Split(Path.PathSeparator))
                {
                    var fullPath = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            // macOS/Linux: 检查常见路径
            var paths = new[]
            {
                "ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/bin/ffmpeg",
                "/opt/homebrew/bin/ffmpeg"  // Apple Silicon Mac
            };

            foreach (var path in paths)
            {
                if (File.Exists(path))
                    return path;
            }
        }

        return "ffmpeg"; // 默认假设在 PATH 中
    }

    /// <summary>
    /// 从视频文件中提取第一帧作为缩略图
    /// </summary>
    /// <param name="videoPath">视频文件的完整路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>生成的缩略图相对路径，如果失败则返回 null</returns>
    public async Task<string?> ExtractThumbnailAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
        {
            return null;
        }

        try
        {
            // 生成缩略图文件名
            var thumbnailRelativePath = PathHelper.GenerateThumbnailRelativePath(videoPath);
            var thumbnailFullPath = PathHelper.GetThumbnailFullPath(thumbnailRelativePath);

            // 构建 FFmpeg 命令参数
            // -i: 输入文件
            // -ss 00:00:01: 从第1秒开始（避免黑屏）
            // -vframes 1: 只提取1帧
            // -vf scale=320:-1: 缩放到宽度320px，高度自动保持比例
            // -y: 覆盖输出文件
            var arguments = $"-i \"{videoPath}\" -ss 00:00:01 -vframes 1 -vf \"scale=320:-1\" -y \"{thumbnailFullPath}\"";

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                return null;
            }

            // 异步等待进程完成
            await Task.Run(() =>
            {
                process.WaitForExit();
            }, cancellationToken);

            // 检查是否成功生成了缩略图
            if (File.Exists(thumbnailFullPath) && new FileInfo(thumbnailFullPath).Length > 0)
            {
                return thumbnailRelativePath;
            }

            return null;
        }
        catch (Exception ex)
        {
            // 记录错误但不中断程序
            Console.WriteLine($"提取缩略图失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 检查 FFmpeg 是否可用
    /// </summary>
    public async Task<bool> IsFFmpegAvailableAsync()
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                return false;
            }

            await Task.Run(() => process.WaitForExit());
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
