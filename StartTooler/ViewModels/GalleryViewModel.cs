using System;
using System.Collections.ObjectModel;
using System.IO;
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
    private string? _projectPath;
    private CancellationTokenSource? _cts;

    // === 数据源 ===
    public ObservableCollection<TimelineEntry> DateGroups { get; } = new();
    public ObservableCollection<MediaFile> CurrentMediaFiles { get; } = new();

    // === 选中态 ===
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

    public GalleryViewModel(IMediaRepository mediaRepo, IThumbnailService thumbnailService, IConfigService configService)
    {
        _mediaRepo = mediaRepo;
        _thumbnailService = thumbnailService;
        _configService = configService;
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

            // 批量替换：先清空再一次性添加，减少 UI 更新次数
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
            // 1. 扫描文件
            var result = await _mediaRepo.ScanDirectoryAsync(_projectPath, progress, _cts?.Token ?? default);

            // 2. 生成缩略图
            ScanStatusMessage = "正在生成缩略图...";
            await _mediaRepo.GenerateThumbnailsAsync(_projectPath, _thumbnailService, progress, _cts?.Token ?? default);

            // 3. 刷新列表
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
        // 不在这里加载，避免与 SelectCommand 重复调用
    }

    partial void OnRefreshStateChanged(RefreshState value)
    {
        if (value == RefreshState.Completed)
        {
            ScanStatusMessage = $"扫描完成 · 共 {ScanProgress?.Total} 个文件，新增 {ScanProgress?.Processed - ScanProgress?.Failed ?? 0}，更新 0";
            // 2s 后清空
            _ = Task.Delay(2000).ContinueWith(_ => ScanStatusMessage = null);
        }
    }

    public bool HasNoProject => string.IsNullOrEmpty(_projectPath);
    public bool IsEmpty => !IsLoadingDateGroups && DateGroups.Count == 0;
    public string? ProjectPath => _projectPath;

    public void StopScan()
    {
        _cts?.Cancel();
        RefreshState = RefreshState.Stopped;
        IsScanning = false;
        ScanStatusMessage = $"已停止，扫描了 {ScanProgress?.Processed ?? 0} / {ScanProgress?.Total ?? 0}";
        _ = Task.Delay(2000).ContinueWith(_ => ScanStatusMessage = null);
    }
}
