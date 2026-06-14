using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StartTooler.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<SettingsPageViewModel> _pages = new();

    [ObservableProperty]
    private SettingsPageViewModel? _selectedPage;

    public SettingsViewModel()
    {
        InitializePages();
    }

    private void InitializePages()
    {
        Pages.Add(new SettingsPageViewModel("AI 配置", new AiSettingsViewModel()));
        Pages.Add(new SettingsPageViewModel("云存储", new CloudStorageSettingsViewModel()));
        // 可在此添加更多子配置页，例如：
        // Pages.Add(new SettingsPageViewModel("通用", new GeneralSettingsViewModel()));
        SelectedPage = Pages[0];
    }

    [RelayCommand]
    public void Save()
    {
        foreach (var page in Pages)
        {
            if (page.Content is AiSettingsViewModel aiVm)
            {
                aiVm.Save();
            }
            if (page.Content is CloudStorageSettingsViewModel cloudVm)
            {
                cloudVm.Save();
            }
            // 在此添加其他子页面的保存逻辑
        }
    }
}

public partial class SettingsPageViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private object? _content;

    public SettingsPageViewModel(string title, object? content)
    {
        Title = title;
        Content = content;
    }
}
