using System;

namespace StartTooler.Models;

public class MediaFile
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty; // "Image" or "Video"
    public DateTime ModifiedTime { get; set; }
    public long FileSize { get; set; }
    public string? ThumbnailPath { get; set; } // 缩略图路径
    
    public string FormattedFileSize
    {
        get
        {
            if (FileSize < 1024)
                return $"{FileSize} B";
            else if (FileSize < 1024 * 1024)
                return $"{FileSize / 1024.0:F2} KB";
            else if (FileSize < 1024 * 1024 * 1024)
                return $"{FileSize / (1024.0 * 1024):F2} MB";
            else
                return $"{FileSize / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
