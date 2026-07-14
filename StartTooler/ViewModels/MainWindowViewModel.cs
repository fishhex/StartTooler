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
    private readonly NetworkStatusService _networkStatus;  // v0.11 spec/09 §3

    /// <summary>暴露给 App 层(创建 DragDropHandler 等需要 ConfigService 的服务用)</summary>
    public ConfigService ConfigService => _configService;

    [ObservableProperty] private GalleryViewModel galleryViewModel;
    [ObservableProperty] private SettingsViewModel settingsViewModel;
    [ObservableProperty] private UploadServerViewModel uploadServerViewModel;
    [ObservableProperty] private TrashViewModel trashViewModel;  // v0.8
    [ObservableProperty] private object currentView;
    [ObservableProperty] private bool isSettingsPage;
    [ObservableProperty] private ViewPage currentPage = ViewPage.Gallery;

    // v0.11 状态栏数据：OssStatus（其他字段桥接到 GalleryVM / TrashVM，避免重复状态源）
    public enum OssStatusKind { Unknown, Configured, NotConfigured }
    [ObservableProperty] private OssStatusKind _ossStatus = OssStatusKind.Unknown;
    partial void OnOssStatusChanged(OssStatusKind value)
    {
        OnPropertyChanged(nameof(OssStatusText));
        OnPropertyChanged(nameof(OssStatusColor));
    }
    /// <summary>状态栏 OSS 文字（"已配置" / "未配置"）。</summary>
    public string OssStatusText => OssStatus == OssStatusKind.Configured ? "OSS 已配置" : "OSS 未配置";
    /// <summary>状态栏 OSS 圆点颜色（绿/红）。</summary>
    public string OssStatusColor => OssStatus == OssStatusKind.Configured ? "#66BB6A" : "#EF5350";

    // === v0.11 spec/10 §4.1: 大图库提示（状态栏）===
    // GalleryVM 扫描完成后根据文件数（> 2000）触发，null = 清除。
    [ObservableProperty] private string? _largeLibraryHint;

    // === v0.11 spec/09 §3/§6: 网络状态 + 磁盘空间（状态栏）===
    [ObservableProperty] private bool _isOnline = true;
    [ObservableProperty] private string _diskSpaceText = "—";

    private void OnNetworkChanged(bool isOnline)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsOnline = isOnline);
        // 网络变化时刷新磁盘空间（用户可能切换了 wifi，跨盘符移动概率小但安全）
        UpdateDiskSpace();
    }

    private void UpdateDiskSpace()
    {
        var projectPath = GalleryViewModel?.ProjectPath;
        if (string.IsNullOrEmpty(projectPath))
        {
            DiskSpaceText = "—";
            return;
        }
        try
        {
            var free = DiskSpaceService.GetAvailableFreeSpace(projectPath);
            DiskSpaceText = free < 0 ? "—" : DiskSpaceService.FormatBytes(free);
        }
        catch
        {
            DiskSpaceText = "—";
        }
    }

    /// <summary>
    /// v0.11: 标题动态化。随 CurrentPage 变化更新（spec demand/06 §1）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _title = "星助";
    public string WindowTitle
    {
        get
        {
            var pageName = CurrentPage switch
            {
                ViewPage.Gallery => "媒体",
                ViewPage.Settings => "设置",
                ViewPage.UploadServer => "上传服务",
                ViewPage.Trash => "垃圾筒",
                _ => string.Empty,
            };
            return string.IsNullOrEmpty(pageName) ? "星助" : $"星助 — {pageName}";
        }
    }

    public bool HasProject => !string.IsNullOrEmpty(GalleryViewModel?.ProjectPath);

    public bool IsMediaActive => CurrentPage == ViewPage.Gallery;

    // v0.11: NavRail tooltip 快捷键提示（spec §3.2），macOS 用 ⌘，其它用 Ctrl
    public string NavMediaTooltip => OperatingSystem.IsMacOS() ? "媒体 (⌘1)" : "媒体 (Ctrl+1)";
    public string NavUploadTooltip => OperatingSystem.IsMacOS() ? "上传 (⌘2)" : "上传 (Ctrl+2)";
    public string NavTrashTooltip => OperatingSystem.IsMacOS() ? "垃圾筒 (⌘3)" : "垃圾筒 (Ctrl+3)";
    public string NavSettingsTooltip => OperatingSystem.IsMacOS() ? "设置 (⌘4)" : "设置 (Ctrl+4)";

    /// <summary>
    /// v0.11: 通知历史（spec §14）—— 状态栏铃铛 Flyout 绑定这个集合。
    /// 直接代理 NotificationService.Current.History，保持单一数据源。
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<Services.NotificationItem> NotificationHistory
        => Services.NotificationService.Current.History;

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

        // v0.11 spec/09 §3: 网络状态监控（OSS endpoint 探活,30s 周期）
        // endpoint 用 region 拼 https://oss-{region}.aliyuncs.com/，未配置 region 时跳过探活
        var ossConfig = _configService.GetAsync<OssConfig>(ConfigKeys.Oss)
            .GetAwaiter().GetResult();
        var ossEndpoint = !string.IsNullOrWhiteSpace(ossConfig?.Region)
            ? $"https://oss-{ossConfig.Region}.aliyuncs.com"
            : null;
        _networkStatus = new NetworkStatusService(ossEndpoint);
        _networkStatus.NetworkStatusChanged += OnNetworkChanged;
        _networkStatus.Start();

        SettingsViewModel = new SettingsViewModel(new DirectoryPickerService(), new FilePickerService(), _configService, ossFactory);

        // 创建 ViewModel
        // onOssNotConfigured: Gallery 触发上传时如果 OSS 未配置，由 MainWindow 弹对话框并提供「去设置」入口
        GalleryViewModel = new GalleryViewModel(
            _mediaRepository, thumbnailService, _configService, systemShell, ossFactory, _uploadJobRepo,
            aiTagger,
            onOssNotConfigured: ShowOssNotConfiguredDialogAsync);

        // v0.11 spec/10 §4.1: 大图库提示桥接（Gallery 扫描完成 → 状态栏文字）
        // 必须 Dispatcher.Post:OnLargeLibraryHint 可能在 background 线程触发(RequestRefreshDebounced 内部 Task.Run)
        GalleryViewModel.OnLargeLibraryHint = hint =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LargeLibraryHint = hint);

        // v0.11 spec/09 §6.2: Gallery 项目目录变化时刷新磁盘空间显示
        GalleryViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.ProjectPath))
                UpdateDiskSpace();
        };
        UpdateDiskSpace();  // 初始显示

        // v0.11 spec/07: 引导卡片跳转回调（Onboarding 按钮触发）
        GalleryViewModel.NavigateToSettings = NavigateToSettings;
        GalleryViewModel.NavigateToOssSettings = NavigateToOssSettings;

        // v0.11 spec/07: 检查引导状态 + OSS 配置（启动时跑一次）
        _ = GalleryViewModel.CheckOnboardingStatusAsync();
        _ = GalleryViewModel.RefreshOssConfigAsync();
        UploadServerViewModel = new UploadServerViewModel(
            GalleryViewModel,
            new PublicRelayViewModel(_configService, new PublicRelayService(), new FilePickerService(), GalleryViewModel));

        // v0.8: 垃圾筒 VM（spec doc/14-delete-and-trash.md §7.1）
        // 复用 mediaRepo / uploadJobRepo / ossFactory / configService；onOssNotConfigured 复用 MainWindow 的弹窗。
        // v0.8.1: 新增 thumbnailService 用于下载后重生成缩略图（spec §7.4）。
        // v0.11: 加 onNavigateToFile 回调——Restore 成功后 Toast「跳转」按钮触发。
        // v0.11 spec/08 §5: DontAskAgainService 注入，启用「清空垃圾筒 30 天内不再提示」。
        var dontAskAgain = new DontAskAgainService(_configService);
        TrashViewModel = new TrashViewModel(
            _mediaRepository, _uploadJobRepo, ossFactory, _configService, thumbnailService,
            dontAskAgain: dontAskAgain,
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

        // v0.11: 启动后评估 OSS 状态（状态栏圆点初始化）
        EvaluateOssStatus();

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

    /// <summary>
    /// v0.11 spec/07: 引导跳转 — 切到 Settings 页 + 自动选 OSS Tab。
    /// </summary>
    public void NavigateToOssSettings()
    {
        CurrentView = SettingsViewModel;
        IsSettingsPage = true;
        CurrentPage = ViewPage.Settings;
        SettingsViewModel.SelectedTab = SettingsTab.Oss;
        Trace.WriteLine("[MainWindow] NavigateToOssSettings");
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
        OnPropertyChanged(nameof(WindowTitle));  // v0.11: 标题随 CurrentPage 变化
        // v0.11: 切到设置页时重新评估 OSS 状态（用户在设置页改完配置后回到 Gallery 应立即反映）
        // 注意：OnCurrentPageChanged 在构造期可能也会触发，OssStorageFactory 此时可能未就绪。
        // 用 try-catch 兜底。
        try
        {
            EvaluateOssStatus();
        }
        catch
        {
            // ignore - 构造期 Evaluate 失败不影响其他初始化
        }
    }

    /// <summary>
    /// v0.11: 重新评估 OSS 配置状态。给状态栏圆点用。
    /// 用 IOssStorageFactory.IsConfigured(Config) 判断——它已经定义了。
    /// </summary>
    private void EvaluateOssStatus()
    {
        try
        {
            // 直接从 ConfigService 拿当前配置
            var cfg = _configService.GetAsync<OssConfig>(ConfigKeys.Oss)
                .GetAwaiter().GetResult() ?? new OssConfig();
            var configured = !string.IsNullOrWhiteSpace(cfg.Region)
                && !string.IsNullOrWhiteSpace(cfg.Bucket)
                && !string.IsNullOrWhiteSpace(cfg.AccessKeyId)
                && !string.IsNullOrWhiteSpace(cfg.AccessKeySecret);
            OssStatus = configured ? OssStatusKind.Configured : OssStatusKind.NotConfigured;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] EvaluateOssStatus 失败: {ex.Message}");
            OssStatus = OssStatusKind.Unknown;
        }
    }
}
