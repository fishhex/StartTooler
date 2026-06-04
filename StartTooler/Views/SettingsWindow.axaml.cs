using Avalonia.Controls;
using Avalonia.Interactivity;

namespace StartTooler.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
