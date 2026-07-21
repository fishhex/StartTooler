using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace StartTooler.Controls;

/// <summary>
/// SkiaSharp 自绘条形图/柱状图（D09 统计仪表盘）。
/// </summary>
public partial class SkiaBarChart : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<BarItem>> ItemsProperty =
        AvaloniaProperty.Register<SkiaBarChart, IReadOnlyList<BarItem>>(nameof(Items), defaultValue: Array.Empty<BarItem>());

    public static readonly StyledProperty<BarOrientation> OrientationProperty =
        AvaloniaProperty.Register<SkiaBarChart, BarOrientation>(nameof(Orientation), defaultValue: BarOrientation.Horizontal);

    public static readonly StyledProperty<bool> ShowValueProperty =
        AvaloniaProperty.Register<SkiaBarChart, bool>(nameof(ShowValue), defaultValue: true);

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

    private const int RowHeight = 28;
    private const int LabelWidth = 100;
    private const int BarAreaPadding = 8;
    private const int VerticalBarWidth = 32;

    private WriteableBitmap? _bitmap;
    private readonly List<(Rect Bounds, BarItem Item)> _hitTestRegions = new();

    public SkiaBarChart()
    {
        InitializeComponent();
        PropertyChanged += OnPropertyChanged;
        PointerPressed += OnPointerPressed;
        AttachedToVisualTree += OnAttachedToVisualTree;
        SizeChanged += OnSizeChanged;
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ItemsProperty || e.Property == OrientationProperty || e.Property == ShowValueProperty)
        {
            _ = RenderAsync();
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _ = RenderAsync();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _ = RenderAsync();
    }

    private async System.Threading.Tasks.Task RenderAsync()
    {
        await System.Threading.Tasks.Task.Yield();
        Render();
    }

    private void Render()
    {
        try
        {
            var items = Items ?? Array.Empty<BarItem>();
            _hitTestRegions.Clear();

            var width = (int)Bounds.Width;
            if (width <= 0) width = Orientation == BarOrientation.Horizontal ? 400 : 600;
            var height = Orientation == BarOrientation.Horizontal
                ? items.Count * RowHeight + BarAreaPadding * 2
                : 240;

            if (width <= 0 || height <= 0) return;

            var pixelSize = new PixelSize(width, height);
            if (_bitmap == null || _bitmap.PixelSize != pixelSize)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(pixelSize, new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
            }

            using var buffer = _bitmap.Lock();
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, buffer.Address, buffer.RowBytes);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var accentColor = GetColor("Chart.Bar.Accent");
            var secondaryColor = GetColor("Chart.Bar.Secondary");
            var textColor = GetColor("Text.Primary");
            var secondaryTextColor = GetColor("Text.Secondary");

            var maxValue = items.Count > 0 ? items.Max(i => i.Value) : 0;

            if (Orientation == BarOrientation.Horizontal)
            {
                RenderHorizontal(canvas, items, width, height, maxValue, accentColor, textColor, secondaryTextColor);
            }
            else
            {
                RenderVertical(canvas, items, width, height, maxValue, accentColor, textColor, secondaryTextColor);
            }

            canvas.Flush();

            SkiaHost.Content = new Image
            {
                Source = _bitmap,
                Width = width,
                Height = height,
                Stretch = Stretch.None,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[SkiaBarChart] Render failed: {ex}");
        }
    }

    private void RenderHorizontal(SKCanvas canvas, IReadOnlyList<BarItem> items, int width, int height, double maxValue,
        SKColor accentColor, SKColor textColor, SKColor secondaryTextColor)
    {
        const int labelOffsetX = 8;
        var maxBarWidth = Math.Max(40, width - LabelWidth - BarAreaPadding * 2 - 80); // 留 80 给数值

        using var labelPaint = new SKPaint
        {
            Color = textColor,
            TextSize = 12,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("PingFang SC", SKFontStyle.Normal),
        };
        using var valuePaint = new SKPaint
        {
            Color = secondaryTextColor,
            TextSize = 11,
            IsAntialias = true,
        };
        using var barPaint = new SKPaint { Color = accentColor, IsAntialias = true };

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var y = BarAreaPadding + i * RowHeight;
            var barLength = maxValue > 0 ? (float)(item.Value / maxValue * maxBarWidth) : 0;

            // 标签
            canvas.DrawText(item.Label, labelOffsetX, y + RowHeight / 2 + 4, labelPaint);

            // 条形
            var barRect = new SKRect(LabelWidth, y + 4, LabelWidth + barLength, y + RowHeight - 4);
            canvas.DrawRoundRect(barRect, 4, 4, barPaint);

            // 数值
            if (ShowValue && !string.IsNullOrEmpty(item.DisplayValue))
            {
                canvas.DrawText(item.DisplayValue, LabelWidth + barLength + 8, y + RowHeight / 2 + 4, valuePaint);
            }

            _hitTestRegions.Add((new Rect(0, y, width, RowHeight), item));
        }
    }

    private void RenderVertical(SKCanvas canvas, IReadOnlyList<BarItem> items, int width, int height, double maxValue,
        SKColor accentColor, SKColor textColor, SKColor secondaryTextColor)
    {
        var chartHeight = height - 40; // 底部留给标签
        var gap = items.Count > 1 ? (width - items.Count * VerticalBarWidth) / (items.Count + 1) : 20;
        if (gap < 8) gap = 8;

        using var labelPaint = new SKPaint
        {
            Color = textColor,
            TextSize = 11,
            IsAntialias = true,
        };
        using var valuePaint = new SKPaint
        {
            Color = secondaryTextColor,
            TextSize = 10,
            IsAntialias = true,
        };
        using var barPaint = new SKPaint { Color = accentColor, IsAntialias = true };

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var x = gap + i * (VerticalBarWidth + gap);
            var barHeight = maxValue > 0 ? (float)(item.Value / maxValue * chartHeight) : 0;
            var y = chartHeight - barHeight;

            var barRect = new SKRect(x, y, x + VerticalBarWidth, chartHeight);
            canvas.DrawRoundRect(barRect, 4, 4, barPaint);

            // 数值
            if (ShowValue && !string.IsNullOrEmpty(item.DisplayValue))
            {
                canvas.DrawText(item.DisplayValue, x, y - 4, valuePaint);
            }

            // 标签（旋转或截断，简化版：水平居中）
            canvas.DrawText(item.Label, x + VerticalBarWidth / 2, chartHeight + 14, labelPaint);

            _hitTestRegions.Add((new Rect(x - gap / 2, 0, VerticalBarWidth + gap, chartHeight), item));
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        foreach (var (bounds, item) in _hitTestRegions)
        {
            if (bounds.Contains(pos))
            {
                ItemClicked?.Invoke(this, item);
                return;
            }
        }
    }

    private static SKColor GetColor(string resourceKey)
    {
        if (Application.Current?.Resources.TryGetResource(resourceKey, null, out var value) == true
            && value is Color color)
        {
            return new SKColor(color.R, color.G, color.B, color.A);
        }
        return SKColors.Gray;
    }
}
