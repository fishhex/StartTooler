using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace StartTooler.Controls;

public partial class TimelineBar : UserControl
{
    public TimelineBar()
    {
        InitializeComponent();
    }

    private void OnScrollLeftClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ScrollViewer>("TimelineScrollViewer") is { } sv)
        {
            sv.Offset = sv.Offset.WithX(Math.Max(0, sv.Offset.X - 120));
        }
    }

    private void OnScrollRightClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ScrollViewer>("TimelineScrollViewer") is { } sv)
        {
            sv.Offset = sv.Offset.WithX(sv.Offset.X + 120);
        }
    }
}