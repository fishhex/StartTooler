using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Data;
using StartTooler.Helpers;
using StartTooler.Models;
using StartTooler.Services;
using StartTooler.Views;

namespace StartTooler.ViewModels;

public partial class GalleryViewModel : ObservableObject
{
    private readonly IMediaRepository _mediaRepo;
    private readonly IThumbnailService _thumbnailService;
    private readonly IConfigService _configService;
    private readonly ISystemShellService _systemShell;
    private readonly IOssStorageFactory _ossFactory;
    private readonly IUploadJobRepository _uploadJobRepo;
    private readonly IAITagger _aiTagger;  // v0.6 新增
    private readonly Func<Task<bool>>? _onOssNotConfigured;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoProject))]
    private string? _projectPath;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _uploadCts;
    private CancellationTokenSource? _tagCts;  // v0.6 新增，独立于 _cts，切日期/刷新不取消打标
    private CancellationTokenSource? _downloadCts;  // v0.8.1 新增，批量下载取消源

    // === 数据源 ===
    public ObservableCollection<TimelineEntry> DateGroups { get; } = new();
    public ObservableCollection<MediaFile> CurrentMediaFiles { get; } = new();

    // === v0.12 标签变更 debouncer（spec doc/15-manual-tag-edit.md §6.4）===
    // 500ms 内连续多次 tag 变化合并成 1 次 LoadTagGroupsAsync 调用，避免连续编辑刷 N 次左栏。
    private readonly StartTooler.Helpers.TagChangeDebouncer _tagChangeDebouncer = new(500);
    // 跟踪已订阅 PropertyChanged 的 file，CurrentMediaFiles.Clear() / Reset 时统一解绑。
    private readonly HashSet<MediaFile> _tagSubscribedFiles = new();

    // === 选中态（日期） ===
    [ObservableProperty] private TimelineEntry? _selectedDate;

    // === 加载状态 ===
    [ObservableProperty] private bool _isLoadingDateGroups;
    [ObservableProperty] private bool _isLoadingMedia;
    [ObservableProperty] private string? _loadErrorMessage;

    // === 扫描进度状态 ===
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private ScanProgress? _scanProgress;
    [ObservableProperty] private string? _scanStatusMessage;
    [ObservableProperty] private RefreshState _refreshState = RefreshState.Idle;

    // === v2.3 多选模式 ===
    [ObservableProperty] private bool _isMultiSelectMode;
    [ObservableProperty] private string? _toastMessage;
    public ObservableCollection<MediaFile> SelectedFiles { get; } = new();

    // === v3.0 上传状态 ===
    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private int _uploadCompletedCount;
    [ObservableProperty] private int _uploadTotalCount;
    public string UploadProgressText => IsUploading && UploadTotalCount > 0
        ? $"上传中 {UploadCompletedCount}/{UploadTotalCount}"
        : string.Empty;
    partial void OnIsUploadingChanged(bool value) => OnPropertyChanged(nameof(UploadProgressText));
    partial void OnUploadCompletedCountChanged(int value) => OnPropertyChanged(nameof(UploadProgressText));
    partial void OnUploadTotalCountChanged(int value) => OnPropertyChanged(nameof(UploadProgressText));

    // === v0.6 AI 打标状态（spec doc/12-ai-toolbar-buttons.md §3.3.1） ===
    [ObservableProperty] private bool _isTagging;
    [ObservableProperty] private int _tagCompletedCount;
    [ObservableProperty] private int _tagTotalCount;
    public string TagProgressText => IsTagging && TagTotalCount > 0
        ? $"打标中 {TagCompletedCount}/{TagTotalCount}"
        : string.Empty;
    partial void OnIsTaggingChanged(bool value)
    {
        // IsTagging 影响：进度文本、批量操作可用性、多选/全选/反选命令的可用性
        OnPropertyChanged(nameof(TagProgressText));
        OnPropertyChanged(nameof(IsBatchActionEnabled));
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
    }
    partial void OnTagCompletedCountChanged(int value) => OnPropertyChanged(nameof(TagProgressText));
    partial void OnTagTotalCountChanged(int value) => OnPropertyChanged(nameof(TagProgressText));

    // === v0.6 分类与排序（spec §3.3.1） ===
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDateGroupMode))]
    [NotifyPropertyChangedFor(nameof(IsTagGroupMode))]
    [NotifyPropertyChangedFor(nameof(GroupModeIndex))]
    private GroupMode _groupMode = GroupMode.Date;
    [ObservableProperty] private SortMode _sortMode = SortMode.TimeDesc;

    /// <summary>
    /// ComboBox SelectedIndex 桥接属性：0=时间↓ / 1=评分↓。
    /// 比直接绑 SortMode 更适合 Avalonia ComboBoxItem 列表场景。
    /// </summary>
    public int SortModeIndex
    {
        get => SortMode == SortMode.TimeDesc ? 0 : 1;
        set => SortMode = value == 0 ? SortMode.TimeDesc : SortMode.ScoreDesc;
    }

    /// <summary>左栏"标签"tab 用（v0.6.1 已接 UI）。</summary>
    public ObservableCollection<TagGroupItem> TagGroups { get; } = new();

    // === v0.6.1 左栏分类状态（spec doc/11-ai-tagging.md §5.5/§5.6） ===

    /// <summary>当前选中的标签名（Tag 视图下有效）。</summary>
    [ObservableProperty] private string? _selectedTag;

    /// <summary>TabControl SelectedIndex 桥接：0=Date, 1=Tag。</summary>
    public int GroupModeIndex
    {
        get => GroupMode == GroupMode.Date ? 0 : 1;
        set => GroupMode = value == 0 ? GroupMode.Date : GroupMode.Tag;
    }

    /// <summary>ScrollViewer IsVisible 绑定（Date 视图）。</summary>
    public bool IsDateGroupMode => GroupMode == GroupMode.Date;

    /// <summary>ScrollViewer IsVisible 绑定（Tag 视图）。</summary>
    public bool IsTagGroupMode => GroupMode == GroupMode.Tag;

    /// <summary>
    /// GroupMode 切换回调：Date → 重新初始化时间轴；Tag → 加载 TagGroups + 自动选中第一个。
    /// v0.6.1 用户决策：切到「标签」tab 自动选中第一个，不再留空白。
    /// </summary>
    partial void OnGroupModeChanged(GroupMode value)
    {
        Trace.WriteLine($"[Gallery] GroupMode 切换 → {value}, ProjectPath={ProjectPath ?? "(null)"}");
        switch (value)
        {
            case GroupMode.Date:
                _ = InitializeAsync();
                break;
            case GroupMode.Tag:
                _ = LoadTagGroupsAsync(_cts?.Token ?? default);
                break;
        }
    }

    public int SelectedCount => SelectedFiles.Count;
    public bool IsBatchActionEnabled => IsMultiSelectMode && SelectedFiles.Count > 0 && !IsUploading && !IsTagging;
    public bool HasNoProject => string.IsNullOrEmpty(ProjectPath);
    public bool IsEmpty => !HasNoProject && !IsLoadingDateGroups && DateGroups.Count == 0;

    // === v0.6 排序联动（spec §3.3.2 / v0.6.1 §6.2） ===
    // 切排序方式 → 重新加载当前视图（Date 或 Tag），按新排序展示。
    // 走 _cts 复用 SelectAsync 的 cancel-and-restart 模式，避免双发请求。
    partial void OnSortModeChanged(SortMode value)
    {
        Trace.WriteLine($"[Gallery] SortMode 切换 → {value}, GroupMode={GroupMode}, SelectedDate={SelectedDate?.Date:yyyy-MM-dd}, SelectedTag={SelectedTag ?? "(null)"}");
        if (string.IsNullOrEmpty(ProjectPath)) return;

        switch (GroupMode)
        {
            case GroupMode.Date when SelectedDate != null:
                _ = SelectAsync(SelectedDate);
                break;
            case GroupMode.Tag when SelectedTag != null:
                // 通过 Tag 找 TagGroupItem 重载（保留 TagGroups 顺序）
                var group = TagGroups.FirstOrDefault(g => g.Tag == SelectedTag);
                if (group != null) _ = SelectTagAsync(group);
                break;
        }
    }

    public GalleryViewModel(
        IMediaRepository mediaRepo,
        IThumbnailService thumbnailService,
        IConfigService configService,
        ISystemShellService systemShell,
        IOssStorageFactory ossFactory,
        IUploadJobRepository uploadJobRepo,
        IAITagger aiTagger,
        Func<Task<bool>>? onOssNotConfigured = null)
    {
        _mediaRepo = mediaRepo;
        _thumbnailService = thumbnailService;
        _configService = configService;
        _systemShell = systemShell;
        _ossFactory = ossFactory;
        _uploadJobRepo = uploadJobRepo;
        _aiTagger = aiTagger;
        _onOssNotConfigured = onOssNotConfigured;
        SelectedFiles.CollectionChanged += OnSelectedFilesChanged;
        CurrentMediaFiles.CollectionChanged += OnCurrentMediaFilesChanged;
    }

    private void OnCurrentMediaFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // CurrentMediaFiles 内容变化（全选/反选命令的可用性主要依赖它）
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();

        // v0.12: 订阅新增 file 的 PropertyChanged，监听 Tags 变化触发左栏 TagGroups 刷新。
        // OldItems / Reset 时必须解绑，否则 MediaFile 引用泄漏（spec §6.4 内存风险）。
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is MediaFile mf)
                {
                    if (_tagSubscribedFiles.Add(mf))
                    {
                        mf.PropertyChanged += OnMediaFilePropertyChanged;
                    }
                }
            }
        }
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is MediaFile mf)
                {
                    if (_tagSubscribedFiles.Remove(mf))
                    {
                        mf.PropertyChanged -= OnMediaFilePropertyChanged;
                    }
                }
            }
        }
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            // ObservableCollection.Clear() 触发 Reset 但 OldItems 为 null——
            // 用 _tagSubscribedFiles HashSet 跟踪所有已订阅的 file，统一解绑。
            // Clear() 通常在切日期/切 tag 时调用，紧接着 CurrentMediaFiles 会被新数据填满，
            // 新 file 在 NewItems 阶段重新订阅。中间窗口没有任何订阅，无事件丢失。
            foreach (var mf in _tagSubscribedFiles)
            {
                mf.PropertyChanged -= OnMediaFilePropertyChanged;
            }
            _tagSubscribedFiles.Clear();
            Trace.WriteLine($"[Gallery] OnCurrentMediaFilesChanged Reset: cleared {_tagSubscribedFiles.Count} subscriptions");
        }
    }

    /// <summary>
    /// v0.12: 监听 MediaFile.Tags 变化 → 触发 debouncer → 500ms 后刷新左栏 TagGroups（spec §6.4）。
    /// 注意：IsSelected / UploadStatus / TagError 变化不应该触发（会过度刷新左栏）。
    /// </summary>
    private void OnMediaFilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Data.MediaFile.Tags)) return;
        if (sender is not MediaFile mf) return;
        OnFileTagsChanged(mf);
    }

    private void OnSelectedFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 把 IsSelected 同步给所有 MediaFile (新增 = true, 移除 = false)
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is MediaFile mf) mf.IsSelected = true;
            }
        }
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is MediaFile mf) mf.IsSelected = false;
            }
        }
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            // Reset 不会带 NewItems/OldItems，遍历当前列表
            // 注意：CurrentMediaFiles 中可能还有其他文件需要清理
        }
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(IsBatchActionEnabled));
    }

    public async Task InitializeAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            IsLoadingDateGroups = true;
            LoadErrorMessage = null;
            DateGroups.Clear();
            ExitMultiSelect();  // 先清空 SelectedFiles → 触发 IsSelected 同步
            CurrentMediaFiles.Clear();

            // 读项目配置
            var projectConfig = await _configService.GetOrCreateAsync<ProjectConfig>(ConfigKeys.Project);
            ProjectPath = projectConfig.CurrentDirectory;

            if (string.IsNullOrEmpty(ProjectPath))
            {
                IsLoadingDateGroups = false;
                return;
            }

            // 加载日期分组
            var dateGroups = await _mediaRepo.GetDateGroupsAsync(ProjectPath, ct);
            foreach (var group in dateGroups)
            {
                DateGroups.Add(new TimelineEntry(group.Date, group.Count));
            }

            IsLoadingDateGroups = false;

            if (DateGroups.Count == 0)
            {
                return;
            }

            // 自动选中第一个日期
            await SelectAsync(DateGroups[0]);
        }
        catch (OperationCanceledException)
        {
            // 忽略取消
        }
        catch (Exception ex)
        {
            LoadErrorMessage = $"加载失败：{ex.Message}";
            IsLoadingDateGroups = false;
            IsLoadingMedia = false;
        }
    }

    [RelayCommand]
    private async Task SelectAsync(TimelineEntry? entry)
    {
        if (entry == null) return;

        // 取消之前的加载
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 切日期时退出多选模式
        ExitMultiSelect();

        // 更新选中态
        if (SelectedDate != null)
            SelectedDate.IsSelected = false;

        entry.IsSelected = true;
        SelectedDate = entry;

        await LoadDateAsync(entry, ct);
    }

    private async Task LoadDateAsync(TimelineEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ProjectPath)) return;

        try
        {
            IsLoadingMedia = true;

            var files = await _mediaRepo.GetByDateAsync(ProjectPath, entry.Date, SortMode, ct);

            // 反推 UploadStatus：upload_jobs 里有未完成 job 的 → Paused，否则按 IsUploaded
            // 单日最多几千条，直接全表扫成本可接受
            IReadOnlyList<UploadJob> jobs;
            try
            {
                jobs = await _uploadJobRepo.GetInProgressAsync(ProjectPath, ct);
            }
            catch
            {
                jobs = Array.Empty<UploadJob>();
            }
            var pausedSet = new HashSet<string>(
                jobs.Select(j => j.RelativePath),
                StringComparer.OrdinalIgnoreCase);

            // 批量替换
            CurrentMediaFiles.Clear();
            foreach (var file in files)
            {
                if (file.IsUploaded)
                {
                    file.UploadStatus = UploadStatus.Uploaded;
                }
                else if (pausedSet.Contains(file.RelativePath))
                {
                    file.UploadStatus = UploadStatus.Paused;
                }
                else
                {
                    file.UploadStatus = UploadStatus.NotUploaded;
                }
                CurrentMediaFiles.Add(file);
            }

            IsLoadingMedia = false;
        }
        catch (OperationCanceledException)
        {
            // 忽略取消
        }
        catch (Exception ex)
        {
            LoadErrorMessage = $"加载失败：{ex.Message}";
            IsLoadingMedia = false;
        }
    }

    // === v0.6.1 标签分类（spec doc/11-ai-tagging.md §5.6） ===

    /// <summary>
    /// 加载项目的 TagGroups + 自动选中第一个 tag（用户决策：切 tab 即看到内容）。
    /// 复用 _cts 模式：被 OnGroupModeChanged 调用前会先 _cts.Cancel() 旧请求。
    /// </summary>
    [RelayCommand]
    private async Task LoadTagGroupsAsync(CancellationToken ct)
    {
        Trace.WriteLine($"[Gallery] LoadTagGroupsAsync 启动: ProjectPath={ProjectPath ?? "(null)"}");

        if (string.IsNullOrEmpty(ProjectPath))
        {
            Trace.WriteLine("[Gallery] LoadTagGroupsAsync 早返回: ProjectPath 为空");
            return;
        }

        try
        {
            var groups = await _mediaRepo.GetTagGroupsAsync(ProjectPath, ct);
            TagGroups.Clear();
            foreach (var g in groups) TagGroups.Add(g);
            Trace.WriteLine($"[Gallery] LoadTagGroupsAsync 完成: 共 {TagGroups.Count} 个 tag");

            if (TagGroups.Count > 0)
            {
                // 用户决策：自动选中第一个 tag
                Trace.WriteLine($"[Gallery] 自动选中第一个 tag: '{TagGroups[0].Tag}' ({TagGroups[0].Count} 张)");
                await SelectTagAsync(TagGroups[0]);
            }
            else
            {
                // 项目还没打过标 → 清空 + 显示空态（沿用现有 IsEmpty 逻辑）
                Trace.WriteLine("[Gallery] TagGroups 为空, 清空 CurrentMediaFiles");
                SelectedTag = null;
                CurrentMediaFiles.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // 忽略取消（OnGroupModeChanged 切走时会触发）
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Gallery] LoadTagGroupsAsync 失败: {ex.Message}");
            LoadErrorMessage = $"加载标签失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 选中某个 tag → 调 GetByTagAsync + 反推 UploadStatus + 灌入 CurrentMediaFiles。
    /// 镜像 LoadDateAsync 模式（cancel-and-restart + UploadStatus 派生）。
    /// </summary>
    [RelayCommand]
    private async Task SelectTagAsync(TagGroupItem? group)
    {
        if (group == null)
        {
            Trace.WriteLine("[Gallery] SelectTagAsync 早返回: group 为 null");
            return;
        }

        Trace.WriteLine($"[Gallery] SelectTagAsync: tag='{group.Tag}', count={group.Count}");

        if (string.IsNullOrEmpty(ProjectPath)) return;

        // 取消之前的加载 + 切 tag 时退出多选模式
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ExitMultiSelect();
        SelectedTag = group.Tag;

        try
        {
            IsLoadingMedia = true;

            var files = await _mediaRepo.GetByTagAsync(ProjectPath, group.Tag, SortMode, ct);
            Trace.WriteLine($"[Gallery] GetByTagAsync 返回: {files.Count} 个文件, SortMode={SortMode}");

            // 反推 UploadStatus（跟 LoadDateAsync 一致）
            IReadOnlyList<UploadJob> jobs;
            try
            {
                jobs = await _uploadJobRepo.GetInProgressAsync(ProjectPath, ct);
            }
            catch
            {
                jobs = Array.Empty<UploadJob>();
            }
            var pausedSet = new HashSet<string>(
                jobs.Select(j => j.RelativePath),
                StringComparer.OrdinalIgnoreCase);

            CurrentMediaFiles.Clear();
            foreach (var file in files)
            {
                if (file.IsUploaded)
                {
                    file.UploadStatus = UploadStatus.Uploaded;
                }
                else if (pausedSet.Contains(file.RelativePath))
                {
                    file.UploadStatus = UploadStatus.Paused;
                }
                else
                {
                    file.UploadStatus = UploadStatus.NotUploaded;
                }
                CurrentMediaFiles.Add(file);
            }

            IsLoadingMedia = false;
        }
        catch (OperationCanceledException)
        {
            // 忽略取消
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Gallery] SelectTagAsync 失败: {ex.Message}");
            LoadErrorMessage = $"加载标签文件失败：{ex.Message}";
            IsLoadingMedia = false;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        await InitializeAsync();
    }

    // v0.11: 上传完自动刷新媒体（spec §3 延伸需求）
    // 多次上传合并成一次扫描：2s 内连拍 30 张也只跑一次 RefreshAsync。
    // IsScanning 时跳过（用户手动 Refresh 在跑就别打扰）。
    private CancellationTokenSource? _autoRefreshDebounceCts;

    public void RequestRefreshDebounced(int delayMs = 2000)
    {
        // 取消上一轮 debounce 等待
        _autoRefreshDebounceCts?.Cancel();
        _autoRefreshDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _autoRefreshDebounceCts = cts;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, ct);
                if (ct.IsCancellationRequested) return;
                if (IsScanning)
                {
                    Trace.WriteLine("[Gallery] RequestRefreshDebounced skip: manual scan in progress");
                    return;
                }
                if (string.IsNullOrEmpty(ProjectPath))
                {
                    Trace.WriteLine("[Gallery] RequestRefreshDebounced skip: no ProjectPath");
                    return;
                }
                Trace.WriteLine("[Gallery] auto refresh start (triggered by upload)");
                await RefreshAsync();
            }
            catch (OperationCanceledException)
            {
                // 新一轮上传把它取消了，正常路径
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Gallery] auto refresh err: {ex.Message}");
            }
        }, ct);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(ProjectPath))
        {
            return;
        }

        RefreshState = Models.RefreshState.Scanning;
        IsScanning = true;
        ScanProgress = new ScanProgress();
        ScanStatusMessage = null;

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgress = p;
            if (p.Total > 0)
            {
                ScanStatusMessage = $"扫描中 {p.Processed} / {p.Total} · 当前文件：{p.CurrentFile}";
            }
            else
            {
                ScanStatusMessage = "正在扫描...";
            }
        });

        try
        {
            var result = await _mediaRepo.ScanDirectoryAsync(ProjectPath, progress, _cts?.Token ?? default);

            ScanStatusMessage = "正在生成缩略图...";
            await _mediaRepo.GenerateThumbnailsAsync(ProjectPath, _thumbnailService, progress, _cts?.Token ?? default);

            await InitializeAsync();

            RefreshState = Models.RefreshState.Completed;
            ScanStatusMessage = $"扫描完成 · 共 {result.Processed} 个文件，新增 {result.NewFiles}，更新 {result.UpdatedFiles}";
        }
        catch (OperationCanceledException)
        {
            RefreshState = Models.RefreshState.Idle;
            ScanStatusMessage = $"已停止，扫描了 {ScanProgress.Processed} / {ScanProgress.Total}";
        }
        catch (Exception ex)
        {
            RefreshState = Models.RefreshState.Idle;
            ScanStatusMessage = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    partial void OnSelectedDateChanged(TimelineEntry? value)
    {
    }

    partial void OnRefreshStateChanged(RefreshState value)
    {
        if (value == RefreshState.Completed)
        {
            ScanStatusMessage = $"扫描完成 · 共 {ScanProgress?.Total} 个文件";
            _ = Task.Delay(2000).ContinueWith(_ => ScanStatusMessage = null);
        }
    }

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        // 退出多选时清空选中
        if (!value)
        {
            SelectedFiles.Clear();
        }
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
    }

    // === v2.3 多选模式命令 ===

    [RelayCommand]
    private void EnterMultiSelect()
    {
        IsMultiSelectMode = true;
    }

    [RelayCommand]
    private void ExitMultiSelect()
    {
        IsMultiSelectMode = false;
    }

    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAll()
    {
        if (!IsMultiSelectMode) return;

        BatchUpdateSelectedFiles(() =>
        {
            foreach (var mf in CurrentMediaFiles)
            {
                if (!SelectedFiles.Contains(mf))
                {
                    SelectedFiles.Add(mf);
                }
            }
        });
    }

    private bool CanSelectAll() => IsMultiSelectMode && CurrentMediaFiles.Count > 0;

    [RelayCommand(CanExecute = nameof(CanInvertSelection))]
    private void InvertSelection()
    {
        if (!IsMultiSelectMode) return;

        BatchUpdateSelectedFiles(() =>
        {
            foreach (var mf in CurrentMediaFiles)
            {
                if (SelectedFiles.Contains(mf))
                {
                    SelectedFiles.Remove(mf);
                }
                else
                {
                    SelectedFiles.Add(mf);
                }
            }
        });
    }

    private bool CanInvertSelection() => IsMultiSelectMode && CurrentMediaFiles.Count > 0;

    /// <summary>
    /// 批量修改 SelectedFiles：先 unsubscribe CollectionChanged 抑制逐条同步，
    /// 跑完 callback 后 re-subscribe，再手动把全部 mf.IsSelected 同步成与 SelectedFiles 一致，
    /// 并触发一次 Reset 让 VM 的 SelectedCount / IsBatchActionEnabled 重新计算。
    /// 避免逐条 Add/Remove 触发 N 次 PropertyChanged 让 UI 抖动。
    /// </summary>
    private void BatchUpdateSelectedFiles(Action mutate)
    {
        SelectedFiles.CollectionChanged -= OnSelectedFilesChanged;

        try
        {
            mutate();
        }
        finally
        {
            SelectedFiles.CollectionChanged += OnSelectedFilesChanged;
        }

        // 手动同步 mf.IsSelected（基于 SelectedFiles 当前的真相）
        foreach (var mf in CurrentMediaFiles)
        {
            var shouldBeSelected = SelectedFiles.Contains(mf);
            if (mf.IsSelected != shouldBeSelected)
            {
                mf.IsSelected = shouldBeSelected;
            }
        }

        // 通知 VM 自身的属性变化（SelectedCount / IsBatchActionEnabled）
        OnSelectedFilesChanged(
            SelectedFiles,
            new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }

    [RelayCommand]
    private void ToggleSelection(MediaFile? file)
    {
        if (file == null) return;

        if (!IsMultiSelectMode)
        {
            // 非多选模式：单击无操作（v2.3: 单击仅做选中，不做预览）
            return;
        }

        if (SelectedFiles.Contains(file))
        {
            SelectedFiles.Remove(file);
        }
        else
        {
            SelectedFiles.Add(file);
        }
    }

    [RelayCommand]
    private async Task BatchUpload()
    {
        if (IsUploading || !IsBatchActionEnabled) return;

        var storage = _ossFactory.TryCreate();
        if (storage == null)
        {
            await PromptOssNotConfiguredAsync();
            return;
        }

        var allSelected = SelectedFiles.ToList();
        // 过滤已上传（IsUploaded 是 DB 持久化字段；进 Gallery 时由 LoadDateAsync 反推 UploadStatus=Uploaded）
        var files = allSelected.Where(f => !f.IsUploaded).ToList();
        var skipped = allSelected.Count - files.Count;

        ExitMultiSelect();

        if (files.Count == 0)
        {
            ShowToast($"所选 {skipped} 个文件均已上传，无需重复上传");
            return;
        }

        ShowToast(skipped > 0
            ? $"跳过 {skipped} 个已上传，开始上传 {files.Count} 个文件…"
            : $"开始上传 {files.Count} 个文件…");

        var ossCfg = await GetOssConfigSnapshotAsync();
        await UploadManyAsync(files, storage, ossCfg);
    }

    [RelayCommand]
    private void BatchDelete()
    {
        if (!IsBatchActionEnabled) return;
        _ = BatchDeleteCoreAsync();
    }

    /// <summary>
    /// 软删除核心逻辑（spec doc/14-delete-and-trash.md §5.1 BatchDelete）。
    /// 拆成 async 单独方法方便 _ = BatchDeleteCoreAsync() 异步触发。
    ///
    /// 决策点：删除有 in-progress upload job 的文件时——
    ///   查 upload_jobs 表，若有 Uploading/Paused 状态 → 弹窗二次确认"取消上传并删除" / "取消"。
    ///   选前者 → 调 AbortMultipart + DeleteByFileAsync + SoftDeleteAsync（顺序：清 job → 标 deleted）。
    /// </summary>
    private async Task BatchDeleteCoreAsync()
    {
        var files = SelectedFiles.ToList();
        var count = files.Count;
        Trace.WriteLine($"[Gallery] BatchDelete: 用户请求删除 {count} 个文件");

        // 1) 二次确认
        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        var confirmed = await DialogHelper.ShowConfirmAsync(
            window,
            title: "移入垃圾筒",
            message: $"确定将 {count} 个文件移入垃圾筒？\n可在「垃圾筒」页恢复或彻底删除。",
            primaryButtonText: "移入垃圾筒",
            secondaryButtonText: "取消");

        if (!confirmed)
        {
            Trace.WriteLine($"[Gallery] BatchDelete: 用户取消");
            return;
        }

        // 2) 退出多选（要先 ExitMultiSelect 让 SelectedFiles 清空，避免后续 SoftDelete 还在 IsBatchActionEnabled 状态）
        ExitMultiSelect();

        // 3) 检查 in-progress upload job —— 决策点 #1
        var inProgressCount = await CountInProgressUploadsAsync(files);
        if (inProgressCount > 0)
        {
            var proceed = await DialogHelper.ShowConfirmAsync(
                window,
                title: "有未完成的上传",
                message: $"其中 {inProgressCount} 个文件正在上传。\n删除将一并取消上传并清理续传任务。",
                primaryButtonText: "取消上传并删除",
                secondaryButtonText: "取消");

            if (!proceed)
            {
                Trace.WriteLine($"[Gallery] BatchDelete: 用户在 upload 检查处取消");
                return;
            }
        }

        // 4) 执行：先 Abort + 清 upload_job（如果存在），再 SoftDelete
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var softDeleted = 0;
        var failed = 0;

        foreach (var file in files)
        {
            try
            {
                // 清 upload_job（如果有 in-progress）
                var job = await _uploadJobRepo.GetByFileAsync(file.ProjectPath, file.RelativePath);
                if (job != null)
                {
                    // 尝试 Abort multipart（不强制成功 — 即使 Abort 失败也要继续 SoftDelete，
                    // 否则 job 留着下次 resume 时找不到 media_files 行，体验更差）
                    try
                    {
                        var handle = new MultipartHandle
                        {
                            ObjectKey = job.ObjectKey,
                            UploadId = job.UploadId,
                        };
                        // IOssStorage 没注入到 Gallery —— Abort 走 OssStorageFactory
                        var storage = _ossFactory.TryCreate();
                        if (storage != null)
                        {
                            await storage.AbortMultipartAsync(handle);
                        }
                    }
                    catch (Exception abortEx)
                    {
                        Trace.WriteLine($"[Gallery] BatchDelete: Abort multipart 失败 (id={file.Id}): {abortEx.Message}");
                    }

                    await _uploadJobRepo.DeleteByFileAsync(file.ProjectPath, file.RelativePath);
                }

                await _mediaRepo.SoftDeleteAsync(file.Id, now);
                softDeleted++;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Gallery] BatchDelete: SoftDelete 失败 id={file.Id}: {ex.Message}");
                failed++;
            }
        }

        // 5) 从 CurrentMediaFiles 移除已删除的（不重新查 DB —— 让 UI 立刻响应）
        foreach (var file in files)
        {
            CurrentMediaFiles.Remove(file);
        }

        var msg = failed == 0
            ? $"已将 {softDeleted} 个文件移入垃圾筒"
            : $"已将 {softDeleted} 个文件移入垃圾筒（{failed} 个失败）";
        ShowToast(msg);
        Trace.WriteLine($"[Gallery] BatchDelete: 完成 softDeleted={softDeleted}, failed={failed}");
    }

    /// <summary>
    /// 统计 files 中有 in-progress upload job 的数量（Uploading 或 Paused 状态）。
    /// 用于删除前的二次确认。
    /// </summary>
    private async Task<int> CountInProgressUploadsAsync(IEnumerable<MediaFile> files)
    {
        var count = 0;
        foreach (var file in files)
        {
            // UploadStatus 是 UI 瞬时态，进 Gallery 时反推过；NotUploaded 表示无 job 或已结束
            if (file.UploadStatus == UploadStatus.Uploading || file.UploadStatus == UploadStatus.Paused)
            {
                count++;
            }
        }
        return await Task.FromResult(count);
    }

    /// <summary>
    /// 单文件软删除（右键菜单「删除」调用，spec §5.2 DeleteSingle）。
    /// in-progress upload 检查同 BatchDelete 路径。
    /// </summary>
    private async Task DeleteSingleCoreAsync(MediaFile? file)
    {
        if (file == null || !file.LocalExists) return;

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        Trace.WriteLine($"[Gallery] DeleteSingle: file={file.FileName} (id={file.Id})");

        var confirmed = await DialogHelper.ShowConfirmAsync(
            window,
            title: "移入垃圾筒",
            message: $"确定将「{file.FileName}」移入垃圾筒？",
            primaryButtonText: "移入垃圾筒",
            secondaryButtonText: "取消");

        if (!confirmed) return;

        // in-progress upload 检查
        if (file.UploadStatus == UploadStatus.Uploading || file.UploadStatus == UploadStatus.Paused)
        {
            var proceed = await DialogHelper.ShowConfirmAsync(
                window,
                title: "有未完成的上传",
                message: $"该文件正在上传。\n删除将一并取消上传并清理续传任务。",
                primaryButtonText: "取消上传并删除",
                secondaryButtonText: "取消");

            if (!proceed) return;
        }

        try
        {
            var job = await _uploadJobRepo.GetByFileAsync(file.ProjectPath, file.RelativePath);
            if (job != null)
            {
                try
                {
                    var storage = _ossFactory.TryCreate();
                    if (storage != null)
                    {
                        await storage.AbortMultipartAsync(new MultipartHandle
                        {
                            ObjectKey = job.ObjectKey,
                            UploadId = job.UploadId,
                        });
                    }
                }
                catch (Exception abortEx)
                {
                    Trace.WriteLine($"[Gallery] DeleteSingle: Abort 失败: {abortEx.Message}");
                }
                await _uploadJobRepo.DeleteByFileAsync(file.ProjectPath, file.RelativePath);
            }

            await _mediaRepo.SoftDeleteAsync(file.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            CurrentMediaFiles.Remove(file);
            ShowToast($"已将 {file.FileName} 移入垃圾筒");
            Trace.WriteLine($"[Gallery] DeleteSingle: 完成 id={file.Id}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Gallery] DeleteSingle: 失败 id={file.Id}: {ex.Message}");
            ShowToast($"删除失败: {ex.Message}");
        }
    }

    // === v0.8 释放本地空间（spec §6 Gallery 侧独立操作，不入垃圾筒） ===

    [RelayCommand]
    private void BatchFreeUpSpace()
    {
        if (!IsBatchActionEnabled) return;
        _ = BatchFreeUpSpaceCoreAsync();
    }

    /// <summary>
    /// 批量释放本地空间（spec §6.2）。
    /// 条件：file.IsUploaded && file.LocalExists（已上传且本地仍在）。
    /// 不动 DB 行的 deleted_at，不动云端 —— 仅 File.Delete + local_exists = 0。
    /// </summary>
    private async Task BatchFreeUpSpaceCoreAsync()
    {
        var files = SelectedFiles.Where(f => f.IsUploaded && f.LocalExists).ToList();
        var skipped = SelectedFiles.Count - files.Count;

        Trace.WriteLine($"[Gallery] BatchFreeUpSpace: 选中 {SelectedFiles.Count} 个, 可释放 {files.Count} 个, 跳过 {skipped}");

        if (files.Count == 0)
        {
            ExitMultiSelect();
            ShowToast(skipped > 0
                ? $"所选 {skipped} 个文件无需释放（未上传或本地已缺失）"
                : "未选中文件");
            return;
        }

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        var confirmed = await DialogHelper.ShowConfirmAsync(
            window,
            title: "释放本地空间",
            message: $"将删除 {files.Count} 个文件（云端保留）。\n可稍后从垃圾筒 → 下载重新拉回。",
            primaryButtonText: "释放",
            secondaryButtonText: "取消");

        if (!confirmed) return;

        ExitMultiSelect();

        var released = 0;
        var failed = 0;
        foreach (var file in files)
        {
            try
            {
                DeleteLocalFile(file);
                file.LocalExists = false;
                await _mediaRepo.UpdateLocalExistsAsync(file.Id, false);
                released++;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Gallery] BatchFreeUpSpace: 失败 id={file.Id}: {ex.Message}");
                failed++;
            }
        }

        var msg = failed == 0
            ? $"已释放 {released} 个文件"
            : $"已释放 {released} 个文件（{failed} 个失败）";
        ShowToast(msg);
        Trace.WriteLine($"[Gallery] BatchFreeUpSpace: 完成 released={released}, failed={failed}");
    }

    /// <summary>
    /// 单文件释放本地空间（右键菜单「释放本地空间」调用，spec §6.1）。
    /// </summary>
    [RelayCommand]
    private async Task FreeUpSpaceAsync(MediaFile? file)
    {
        if (file == null || !file.IsUploaded || !file.LocalExists) return;

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        Trace.WriteLine($"[Gallery] FreeUpSpace: file={file.FileName} (id={file.Id})");

        var confirmed = await DialogHelper.ShowConfirmAsync(
            window,
            title: "释放本地空间",
            message: $"将删除本地文件「{file.FileName}」\n云端保留，可稍后重新下载。",
            primaryButtonText: "释放",
            secondaryButtonText: "取消");

        if (!confirmed) return;

        try
        {
            DeleteLocalFile(file);
            file.LocalExists = false;
            await _mediaRepo.UpdateLocalExistsAsync(file.Id, false);
            ShowToast($"已释放 {file.FileName}");
            Trace.WriteLine($"[Gallery] FreeUpSpace: 完成 id={file.Id}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Gallery] FreeUpSpace: 失败 id={file.Id}: {ex.Message}");
            ShowToast($"释放失败: {ex.Message}");
        }
    }

    /// <summary>删除本地文件，FileNotFound 视为已删（幂等）。</summary>
    private static void DeleteLocalFile(MediaFile file)
    {
        var fullPath = Path.Combine(file.ProjectPath, file.RelativePath);
        if (!File.Exists(fullPath)) return;
        try
        {
            File.Delete(fullPath);
        }
        catch (FileNotFoundException)
        {
            // 用户已手动删了，幂等
        }
    }

    // === v0.6 AI 打标命令（spec doc/12-ai-toolbar-buttons.md §3.3.4） ===

    [RelayCommand]
    private async Task BatchTag()
    {
        if (IsTagging || !IsBatchActionEnabled) return;

        // 1. AI 配置检查（spec §3.3.4 BatchTag）
        AIConfig aiCfg;
        try
        {
            aiCfg = await _configService.GetAsync<AIConfig>(ConfigKeys.AI) ?? new AIConfig();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Gallery] BatchTag: 读 AIConfig 失败: {ex.Message}");
            ShowToast("读取 AI 配置失败");
            return;
        }
        if (string.IsNullOrWhiteSpace(aiCfg.ApiKey) || string.IsNullOrWhiteSpace(aiCfg.Protocol))
        {
            ShowToast("AI 未配置，请在设置页填写 API Key 和协议");
            return;
        }

        // 2. 过滤本地不存在的文件
        var allSelected = SelectedFiles.ToList();
        var files = allSelected.Where(f => f.LocalExists).ToList();
        var skipped = allSelected.Count - files.Count;

        ExitMultiSelect();

        if (files.Count == 0)
        {
            ShowToast(skipped > 0 ? "所选文件本地不存在，无法打标" : "未选中文件");
            return;
        }

        ShowToast(skipped > 0
            ? $"跳过 {skipped} 个本地缺失，开始打标 {files.Count} 个文件…"
            : $"开始打标 {files.Count} 个文件…");

        await BatchTagCoreAsync(files, aiCfg);
    }

    [RelayCommand(CanExecute = nameof(CanCancelTag))]
    private void CancelTag()
    {
        _tagCts?.Cancel();
        Trace.WriteLine("[Gallery] CancelTag: 用户请求取消打标");
    }
    private bool CanCancelTag() => IsTagging;

    [RelayCommand]
    private async Task TagSingle(MediaFile? file)
    {
        if (file == null || !file.LocalExists || IsTagging) return;

        AIConfig aiCfg;
        try
        {
            aiCfg = await _configService.GetAsync<AIConfig>(ConfigKeys.AI) ?? new AIConfig();
        }
        catch
        {
            ShowToast("读取 AI 配置失败");
            return;
        }
        if (string.IsNullOrWhiteSpace(aiCfg.ApiKey) || string.IsNullOrWhiteSpace(aiCfg.Protocol))
        {
            ShowToast("AI 未配置");
            return;
        }

        ShowToast($"开始打标 {file.FileName}…");
        await BatchTagCoreAsync(new List<MediaFile> { file }, aiCfg);
    }

    /// <summary>
    /// 打标核心循环：串行调用 AI + 200ms 节流 + 失败汇总 + 成功写 DB。
    /// 镜像 UploadManyAsync 模式，但用 _tagCts（独立）保证切日期/刷新不取消打标。
    /// </summary>
    private async Task BatchTagCoreAsync(IList<MediaFile> files, AIConfig aiCfg)
    {
        IsTagging = true;
        TagCompletedCount = 0;
        TagTotalCount = files.Count;
        _tagCts = new CancellationTokenSource();
        var ct = _tagCts.Token;

        var ok = 0;
        var fail = 0;
        var cancelled = 0;
        var errors = new List<(string FileName, string Reason)>();
        var fatalError = (string?)null;

        Trace.WriteLine($"[Gallery] BatchTag 启动: files={files.Count}, protocol={aiCfg.Protocol}, model={aiCfg.Model}");

        try
        {
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var (result, failure) = await _aiTagger.TagFileAsync(file, aiCfg, ct);
                    if (ct.IsCancellationRequested) { cancelled++; break; }

                    if (failure != null)
                    {
                        file.TagError = TruncateError(failure.Reason);
                        if (failure.IsFatal)
                        {
                            fatalError = failure.Reason;
                            ShowToast($"AI 错误：{failure.Reason}");
                            break;
                        }
                        errors.Add((file.FileName, failure.Reason));
                    }
                    else if (result != null)
                    {
                        file.Tags = result.Tags.ToList();
                        file.QualityTags = result.QualityTags.ToList();
                        file.Score = result.Score;
                        file.TagError = null;
                        await _mediaRepo.UpdateTagAsync(
                            file.Id,
                            result.Tags,
                            result.QualityTags,
                            result.Score,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            null,
                            ct);
                        ok++;
                    }
                    else
                    {
                        errors.Add((file.FileName, "AI 返回为空"));
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled++;
                    break;
                }
                catch (Exception ex)
                {
                    file.TagError = TruncateError(ex.Message);
                    errors.Add((file.FileName, ex.Message));
                }

                TagCompletedCount++;

                // 节流：避免连续打爆 AI。CancellationToken 让 Task.Delay 也能被取消。
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { cancelled++; break; }
            }
        }
        finally
        {
            _tagCts?.Dispose();
            _tagCts = null;
            IsTagging = false;
            Trace.WriteLine($"[Gallery] BatchTag 结束: ok={ok}, fail={fail}, cancelled={cancelled}, total={files.Count}");
        }

        // 摘要 toast
        if (fatalError != null)
        {
            // fatal 在循环内已 ShowToast，这里不重复
        }
        else if (cancelled > 0)
        {
            ShowToast($"打标已取消（完成 {ok + fail}/{files.Count}）");
        }
        else if (fail == 0)
        {
            ShowToast($"打标完成：{ok} 个文件");
        }
        else
        {
            ShowToast($"打标完成：成功 {ok}，失败 {fail}");
        }

        // 失败详情弹窗（多的时候）
        if (errors.Count > 0)
        {
            var window = DialogHelper.GetMainWindow();
            if (window == null) return;
            var sb = new StringBuilder();
            foreach (var (name, reason) in errors.Take(20))
            {
                sb.AppendLine($"• {name}: {reason}");
            }
            if (errors.Count > 20) sb.AppendLine($"…及其他 {errors.Count - 20} 项");
            await DialogHelper.ShowAlertAsync(window, $"打标失败（{errors.Count}）", sb.ToString());
        }

        // 排序模式是按评分时，打标完重排一次让用户立刻看到效果
        if (ok > 0 && SortMode == SortMode.ScoreDesc)
        {
            _ = SelectAsync(SelectedDate);
        }
    }

    private static string TruncateError(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length > 80 ? s[..80] + "…" : s);

    [RelayCommand]
    private void OpenInFolder(MediaFile? file)
    {
        if (file == null) return;

        try
        {
            var absolutePath = Path.Combine(file.ProjectPath, file.RelativePath);
            _systemShell.RevealInFolder(absolutePath);
        }
        catch (Exception ex)
        {
            ShowToast($"打开失败：{ex.Message}");
        }
    }

    /// <summary>
    /// v0.11: 双击 photo/video tile 打开灯箱预览窗口（图片 + 视频缩略图）。
    /// 视频文件在灯箱里只显示缩略图 + ▶ overlay，用户需播放时点底栏「打开外部」
    /// 或按 Space 键（spec §5）。
    /// 非模态：允许多个灯箱同时开，Gallery 切日期不影响当前灯箱。
    /// </summary>
    [RelayCommand]
    private void Preview(MediaFile? file)
    {
        if (file == null) return;

        // 拿当前列表的快照（图片 + 视频混合）+ 当前文件索引（spec §6.1）
        var files = CurrentMediaFiles.ToList();
        var index = files.IndexOf(file);
        if (index < 0)
        {
            Trace.WriteLine($"[Gallery] Preview: file not found in CurrentMediaFiles: {file.FileName}");
            return;
        }

        Trace.WriteLine($"[Gallery] Preview: opening lightbox for {file.FileName} (index={index}/{files.Count - 1}, type={file.MediaType})");

        var lightboxVm = new LightboxViewModel(files, index, _systemShell, _mediaRepo, this);
        var window = new LightboxWindow { DataContext = lightboxVm };
        // 非模态 Show()：不阻塞 Gallery，用户可在灯箱和 Gallery 间切换
        window.Show();
    }

    /// <summary>
    /// v0.12 手动编辑标签（spec doc/15-manual-tag-edit.md §4）：右键 photo tile → 调模态弹窗。
    /// CanExecute = CanEditTagsSingle：文件非空、未软删除、AI 没在打标。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditTagsSingle))]
    private async Task EditTagsSingleAsync(MediaFile? file)
    {
        if (file == null || file.DeletedAt != null) return;
        if (IsTagging) return;
        if (_mediaRepo == null) return;

        Trace.WriteLine($"[Gallery] EditTagsSingle: file={file.FileName}, currentTags={file.Tags?.Count ?? 0}");

        var dialogVm = new EditTagsDialogViewModel(file, _mediaRepo, this);
        var dialog = new EditTagsDialog(dialogVm);
        var owner = GetMainWindow();
        // owner = null 时 Avalonia 11 自动 fallback 到 CenterScreen
        await dialog.ShowDialog(owner!);

        if (dialogVm.SavedTags != null)
        {
            ShowToast($"已更新 {file.FileName} 的标签");
            Trace.WriteLine($"[Gallery] EditTagsSingle: saved, fileId={file.Id}, newTags={dialogVm.SavedTags.Count}");
        }
        else
        {
            Trace.WriteLine($"[Gallery] EditTagsSingle: cancelled or failed, fileId={file.Id}");
        }
    }

    private bool CanEditTagsSingle(MediaFile? file) =>
        file != null && file.DeletedAt == null && !IsTagging;

    /// <summary>
    /// v0.12 边界锁状态查询（spec §7）：供右键菜单 / 工具栏按钮 IsEnabled 用。
    /// 与 CanEditTagsSingle 同语义，加 public 包装让 code-behind 右键菜单构建能用。
    /// 原因：[RelayCommand] 生成的 private CanExecute 方法不能直接外部访问。
    /// </summary>
    public bool CanEditTagsSingleForMenu(MediaFile? file) => CanEditTagsSingle(file);

    /// <summary>
    /// v0.12 批量编辑标签（spec doc/15-manual-tag-edit.md §5 / §7）：多选模式下工具栏按钮触发。
    /// CanExecute = IsBatchActionEnabled（已包含多选 + 非打标 + 非上传）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsBatchActionEnabled))]
    private async Task EditTagsBatchAsync()
    {
        if (SelectedFiles.Count == 0) return;
        if (_mediaRepo == null) return;

        var files = SelectedFiles.ToList();
        Trace.WriteLine($"[Gallery] EditTagsBatch: files={files.Count}");

        var dialogVm = new EditTagsBatchDialogViewModel(files, _mediaRepo, this);
        var dialog = new EditTagsBatchDialog(dialogVm);
        var owner = GetMainWindow();
        await dialog.ShowDialog(owner!);

        Trace.WriteLine($"[Gallery] EditTagsBatch: applied={dialogVm.Applied}, fileCount={files.Count}");
    }

    /// <summary>
    /// 取主窗口（Avalonia 11 ApplicationLifetime）作为 dialog owner。
    /// null 时 dialog 自动 CenterScreen fallback。
    /// </summary>
    private static Avalonia.Controls.Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    [RelayCommand]
    private async Task OpenFileAsync(MediaFile? file)
    {
        if (file == null) return;

        var localPath = Path.Combine(file.ProjectPath, file.RelativePath);

        // 1. 本地存在 → 直接打开
        if (file.LocalExists && File.Exists(localPath))
        {
            try
            {
                _systemShell.OpenWithDefaultApp(localPath);
            }
            catch (Exception ex)
            {
                ShowToast($"打开失败：{ex.Message}");
            }
            return;
        }

        // 2. 本地缺失 → 询问是否下载
        var window = DialogHelper.GetMainWindow();
        if (window == null)
        {
            ShowToast("无法弹出对话框（未找到主窗口）");
            return;
        }

        var sizeText = file.FileSize > 0 ? $"（{FormatSize(file.FileSize)}）" : "";
        var yes = await DialogHelper.ShowConfirmAsync(
            window,
            "本地文件不存在",
            $"「{file.FileName}」{sizeText} 本地不存在，要从云端下载吗？",
            "下载",
            "取消");

        if (!yes) return;

        ShowToast($"正在下载 {file.FileName}…");

        // 3. 走共享下载核心；下载完自动打开（保留 v0.8 之前的双击下载后即开行为）
        try
        {
            var success = await DownloadToLocalCoreAsync(file);
            if (success)
            {
                try
                {
                    _systemShell.OpenWithDefaultApp(localPath);
                }
                catch (Exception ex)
                {
                    ShowToast($"下载成功，但打开失败：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            ShowToast($"已取消下载：{file.FileName}");
        }
        catch (Exception ex)
        {
            ShowToast($"下载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 共享下载核心（spec doc/14-delete-and-trash.md §7.2）。
    /// 被 OpenFileAsync / DownloadSingle / BatchDownload 共用。
    /// 步骤：
    ///   1. 本地已存在 → 跳过 + 修正 local_exists
    ///   2. OSS 未配置 → 弹引导对话框
    ///   3. 拉取 OSS 对象到 localPath
    ///   4. 更新 LocalExists = true + DB
    ///   5. 重新生成缩略图（吞错，不阻塞主流程）
    /// </summary>
    private async Task<bool> DownloadToLocalCoreAsync(MediaFile file, CancellationToken ct = default)
    {
        if (file == null) return false;

        var localPath = Path.Combine(file.ProjectPath, file.RelativePath);

        // 1. 本地已存在 → 直接对齐 local_exists
        if (File.Exists(localPath))
        {
            if (!file.LocalExists)
            {
                file.LocalExists = true;
                await _mediaRepo.UpdateLocalExistsAsync(file.Id, true, ct);
            }
            return true;
        }

        // 2. OSS 配置检查
        var storage = _ossFactory.TryCreate();
        if (storage == null)
        {
            await PromptOssNotConfiguredAsync();
            return false;
        }

        // 3. 构建 objectKey + 下载
        var ossCfg = await GetOssConfigSnapshotAsync();
        var objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);

        // 确保目标目录存在（OSS 下载自身不创建）
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await storage.DownloadAsync(objectKey, localPath, ct);

        // 4. 更新本地状态 + DB
        file.LocalExists = true;
        await _mediaRepo.UpdateLocalExistsAsync(file.Id, true, ct);

        // 5. 重新生成缩略图（修复「路径有效但文件不存在」的死链）
        try
        {
            var newThumb = await _thumbnailService.GenerateThumbnailAsync(localPath, file.ProjectPath, ct);
            if (!string.IsNullOrEmpty(newThumb))
            {
                file.ThumbnailPath = newThumb;
            }
        }
        catch
        {
            // 缩略图失败不影响主流程，UI 用占位图标兜底
        }

        return true;
    }

    /// <summary>
    /// 单文件下载（右键菜单「下载到本地」，spec §7.3）。
    /// 条件：file.IsUploaded（云端有）+ !file.LocalExists（本地没有）。
    /// </summary>
    [RelayCommand]
    private async Task DownloadSingle(MediaFile? file)
    {
        if (file == null) return;
        if (!file.IsUploaded) return;            // 云端没有
        if (file.LocalExists)
        {
            ShowToast("本地已存在");
            return;
        }

        Trace.WriteLine($"[Gallery] DownloadSingle: file={file.FileName} (id={file.Id})");
        ShowToast($"正在下载 {file.FileName}…");

        try
        {
            var success = await DownloadToLocalCoreAsync(file);
            if (success)
            {
                ShowToast($"已下载：{file.FileName}");
                Trace.WriteLine($"[Gallery] DownloadSingle: 完成 id={file.Id}");
            }
        }
        catch (OperationCanceledException)
        {
            ShowToast($"已取消下载：{file.FileName}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Gallery] DownloadSingle: 失败 id={file.Id}: {ex.Message}");
            ShowToast($"下载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 批量下载（多选工具栏「批量下载」，spec §7.3）。
    /// 条件：f.IsUploaded && !f.LocalExists。
    /// 走 toast 渐进通知（"下载中 3/10…"），不做进度条（spec §7.6）。
    /// </summary>
    [RelayCommand]
    private void BatchDownload()
    {
        if (!IsBatchActionEnabled) return;
        _ = BatchDownloadCoreAsync();
    }

    private async Task BatchDownloadCoreAsync()
    {
        var candidates = SelectedFiles.Where(f => f.IsUploaded && !f.LocalExists).ToList();
        var total = candidates.Count;
        Trace.WriteLine($"[Gallery] BatchDownload: 选中 {SelectedFiles.Count} 个, 可下载 {total} 个");

        if (total == 0)
        {
            ExitMultiSelect();
            ShowToast("所选文件均已在本地");
            return;
        }

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        var confirmed = await DialogHelper.ShowConfirmAsync(
            window,
            title: "批量下载",
            message: $"将从云端下载 {total} 个文件，继续？",
            primaryButtonText: "下载",
            secondaryButtonText: "取消");

        if (!confirmed) return;

        ExitMultiSelect();

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        var completed = 0;
        var failed = 0;
        var cancelled = 0;

        try
        {
            foreach (var file in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    ShowToast($"下载中 {completed + failed + 1}/{total}：{file.FileName}");
                    var success = await DownloadToLocalCoreAsync(file, ct);
                    if (success)
                    {
                        completed++;
                        Trace.WriteLine($"[Gallery] BatchDownload: 完成 {completed}/{total} id={file.Id}");
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled++;
                    Trace.WriteLine($"[Gallery] BatchDownload: 取消 id={file.Id}");
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    Trace.WriteLine($"[Gallery] BatchDownload: 失败 id={file.Id}: {ex.Message}");
                }
            }
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }

        var summary = $"下载完成：{completed}/{total} 成功";
        if (failed > 0) summary += $"，{failed} 失败";
        if (cancelled > 0) summary += $"，{cancelled} 取消";
        ShowToast(summary);
        Trace.WriteLine($"[Gallery] BatchDownload: 总结 completed={completed}, failed={failed}, cancelled={cancelled}");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    [RelayCommand]
    private async Task UploadSingle(MediaFile? file)
    {
        if (file == null || IsUploading) return;

        var storage = _ossFactory.TryCreate();
        if (storage == null)
        {
            await PromptOssNotConfiguredAsync();
            return;
        }

        ShowToast($"开始上传 {file.FileName}…");

        var ossCfg = await GetOssConfigSnapshotAsync();
        await UploadManyAsync(new[] { file }, storage, ossCfg);
    }

    [RelayCommand]
    private void CancelUpload()
    {
        _uploadCts?.Cancel();
    }

    /// <summary>
    /// 启动恢复流程：把 upload_jobs 里的未完成任务投影到当前 Gallery 中的 MediaFile，
    /// 然后走 <see cref="UploadManyAsync"/>。找不到对应文件（可能已被删）→ 跳过。
    /// </summary>
    public async Task ResumeInterruptedAsync(IReadOnlyList<UploadJob> jobs)
    {
        if (IsUploading || jobs.Count == 0) return;

        var storage = _ossFactory.TryCreate();
        if (storage == null) return;

        var files = new List<MediaFile>();
        foreach (var job in jobs)
        {
            var mf = CurrentMediaFiles.FirstOrDefault(m =>
                string.Equals(m.ProjectPath, job.ProjectPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.RelativePath, job.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (mf != null) files.Add(mf);
        }

        if (files.Count == 0)
        {
            ShowToast("找不到可恢复的媒体文件（可能已删除）");
            return;
        }

        ShowToast($"开始恢复 {files.Count} 个上传…");
        var ossCfg = await GetOssConfigSnapshotAsync();
        await UploadManyAsync(files, storage, ossCfg);
    }

    // ===== 上传核心流程 =====

    private async Task<OssConfig> GetOssConfigSnapshotAsync()
    {
        // 拿到的是一个快照——后续 Settings 改配置不影响本次上传
        return (await _configService.GetAsync<OssConfig>(ConfigKeys.Oss)) ?? new OssConfig();
    }

    private async Task UploadManyAsync(IList<MediaFile> files, IOssStorage storage, OssConfig ossCfg)
    {
        // Debug 打印：本次上传用到的 OSS 配置（完整 key，方便核对 SignatureDoesNotMatch 等凭据问题）
        // 用 System.Diagnostics.Debug，IDE 调试输出窗口和 dotnet test 都能看到
        Debug.WriteLine($"[OSS Upload] provider={ossCfg.Provider}, files={files.Count}, threshold={storage.MultipartThresholdBytes}B");
        Debug.WriteLine($"[OSS Upload] config: region={ossCfg.Region}, bucket={ossCfg.Bucket}, " +
            $"accessKeyId={ossCfg.AccessKeyId}, accessKeySecret={ossCfg.AccessKeySecret}, " +
            $"pathPrefix='{ossCfg.PathPrefix}'");

        IsUploading = true;
        UploadCompletedCount = 0;
        UploadTotalCount = files.Count;

        _uploadCts = new CancellationTokenSource();
        var ct = _uploadCts.Token;

        var ok = 0;
        var fail = 0;
        var cancelled = 0;
        var errors = new List<(string fileName, string error)>();

        try
        {
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var result = await UploadOneAsync(f, storage, ossCfg, ct);
                    switch (result)
                    {
                        case UploadOneResult.Success: ok++; break;
                        case UploadOneResult.Cancelled: cancelled++; break;
                        default:
                            fail++;
                            errors.Add((f.FileName, f.UploadError ?? "未知错误"));
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    f.UploadStatus = UploadStatus.Failed;
                    f.UploadError = "已取消";
                    cancelled++;
                }
                catch (Exception ex)
                {
                    f.UploadStatus = UploadStatus.Failed;
                    f.UploadError = ex.Message;
                    fail++;
                    errors.Add((f.FileName, ex.Message));
                }

                UploadCompletedCount++;
            }
        }
        finally
        {
            _uploadCts?.Dispose();
            _uploadCts = null;
            IsUploading = false;
        }

        // 摘要 toast
        string summary;
        if (cancelled > 0)
            summary = $"已取消：成功 {ok}，失败 {fail}，取消 {cancelled}";
        else if (fail > 0)
            summary = $"上传完成：成功 {ok}，失败 {fail}";
        else
            summary = $"上传完成：{ok} 个";
        ShowToast(summary);

        // 失败详情：弹模态对话框列出每个失败文件 + 原因
        if (errors.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"共 {errors.Count} 个文件上传失败：");
            sb.AppendLine();
            foreach (var (name, err) in errors)
            {
                sb.AppendLine($"• {name}");
                sb.AppendLine($"  {err}");
            }
            if (errors.Count == 0 && ok == 0)
            {
                // 全失败的兜底
            }
            else if (ok > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"（其余 {ok} 个已成功）");
            }

            var window = DialogHelper.GetMainWindow();
            if (window != null)
            {
                await DialogHelper.ShowAlertAsync(window, $"上传失败（{errors.Count}）", sb.ToString());
            }
        }
    }

    private enum UploadOneResult { Success, Failed, Cancelled }

    private async Task<UploadOneResult> UploadOneAsync(MediaFile file, IOssStorage storage, OssConfig ossCfg, CancellationToken ct)
    {
        file.UploadStatus = UploadStatus.Uploading;
        file.UploadError = null;

        var localPath = Path.Combine(file.ProjectPath, file.RelativePath);
        if (!File.Exists(localPath))
        {
            file.UploadStatus = UploadStatus.Failed;
            file.UploadError = "本地文件不存在";
            return UploadOneResult.Failed;
        }

        var fileSize = new FileInfo(localPath).Length;
        var objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);

        // 查 upload_jobs 是否有未完成 job
        var existingJob = await _uploadJobRepo.GetByFileAsync(file.ProjectPath, file.RelativePath, ct);

        if (existingJob != null)
        {
            return await ResumeUploadAsync(file, storage, existingJob, fileSize, objectKey, ct);
        }

        // 没有 job：
        if (fileSize < storage.MultipartThresholdBytes)
        {
            return await UploadSinglePutAsync(file, storage, localPath, objectKey, ct);
        }
        return await UploadMultipartNewAsync(file, storage, localPath, objectKey, fileSize, ct);
    }

    private async Task<UploadOneResult> UploadSinglePutAsync(MediaFile file, IOssStorage storage, string localPath, string objectKey, CancellationToken ct)
    {
        var result = await storage.UploadAsync(localPath, objectKey, ct);
        if (result.Success)
        {
            var url = await storage.GetCoverUrlAsync(objectKey, TimeSpan.FromHours(1), ct);
            await ApplyUploadSuccessAsync(file, url);
            return UploadOneResult.Success;
        }
        file.UploadStatus = UploadStatus.Failed;
        file.UploadError = result.Error ?? "未知错误";
        return UploadOneResult.Failed;
    }

    private async Task<UploadOneResult> UploadMultipartNewAsync(
        MediaFile file, IOssStorage storage, string localPath, string objectKey, long fileSize, CancellationToken ct)
    {
        var handle = await storage.InitiateMultipartAsync(objectKey, ct);
        var now = DateTime.UtcNow;
        var job = new UploadJob
        {
            ProjectPath = file.ProjectPath,
            RelativePath = file.RelativePath,
            ObjectKey = objectKey,
            UploadId = handle.UploadId,
            FileSize = fileSize,
            PartSize = handle.PartSize,
            PartsUploaded = new List<UploadedPart>(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _uploadJobRepo.UpsertAsync(job, ct);

        try
        {
            var uploaded = await UploadMissingPartsAsync(file, storage, localPath, handle, fileSize, job, new HashSet<int>(), ct);
            var allParts = uploaded.OrderBy(p => p.PartNumber).ToList();
            await storage.CompleteMultipartAsync(handle, allParts, ct);

            var url = await storage.GetCoverUrlAsync(objectKey, TimeSpan.FromHours(1), ct);
            await ApplyUploadSuccessAsync(file, url);
            await _uploadJobRepo.DeleteAsync(job.Id, ct);
            return UploadOneResult.Success;
        }
        catch (OperationCanceledException)
        {
            // job 留着不删，下次续传
            throw;
        }
        catch
        {
            // 错误路径：job 留着，用户可手动重试
            throw;
        }
    }

    private async Task<UploadOneResult> ResumeUploadAsync(
        MediaFile file, IOssStorage storage, UploadJob job, long fileSize, string objectKey, CancellationToken ct)
    {
        var localPath = Path.Combine(file.ProjectPath, file.RelativePath);

        // 文件大小变了 → job 失效，删掉重新走全新路径
        if (job.FileSize != fileSize)
        {
            await _uploadJobRepo.DeleteAsync(job.Id, ct);
            file.UploadError = "本地文件已变更，重新上传";
            if (fileSize < storage.MultipartThresholdBytes)
            {
                return await UploadSinglePutAsync(file, storage, localPath, objectKey, ct);
            }
            return await UploadMultipartNewAsync(file, storage, localPath, objectKey, fileSize, ct);
        }

        var handle = new MultipartHandle
        {
            ObjectKey = job.ObjectKey,
            UploadId = job.UploadId,
            PartSize = job.PartSize,
        };

        // OSS 端拉已传分片（权威）。如果 job 已被 OSS 清理，ListParts 会抛错 → 删 DB job 重来
        IReadOnlyList<PartETag> ossParts;
        try
        {
            ossParts = await storage.ListPartsAsync(handle, ct);
        }
        catch
        {
            await _uploadJobRepo.DeleteAsync(job.Id, ct);
            file.UploadStatus = UploadStatus.Failed;
            file.UploadError = "OSS 端任务已失效，重新上传";
            return UploadOneResult.Failed;
        }

        // 合并：OSS 已传 ∪ DB 已传（DB 可能落后于 OSS，比如 DB 写失败过）
        var uploadedSet = new HashSet<int>(ossParts.Select(p => p.PartNumber));
        foreach (var p in job.PartsUploaded)
        {
            uploadedSet.Add(p.PartNumber);
        }

        // 同步 DB：把 OSS 端多出来的分片也写回 DB，避免下次续传重传
        job.PartsUploaded = ossParts.Select(p => new UploadedPart { PartNumber = p.PartNumber, ETag = p.ETag }).ToList();
        job.UpdatedAt = DateTime.UtcNow;
        await _uploadJobRepo.UpsertAsync(job, ct);

        // 传缺失分片
        var newParts = await UploadMissingPartsAsync(file, storage, localPath, handle, fileSize, job, uploadedSet, ct);

        // 合并最终 part 列表：oss parts + 本次新传（去重）
        var finalParts = new Dictionary<int, PartETag>();
        foreach (var p in ossParts) finalParts[p.PartNumber] = p;
        foreach (var p in newParts) finalParts[p.PartNumber] = p;

        var allParts = finalParts.Values.OrderBy(p => p.PartNumber).ToList();
        await storage.CompleteMultipartAsync(handle, allParts, ct);

        var url = await storage.GetCoverUrlAsync(objectKey, TimeSpan.FromHours(1), ct);
        await ApplyUploadSuccessAsync(file, url);
        await _uploadJobRepo.DeleteAsync(job.Id, ct);
        return UploadOneResult.Success;
    }

    private async Task<List<PartETag>> UploadMissingPartsAsync(
        MediaFile file, IOssStorage storage, string localPath,
        MultipartHandle handle, long fileSize, UploadJob job,
        HashSet<int> alreadyUploaded, CancellationToken ct)
    {
        var partSize = handle.PartSize;
        var partCount = (int)Math.Ceiling((double)fileSize / partSize);
        var uploaded = new List<PartETag>();

        await using var stream = File.OpenRead(localPath);

        for (int partNumber = 1; partNumber <= partCount; partNumber++)
        {
            ct.ThrowIfCancellationRequested();

            if (alreadyUploaded.Contains(partNumber))
            {
                continue;
            }

            var offset = (long)(partNumber - 1) * partSize;
            var length = (int)Math.Min(partSize, fileSize - offset);

            stream.Seek(offset, SeekOrigin.Begin);
            using var bounded = new BoundedReadStream(stream, length);
            var etag = await storage.UploadPartAsync(handle, partNumber, bounded, length, ct);
            uploaded.Add(etag);

            // 每片成功 → 立刻写 DB（崩溃后最多少传一片）
            job.PartsUploaded.Add(new UploadedPart { PartNumber = partNumber, ETag = etag.ETag });
            job.UpdatedAt = DateTime.UtcNow;
            await _uploadJobRepo.UpsertAsync(job, ct);
        }

        return uploaded;
    }

    private async Task ApplyUploadSuccessAsync(MediaFile file, string remoteUrl)
    {
        file.IsUploaded = true;
        file.UploadedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        file.RemoteUrl = remoteUrl;
        file.UploadStatus = UploadStatus.Uploaded;
        file.UploadError = null;

        try
        {
            await _mediaRepo.UpdateUploadStateAsync(file.Id, true, file.UploadedAt, remoteUrl, CancellationToken.None);
        }
        catch
        {
            // DB 写失败：UI 状态已正确，不撤销；下次重试会重新写。
            // 错误明细已在 BatchUpload 的 errors 列表里通过 toast 展示。
        }
    }

    [RelayCommand]
    private void DeleteSingle(MediaFile? file)
    {
        _ = DeleteSingleCoreAsync(file);
    }

    private void ShowToast(string message)
    {
        ToastMessage = message;
        _ = Task.Delay(3000).ContinueWith(_ => ToastMessage = null);
    }

    /// <summary>
    /// v0.12 手动编辑标签（spec doc/15-manual-tag-edit.md §6.2）：供 LightboxVM 等同进程 ViewModel
    /// 弹出 toast。内部复用 ShowToast（ToastMessage + 3s 自动清空）。
    /// 改 public 是因为 LightboxVM 在编辑失败时需要提示用户。
    /// </summary>
    public void ShowToastPublic(string message) => ShowToast(message);

    /// <summary>
    /// v0.12 手动编辑标签：文件主体 tag 变化时通知 GalleryVM 触发左栏 TagGroups 刷新。
    /// 走 500ms TagChangeDebouncer（spec §6.4）—— 连续编辑 N 张文件时合并成 1 次 DB 查询。
    /// 触发源：MediaFile.PropertyChanged("Tags")，见 OnMediaFilePropertyChanged。
    /// 也被 LightboxViewModel.SaveEditTagsAsync / EditTagsDialog / EditTagsBatch 直接调用。
    /// </summary>
    public void OnFileTagsChanged(MediaFile file)
    {
        if (file == null) return;
        Trace.WriteLine($"[Gallery] OnFileTagsChanged: file={file.FileName}, newTags={file.Tags?.Count ?? 0}");
        _tagChangeDebouncer.Trigger(async ct =>
        {
            Trace.WriteLine($"[Gallery] TagChangeDebouncer fired: reloading TagGroups");
            await LoadTagGroupsAsync(ct);
        });
    }

    /// <summary>
    /// OSS 未配置时唤起对话框。优先用注入的回调（MainWindow 提供，含跳转逻辑），
    /// 兜底降级为 toast（单元测试 / 异常路径不会崩）。
    /// </summary>
    private async Task PromptOssNotConfiguredAsync()
    {
        if (_onOssNotConfigured != null)
        {
            await _onOssNotConfigured();
        }
        else
        {
            ShowToast("OSS 未配置，请先到设置页填写");
        }
    }
}
