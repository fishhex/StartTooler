using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StartTooler.Services;

/// <summary>
/// v0.11 spec/09 §6: 磁盘空间查询 + 批量下载预警。
/// macOS/Windows/Linux 跨平台（依赖 <see cref="DriveInfo.AvailableFreeSpace"/>）。
/// </summary>
public static class DiskSpaceService
{
    /// <summary>获取指定路径所在磁盘的可用空间（字节）。失败返回 -1。</summary>
    public static long GetAvailableFreeSpace(string path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return -1;
            var driveInfo = new DriveInfo(root);
            return driveInfo.AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 批量下载前检查空间。
    /// 返回 (Sufficient, Warning)：Sufficient=true 时 Warning 为 null；false 时 Warning 含详细文案。
    /// 触发告警阈值: 预计需要 > 90% 当前可用空间。
    /// </summary>
    public static (bool Sufficient, string? Warning) CheckBeforeDownload(
        string targetDir,
        IEnumerable<(string Key, long Size)> filesToDownload)
    {
        if (filesToDownload == null) return (true, null);

        long totalSize = 0;
        foreach (var (_, size) in filesToDownload) totalSize += size;
        if (totalSize == 0) return (true, null);

        var available = GetAvailableFreeSpace(targetDir);
        if (available < 0) return (true, null);  // 检测不到盘符就不拦

        // 预留 200MB 缓冲
        var requiredSize = totalSize + 200L * 1024 * 1024;

        if (requiredSize > available * 0.9)
        {
            var warning = $"磁盘空间不足！预计需要 {FormatBytes(totalSize)}，" +
                          $"剩余仅 {FormatBytes(available)}。" +
                          "是否继续？";
            return (false, warning);
        }

        return (true, null);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):F1} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
