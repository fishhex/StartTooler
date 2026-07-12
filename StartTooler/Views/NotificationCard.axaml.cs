using Avalonia.Controls;
using Avalonia.Interactivity;
using StartTooler.Services;

namespace StartTooler.Views;

public partial class NotificationCard : UserControl
{
    public NotificationCard()
    {
        InitializeComponent();
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NotificationItem item)
        {
            NotificationService.Current.Dismiss(item);
        }
    }
}
