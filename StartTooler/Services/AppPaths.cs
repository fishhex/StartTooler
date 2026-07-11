using System;
using System.IO;

namespace StartTooler.Services;

/// <summary>
/// 应用数据目录统一入口。各模块通过此类获取路径，避免重复计算和盘符不一致。
/// </summary>
public static class AppPaths
{
    /// <summary>配置数据库路径（config.db，用户设置、上传凭据等）。</summary>
    public static string ConfigDbPath => LazyConfigDbPath.Value;

    /// <summary>媒体数据库路径（media.db，媒体文件索引）。</summary>
    public static string MediaDbPath => LazyMediaDbPath.Value;

    /// <summary>缩略图缓存目录。</summary>
    public static string ThumbnailDir => LazyThumbnailDir.Value;

    /// <summary>用户配置目录（ApplicationData/StartTooler）。</summary>
    public static string ConfigDir => LazyConfigDir.Value;

    /// <summary>本地数据目录（LocalApplicationData/StartTooler）。</summary>
    public static string LocalDataDir => LazyLocalDataDir.Value;

    private static readonly Lazy<string> LazyConfigDir = new(() =>
        EnsureDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StartTooler")));

    private static readonly Lazy<string> LazyLocalDataDir = new(() =>
        EnsureDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartTooler")));

    private static readonly Lazy<string> LazyConfigDbPath = new(() =>
        Path.Combine(LazyConfigDir.Value, "config.db"));

    private static readonly Lazy<string> LazyMediaDbPath = new(() =>
        Path.Combine(LazyLocalDataDir.Value, "media.db"));

    private static readonly Lazy<string> LazyThumbnailDir = new(() =>
        EnsureDir(Path.Combine(LazyLocalDataDir.Value, "thumbnails")));

    private static string EnsureDir(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
        return path;
    }
}
