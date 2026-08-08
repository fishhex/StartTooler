using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StartTooler.ViewModels;

namespace StartTooler.Controls;

public partial class DiaryPage : UserControl
{
    public DiaryPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 笔记 TextBox 失焦时保存到 VM（再由 VM 持久化到 SessionRepository）。
    /// </summary>
    private void OnNotesLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiaryViewModel vm)
        {
            // fire-and-forget；失败由 VM.StatusMessage 显示
            _ = vm.SaveNotesCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// 地点 TextBox 失焦时保存。
    /// </summary>
    private void OnLocationLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiaryViewModel vm)
        {
            _ = vm.SaveLocationCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// 地点编辑框按 Enter 保存、按 Esc 取消。
    /// </summary>
    private void OnLocationKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not DiaryViewModel vm || sender is not TextBox tb) return;

        if (e.Key == Key.Enter)
        {
            _ = vm.SaveLocationCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (vm.CurrentPage != null)
            {
                vm.CurrentPage.IsEditingLocation = false;
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// 点击地点胶囊或"添加地点"按钮后进入编辑模式，并自动聚焦 TextBox。
    /// </summary>
    private void OnLocationEditClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiaryViewModel vm) return;

        vm.EditLocationCommand.Execute(null);

        // 等待 UI 刷新后聚焦
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<TextBox>("LocationTextBox") is { } tb)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// 点击天气胶囊或"添加天气"按钮后打开天气选择器。
    /// </summary>
    private async void OnWeatherEditClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiaryViewModel vm) return;

        await vm.OpenWeatherPickerCommand.ExecuteAsync(null);
    }
}