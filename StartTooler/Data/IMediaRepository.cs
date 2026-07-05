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
    /// </summary>
    Task UpdateTagAsync(long fileId, IEnumerable<string> tags, int score, long taggedAt, string? tagError, CancellationToken ct = default);

    /// <summary>
    /// 获取标签分组（标签名 → 文件数），按数量降序。UI 左栏"标签"tab 用（本期不实现 UI，方法先准备好）。
    /// </summary>
    Task<IReadOnlyList<(string Tag, int Count)>> GetTagGroupsAsync(string projectPath, CancellationToken ct = default);

    /// <summary>
    /// 按标签筛选文件。用 LIKE '%"标签"%' 匹配 JSON 数组里的标签项（假设标签名不含双引号）。
    /// </summary>
    Task<IReadOnlyList<MediaFile>> GetByTagAsync(string projectPath, string tag, CancellationToken ct = default);
}

public class ScanResult
{
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Failed { get; set; }
    public int NewFiles { get; set; }
    public int UpdatedFiles { get; set; }
}