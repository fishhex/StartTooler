using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Models;

namespace StartTooler.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private GalleryViewModel galleryViewModel;
    [ObservableProperty] private SettingsViewModel settingsViewModel;
    [ObservableProperty] private object currentView;
    [ObservableProperty] private string title = "星助";

    public MainWindowViewModel()
    {
        GalleryViewModel = new GalleryViewModel();
        SettingsViewModel = new SettingsViewModel();
        CurrentView = GalleryViewModel;
    }

    [RelayCommand]
    private void NavigateToGallery()
    {
        CurrentView = GalleryViewModel;
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentView = SettingsViewModel;
    }
}
