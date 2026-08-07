using Avalonia.Controls;
using Avalonia.Input;
using StartTooler.ViewModels;

namespace StartTooler.Views;

public partial class DiaryView : UserControl
{
    public DiaryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 键盘左右方向键翻页。spec §3.2：左右键翻动日记。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is DiaryViewModel vm)
        {
            if (e.Key == Key.Left && vm.HasPrev)
            {
                vm.NavigatePrevCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Right && vm.HasNext)
            {
                vm.NavigateNextCommand.Execute(null);
                e.Handled = true;
            }
        }
        base.OnKeyDown(e);
    }
}