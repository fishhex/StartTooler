using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Data;
using StartTooler.Helpers;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public enum ViewPage
{
    Gallery,
    Settings,
    UploadServer
}

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private GalleryViewModel galleryViewModel;
    [ObservableProperty] private SettingsViewModel settingsViewModel;
    [ObservableProperty] private UploadServerViewModel uploadServerViewModel;
    [ObservableProperty] private object currentView;
    [ObservableProperty] private string title = "星助";
    [ObservableProperty] private bool isSettingsPage;
    [ObservableProperty] private ViewPage currentPage = ViewPage.Gallery;

    public bool HasProject => !string.IsNullOrEmpty(GalleryViewModel?.ProjectPath);

    public bool IsMediaActive => CurrentPage == ViewPage.Gallery;

    public bool IsSettingsActive => CurrentPage == ViewPage.Settings;

    public bool IsUploadServerActive => CurrentPage == ViewPage.UploadServer;

    public bool IsGalleryPage => CurrentPage == ViewPage.Gallery;

    public bool IsSettingsPageVisible => CurrentPage == ViewPage.Settings;

    public MainWindowViewModel()
    {
        // 创建服务实例
        var configService = new ConfigService();
        var mediaRepository = new MediaRepository();
        var uploadJobRepo = new UploadJobRepository();
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

        SettingsViewModel = new SettingsViewModel(new DirectoryPickerService(), new FilePickerService(), configService);

        // 创建 ViewModel
        // onOssNotConfigured: Gallery 触发上传时如果 OSS 未配置，由 MainWindow 弹对话框并提供「去设置」入口
        GalleryViewModel = new GalleryViewModel(
            mediaRepository, thumbnailService, configService, systemShell, ossFactory, uploadJobRepo,
            onOssNotConfigured: ShowOssNotConfiguredDialogAsync);
        UploadServerViewModel = new UploadServerViewModel(
            GalleryViewModel,
            new PublicRelayViewModel(configService, new PublicRelayService(), new FilePickerService(), GalleryViewModel));
        CurrentView = GalleryViewModel;
        IsSettingsPage = false;
        CurrentPage = ViewPage.Gallery;

        // 退出兜底：进程退出前杀掉 VPS 上的 nohup 进程（fire-and-forget，5s timeout）
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try
            {
                UploadServerViewModel?.PublicRelayViewModel
                    ?.EnsureRemoteKilledOnExitAsync()
                    .Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[MainWindow] exit cleanup failed: {ex.Message}");
            }
        };

        // 初始化
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await SettingsViewModel.InitializeAsync();
        await GalleryViewModel.InitializeAsync();
        await UploadServerViewModel.InitializeAsync();
        OnPropertyChanged(nameof(HasProject));

        // 启动恢复弹窗：扫描 upload_jobs，如有未完成任务询问用户
        await TryPromptResumeInterruptedAsync();
    }

    private bool _resumePrompted;
    private async Task TryPromptResumeInterruptedAsync()
    {
        if (_resumePrompted) return;
        _resumePrompted = true;

        var projectPath = GalleryViewModel.ProjectPath;
        if (string.IsNullOrEmpty(projectPath)) return;

        var uploadJobRepo = new UploadJobRepository();
        IReadOnlyList<UploadJob> jobs;
        try
        {
            jobs = await uploadJobRepo.GetInProgressAsync(projectPath);
        }
        catch
        {
            // 启动时 DB 错误不该阻塞 UI
            return;
        }

        if (jobs.Count == 0) return;

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        var resume = await DialogHelper.ShowConfirmAsync(
            window,
            title: "检测到未完成的上传",
            message: $"有 {jobs.Count} 个文件上次没传完（可能因断网或退出）。是否现在恢复？\n恢复会自动跳过已传的分片。",
            primaryButtonText: "恢复",
            secondaryButtonText: "稍后");

        if (resume)
        {
            await GalleryViewModel.ResumeInterruptedAsync(jobs);
        }
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
    private void NavigateToUploadServer()
    {
        // 如果没有项目路径，传入空字符串，UploadServerService 会报错
        if (UploadServerViewModel == null)
        {
            UploadServerViewModel = new UploadServerViewModel(
                GalleryViewModel,
                new PublicRelayViewModel(new ConfigService(), new PublicRelayService(), new FilePickerService(), GalleryViewModel));
        }
        CurrentView = UploadServerViewModel;
        IsSettingsPage = false;
        CurrentPage = ViewPage.UploadServer;
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

    /// <summary>
    /// OSS 未配置时弹出的对话框。返回 true 表示用户选择「去设置」并已跳转。
    /// 给 GalleryViewModel 用，避免在两个 ViewModel 之间循环依赖。
    /// </summary>
    public async Task<bool> ShowOssNotConfiguredDialogAsync()
    {
        var window = DialogHelper.GetMainWindow();
        if (window == null) return false;

        var goSettings = await DialogHelper.ShowConfirmAsync(
            window,
            title: "OSS 未配置",
            message: "上传前需要先配置 OSS（Region / Bucket / AccessKey），是否前往设置页？",
            primaryButtonText: "去设置",
            secondaryButtonText: "取消");

        if (goSettings)
        {
            NavigateToSettings();
        }
        return goSettings;
    }

    private async Task<bool> ShowDiscardConfirmDialog()
    {
        var window = DialogHelper.GetMainWindow();
        if (window == null) return false;

        return await DialogHelper.ShowConfirmAsync(
            window,
            title: "有未保存的修改",
            message: "离开将丢弃所有修改，确定吗？",
            primaryButtonText: "丢弃",
            secondaryButtonText: "取消");
    }

    partial void OnCurrentPageChanged(ViewPage value)
    {
        OnPropertyChanged(nameof(IsMediaActive));
        OnPropertyChanged(nameof(IsSettingsActive));
        OnPropertyChanged(nameof(IsGalleryPage));
        OnPropertyChanged(nameof(IsSettingsPageVisible));
        OnPropertyChanged(nameof(IsUploadServerActive));
    }
}
