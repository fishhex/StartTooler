using Avalonia.Controls;
using Avalonia.Interactivity;
using StartTooler.ViewModels;

namespace StartTooler.Views;

/// <summary>
/// 批量编辑标签模态弹窗 code-behind（spec doc/15-manual-tag-edit.md §5.2）。
/// ShowDialog 后由调用方读 vm.Applied：
///   - true：用户成功 Apply
///   - false：用户取消
/// </summary>
public partial class EditTagsBatchDialog : Window
{
    public EditTagsBatchDialogViewModel? ViewModel { get; }

    public EditTagsBatchDialog()
    {
        InitializeComponent();
    }

    public EditTagsBatchDialog(EditTagsBatchDialogViewModel vm) : this()
    {
        ViewModel = vm;
        DataContext = vm;
        vm.RequestClose += (_, _) => Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
