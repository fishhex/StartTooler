using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using StartTooler.Models;

namespace StartTooler.Controls;

public enum BarOrientation
{
    Horizontal,
    Vertical,
}

/// <summary>
/// 条形图/柱状图控件（D09 统计仪表盘）。包装 SkiaBarChart，提供标题与点击事件。
/// </summary>
public partial class BarChart : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<BarChart, string>(nameof(Title), defaultValue: "");

    public static readonly StyledProperty<IReadOnlyList<BarItem>> ItemsProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<BarItem>>(nameof(Items), defaultValue: Array.Empty<BarItem>());

    public static readonly StyledProperty<BarOrientation> OrientationProperty =
        AvaloniaProperty.Register<BarChart, BarOrientation>(nameof(Orientation), defaultValue: BarOrientation.Horizontal);

    public static readonly StyledProperty<bool> ShowValueProperty =
        AvaloniaProperty.Register<BarChart, bool>(nameof(ShowValue), defaultValue: true);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IReadOnlyList<BarItem> Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public BarOrientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public bool ShowValue
    {
        get => GetValue(ShowValueProperty);
        set => SetValue(ShowValueProperty, value);
    }

    public event EventHandler<BarItem>? ItemClicked;

    public BarChart()
    {
        InitializeComponent();
    }

    private void OnItemClicked(object? sender, BarItem e)
    {
        ItemClicked?.Invoke(this, e);
    }
}

/// <summary>
/// 条形图数据项。
/// </summary>
public sealed class BarItem
{
    public string Label { get; init; } = "";
    public double Value { get; init; }
    public string? DisplayValue { get; init; }
    public string? Tag { get; init; }
}
