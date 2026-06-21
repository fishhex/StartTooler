using System;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace StartTooler.Views;

public partial class SettingsView : UserControl
{
    private MenuFlyout? _selectFlyout;

    public SettingsView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _selectFlyout = SelectButton?.Flyout as MenuFlyout;
        if (_selectFlyout != null)
        {
            _selectFlyout.Opened += OnSelectFlyoutOpened;
        }
    }

    private void OnSelectFlyoutOpened(object? sender, EventArgs e)
    {
        if (SelectButton != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var popup = this.FindDescendantOfType<Popup>(true);
                if (popup != null)
                {
                    popup.Width = SelectButton.Bounds.Width;
                }
            }, DispatcherPriority.Loaded);
        }
    }
}
