using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StartTooler.ViewModels;

namespace StartTooler.Views;

public partial class AdvancedView : UserControl
{
    public AdvancedView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) =>
        {
            if (DataContext is AdvancedViewModel vm)
            {
                await vm.RefreshAsync();
            }
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}