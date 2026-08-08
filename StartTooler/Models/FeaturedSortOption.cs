namespace StartTooler.Models;

/// <summary>
/// v0.12: 精选照片排序选项（供 ComboBox 显示用）。
/// </summary>
public sealed class FeaturedSortOption
{
    public DiaryPhotoSortMode Mode { get; }
    public string DisplayName { get; }

    public FeaturedSortOption(DiaryPhotoSortMode mode, string displayName)
    {
        Mode = mode;
        DisplayName = displayName;
    }
}
