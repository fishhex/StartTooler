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

    /// <summary>
    /// v0.12: 会话间隔阈值（小时）。SessionClusteringService 在两次拍摄间隔超过此时视为不同会话。
    /// 范围 1-24，默认 4。
    /// </summary>
    public int SessionIntervalHours { get; set; } = 4;

    /// <summary>
    /// v0.12: 高德地图 API Key。空 = EnvironmentService 自动回退到 Nominatim（免费，1 req/s 限速）。
    /// 用于逆地理编码（经纬度 → 地名）。
    /// </summary>
    public string? AmapApiKey { get; set; }
}