using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace StartTooler.Components;

/// <summary>
/// v0.11 spec/08 §3.1: 骨架屏占位控件。
/// 用法: XAML 里 <c>placeholder:ShimmerPlaceholder PlaceholderItemCount="20" ItemWidth="160" ItemHeight="120"</c>
/// 渲染 N 个矩形,每个矩形带 1.2s 周期的 Opacity 脉动。
/// </summary>
public partial class ShimmerPlaceholder : UserControl
{
    public static readonly StyledProperty<int> PlaceholderItemCountProperty =
        AvaloniaProperty.Register<ShimmerPlaceholder, int>(nameof(PlaceholderItemCount), defaultValue: 20);

    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<ShimmerPlaceholder, double>(nameof(ItemWidth), defaultValue: 160);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<ShimmerPlaceholder, double>(nameof(ItemHeight), defaultValue: 120);

    public int PlaceholderItemCount
    {
        get => GetValue(PlaceholderItemCountProperty);
        set => SetValue(PlaceholderItemCountProperty, value);
    }

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public ShimmerPlaceholder()
    {
        InitializeComponent();
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlaceholderItemCountProperty
            || change.Property == ItemWidthProperty
            || change.Property == ItemHeightProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        if (RootPanel == null) return;
        RootPanel.Children.Clear();

        var bg = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x2E)); // Bg.Surface 兜底
        for (int i = 0; i < PlaceholderItemCount; i++)
        {
            RootPanel.Children.Add(new Border
            {
                Width = ItemWidth,
                Height = ItemHeight,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                Classes = { "shimmer-cell" },
                Background = bg,
            });
        }
    }
}
