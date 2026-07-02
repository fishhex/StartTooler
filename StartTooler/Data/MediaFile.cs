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

    /// <summary>
    /// 本地文件是否存在（驱动 SyncStatus 徽章和 Image 渲染）。
    /// 由下载完成 / 本地扫描维护。
    /// </summary>
    [ObservableProperty]
    private bool _localExists = true;

    /// <summary>
    /// 缩略图缓存路径（绝对路径，存 DB 后跨会话有效）。
    /// 文件可能因缓存清理而丢失——这种情况下 FilePathToVisibilityConverter 会自动隐藏 Image。
    /// </summary>
    [ObservableProperty]
    private string? _thumbnailPath;

    public string? RemoteUrl { get; set; }
    public long? UploadedAt { get; set; }
    public long ScannedAt { get; set; }

    /// <summary>
    /// 记录首次创建时间。INSERT 时写入 UTC，ON CONFLICT DO UPDATE 时由 SQL "created_at = created_at" 保留原值。
    /// 详见 02-data-layer.md §11。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 记录最近一次更新时间。每次 UPDATE（扫描 / 上传完成 / 缩略图生成）由 SQL DEFAULT 或应用层显式刷新。
    /// </summary>
    public DateTime UpdatedAt { get; set; }

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
