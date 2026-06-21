using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private GalleryViewModel galleryViewModel;
    [ObservableProperty] private SettingsViewModel settingsViewModel;
    [ObservableProperty] private object currentView;
    [ObservableProperty] private string title = "星助";
    [ObservableProperty] private bool isSettingsPage;

    public MainWindowViewModel()
    {
        GalleryViewModel = new GalleryViewModel();
        SettingsViewModel = new SettingsViewModel(new DirectoryPickerService());
        CurrentView = GalleryViewModel;
        IsSettingsPage = false;
    }

    [RelayCommand]
    private void NavigateToGallery()
    {
        CurrentView = GalleryViewModel;
        IsSettingsPage = false;
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentView = SettingsViewModel;
        IsSettingsPage = true;
    }
}
