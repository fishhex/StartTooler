using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartTooler.Data;

public enum MediaType
{
    Image,
    Video
}

/// <summary>
/// 上传状态机。IsUploaded/UploadedAt/RemoteUrl 是 DB 持久化字段（spec §17.1）；
/// UploadStatus/UploadError 是 UI 瞬时态，不入 DB，每次进 Gallery 从 upload_jobs 反推。
///
///   NotUploaded → Uploading → Uploaded     （happy path）
///   NotUploaded → Uploading → Failed       （上传失败）
///   Uploading   → Paused                  （取消或 app 崩溃，job 留底）
///   Paused      → Uploading → ...          （下次点上传自动续）
/// </summary>
public enum UploadStatus
{
    NotUploaded,
    Uploading,
    Uploaded,
    Failed,
    Paused,
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

    /// <summary>
    /// UI 用的瞬时上传状态。默认 NotUploaded；进 Gallery 时根据 upload_jobs 反推。
    /// </summary>
    [ObservableProperty]
    private UploadStatus _uploadStatus;

    /// <summary>
    /// UI 用的瞬时错误信息。UploadStatus==Failed 时显示。
    /// </summary>
    [ObservableProperty]
    private string? _uploadError;

    public DateTime? ShotAtDateTime => ShotAt.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(ShotAt.Value).DateTime
        : null;
}
