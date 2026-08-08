using CommunityToolkit.Mvvm.ComponentModel;
using StartTooler.Data;

namespace StartTooler.Models;

/// <summary>
/// v2.1: 精选照片选择器中的可选项，包含原始照片与是否被选中的状态。
/// </summary>
public sealed partial class FeaturedPhotoSelectableItem : ObservableObject
{
    /// <summary>原始照片。</summary>
    public required MediaFile Photo { get; init; }

    /// <summary>是否被选中为精选照片。</summary>
    [ObservableProperty]
    private bool _isSelected;
}
