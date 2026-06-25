using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Data;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public enum ViewPage
{
    Gallery,
    Settings
}

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private GalleryViewModel galleryViewModel;
    [ObservableProperty] private SettingsViewModel settingsViewModel;
    [ObservableProperty] private object currentView;
    [ObservableProperty] private string title = "星助";
    [ObservableProperty] private bool isSettingsPage;
    [ObservableProperty] private ViewPage currentPage = ViewPage.Gallery;

    public bool HasProject => !string.IsNullOrEmpty(GalleryViewModel?.ProjectPath);

    public bool IsMediaActive => CurrentPage == ViewPage.Gallery;

    public bool IsSettingsActive => CurrentPage == ViewPage.Settings;

    public bool IsGalleryPage => CurrentPage == ViewPage.Gallery;

    public bool IsSettingsPageVisible => CurrentPage == ViewPage.Settings;

    public MainWindowViewModel()
    {
        // 创建服务实例
        var configService = new ConfigService();
        var mediaRepository = new MediaRepository();
        var thumbnailService = new ThumbnailService();
        var systemShell = new SystemShellService();

        // OSS Storage 工厂：OssConfig 在 Settings 加载前为空，所以延迟构造。
        // configProvider 每次调用都从 configService 拿最新值，确保用户在 Settings
        // 改完 OSS 配置后下次上传能拿到新凭据。
        IOssStorageFactory ossFactory = new OssStorageFactory(() =>
        {
            return configService.GetAsync<OssConfig>(ConfigKeys.Oss)
                .GetAwaiter().GetResult() ?? new OssConfig();
        });

        // 创建 ViewModel
        GalleryViewModel = new GalleryViewModel(mediaRepository, thumbnailService, configService, systemShell, ossFactory);
        SettingsViewModel = new SettingsViewModel(new DirectoryPickerService(), configService);
        CurrentView = GalleryViewModel;
        IsSettingsPage = false;
        CurrentPage = ViewPage.Gallery;

        // 初始化
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await SettingsViewModel.InitializeAsync();
        await GalleryViewModel.InitializeAsync();
        OnPropertyChanged(nameof(HasProject));
    }

    [RelayCommand]
    private async Task NavigateToGallery()
    {
        if (IsSettingsPage && SettingsViewModel.IsDirty)
        {
            // 有未保存的修改，弹出确认对话框
            var result = await ShowDiscardConfirmDialog();
            if (!result)
                return; // 用户取消，留在设置页

            // 用户确认丢弃，重置状态
            SettingsViewModel.DiscardChanges();
        }

        CurrentView = GalleryViewModel;
        IsSettingsPage = false;
        CurrentPage = ViewPage.Gallery;

        // 刷新画廊数据
        GalleryViewModel.ReloadCommand.Execute(null);
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentView = SettingsViewModel;
        IsSettingsPage = true;
        CurrentPage = ViewPage.Settings;
    }

    [RelayCommand]
    private void NavigateToMedia()
    {
        NavigateToGalleryCommand.Execute(null);
    }

    [RelayCommand]
    private async Task Refresh()
    {
        if (GalleryViewModel != null)
        {
            await GalleryViewModel.RefreshCommand.ExecuteAsync(null);
        }
    }

    private async Task<bool> ShowDiscardConfirmDialog()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window != null)
            {
                var dialog = new Window
                {
                    Title = "确认",
                    Width = 320,
                    Height = 160,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    Background = Avalonia.Media.Brushes.Transparent
                };

                var result = false;
                var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
                panel.Children.Add(new TextBlock
                {
                    Text = "有未保存的修改",
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "离开将丢弃所有修改，确定吗？",
                    FontSize = 12,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                });
                var buttonPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                var cancelButton = new Button { Content = "取消", Width = 80, Padding = new Thickness(12, 6) };
                cancelButton.Click += (s, e) => { result = false; dialog.Close(); };
                var confirmButton = new Button { Content = "丢弃", Width = 80, Padding = new Thickness(12, 6) };
                confirmButton.Click += (s, e) => { result = true; dialog.Close(); };
                buttonPanel.Children.Add(cancelButton);
                buttonPanel.Children.Add(confirmButton);
                panel.Children.Add(buttonPanel);
                dialog.Content = panel;

                await dialog.ShowDialog(window);
                return result;
            }
        }
        return false;
    }

    partial void OnCurrentPageChanged(ViewPage value)
    {
        OnPropertyChanged(nameof(IsMediaActive));
        OnPropertyChanged(nameof(IsSettingsActive));
        OnPropertyChanged(nameof(IsGalleryPage));
        OnPropertyChanged(nameof(IsSettingsPageVisible));
    }
}
