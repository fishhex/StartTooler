using Avalonia.Controls;
using Avalonia.Input;
using StartTooler.Data;
using StartTooler.ViewModels;

namespace StartTooler.Views;

public partial class TrashView : UserControl
{
    public TrashView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// v0.11 spec §5.2: 右键卡片 → 进入多选模式并选中该卡片；
    /// 已在多选模式时左/右键都切换该卡片选中态（spec §10 边界）。
    /// 双击不处理。
    /// </summary>
    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (sender is not Control c || c.DataContext is not MediaFile file) return;
        if (DataContext is not TrashViewModel vm) return;

        var isLeft = point.Properties.IsLeftButtonPressed;
        var isRight = point.Properties.IsRightButtonPressed;
        if (!isLeft && !isRight) return;

        if (!vm.IsMultiSelectMode)
        {
            // 仅右键可触发进入多选模式
            if (!isRight) return;
            e.Handled = true;
            vm.EnterMultiSelectCommand.Execute(null);
        }
        else
        {
            // 多选模式下左/右键都切换选中
            e.Handled = true;
        }
        vm.ToggleSelectCommand.Execute(file);
    }
}
