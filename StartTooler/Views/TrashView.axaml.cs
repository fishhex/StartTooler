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
    /// 已在多选模式时仅切换该卡片选中态（spec §10 边界）。
    /// 左键 / 双击不处理（让 Button.Command 继续生效）。
    /// </summary>
    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsRightButtonPressed) return;
        if (sender is not Control c || c.DataContext is not MediaFile file) return;
        if (DataContext is not TrashViewModel vm) return;

        // 已处理右键，阻止冒泡（避免被外层 ScrollViewer 拦截或弹出系统菜单）
        e.Handled = true;

        if (!vm.IsMultiSelectMode)
        {
            // 进入多选 + 立即选中该卡
            vm.EnterMultiSelectCommand.Execute(null);
        }
        // ToggleSelect（已选中的会变成未选中，符合桌面右键习惯）
        vm.ToggleSelectCommand.Execute(file);
    }
}
