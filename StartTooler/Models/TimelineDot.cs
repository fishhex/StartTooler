using Avalonia.Media;

namespace StartTooler.Models;

/// <summary>
/// v0.12: 时间轴上的圆点 ——
/// - Index: 在 AllPages 中的索引
/// - IsCurrent: 是否为当前页（高亮）
/// - TooltipText: hover 显示
/// - DotBrush: 由 VM 根据 IsCurrent 设置（高亮 vs 普通）
/// </summary>
public sealed class TimelineDot
{
    public int Index { get; init; }
    public bool IsCurrent { get; init; }
    public string TooltipText { get; init; } = "";
    public IBrush? DotBrush { get; init; }
}