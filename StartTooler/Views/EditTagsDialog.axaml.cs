using Avalonia.Controls;
using Avalonia.Interactivity;
using StartTooler.ViewModels;

namespace StartTooler.Views;

/// <summary>
/// 单张编辑标签模态弹窗 code-behind（spec doc/15-manual-tag-edit.md §4.2）。
/// ShowDialog 后由调用方读 vm.SavedTags：
///   - null：用户取消 或 保存失败
///   - List&lt;string&gt;：保存成功，包含新 tags
/// </summary>
public partial class EditTagsDialog : Window
{
    public EditTagsDialogViewModel? ViewModel { get; }

    public EditTagsDialog()
    {
        InitializeComponent();
    }

    public EditTagsDialog(EditTagsDialogViewModel vm) : this()
    {
        ViewModel = vm;
        DataContext = vm;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        // 标记取消（SavedTags = null），关闭弹窗
        Close();
    }
}
