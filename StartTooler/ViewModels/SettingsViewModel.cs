using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private string selectedProjectDirectory;
    [ObservableProperty] private ObservableCollection<string> availableDirectories;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private int selectedTabIndex;
    [ObservableProperty] private bool isDeepSpaceTheme = true;
    [ObservableProperty] private bool isRedNightTheme;

    public SettingsViewModel()
    {
        AvailableDirectories = new ObservableCollection<string>();
        SelectedProjectDirectory = string.Empty;
        IsDirty = false;
        IsSaving = false;

        AvailableDirectories.Add("/Users/hex/Pictures/Astrophotography");
        AvailableDirectories.Add("/Users/hex/Pictures/RAW");
        AvailableDirectories.Add("/Volumes/External/Photos");
    }

    partial void OnSelectedProjectDirectoryChanged(string value)
    {
        IsDirty = true;
        StatusMessage = null;
    }

    partial void OnIsDeepSpaceThemeChanged(bool value)
    {
        if (value)
        {
            IsRedNightTheme = false;
            ThemeManager.SetTheme(false);
            IsDirty = true;
        }
    }

    partial void OnIsRedNightThemeChanged(bool value)
    {
        if (value)
        {
            IsDeepSpaceTheme = false;
            ThemeManager.SetTheme(true);
            IsDirty = true;
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        if (string.IsNullOrEmpty(SelectedProjectDirectory))
        {
            StatusMessage = "请选择项目目录";
            return;
        }

        IsSaving = true;
        await Task.Delay(500);
        IsSaving = false;
        IsDirty = false;
        StatusMessage = "已保存";
    }
}
