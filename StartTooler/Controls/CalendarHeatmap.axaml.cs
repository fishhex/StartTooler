using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using StartTooler.Models;

namespace StartTooler.Controls;

/// <summary>
/// 拍摄热力图控件（D09 统计仪表盘）。包装 SkiaHeatmap，提供标题与点击事件。
/// </summary>
public partial class CalendarHeatmap : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<CalendarHeatmap, string>(nameof(Title), defaultValue: "拍摄热力图");

    public static readonly StyledProperty<IReadOnlyList<HeatmapDay>> DaysProperty =
        AvaloniaProperty.Register<CalendarHeatmap, IReadOnlyList<HeatmapDay>>(nameof(Days), defaultValue: Array.Empty<HeatmapDay>());

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IReadOnlyList<HeatmapDay> Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public event EventHandler<HeatmapDay>? DayClicked;

    public CalendarHeatmap()
    {
        InitializeComponent();
    }

    private void OnDayClicked(object? sender, HeatmapDay e)
    {
        DayClicked?.Invoke(this, e);
    }
}
