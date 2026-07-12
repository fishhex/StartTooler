using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    UploadServer,
    Trash,  // v0.8: 垃圾筒（spec doc/14-delete-and-trash.md §9.1）
}

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly MediaRepository _mediaRepository;
    private readonly UploadJobRepository _uploadJobRepo;

    [ObservableProperty] private GalleryViewModel galleryViewModel;
    [ObservableProperty] private SettingsViewModel settingsViewModel;
    [ObservableProperty] private UploadServerViewModel uploadServerViewModel;
    [ObservableProperty] private TrashViewModel trashViewModel;  // v0.8
    [ObservableProperty] private object currentView;
    [ObservableProperty] private string title = "星助";
    [ObservableProperty] private bool isSettingsPage;
    [ObservableProperty] private ViewPage currentPage = ViewPage.Gallery;

    public bool HasProject => !string.IsNullOrEmpty(GalleryViewModel?.ProjectPath);

    public bool IsMediaActive => CurrentPage == ViewPage.Gallery;

    public bool IsSettingsActive => CurrentPage == ViewPage.Settings;

    public bool IsUploadServerActive => CurrentPage == ViewPage.UploadServer;

    public bool IsTrashActive => CurrentPage == ViewPage.Trash;  // v0.8

    public bool IsGalleryPage => CurrentPage == ViewPage.Gallery;

    public bool IsSettingsPageVisible => CurrentPage == ViewPage.Settings;

    public MainWindowViewModel()
    {
        // 创建服务实例
        _configService = new ConfigService();
        _mediaRepository = new MediaRepository();
        _uploadJobRepo = new UploadJobRepository();

        var thumbnailService = new ThumbnailService();
        var systemShell = new SystemShellService();
        var aiTagger = new AITagger();  // v0.6 新增：AI 打标服务，静态 HttpClient 池化

        // OSS Storage 工厂：OssConfig 在 Settings 加载前为空，所以延迟构造。
        // configProvider 每次调用都从 configService 拿最新值，确保用户在 Settings
        // 改完 OSS 配置后下次上传能拿到新凭据。
        IOssStorageFactory ossFactory = new OssStorageFactory(() =>
        {
            return _configService.GetAsync<OssConfig>(ConfigKeys.Oss)
                .GetAwaiter().GetResult() ?? new OssConfig();
        });

        SettingsViewModel = new SettingsViewModel(new DirectoryPickerService(), new FilePickerService(), _configService, ossFactory);

        // 创建 ViewModel
        // onOssNotConfigured: Gallery 触发上传时如果 OSS 未配置，由 MainWindow 弹对话框并提供「去设置」入口
        GalleryViewModel = new GalleryViewModel(
            _mediaRepository, thumbnailService, _configService, systemShell, ossFactory, _uploadJobRepo,
            aiTagger,
            onOssNotConfigured: ShowOssNotConfiguredDialogAsync);
        UploadServerViewModel = new UploadServerViewModel(
            GalleryViewModel,
            new PublicRelayViewModel(_configService, new PublicRelayService(), new FilePickerService(), GalleryViewModel));

        // v0.8: 垃圾筒 VM（spec doc/14-delete-and-trash.md §7.1）
        // 复用 mediaRepo / uploadJobRepo / ossFactory / configService；onOssNotConfigured 复用 MainWindow 的弹窗。
        // v0.8.1: 新增 thumbnailService 用于下载后重生成缩略图（spec §7.4）。
        // v0.11: 加 onNavigateToFile 回调——Restore 成功后 Toast「跳转」按钮触发。
        TrashViewModel = new TrashViewModel(
            _mediaRepository, _uploadJobRepo, ossFactory, _configService, thumbnailService,
            onOssNotConfigured: ShowOssNotConfiguredDialogAsync,
            onNavigateToFile: NavigateToGalleryAndLocateFile);

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

        var uploadJobRepo = _uploadJobRepo;
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
            // v0.11 §6: 三选导航守卫 —— 保存并离开 / 放弃更改 / 取消
            var result = await ShowUnsavedChangesChoiceAsync();
            switch (result)
            {
                case DialogHelper.DialogChoice.Primary:
                    // 保存并离开
                    await SettingsViewModel.SaveCommand.ExecuteAsync(null);
                    // Save 失败（验证错误）时 IsDirty 仍为 true，留在设置页
                    if (SettingsViewModel.IsDirty) return;
                    break;
                case DialogHelper.DialogChoice.Secondary:
                    // 放弃更改
                    SettingsViewModel.DiscardChanges();
                    break;
                default:
                    // Tertiary / Cancelled：取消，留在设置页
                    return;
            }
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
                new PublicRelayViewModel(_configService, new PublicRelayService(), new FilePickerService(), GalleryViewModel));
        }
        CurrentView = UploadServerViewModel;
        IsSettingsPage = false;
        CurrentPage = ViewPage.UploadServer;
    }

    /// <summary>
    /// v0.8: 跳到垃圾筒页（spec §9.1 NavigateToTrash）。
    /// 加载当前项目的垃圾筒数据；Gallery 查询删文件后这里能看到。
    /// v0.11: 切到 Trash 时让 GalleryVM 退出多选（spec §10 边界）。
    /// </summary>
    [RelayCommand]
    private async Task NavigateToTrash()
    {
        // 离开 Gallery 之前退出多选（避免切回 Gallery 时 SelectedFiles 残留）
        if (GalleryViewModel != null && GalleryViewModel.IsMultiSelectMode)
        {
            GalleryViewModel.ExitMultiSelectPublic();
        }

        CurrentView = TrashViewModel;
        IsSettingsPage = false;
        CurrentPage = ViewPage.Trash;

        var projectPath = GalleryViewModel?.ProjectPath ?? string.Empty;
        try
        {
            await TrashViewModel.LoadAsync(projectPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MainWindow] NavigateToTrash 加载失败: {ex.Message}");
        }
    }

    /// <summary>
    /// v0.11: 从垃圾筒「已恢复 xxx」Toast 的「跳转」按钮触发。
    /// 切到 Gallery + 让 GalleryVM 定位到该文件（按 shot_at 算日期切过去）。
    /// 简化版：不做 pixel-perfect scroll（spec §7.2 自己说"本次简化"），
    /// 只做"切到该文件所在日期"，按 shot_at DESC 排序后用户能看到。
    /// </summary>
    private void NavigateToGalleryAndLocateFile(long mediaId)
    {
        Trace.WriteLine($"[MainWindow] NavigateToGalleryAndLocateFile: mediaId={mediaId}");

        // 1. 切到 Gallery
        CurrentView = GalleryViewModel;
        IsSettingsPage = false;
        CurrentPage = ViewPage.Gallery;

        // 2. 退出 Trash 的多选（如果有）
        if (TrashViewModel != null && TrashViewModel.IsMultiSelectMode)
        {
            TrashViewModel.ExitMultiSelectPublic();
        }

        // 3. 让 GalleryVM 切到对应日期（spec §7.2 LocateAndScrollTo）
        GalleryViewModel?.LocateAndScrollTo(mediaId);
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

    /// <summary>
    /// v0.11 §6: 三选导航守卫对话框。
    ///   Primary = 保存并离开
    ///   Secondary = 放弃更改
    ///   Tertiary = 取消
    /// 用户点 X / Esc 走 Cancelled → 也按"取消"处理（不离开）。
    /// </summary>
    private async Task<DialogHelper.DialogChoice> ShowUnsavedChangesChoiceAsync()
    {
        var window = DialogHelper.GetMainWindow();
        if (window == null) return DialogHelper.DialogChoice.Cancelled;

        return await DialogHelper.ShowChoiceAsync(
            window,
            title: "有未保存的修改",
            message: "是否保存后再离开？",
            primaryButtonText: "保存并离开",
            secondaryButtonText: "放弃更改",
            tertiaryButtonText: "取消");
    }

    partial void OnCurrentPageChanged(ViewPage value)
    {
        OnPropertyChanged(nameof(IsMediaActive));
        OnPropertyChanged(nameof(IsSettingsActive));
        OnPropertyChanged(nameof(IsGalleryPage));
        OnPropertyChanged(nameof(IsSettingsPageVisible));
        OnPropertyChanged(nameof(IsUploadServerActive));
        OnPropertyChanged(nameof(IsTrashActive));
    }
}
