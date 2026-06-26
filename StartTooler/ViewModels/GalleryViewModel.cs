using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Data;
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
    private readonly Func<Task<bool>>? _onOssNotConfigured;
    private string? _projectPath;
    private CancellationTokenSource? _cts;

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

    public int SelectedCount => SelectedFiles.Count;
    public bool IsBatchActionEnabled => IsMultiSelectMode && SelectedFiles.Count > 0;
    public string? ProjectPath => _projectPath;
    public bool HasNoProject => string.IsNullOrEmpty(_projectPath);
    public bool IsEmpty => !IsLoadingDateGroups && DateGroups.Count == 0;

    public GalleryViewModel(
        IMediaRepository mediaRepo,
        IThumbnailService thumbnailService,
        IConfigService configService,
        ISystemShellService systemShell,
        IOssStorageFactory ossFactory,
        Func<Task<bool>>? onOssNotConfigured = null)
    {
        _mediaRepo = mediaRepo;
        _thumbnailService = thumbnailService;
        _configService = configService;
        _systemShell = systemShell;
        _ossFactory = ossFactory;
        _onOssNotConfigured = onOssNotConfigured;
        SelectedFiles.CollectionChanged += OnSelectedFilesChanged;
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
            _projectPath = projectConfig.CurrentDirectory;

            if (string.IsNullOrEmpty(_projectPath))
            {
                IsLoadingDateGroups = false;
                return;
            }

            // 加载日期分组
            var dateGroups = await _mediaRepo.GetDateGroupsAsync(_projectPath, ct);
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
        if (string.IsNullOrEmpty(_projectPath)) return;

        try
        {
            IsLoadingMedia = true;

            var files = await _mediaRepo.GetByDateAsync(_projectPath, entry.Date, ct);

            // 批量替换
            CurrentMediaFiles.Clear();
            foreach (var file in files)
            {
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
        if (string.IsNullOrEmpty(_projectPath))
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
            var result = await _mediaRepo.ScanDirectoryAsync(_projectPath, progress, _cts?.Token ?? default);

            ScanStatusMessage = "正在生成缩略图...";
            await _mediaRepo.GenerateThumbnailsAsync(_projectPath, _thumbnailService, progress, _cts?.Token ?? default);

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
        if (!IsBatchActionEnabled) return;

        var storage = _ossFactory.TryCreate();
        if (storage == null)
        {
            await PromptOssNotConfiguredAsync();
            return;
        }

        var files = SelectedFiles.ToList();
        var count = files.Count;
        ShowToast($"开始上传 {count} 个文件…");
        ExitMultiSelect();

        var ossCfg = (await _configService.GetAsync<OssConfig>(ConfigKeys.Oss)) ?? new OssConfig();

        var ok = 0;
        var fail = 0;
        foreach (var f in files)
        {
            try
            {
                var localPath = Path.Combine(f.ProjectPath, f.RelativePath);
                var key = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, f.RelativePath);

                var result = await storage.UploadAsync(localPath, key, _cts?.Token ?? default);
                if (result.Success)
                {
                    f.IsUploaded = true;
                    f.UploadedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    f.RemoteUrl = await storage.GetCoverUrlAsync(key, TimeSpan.FromHours(1), _cts?.Token ?? default);
                    ok++;
                }
                else
                {
                    fail++;
                }
            }
            catch
            {
                fail++;
            }
        }

        ShowToast(fail == 0 ? $"上传完成：{ok} 个" : $"上传完成：成功 {ok}，失败 {fail}");
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
    private void ViewFile(MediaFile? file)
    {
        if (file == null) return;
        ShowToast($"查看 {file.FileName}（待实现）");
    }

    [RelayCommand]
    private async Task UploadSingle(MediaFile? file)
    {
        if (file == null) return;

        var storage = _ossFactory.TryCreate();
        if (storage == null)
        {
            await PromptOssNotConfiguredAsync();
            return;
        }

        try
        {
            var localPath = Path.Combine(file.ProjectPath, file.RelativePath);
            var ossCfg = (await _configService.GetAsync<OssConfig>(ConfigKeys.Oss)) ?? new OssConfig();
            var key = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);

            ShowToast($"开始上传 {file.FileName}…");
            var result = await storage.UploadAsync(localPath, key, _cts?.Token ?? default);
            if (result.Success)
            {
                file.IsUploaded = true;
                file.UploadedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                file.RemoteUrl = await storage.GetCoverUrlAsync(key, TimeSpan.FromHours(1), _cts?.Token ?? default);
                ShowToast($"已上传 {file.FileName}");
            }
            else
            {
                ShowToast($"上传失败：{result.Error}");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"上传失败：{ex.Message}");
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
