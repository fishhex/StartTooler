namespace StartTooler.Services;

public class AppConfig
{
    public string Theme { get; set; } = "DeepSpace";

    /// <summary>
    /// ffmpeg 可执行文件绝对路径。空 = 走 PATH。
    /// </summary>
    public string? FFmpegPath { get; set; }

    /// <summary>
    /// ffprobe 可执行文件绝对路径。空 = 走 PATH。
    /// 跟 FFmpegPath 独立，允许两个二进制放在不同目录。
    /// </summary>
    public string? FFprobePath { get; set; }
}