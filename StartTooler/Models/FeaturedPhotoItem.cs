using StartTooler.Data;

namespace StartTooler.Models;

/// <summary>
/// v0.12: 日记页精选照片的展示项，包含原始照片与 UI 展示所需元数据。
/// </summary>
public sealed class FeaturedPhotoItem
{
    /// <summary>原始照片。</summary>
    public required MediaFile Photo { get; init; }

    /// <summary>在精选列表中的序号（从 1 开始），例如 "01"。</summary>
    public required int Index { get; init; }

    /// <summary>序号文本，例如 "01"（自动补零）。</summary>
    public string IndexText => Index.ToString("D2");

    /// <summary>拍摄时间文本，例如 "21:34"。</summary>
    public required string TimeText { get; init; }
}
