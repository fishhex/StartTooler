using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartTooler.Data;

public enum MediaType
{
    Image,
    Video
}

public partial class MediaFile : ObservableObject
{
    public long Id { get; set; }
    public string ProjectPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public MediaType MediaType { get; set; }
    public long FileSize { get; set; }
    public long LastModified { get; set; }
    public long? ShotAt { get; set; }
    public bool IsUploaded { get; set; }
    public bool LocalExists { get; set; } = true;
    public string? ThumbnailPath { get; set; }
    public string? RemoteUrl { get; set; }
    public long? UploadedAt { get; set; }
    public long ScannedAt { get; set; }

    /// <summary>
    /// UI 多选模式下的选中状态。由 GalleryViewModel.SelectedFiles 同步控制。
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    public DateTime? ShotAtDateTime => ShotAt.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(ShotAt.Value).DateTime
        : null;
}
