using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using StartTooler.Services;

// v0.11 spec/06: 用的是 Avalonia 11 过渡期 DragDrop API，升级到 DataTransfer 留 v0.12 一起做。
#pragma warning disable CS0618

namespace StartTooler.Views;

public partial class MainWindow : Window
{
    private DragDropHandler? _dragDropHandler;

    public MainWindow()
    {
        InitializeComponent();

        // v0.11 spec/06 §5.2: 在 Window 级别注册 routed drag-drop 事件,
        // 子控件不拦截时由 Window 顶层统一处理。
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>由 MainWindowViewModel 在初始化时注入(避免 Window 反向依赖 VM)</summary>
    public void SetDragDropHandler(DragDropHandler handler)
    {
        _dragDropHandler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // === v0.11 spec/06: 拖拽事件桥接 ===

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (_dragDropHandler == null) return;
        var effects = _dragDropHandler.OnDragOver(e);
        e.DragEffects = effects;
        e.Handled = true;

        // 显示遮罩 + 文件数(只有接受时才显示)
        if (effects != DragDropEffects.None && DragDropOverlay != null)
        {
            var paths = e.Data.GetFileNames()?.ToList();
            var count = paths?.Count ?? 0;
            if (DragDropFileCount != null)
                DragDropFileCount.Text = count > 0 ? $"{count} 项" : "";
            DragDropOverlay.IsVisible = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_dragDropHandler == null) return;
        e.DragEffects = _dragDropHandler.OnDragOver(e);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DragDropOverlay != null) DragDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DragDropOverlay != null) DragDropOverlay.IsVisible = false;

        if (_dragDropHandler == null) return;
        e.Handled = true;
        try
        {
            await _dragDropHandler.OnDropAsync(e, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Trace.WriteLine("[MainWindow] 拖拽被取消");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 拖拽处理异常: {ex.Message}");
            NotificationService.Current.Show("拖拽失败", ex.Message, NotificationType.Error);
        }
    }
}
