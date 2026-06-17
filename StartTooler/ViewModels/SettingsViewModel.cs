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

    private readonly MainWindowViewModel _mainWindowViewModel;

    public InternalServerSettingsViewModel? InternalServerSettings { get; private set; }

    public SettingsViewModel(MainWindowViewModel mainWindowViewModel)
    {
        _mainWindowViewModel = mainWindowViewModel;
        InitializePages();
    }

    private void InitializePages()
    {
        Pages.Add(new SettingsPageViewModel("AI 配置", new AiSettingsViewModel()));
        Pages.Add(new SettingsPageViewModel("云存储", new CloudStorageSettingsViewModel()));
        InternalServerSettings = new InternalServerSettingsViewModel(_mainWindowViewModel);
        Pages.Add(new SettingsPageViewModel("内网服务", InternalServerSettings));
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
        }
    }

    public void RefreshInternalServerFolders()
    {
        InternalServerSettings?.RefreshRecentFolders();
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
