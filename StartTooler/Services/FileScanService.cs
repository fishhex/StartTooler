using System;
using System.Collections.Generic;
using System.IO;
using StartTooler.Models;

namespace StartTooler.Services;

public class FileScanService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp", ".svg", ".ico"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".rmvb", ".rm"
    };

    public List<MediaFile> ScanDirectory(string directoryPath)
    {
        var mediaFiles = new List<MediaFile>();
        
        if (!Directory.Exists(directoryPath))
            return mediaFiles;

        try
        {
            var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
            
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                
                if (ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var mediaFile = new MediaFile
                        {
                            FilePath = file,
                            FileName = Path.GetFileName(file),
                            FileType = ImageExtensions.Contains(extension) ? "图片" : "视频",
                            ModifiedTime = fileInfo.LastWriteTime,
                            FileSize = fileInfo.Length
                        };
                        
                        mediaFiles.Add(mediaFile);
                    }
                    catch (Exception)
                    {
                        // 跳过无法访问的文件
                        continue;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 处理权限不足的情况
        }
        catch (Exception)
        {
            // 处理其他异常
        }

        // 按修改时间排序（最新的在前）
        mediaFiles.Sort((a, b) => b.ModifiedTime.CompareTo(a.ModifiedTime));
        
        return mediaFiles;
    }
}
