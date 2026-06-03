using System;
using System.IO;

namespace StartTooler.Helpers;

public static class PathHelper
{
    private static readonly string AppDataPath;
    private static readonly string DatabasePath;
    private static readonly string ThumbnailsPath;

    static PathHelper()
    {
        // 获取应用程序数据目录（跨平台兼容）
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AppDataPath = Path.Combine(localAppData, "StartTooler");

        // 确保目录存在
        if (!Directory.Exists(AppDataPath))
        {
            Directory.CreateDirectory(AppDataPath);
        }

        // 数据库文件路径
        DatabasePath = Path.Combine(AppDataPath, "starttooler.db");

        // 缩略图缓存目录
        ThumbnailsPath = Path.Combine(AppDataPath, "Thumbnails");
        if (!Directory.Exists(ThumbnailsPath))
        {
            Directory.CreateDirectory(ThumbnailsPath);
        }
    }

    /// <summary>
    /// 获取数据库文件路径
    /// </summary>
    public static string GetDatabasePath()
    {
        return DatabasePath;
    }

    /// <summary>
    /// 获取缩略图缓存目录
    /// </summary>
    public static string GetThumbnailsPath()
    {
        return ThumbnailsPath;
    }

    /// <summary>
    /// 获取应用程序数据目录
    /// </summary>
    public static string GetAppDataPath()
    {
        return AppDataPath;
    }
}
