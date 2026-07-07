using System;
using System.Collections.Generic;
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

    // === v0.6 AI 打标字段（spec doc/12-ai-toolbar-buttons.md §3.1.1） ===

    /// <summary>
    /// AI 标签列表（DB 存 TEXT，JSON 数组序列化）。批量打标时整列表替换。
    /// UI 通过 HasTags 联动显示标签小条。
    /// </summary>
    [ObservableProperty]
    private List<string> _tags = new();

    /// <summary>
    /// v0.7: AI 质量评价标签列表（如 "欠曝" "噪点"）。DB 列 quality_tags，JSON 序列化。
    /// 与 Tags（主体标签）分开存储，左栏聚合时不参与（只走 Tags）。
    /// UI 通过 HasQualityTags 联动显示质量徽章条（暖红调）。
    /// </summary>
    [ObservableProperty]
    private List<string> _qualityTags = new();

    /// <summary>
    /// AI 评分 0-100，null 表示未打标。DB 列 score INTEGER NULL。
    /// UI 通过 HasScore 联动显示评分角标。
    /// </summary>
    [ObservableProperty]
    private int? _score;

    /// <summary>
    /// 最近一次成功打标的 UTC unix 毫秒时间戳，null 表示从未打标。
    /// DB 列 tagged_at INTEGER NULL。可用于「按打标时间筛选」(后续 phase)。
    /// </summary>
    [ObservableProperty]
    private long? _taggedAt;

    /// <summary>
    /// 打标失败原因：有值 = 卡片右下显示红色三角徽章 + hover tooltip 显示完整原因。
    /// 成功打标后由 BatchTag/TagSingle 清空。
    /// DB 列 tag_error TEXT NULL。
    /// </summary>
    [ObservableProperty]
    private string? _tagError;

    // === v0.8 软删除字段（spec doc/14-delete-and-trash.md §2.1 / §4） ===

    /// <summary>
    /// 软删除时间戳（unix ms），NULL = 正常文件，NOT NULL = 已移入垃圾筒。
    /// DB 列 deleted_at INTEGER NULL。
    /// Gallery 所有查询 WHERE deleted_at IS NULL 自动隐藏已删除文件。
    /// </summary>
    [ObservableProperty]
    private long? _deletedAt;

    // === v0.6 UI 便捷属性（XAML 绑定用） ===

    /// <summary>photo tile 评分角标 IsVisible 绑定。Score=null 时隐藏。</summary>
    public bool HasScore => Score.HasValue;

    /// <summary>photo tile 标签条 IsVisible 绑定。Tags 为空列表或 null 时隐藏。</summary>
    public bool HasTags => Tags is { Count: > 0 };

    /// <summary>v0.7: photo tile 质量标签条 IsVisible 绑定。QualityTags 为空列表或 null 时隐藏。</summary>
    public bool HasQualityTags => QualityTags is { Count: > 0 };

    /// <summary>v0.8: 软删除时间戳存在 = 已移入垃圾筒。UI 偶尔需要快速判断（暂未直接用，预留）。</summary>
    public bool HasDeleted => DeletedAt.HasValue;

    public DateTime? ShotAtDateTime => ShotAt.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(ShotAt.Value).DateTime
        : null;
}
