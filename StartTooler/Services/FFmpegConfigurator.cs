using System;
using System.Diagnostics;
using System.IO;

namespace StartTooler.Services;

/// <summary>
/// 持有用户配置的 FFmpeg / FFprobe 路径，并提供二进制绝对路径解析。
///
/// 之前依赖 FFMpegCore 包，但 5.1.0 的 SnapshotAsync 强制用 PNG codec 写输出，
/// 还会改扩展名为 .png，导致视频缩略图全是 PNG 而不是 JPG。已彻底移除该包，
/// 改用 FfprobeRunner / FfmpegSnapshotRunner 直接 Process.Start 调命令行。
///
/// 调用点：
///   - App.OnFrameworkInitializationCompleted：启动时读 AppConfig 应用一次
///   - SettingsViewModel.Save：保存成功后立即应用，不需重启
///   - FfprobeRunner / FfmpegSnapshotRunner：通过 GetFFmpeg/FFprobeBinaryPath 取路径
/// </summary>
public static class FFmpegConfigurator
{
    private static string? _ffmpegPath;
    private static string? _ffprobePath;

    public static void Apply(string? ffmpegPath, string? ffprobePath)
    {
        _ffmpegPath = Normalize(ffmpegPath);
        _ffprobePath = Normalize(ffprobePath);

        Trace.WriteLine($"[FFmpegConfigurator] Apply: ffmpeg={_ffmpegPath ?? "(PATH)"} ffprobe={_ffprobePath ?? "(PATH)"}");

        ValidateBinary("ffmpeg", _ffmpegPath);
        ValidateBinary("ffprobe", _ffprobePath);
    }

    /// <summary>
    /// 取得 ffmpeg 可执行文件的绝对路径。没配置就从 PATH 里搜。
    /// </summary>
    public static string GetFFmpegBinaryPath()
    {
        if (!string.IsNullOrEmpty(_ffmpegPath)) return _ffmpegPath;
        return ResolveFromPath(OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
    }

    /// <summary>
    /// 取得 ffprobe 可执行文件的绝对路径。没配置就从 PATH 里搜。
    /// </summary>
    public static string GetFFprobeBinaryPath()
    {
        if (!string.IsNullOrEmpty(_ffprobePath)) return _ffprobePath;
        return ResolveFromPath(OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.Trim();
    }

    private static void ValidateBinary(string name, string? path)
    {
        if (path == null) return;  // PATH 模式不校验
        if (!File.Exists(path))
        {
            Trace.WriteLine($"[FFmpegConfigurator] WARN: {name} not found at {path} — {UsageHint(name)} will fail");
        }
    }

    private static string UsageHint(string name) => name switch
    {
        "ffmpeg" => "video snapshot",
        "ffprobe" => "video probe",
        _ => name
    };

    private static string ResolveFromPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return exeName;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // ignore malformed PATH entries
            }
        }
        return exeName;  // 找不到就让 Process.Start 自己报错
    }
}