using System.ComponentModel;
using Avalonia.Controls;
using StartTooler.ViewModels;

namespace StartTooler.Controls;

public partial class NavRail : UserControl
{
    private Button? _mediaButton;
    private Button? _settingsButton;
    private MainWindowViewModel? _viewModel;

    public NavRail()
    {
        InitializeComponent();
        CacheButtons();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateButtonStates();
    }

    private void CacheButtons()
    {
        _mediaButton = this.FindControl<Button>("MediaButton");
        _settingsButton = this.FindControl<Button>("SettingsButton");
    }

    private void UpdateButtonStates()
    {
        if (_viewModel is null) return;

        // 媒体按钮激活状态
        if (_mediaButton != null)
        {
            _mediaButton.Classes.Set("active", _viewModel.CurrentPage == ViewPage.Gallery);
        }

        // 设置按钮激活状态
        if (_settingsButton != null)
        {
            _settingsButton.Classes.Set("active", _viewModel.CurrentPage == ViewPage.Settings);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage))
        {
            UpdateButtonStates();
        }
    }
}
