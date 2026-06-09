using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartTooler.Models;

public partial class MediaFile : ObservableObject
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty; // "Image" or "Video"
    public DateTime ModifiedTime { get; set; }
    public long FileSize { get; set; }
    public string? ThumbnailPath { get; set; } // 缩略图路径
    public long PerceptualHash { get; set; }
    public string? GroupId { get; set; }

    public bool HasGroupId => !string.IsNullOrEmpty(GroupId);

    public bool HasMultiple { get; set; }

    [ObservableProperty]
    private bool _isSelected;
    
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

    public override bool Equals(object? obj)
    {
        return obj is MediaFile other && string.Equals(FilePath, other.FilePath, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(FilePath);
    }
}
