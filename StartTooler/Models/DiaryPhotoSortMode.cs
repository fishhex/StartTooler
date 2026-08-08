namespace StartTooler.Models;

/// <summary>
/// v0.12: 日记页精选照片排序模式。
/// </summary>
public enum DiaryPhotoSortMode
{
    /// <summary>按拍摄时间升序（从早到晚）。</summary>
    TimeAsc,

    /// <summary>按拍摄时间降序（从晚到早）。</summary>
    TimeDesc,

    /// <summary>按评分降序（高分优先）。</summary>
    ScoreDesc,
}
