using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
}