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

    /// <summary>
    /// 按 shot_at 时间范围查询文件（v0.11 快捷时间刷选：今天/本周/本月/今年）。
    /// 范围半开区间 [startTime, endTime)，按 SortMode 排序。
    /// </summary>
    Task<IReadOnlyList<MediaFile>> GetByTimeRangeAsync(string projectPath, DateTimeOffset startTime, DateTimeOffset endTime, SortMode sortMode = SortMode.TimeDesc, CancellationToken ct = default);

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
    /// 手动编辑主体标签专用（spec doc/15-manual-tag-edit.md §2.2 / §8）：
    /// 只动 tags / tagged_at / tag_error（清空），保留 score 和 quality_tags 原值。
    /// 与 UpdateTagAsync 的关键区别：UpdateTagAsync 会把 score 列写 0，对未打标文件（Score=null）会
    /// 导致 photo tile 误显示「评分 0」角标。手动编辑场景不动 score。
    /// </summary>
    Task UpdateTagsOnlyAsync(long fileId, IEnumerable<string> tags, long taggedAt, CancellationToken ct = default);

    /// <summary>
    /// 获取标签分组（标签名 → 文件数），按数量降序。UI 左栏"标签"tab 用（v0.6.1 patch 已接 UI）。
    /// </summary>
    Task<IReadOnlyList<TagGroupItem>> GetTagGroupsAsync(string projectPath, CancellationToken ct = default);

    // === v0.11: 统计仪表盘查询（spec/19 §6.1）===

    Task<DashboardKpi> GetDashboardKpiAsync(string projectPath, CancellationToken ct = default);
    Task<DashboardKpi> GetDashboardKpiAsync(string projectPath, DashboardPeriod period, CancellationToken ct = default);
    Task<IReadOnlyList<HeatmapDay>> GetDashboardHeatmapAsync(string projectPath, int year, CancellationToken ct = default);
    Task<IReadOnlyList<HeatmapDay>> GetDashboardHeatmapAsync(string projectPath, DashboardPeriod period, CancellationToken ct = default);
    Task<IReadOnlyList<MonthStat>> GetDashboardMonthlyStatsAsync(string projectPath, int year, CancellationToken ct = default);
    Task<IReadOnlyList<PeriodStat>> GetDashboardPeriodStatsAsync(string projectPath, DashboardPeriod period, CancellationToken ct = default);
    Task<IReadOnlyList<TagRank>> GetDashboardTagRankingAsync(string projectPath, CancellationToken ct = default);
    Task<IReadOnlyList<TagRank>> GetDashboardTagRankingAsync(string projectPath, DashboardPeriod period, CancellationToken ct = default);
    Task<IReadOnlyList<FocalRangeStat>> GetDashboardFocalDistributionAsync(string projectPath, CancellationToken ct = default);
    Task<IReadOnlyList<FocalRangeStat>> GetDashboardFocalDistributionAsync(string projectPath, DashboardPeriod period, CancellationToken ct = default);
    Task<IReadOnlyList<IsoStat>> GetDashboardIsoDistributionAsync(string projectPath, CancellationToken ct = default);
    Task<IReadOnlyList<IsoStat>> GetDashboardIsoDistributionAsync(string projectPath, DashboardPeriod period, CancellationToken ct = default);
    Task<IReadOnlyList<ExposureStat>> GetDashboardExposureDistributionAsync(string projectPath, CancellationToken ct = default);
    Task<IReadOnlyList<ExposureStat>> GetDashboardExposureDistributionAsync(string projectPath, DashboardPeriod period, CancellationToken ct = default);

    /// <summary>
    /// 获取项目下照片的最大 shot_at 年份，用于仪表盘默认选中最新数据年份。
    /// 无数据时返回 null。
    /// </summary>
    Task<int?> GetLatestPhotoYearAsync(string projectPath, CancellationToken ct = default);

    /// <summary>
    /// 获取指定年份内照片的最新 shot_at 日期，用于季度/月份视图默认选中最近有数据的周期。
    /// 无数据时返回 null。
    /// </summary>
    Task<DateTime?> GetLatestPhotoDateAsync(string projectPath, int year, CancellationToken ct = default);

    /// <summary>
    /// 按标签筛选文件 + 按 SortMode 排序。
    /// v0.11: 先通过 tag 表把 name 解析成 id，再匹配 media_files.tags 中的 id 数组。
    /// v0.8 加 deleted_at IS NULL 过滤：已移入垃圾筒的文件不出现在 Gallery。
    /// </summary>
    Task<IReadOnlyList<MediaFile>> GetByTagAsync(string projectPath, string tag, SortMode sortMode = SortMode.TimeDesc, CancellationToken ct = default);

    // === v0.11 标签字典管理（方案 B：media_files.tags 存 tag id 数组）===

    /// <summary>
    /// 获取项目下所有标签（id + name）。左侧标签面板右键菜单等场景用。
    /// </summary>
    Task<IReadOnlyList<Tag>> GetTagsAsync(string projectPath, CancellationToken ct = default);

    /// <summary>
    /// 根据名称获取或创建标签。写 tags 列前统一走这里，保证 name 与 id 映射存在。
    /// </summary>
    Task<Tag> EnsureTagAsync(string projectPath, string name, CancellationToken ct = default);

    /// <summary>
    /// 重命名标签：把所有文件中该 name 的 tag id 替换为目标 name 的 tag id（目标不存在则创建）。
    /// 若目标 name 已存在则等价于合并两个标签。
    /// </summary>
    Task RenameTagAsync(string projectPath, string oldName, string newName, CancellationToken ct = default);

    /// <summary>
    /// 删除标签：从 tags 表中移除该标签，并把所有 media_files.tags 中对应的 tag id 移除。
    /// </summary>
    Task RemoveTagAsync(string projectPath, string name, CancellationToken ct = default);

    // === v0.8 删除与垃圾筒方法（spec doc/14-delete-and-trash.md §2.3） ===

    /// <summary>
    /// 软删除：标记 deleted_at = now（unix ms），不删本地文件、不删云端。
    /// Gallery 查询自动 WHERE deleted_at IS NULL 过滤掉，软删除后立即从 UI 消失。
    /// </summary>
    Task SoftDeleteAsync(long fileId, long deletedAt, CancellationToken ct = default);

    /// <summary>
    /// 恢复：deleted_at = NULL，文件回到 Gallery。
    /// 不修改 local_exists——若用户在垃圾筒期间手动删了本地文件，恢复后显示为「云端有、本地无」。
    /// </summary>
    Task RestoreAsync(long fileId, CancellationToken ct = default);

    /// <summary>
    /// 彻底删除：DELETE FROM media_files WHERE id = @id AND deleted_at IS NOT NULL。
    /// 调用方需自行清理关联的 upload_jobs（Repository 之间不依赖，靠 TrashViewModel 组合调用）。
    /// 本地文件 / OSS 对象的删除也在调用方处理（顺序：先云端 → 再本地 → 再 DB，失败时回滚看 TrashViewModel）。
    /// </summary>
    Task PermanentDeleteAsync(long fileId, CancellationToken ct = default);

    /// <summary>
    /// 获取某项目下所有已移入垃圾筒的文件，按 deleted_at DESC 排序（最新删除在前）。
    /// TrashViewModel 加载垃圾筒列表用。
    /// </summary>
    Task<IReadOnlyList<MediaFile>> GetDeletedAsync(string projectPath, CancellationToken ct = default);

    /// <summary>
    /// 释放本地空间时更新 local_exists 标记。
    /// 实际 File.Delete 由 GalleryViewModel.FreeUpSpace 负责，DB 状态同步走这里。
    /// </summary>
    Task UpdateLocalExistsAsync(long fileId, bool exists, CancellationToken ct = default);

    /// <summary>
    /// 撤销"仅删除本地"清理：把 deleted_at 重新设回原值，让文件回到垃圾筒。
    /// 配套流程：CleanSingle 选「仅删除本地」时 → File.Delete 本地 → RestoreAsync(deleted_at=NULL)
    /// → 文件回到 Gallery 显示「云端有、本地无」；撤销 → 重新标记 deleted_at → 文件回到垃圾筒「已在云端」段。
    /// 复用 SoftDeleteAsync 也能完成，但后者会生成新时间戳、丢原值——UndoDelete 明确传原 deletedAt。
    /// 不影响 local_exists（用户在「仅删除本地」路径上已经走过 UpdateLocalExistsAsync(false)）。
    /// </summary>
    Task UndoDeleteAsync(long fileId, long deletedAt, CancellationToken ct = default);

    /// <summary>
    /// 按 id 查单条 MediaFile（不区分 deleted_at 状态——垃圾筒撤销后需要读到刚被改回 deleted_at 的行）。
    /// 找不到返回 null。供 TrashViewModel 撤销后回填 ObservableCollection 用。
    /// </summary>
    Task<MediaFile?> GetByIdAsync(long fileId, CancellationToken ct = default);
}

public class ScanResult
{
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Failed { get; set; }
    public int NewFiles { get; set; }
    public int UpdatedFiles { get; set; }
}