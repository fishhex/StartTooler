using System.IO;
using FFMpegCore;

namespace StartTooler.Services;

/// <summary>
/// 把用户配置的 FFmpegPath 应用到 FFMpegCore 全局。
///
/// FFMpegCore 用 GlobalFFOptions.BinaryFolder 找 ffmpeg 和 ffprobe 两个二进制，
/// 且必须放同一目录。所以用户指定 ffmpeg 文件路径时，本类提取其父目录配置。
///
/// 调用点：
///   - App.OnFrameworkInitializationCompleted：启动时读 AppConfig.FFmpegPath 应用一次
///   - SettingsViewModel.Save：保存成功后立即应用，不需重启
/// </summary>
public static class FFmpegConfigurator
{
    public static void Apply(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            // 路径为空 = 还原成 FFMpegCore 默认（PATH 搜索）
            GlobalFFOptions.Configure(new FFOptions());
            return;
        }

        var dir = Path.GetDirectoryName(ffmpegPath);
        if (!string.IsNullOrEmpty(dir))
        {
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = dir });
        }
    }
}