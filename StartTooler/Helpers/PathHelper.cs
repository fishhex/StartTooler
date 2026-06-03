using System;
using System.IO;

namespace StartTooler.Helpers;

/// <summary>
/// 路径辅助类，用于管理应用数据存储目录
/// </summary>
public static class PathHelper
{
    private static readonly string AppDataDirectory;
    private static readonly string ThumbnailsDirectory;
    private static readonly string DatabasePath;

    static PathHelper()
    {
        // 获取跨平台的应用数据目录
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AppDataDirectory = Path.Combine(localAppData, "StartTooler");
        ThumbnailsDirectory = Path.Combine(AppDataDirectory, "Thumbnails");
        DatabasePath = Path.Combine(AppDataDirectory, "starttooler.db");

        // 确保目录存在
        EnsureDirectoriesExist();
    }

    /// <summary>
    /// 获取应用数据根目录
    /// </summary>
    public static string GetAppDataDirectory() => AppDataDirectory;

    /// <summary>
    /// 获取缩略图存储目录
    /// </summary>
    public static string GetThumbnailsDirectory() => ThumbnailsDirectory;

    /// <summary>
    /// 获取数据库文件路径
    /// </summary>
    public static string GetDatabasePath() => DatabasePath;

    /// <summary>
    /// 获取缩略图的完整路径（根据相对路径）
    /// </summary>
    /// <param name="relativePath">相对于 Thumbnails 目录的路径</param>
    /// <returns>缩略图的完整路径</returns>
    public static string GetThumbnailFullPath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return string.Empty;

        return Path.Combine(ThumbnailsDirectory, relativePath);
    }

    /// <summary>
    /// 生成缩略图的相对路径
    /// </summary>
    /// <param name="originalFilePath">原始视频文件路径</param>
    /// <returns>缩略图的相对路径</returns>
    public static string GenerateThumbnailRelativePath(string originalFilePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(originalFilePath);
        var extension = Path.GetExtension(originalFilePath);
        var uniqueName = $"{fileName}_{Guid.NewGuid():N}.jpg";
        return uniqueName;
    }

    /// <summary>
    /// 确保所有必要的目录存在
    /// </summary>
    private static void EnsureDirectoriesExist()
    {
        if (!Directory.Exists(AppDataDirectory))
            Directory.CreateDirectory(AppDataDirectory);

        if (!Directory.Exists(ThumbnailsDirectory))
            Directory.CreateDirectory(ThumbnailsDirectory);
    }
}
