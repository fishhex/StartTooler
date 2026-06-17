using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class InternalServerSettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _port = 9527;

    [ObservableProperty]
    private ObservableCollection<RecentFolder> _recentFolders = new();

    [ObservableProperty]
    private RecentFolder? _selectedFolder;

    private readonly MainWindowViewModel _mainWindowViewModel;

    public InternalServerSettingsViewModel(MainWindowViewModel mainWindowViewModel)
    {
        _mainWindowViewModel = mainWindowViewModel;
        LoadRecentFolders();
    }

    private void LoadRecentFolders()
    {
        RecentFolders.Clear();
        foreach (var folder in _mainWindowViewModel.RecentFolders)
        {
            RecentFolders.Add(folder);
        }
    }

    partial void OnSelectedFolderChanged(RecentFolder? value)
    {
        _mainWindowViewModel.HttpServerFolder = value;
    }

    partial void OnPortChanged(int value)
    {
        _mainWindowViewModel.HttpServerPort = value;
    }

    [RelayCommand]
    public async Task ToggleServer()
    {
        await _mainWindowViewModel.ToggleHttpServerCommand.ExecuteAsync(null);
    }

    public void RefreshRecentFolders()
    {
        LoadRecentFolders();
    }
}
