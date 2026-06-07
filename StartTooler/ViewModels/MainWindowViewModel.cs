using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly FileScanService _fileScanService;
    private readonly ThumbnailService _thumbnailService;
    private const int SimilarityThreshold = 10;

    [ObservableProperty]
    private string _selectedFolderPath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<MediaFile> _mediaFiles = new();

    [ObservableProperty]
    private ObservableCollection<MediaFileDateGroup> _dateGroups = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "请选择一个文件夹开始扫描";

    [ObservableProperty]
    private ObservableCollection<RecentFolder> _recentFolders = new();

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private bool _isMultiSelectMode;

    [ObservableProperty]
    private bool _isSettingsPanelVisible;

    [ObservableProperty]
    private SettingsViewModel _settingsViewModel = new();

    [ObservableProperty]
    private MediaBurstGroup? _selectedBurstGroup;

    [ObservableProperty]
    private bool _isBurstDetailDrawerVisible;

    /// <summary>
    /// 当前选中的文件列表（用于多选操作）
    /// </summary>
    public ObservableCollection<MediaFile> SelectedFiles { get; } = new();

    /// <summary>
    /// 用于在刷新后保留选中状态的文件路径集合
    /// </summary>
    private HashSet<string>? _pendingSelectionPaths;

    public event EventHandler? RefreshStarted;
    public event EventHandler<RefreshProgressChangedEventArgs>? RefreshProgressChanged;
    public event EventHandler? RefreshCompleted;

    public MainWindowViewModel()
    {
        _fileScanService = new FileScanService();
        _thumbnailService = new ThumbnailService();
        LoadRecentFolders(tryAutoLoad: true);
    }

    /// <summary>
    /// 订阅 MediaFile 的 IsSelected 属性变化事件，自动同步 SelectedFiles 集合
    /// </summary>
    private void AttachSelectionTracking(MediaFile file)
    {
        file.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != nameof(MediaFile.IsSelected))
                return;

            if (file.IsSelected)
            {
                if (!SelectedFiles.Contains(file))
                {
                    SelectedFiles.Add(file);
                }
            }
            else
            {
                SelectedFiles.Remove(file);
            }
        };
    }

    public async void ScanFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        SelectedFolderPath = folderPath;

        // 保存到最近打开的文件夹
        DatabaseService.Instance.SaveRecentFolder(folderPath);

        // 重新加载最近文件夹列表
        LoadRecentFolders();

        await LoadFolderFromDatabaseAsync(folderPath);
    }

    private async Task LoadFolderFromDatabaseAsync(string folderPath)
    {
        // 在刷新前保存当前选中状态
        _pendingSelectionPaths = new HashSet<string>(SelectedFiles.Select(f => f.FilePath));

        IsScanning = true;
        StatusMessage = "正在从数据库加载媒体记录...";
        MediaFiles.Clear();

        try
        {
            var records = DatabaseService.Instance.GetMediaFileRecordsByRootPath(folderPath);
            var files = new List<MediaFile>();

            foreach (var record in records)
            {
                var mediaFile = CreateMediaFileFromRecord(record);
                AttachSelectionTracking(mediaFile);
                files.Add(mediaFile);
                MediaFiles.Add(mediaFile);
            }

            // 恢复刷新前的选中状态
            SelectedFiles.Clear();
            if (_pendingSelectionPaths != null)
            {
                foreach (var file in files)
                {
                    if (_pendingSelectionPaths.Contains(file.FilePath))
                    {
                        file.IsSelected = true;
                    }
                }
                _pendingSelectionPaths = null;
            }

            if (files.Count == 0)
            {
                DateGroups.Clear();
                StatusMessage = "该目录暂无媒体记录，请先导入。";
                return;
            }

            StatusMessage = $"已加载 {files.Count} 个媒体记录，正在生成缩略图...";

            // 按日期分组
            BuildDateGroups(files);

            // 异步生成所有文件的缩略图
            await GenerateThumbnailsAsync(files);
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载记录失败: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// 为所有媒体文件生成缩略图
    /// </summary>
    private async Task GenerateThumbnailsAsync(List<MediaFile> files)
    {
        int processedCount = 0;
        int totalCount = files.Count;

        foreach (var file in files)
        {
            try
            {
                await _thumbnailService.GenerateThumbnailAsync(file);
                processedCount++;
                
                // 每处理10个文件更新一次状态
                if (processedCount % 10 == 0 || processedCount == totalCount)
                {
                    StatusMessage = $"正在生成缩略图: {processedCount}/{totalCount}";
                }
            }
            catch (Exception)
            {
                // 忽略单个文件缩略图生成失败
            }
        }

        StatusMessage = $"扫描完成，共找到 {totalCount} 个媒体文件，缩略图生成完毕";
    }

    /// <summary>
    /// 保存媒体文件记录到数据库
    /// </summary>
    private void SaveMediaFileRecords(List<MediaFile> files, string rootPath, Action? progressCallback = null)
    {
        foreach (var file in files)
        {
            try
            {
                SaveSingleMediaFileRecord(file, rootPath);
            }
            catch (Exception)
            {
                // 忽略单个文件记录保存失败
            }
            finally
            {
                progressCallback?.Invoke();
            }
        }
    }

    private static void SaveSingleMediaFileRecord(MediaFile file, string rootPath)
    {
        var featureCode = MediaFileService.GetMultiExposureSignature(file.FilePath);
        var existingRecord = DatabaseService.Instance.GetMediaFileRecordByPath(file.FilePath);

        if (file.PerceptualHash == 0)
        {
            file.PerceptualHash = ImageHashService.ComputePerceptualHash(file.FilePath);
        }

        var record = existingRecord ?? new MediaFileRecord
        {
            LocalPath = file.FilePath,
            CreatedTime = DateTime.Now,
            IsUploaded = false
        };

        record.IsUploaded = existingRecord?.IsUploaded ?? false;

        record.FeatureCode = featureCode;
        record.FileName = file.FileName;
        record.RootPath = rootPath;
        record.PerceptualHash = file.PerceptualHash;

        DatabaseService.Instance.SaveMediaFileRecord(record);
    }

    /// <summary>
    /// 加载最近打开的文件夹列表
    /// </summary>
    private void LoadRecentFolders(bool tryAutoLoad = false)
    {
        try
        {
            var recentFolders = DatabaseService.Instance.GetRecentFolders(10);
            RecentFolders.Clear();
            foreach (var folder in recentFolders)
            {
                RecentFolders.Add(folder);
            }

            if (tryAutoLoad && string.IsNullOrWhiteSpace(SelectedFolderPath) && RecentFolders.Count > 0)
            {
                var latestFolder = RecentFolders.First();
                ScanFolder(latestFolder.FolderPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载最近文件夹失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 切换侧边栏展开/折叠状态
    /// </summary>
    [RelayCommand]
    public void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
        IsSidebarCollapsed = !IsSidebarCollapsed;
    }

    /// <summary>
    /// 删除单个文件
    /// </summary>
    [RelayCommand]
    public void DeleteFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var fileToRemove = MediaFiles.FirstOrDefault(f => f.FilePath == filePath);

            // 先从数据库中查找并删除记录
            var record = DatabaseService.Instance.GetMediaFileRecordByPath(filePath);
            if (record != null)
            {
                DatabaseService.Instance.DeleteMediaFileRecord(record.Id);
            }

            // 删除物理文件（存在时）
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }

            RemoveFileFromCollections(fileToRemove);

            StatusMessage = $"已删除文件：{System.IO.Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 删除整个连拍组
    /// </summary>
    [RelayCommand]
    public void DeleteBurstGroup(MediaBurstGroup burstGroup)
    {
        if (burstGroup == null || burstGroup.Files.Count == 0)
            return;

        try
        {
            int deletedCount = 0;
            int failedCount = 0;

            // 删除组内的所有文件
            var filesToDelete = burstGroup.Files.ToList();
            foreach (var file in filesToDelete)
            {
                try
                {
                    // 从数据库中查找并删除记录
                    var record = DatabaseService.Instance.GetMediaFileRecordByPath(file.FilePath);
                    if (record != null)
                    {
                        DatabaseService.Instance.DeleteMediaFileRecord(record.Id);
                    }

                    // 删除物理文件
                    if (System.IO.File.Exists(file.FilePath))
                    {
                        System.IO.File.Delete(file.FilePath);
                    }

                    RemoveFileFromCollections(file);
                    deletedCount++;
                }
                catch (Exception)
                {
                    failedCount++;
                }
            }

            if (failedCount == 0)
            {
                StatusMessage = $"已删除连拍组：{deletedCount} 个文件";
            }
            else
            {
                StatusMessage = $"删除连拍组完成：成功 {deletedCount} 个，失败 {failedCount} 个";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除连拍组失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 切换多选模式
    /// </summary>
    public void OnMultiSelectModeChanged(bool isMultiSelectMode)
    {
        // 退出多选模式时清除所有选中状态
        if (!isMultiSelectMode)
        {
            foreach (var file in MediaFiles)
            {
                file.IsSelected = false;
            }
        }
    }

    /// <summary>
    /// 全选
    /// </summary>
    [RelayCommand]
    public void SelectAll()
    {
        foreach (var file in MediaFiles)
        {
            file.IsSelected = true;
        }
    }

    /// <summary>
    /// 取消全选
    /// </summary>
    [RelayCommand]
    public void DeselectAll()
    {
        foreach (var file in MediaFiles)
        {
            file.IsSelected = false;
        }
    }

    /// <summary>
    /// 反选
    /// </summary>
    [RelayCommand]
    public void InvertSelection()
    {
        foreach (var file in MediaFiles)
        {
            file.IsSelected = !file.IsSelected;
        }
    }

    /// <summary>
    /// 批量删除选中的文件
    /// </summary>
    [RelayCommand]
    public void BatchDelete()
    {
        var selectedFiles = MediaFiles.Where(f => f.IsSelected).ToList();
        if (selectedFiles.Count == 0)
            return;

        int successCount = 0;
        int failCount = 0;

        foreach (var file in selectedFiles)
        {
            try
            {
                // 从数据库中查找并删除记录
                var record = DatabaseService.Instance.GetMediaFileRecordByPath(file.FilePath);
                if (record != null)
                {
                    DatabaseService.Instance.DeleteMediaFileRecord(record.Id);
                }

                // 删除物理文件
                if (System.IO.File.Exists(file.FilePath))
                {
                    System.IO.File.Delete(file.FilePath);
                }

                RemoveFileFromCollections(file);

                successCount++;
            }
            catch (Exception)
            {
                failCount++;
            }
        }

        StatusMessage = $"批量删除完成：成功 {successCount} 个，失败 {failCount} 个";
    }

    private void RemoveFileFromCollections(MediaFile? file)
    {
        if (file == null)
            return;

        SelectedFiles.Remove(file);

        var dateGroup = DateGroups.FirstOrDefault(g => g.Date == file.ModifiedTime.Date);
        if (dateGroup != null)
        {
            if (dateGroup.Files.Contains(file))
            {
                dateGroup.Files.Remove(file);
            }

            if (dateGroup.Files.Count == 0)
            {
                DateGroups.Remove(dateGroup);
            }
            else
            {
                RebuildBurstGroups(dateGroup);
            }
        }

        if (MediaFiles.Contains(file))
        {
            MediaFiles.Remove(file);
        }
    }

    private MediaFile CreateMediaFileFromRecord(MediaFileRecord record)
    {
        var mediaFile = new MediaFile
        {
            FilePath = record.LocalPath,
            FileName = record.FileName,
            FileType = ResolveFileType(record.LocalPath),
            ModifiedTime = record.UpdatedTime,
            FileSize = 0,
            PerceptualHash = record.PerceptualHash,
            GroupId = record.GroupId
        };

        if (File.Exists(record.LocalPath))
        {
            var info = new FileInfo(record.LocalPath);
            mediaFile.ModifiedTime = info.LastWriteTime;
            mediaFile.FileSize = info.Length;
        }

        return mediaFile;
    }

    private static string ResolveFileType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (extension != null && ImageExtensions.Contains(extension))
        {
            return "图片";
        }

        return "视频";
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp", ".svg", ".ico"
    };

    private async Task ScanAndUpdateDatabaseAsync(string folderPath)
    {
        var progress = new Progress<RefreshProgressChangedEventArgs>(args =>
        {
            RefreshProgressChanged?.Invoke(this, args);
        });

        await Task.Run(() => ScanAndUpdateDatabaseInternal(folderPath, progress));
    }

    private void ScanAndUpdateDatabaseInternal(string folderPath, IProgress<RefreshProgressChangedEventArgs> progress)
    {
        progress.Report(new RefreshProgressChangedEventArgs("正在扫描文件...", 0, 0, true));

        var files = _fileScanService.ScanDirectory(folderPath);

        if (files.Count == 0)
        {
            progress.Report(new RefreshProgressChangedEventArgs("扫描完成，未找到媒体文件", 0, 0, false));
            return;
        }

        int total = files.Count;
        int processed = 0;
        progress.Report(new RefreshProgressChangedEventArgs($"正在更新数据库 0/{total}", 0, total, false));

        SaveMediaFileRecords(files, folderPath, () =>
        {
            processed++;
            progress.Report(new RefreshProgressChangedEventArgs($"正在更新数据库 {processed}/{total}", processed, total, false));
        });

        progress.Report(new RefreshProgressChangedEventArgs("扫描完成", total, total, false));
    }

    /// <summary>
    /// 刷新当前文件夹
    /// </summary>
    [RelayCommand]
    public async Task RefreshFolder()
    {
        if (string.IsNullOrWhiteSpace(SelectedFolderPath))
            return;

        RefreshStarted?.Invoke(this, EventArgs.Empty);

        try
        {
            await ScanAndUpdateDatabaseAsync(SelectedFolderPath);
            await LoadFolderFromDatabaseAsync(SelectedFolderPath);
        }
        finally
        {
            RefreshCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 选择最近打开的文件夹
    /// </summary>
    [RelayCommand]
    public void SelectRecentFolder(string folderPath)
    {
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            ScanFolder(folderPath);
        }
    }

    [RelayCommand]
    public void ToggleSettingsPanel()
    {
        IsSettingsPanelVisible = !IsSettingsPanelVisible;
    }

    [RelayCommand]
    public void SaveSettings()
    {
        SettingsViewModel.Save();
        IsSettingsPanelVisible = false;
        StatusMessage = "设置已保存";
    }

    [RelayCommand]
    public void CancelSettings()
    {
        IsSettingsPanelVisible = false;
    }

    /// <summary>
    /// 打开连拍详情抽屉
    /// </summary>
    [RelayCommand]
    public void OpenBurstDetailDrawer(MediaBurstGroup burstGroup)
    {
        if (burstGroup == null)
            return;
        
        SelectedBurstGroup = burstGroup;
        IsBurstDetailDrawerVisible = true;
    }

    /// <summary>
    /// 关闭连拍详情抽屉
    /// </summary>
    [RelayCommand]
    public void CloseBurstDetailDrawer()
    {
        IsBurstDetailDrawerVisible = false;
        SelectedBurstGroup = null;
    }

    /// <summary>
    /// 将指定文件从当前组中移出
    /// </summary>
    [RelayCommand]
    public async Task RemoveFromGroup(MediaFile? file)
    {
        if (file == null) return;
        // 规则 3：移出时分配一个全新的、只属于它自己的唯一 GUID
        UpdateFileGroupId(file, Guid.NewGuid().ToString());
        await RefreshCurrentViewAsync();
    }

    /// <summary>
    /// 解散指定的连拍组
    /// </summary>
    [RelayCommand]
    public async Task DissolveGroup(MediaBurstGroup? group)
    {
        if (group == null) return;
    
        // 关键修复：不要设为 null，而是给每张照片分配专属的"单身身份证"
        // 防止 pHash 算法再次自动聚类
        foreach (var file in group.Files)
        {
            UpdateFileGroupId(file, Guid.NewGuid().ToString());
        }
    
        IsBurstDetailDrawerVisible = false;
        SelectedBurstGroup = null;
        await RefreshCurrentViewAsync();
    }

    /// <summary>
    /// 合并选中的文件为一个新的连拍组
    /// </summary>
    [RelayCommand]
    public async Task MergeToBurstGroup()
    {
        if (SelectedFiles.Count < 2) return;

        var newGroupId = Guid.NewGuid().ToString();
        foreach (var file in SelectedFiles)
        {
            UpdateFileGroupId(file, newGroupId);
        }

        SelectedFiles.Clear();
        await RefreshCurrentViewAsync();
    }

    private void UpdateFileGroupId(MediaFile file, string? groupId)
    {
        var record = DatabaseService.Instance.GetMediaFileRecordByPath(file.FilePath);
        if (record != null)
        {
            record.GroupId = groupId;
            DatabaseService.Instance.SaveMediaFileRecord(record);
        }
        // 同步更新内存中的 MediaFile 对象，避免刷新闪烁
        file.GroupId = groupId;
    }

    private async Task RefreshCurrentViewAsync()
    {
        if (!string.IsNullOrEmpty(SelectedFolderPath))
        {
            await LoadFolderFromDatabaseAsync(SelectedFolderPath);
        }
    }

    private void BuildDateGroups(List<MediaFile> files)
    {
        DateGroups.Clear();

        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);

        var groups = files
            .GroupBy(f => f.ModifiedTime.Date)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var header = g.Key switch
                {
                    _ when g.Key == today => "今天",
                    _ when g.Key == yesterday => "昨天",
                    _ when g.Key.Year == today.Year => g.Key.ToString("M月d日"),
                    _ => g.Key.ToString("yyyy年M月d日")
                };

                var ordered = g.OrderByDescending(f => f.ModifiedTime).ToList();
                var dateGroup = new MediaFileDateGroup
                {
                    Date = g.Key,
                    DateHeader = header,
                    Files = new ObservableCollection<MediaFile>(ordered)
                };

                RebuildBurstGroups(dateGroup);
                return dateGroup;
            });

        foreach (var group in groups)
        {
            DateGroups.Add(group);
        }
    }

    private void RebuildBurstGroups(MediaFileDateGroup dateGroup)
    {
        dateGroup.BurstGroups.Clear();
        var burstGroups = BuildBurstGroups(dateGroup.Files.ToList());
        foreach (var burst in burstGroups)
        {
            dateGroup.BurstGroups.Add(burst);
        }
    }

    private List<MediaBurstGroup> BuildBurstGroups(List<MediaFile> files)
    {
        var burstGroups = new List<MediaBurstGroup>();

        // 1. 优先处理已有 GroupId 的文件（人工干预组）
        var groupedFiles = files.Where(f => !string.IsNullOrEmpty(f.GroupId)).GroupBy(f => f.GroupId);
        foreach (var group in groupedFiles)
        {
            if (group.Count() > 0)
            {
                burstGroups.Add(new MediaBurstGroup(group.OrderByDescending(f => f.ModifiedTime)));
            }
        }

        // 2. 处理剩余没有 GroupId 的文件（算法自动聚类）
        // 注意：此时所有 GroupId 不为空的图片（包括独立的和成组的）都已在第一步处理完毕
        var remaining = files
            .Where(f => string.IsNullOrEmpty(f.GroupId))
            .OrderByDescending(f => f.ModifiedTime)
            .ToList();

        while (remaining.Count > 0)
        {
            var anchor = remaining[0];
            remaining.RemoveAt(0);

            var cluster = new List<MediaFile> { anchor };

            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                var candidate = remaining[i];

                if (anchor.PerceptualHash == 0 || candidate.PerceptualHash == 0)
                {
                    continue;
                }

                var distance = CalculateHammingDistance(anchor.PerceptualHash, candidate.PerceptualHash);
                if (distance <= SimilarityThreshold)
                {
                    cluster.Add(candidate);
                    remaining.RemoveAt(i);
                }
            }

            burstGroups.Add(new MediaBurstGroup(cluster.OrderByDescending(f => f.ModifiedTime)));
        }

        return burstGroups;
    }

    private static int CalculateHammingDistance(long hash1, long hash2)
    {
        return BitOperations.PopCount((ulong)(hash1 ^ hash2));
    }
}