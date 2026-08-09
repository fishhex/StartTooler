using System.Windows.Input;
using Avalonia.Media;

namespace StartTooler.Models;

/// <summary>
/// v0.12: 时间轴上的圆点 ——
/// - Index: 在 AllPages 中的索引
/// - IsCurrent: 是否为当前页（高亮）
/// - TooltipText: hover 显示
/// - DotBrush: 由 VM 根据 IsCurrent 设置（高亮 vs 普通）
/// - NavigateToPageCommand: VM 注入的跳转命令（参数 = Index）
/// </summary>
public sealed class TimelineDot
{
    public int Index { get; init; }
    public bool IsCurrent { get; init; }
    public string TooltipText { get; init; } = "";
    public string DateLabel { get; init; } = "";
    public string MonthLabel { get; init; } = "";
    public bool ShowMonthLabel { get; init; }
    public bool ShowDateLabel { get; init; } = true;
    public int DotSize { get; init; } = 10;
    public IBrush? DotBrush { get; init; }
    public IBrush? LabelForeground { get; init; }
    public ICommand? NavigateToPageCommand { get; init; }
}