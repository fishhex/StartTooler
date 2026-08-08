using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using StartTooler.Models;

namespace StartTooler.Controls;

public partial class WeatherIconPicker : Window
{
    public static readonly DirectProperty<WeatherIconPicker, IReadOnlyList<WeatherOption>> WeatherOptionsProperty =
        AvaloniaProperty.RegisterDirect<WeatherIconPicker, IReadOnlyList<WeatherOption>>(
            nameof(WeatherOptions),
            o => o.WeatherOptions,
            (o, v) => o.WeatherOptions = v);

    private IReadOnlyList<WeatherOption> _weatherOptions = new List<WeatherOption>();

    public IReadOnlyList<WeatherOption> WeatherOptions
    {
        get => _weatherOptions;
        set
        {
            SetAndRaise(WeatherOptionsProperty, ref _weatherOptions, value);
            if (this.FindControl<ItemsControl>("OptionsItemsControl") is { } ic)
            {
                ic.ItemsSource = value;
            }
        }
    }

    public WeatherOption? SelectedOption { get; private set; }

    public WeatherIconPicker()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (this.FindControl<ItemsControl>("OptionsItemsControl") is { } ic)
        {
            ic.ItemsSource = WeatherOptions;
        }
    }

    private void OnOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WeatherOption option })
        {
            SelectedOption = option;
            Close();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        SelectedOption = null;
        Close();
    }

    public async Task<WeatherOption?> ShowDialogAsync(Window? owner)
    {
        if (owner != null)
        {
            await ShowDialog(owner);
        }
        else
        {
            Show();
            // 等待窗口关闭
            var tcs = new TaskCompletionSource<object?>();
            Closed += (_, _) => tcs.SetResult(null);
            await tcs.Task;
        }
        return SelectedOption;
    }
}
