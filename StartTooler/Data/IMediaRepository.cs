using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.Data;

public interface IMediaRepository
{
    Task<IReadOnlyList<DateCount>> GetDateGroupsAsync(string projectPath, CancellationToken ct = default);
    Task<IReadOnlyList<MediaFile>> GetByDateAsync(string projectPath, DateTime date, SortMode sortMode = SortMode.TimeDesc, CancellationToken ct = default);
    Task<ScanResult> ScanDirectoryAsync(string projectPath, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);
    Task GenerateThumbnailsAsync(string projectPath, IThumbnailService thumbnailService, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// 上传完成后写回 media_files 表的 is_uploaded / uploaded_at / remote_url。
    /// </summary>
    Task UpdateUploadStateAsync(long fileId, bool isUploaded, long? uploadedAt, string? remoteUrl, CancellationToken ct = default);

    // === v0.6 AI 打标方法（spec doc/12-ai-toolbar-buttons.md §3.1.2 + §3.1.3） ===

    /// <summary>
    /// 保存 AI 打标结果（成功或失败）。成功时 tagError 传 null；失败时 tagError 传原因，tags/score 写空值。
    /// v0.7 加 qualityTags 参数：质量评价标签独立写入 quality_tags 列。
    /// </summary>
    Task UpdateTagAsync(long fileId, IEnumerable<string> tags, IEnumerable<string> qualityTags, int score, long taggedAt, string? tagError, CancellationToken ct = default);

    /// <summary>
    /// 获取标签分组（标签名 → 文件数），按数量降序。UI 左栏"标签"tab 用（v0.6.1 patch 已接 UI）。
    /// </summary>
    Task<IReadOnlyList<TagGroupItem>> GetTagGroupsAsync(string projectPath, CancellationToken ct = default);

    /// <summary>
    /// 按标签筛选文件 + 按 SortMode 排序。
    /// 用 LIKE '%"标签"%' 匹配 JSON 数组里的标签项（假设标签名不含双引号）。
    /// v0.6.1 加 SortMode 参数：切「评分↓」时 tag 视图也按评分排序。
    /// </summary>
    Task<IReadOnlyList<MediaFile>> GetByTagAsync(string projectPath, string tag, SortMode sortMode = SortMode.TimeDesc, CancellationToken ct = default);
}

public class ScanResult
{
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Failed { get; set; }
    public int NewFiles { get; set; }
    public int UpdatedFiles { get; set; }
}