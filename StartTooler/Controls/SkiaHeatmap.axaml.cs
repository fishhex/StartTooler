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
using StartTooler.Models;

namespace StartTooler.Controls;

/// <summary>
/// SkiaSharp 自绘日历热力图（D09 统计仪表盘）。
/// 7 行（周一→周日）× 53 列，每格 12×12，间距 2px。
/// </summary>
public partial class SkiaHeatmap : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<HeatmapDay>> DaysProperty =
        AvaloniaProperty.Register<SkiaHeatmap, IReadOnlyList<HeatmapDay>>(nameof(Days), defaultValue: Array.Empty<HeatmapDay>());

    /// <summary>
    /// v0.11: 仪表盘按周期（年/季度/月）筛选时，热力图应展示该周期的日期范围而不是固定"今天倒推 365 天"。
    /// 为 null 时保留旧行为（以今天为结束点向前推满一屏）。
    /// </summary>
    public static readonly StyledProperty<DateTime?> StartDateProperty =
        AvaloniaProperty.Register<SkiaHeatmap, DateTime?>(nameof(StartDate));

    public static readonly StyledProperty<DateTime?> EndDateProperty =
        AvaloniaProperty.Register<SkiaHeatmap, DateTime?>(nameof(EndDate));

    public IReadOnlyList<HeatmapDay> Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public DateTime? StartDate
    {
        get => GetValue(StartDateProperty);
        set => SetValue(StartDateProperty, value);
    }

    public DateTime? EndDate
    {
        get => GetValue(EndDateProperty);
        set => SetValue(EndDateProperty, value);
    }

    public event EventHandler<HeatmapDay>? DayClicked;

    private const int CellSize = 12;
    private const int CellGap = 2;
    private const int Cols = 53;
    private const int Rows = 7;

    private WriteableBitmap? _bitmap;
    private readonly Dictionary<(int Row, int Col), HeatmapDay> _hitTestMap = new();

    public SkiaHeatmap()
    {
        InitializeComponent();
        PropertyChanged += OnPropertyChanged;
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerExited += OnPointerExited;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DaysProperty || e.Property == StartDateProperty || e.Property == EndDateProperty)
        {
            _ = RenderAsync();
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
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
            var days = Days ?? Array.Empty<HeatmapDay>();

            // v0.11: StartDate/EndDate 未设置时保留旧行为（以今天为结束点，向前推满一屏）。
            var endDate = EndDate ?? DateTime.Today;
            var startDate = StartDate ?? endDate.AddDays(-(Cols * Rows - 1));
            var totalDays = (endDate - startDate).Days + 1;
            if (totalDays <= 0) totalDays = Cols * Rows;
            var cols = (int)Math.Ceiling(totalDays / (double)Rows);

            var width = cols * (CellSize + CellGap) + CellGap;
            var height = Rows * (CellSize + CellGap) + CellGap;

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

            // 背景透明
            canvas.Clear(SKColors.Transparent);

            var zeroColor = GetColor("Chart.Heatmap.Zero");
            var maxColor = GetColor("Chart.Heatmap.Max");
            var maxCount = days.Count > 0 ? days.Max(d => d.Count) : 0;

            _hitTestMap.Clear();

            for (int i = 0; i < totalDays; i++)
            {
                var date = startDate.AddDays(i);
                var day = days.FirstOrDefault(d => d.Date.Date == date.Date);
                var count = day?.Count ?? 0;

                var col = i / Rows;
                var row = (int)date.DayOfWeek; // 0=Sunday, 1=Monday...
                // 调整为周一在第一行
                row = row == 0 ? 6 : row - 1;

                var x = CellGap + col * (CellSize + CellGap);
                var y = CellGap + row * (CellSize + CellGap);

                var color = count == 0
                    ? zeroColor
                    : InterpolateColor(zeroColor, maxColor, maxCount > 0 ? count / (double)maxCount : 0);

                using var paint = new SKPaint { Color = color, IsAntialias = true };
                canvas.DrawRoundRect(x, y, CellSize, CellSize, 2, 2, paint);

                if (day != null)
                {
                    _hitTestMap[(row, col)] = day;
                }
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
            System.Diagnostics.Trace.WriteLine($"[SkiaHeatmap] Render failed: {ex}");
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        var (col, row) = HitTest(pos);
        if (_hitTestMap.TryGetValue((row, col), out var day))
        {
            ShowTooltip(day, pos);
        }
        else
        {
            HideTooltip();
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        HideTooltip();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var (col, row) = HitTest(pos);
        if (_hitTestMap.TryGetValue((row, col), out var day))
        {
            DayClicked?.Invoke(this, day);
        }
    }

    private (int Col, int Row) HitTest(Point point)
    {
        var col = (int)((point.X - CellGap) / (CellSize + CellGap));
        var row = (int)((point.Y - CellGap) / (CellSize + CellGap));
        return (col, row);
    }

    private void ShowTooltip(HeatmapDay day, Point pos)
    {
        TooltipDate.Text = day.Date.ToString("yyyy-MM-dd dddd");
        TooltipCount.Text = $"{day.Count} 张";
        TooltipTarget.Text = string.IsNullOrEmpty(day.TopTarget) ? "" : $"主要目标：{day.TopTarget}";
        TooltipTarget.IsVisible = !string.IsNullOrEmpty(day.TopTarget);
        TooltipBorder.IsVisible = true;

        // 简单定位：跟随鼠标右下方
        Canvas.SetLeft(TooltipBorder, pos.X + 12);
        Canvas.SetTop(TooltipBorder, pos.Y + 12);
    }

    private void HideTooltip()
    {
        TooltipBorder.IsVisible = false;
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

    private static SKColor InterpolateColor(SKColor from, SKColor to, double t)
    {
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        var r = (byte)(from.Red + (to.Red - from.Red) * t);
        var g = (byte)(from.Green + (to.Green - from.Green) * t);
        var b = (byte)(from.Blue + (to.Blue - from.Blue) * t);
        var a = (byte)(from.Alpha + (to.Alpha - from.Alpha) * t);
        return new SKColor(r, g, b, a);
    }
}
