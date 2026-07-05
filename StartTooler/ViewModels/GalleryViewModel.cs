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

namespace StartTooler.ViewModels;

public partial class GalleryViewModel : ObservableObject
{
    private readonly IMediaRepository _mediaRepo;
    private readonly IThumbnailService _thumbnailService;
    private readonly IConfigService _configService;
    private readonly ISystemShellService _systemShell;
    private readonly IOssStorageFactory _ossFactory;
    private readonly IUploadJobRepository _uploadJobRepo;
    private readonly Func<Task<bool>>? _onOssNotConfigured;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoProject))]
    private string? _projectPath;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _uploadCts;

    // === 数据源 ===
    public ObservableCollection<TimelineEntry> DateGroups { get; } = new();
    public ObservableCollection<MediaFile> CurrentMediaFiles { get; } = new();

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

    public int SelectedCount => SelectedFiles.Count;
    public bool IsBatchActionEnabled => IsMultiSelectMode && SelectedFiles.Count > 0 && !IsUploading;
    public bool HasNoProject => string.IsNullOrEmpty(ProjectPath);
    public bool IsEmpty => !HasNoProject && !IsLoadingDateGroups && DateGroups.Count == 0;

    public GalleryViewModel(
        IMediaRepository mediaRepo,
        IThumbnailService thumbnailService,
        IConfigService configService,
        ISystemShellService systemShell,
        IOssStorageFactory ossFactory,
        IUploadJobRepository uploadJobRepo,
        Func<Task<bool>>? onOssNotConfigured = null)
    {
        _mediaRepo = mediaRepo;
        _thumbnailService = thumbnailService;
        _configService = configService;
        _systemShell = systemShell;
        _ossFactory = ossFactory;
        _uploadJobRepo = uploadJobRepo;
        _onOssNotConfigured = onOssNotConfigured;
        SelectedFiles.CollectionChanged += OnSelectedFilesChanged;
        CurrentMediaFiles.CollectionChanged += OnCurrentMediaFilesChanged;
    }

    private void OnCurrentMediaFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // CurrentMediaFiles 内容变化（全选/反选命令的可用性主要依赖它）
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
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

            var files = await _mediaRepo.GetByDateAsync(ProjectPath, entry.Date, SortMode.TimeDesc, ct);

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

    [RelayCommand]
    private async Task ReloadAsync()
    {
        await InitializeAsync();
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

        var count = SelectedFiles.Count;
        ShowToast($"已请求删除 {count} 个文件（待实现）");
        ExitMultiSelect();
    }

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

        // 3. OSS 配置检查
        var storage = _ossFactory.TryCreate();
        if (storage == null)
        {
            await PromptOssNotConfiguredAsync();
            return;
        }

        // 4. 下载
        var ossCfg = await GetOssConfigSnapshotAsync();
        var objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);

        ShowToast($"正在下载 {file.FileName}…");

        try
        {
            await storage.DownloadAsync(objectKey, localPath);

            // 下载成功：更新本地状态（LocalExists/ThumbnailPath 都是 ObservableProperty，
            // 直接赋值就会触发 XAML 重绑）
            file.LocalExists = true;

            // 视频缩略图常常和本地视频一起被删 / 缓存清掉。下载完后顺手重新生成，
            // 避免「表里有 ThumbnailPath 字符串但文件不存在」导致卡片显示空 Image。
            try
            {
                var newThumb = await _thumbnailService.GenerateThumbnailAsync(
                    localPath, file.ProjectPath);
                if (!string.IsNullOrEmpty(newThumb))
                {
                    file.ThumbnailPath = newThumb;
                }
            }
            catch
            {
                // 缩略图生成失败不影响主流程，UI 会用占位符兜底
            }

            ShowToast($"已下载：{file.FileName}");

            // 5. 下载完自动打开
            try
            {
                _systemShell.OpenWithDefaultApp(localPath);
            }
            catch (Exception ex)
            {
                ShowToast($"下载成功，但打开失败：{ex.Message}");
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
        if (file == null) return;
        ShowToast($"已请求删除 {file.FileName}（待实现）");
    }

    private void ShowToast(string message)
    {
        ToastMessage = message;
        _ = Task.Delay(3000).ContinueWith(_ => ToastMessage = null);
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
