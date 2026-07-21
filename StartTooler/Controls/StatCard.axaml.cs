using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StartTooler.Controls;

/// <summary>
/// KPI 数字卡片控件（D09 统计仪表盘）。
/// </summary>
public partial class StatCard : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<StatCard, string>(nameof(Label), defaultValue: "");

    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<StatCard, string>(nameof(Value), defaultValue: "");

    public static readonly StyledProperty<Geometry?> IconDataProperty =
        AvaloniaProperty.Register<StatCard, Geometry?>(nameof(IconData));

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Geometry? IconData
    {
        get => GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public StatCard()
    {
        InitializeComponent();
    }
}
