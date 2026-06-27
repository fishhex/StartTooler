namespace StartTooler.Services;

public class AppConfig
{
    public string Theme { get; set; } = "DeepSpace";

    /// <summary>
    /// ffmpeg 可执行文件绝对路径。空 = 走 PATH。
    /// ffprobe 必须在同一目录（FFMpegCore 按父目录找两个二进制）。
    /// </summary>
    public string? FFmpegPath { get; set; }
}
