using Avalonia.Controls;
using Avalonia.Interactivity;
using StartTooler.ViewModels;
using System;

namespace StartTooler.Views;

public partial class MainWindow : Window
{
    private MediaManagerWindow? _mediaManagerWindow;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Hide();
    }

    private void OnOpenMediaManagerClick(object? sender, RoutedEventArgs e)
    {
        if (_mediaManagerWindow != null)
        {
            if (_mediaManagerWindow.IsVisible)
            {
                _mediaManagerWindow.Activate();
                return;
            }

            _mediaManagerWindow.Show();
            return;
        }

        _mediaManagerWindow = new MediaManagerWindow
        {
            DataContext = new MainWindowViewModel()
        };

        _mediaManagerWindow.Closed += (_, _) =>
        {
            _mediaManagerWindow = null;
            Show();
            Activate();
        };

        _mediaManagerWindow.Show();
        Hide();
    }
}
