using Avalonia.Controls;
using Avalonia.Interactivity;
using StartTooler.Controls;
using StartTooler.Models;
using StartTooler.ViewModels;

namespace StartTooler.Views;

/// <summary>
/// v0.11: 统计仪表盘视图 code-behind（spec/19 §7.3）。
/// 负责把自定义控件点击事件转发给 DashboardViewModel。
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private void OnCalendarDayClicked(object? sender, HeatmapDay e)
    {
        if (DataContext is DashboardViewModel vm)
        {
            vm.OnHeatmapDayClicked(e);
        }
    }

    private void OnTagRankClicked(object? sender, BarItem e)
    {
        if (DataContext is DashboardViewModel vm)
        {
            vm.OnTagRankClicked(e);
        }
    }
}
