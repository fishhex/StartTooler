using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    public MainWindowViewModel()
    {
        _fileScanService = new FileScanService();
        _thumbnailService = new ThumbnailService();
        LoadRecentFolders();
    }

    public async void ScanFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        // 保存到最近打开的文件夹
        DatabaseService.Instance.SaveRecentFolder(folderPath);
        
        // 重新加载最近文件夹列表
        LoadRecentFolders();

        IsScanning = true;
        SelectedFolderPath = folderPath;
        StatusMessage = "正在扫描文件...";
        MediaFiles.Clear();

        try
        {
            var files = _fileScanService.ScanDirectory(folderPath);
            
            foreach (var file in files)
            {
                MediaFiles.Add(file);
            }

            StatusMessage = $"扫描完成，共找到 {files.Count} 个媒体文件，正在生成缩略图...";
            
            // 按日期分组
            BuildDateGroups(files);
            
            // 异步生成所有文件的缩略图
            await GenerateThumbnailsAsync(files);
            
            // 保存文件记录到数据库
            SaveMediaFileRecords(files);
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描出错: {ex.Message}";
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
    private void SaveMediaFileRecords(List<MediaFile> files)
    {
        foreach (var file in files)
        {
            try
            {
                // 计算特征码
                var featureCode = MediaFileService.GetMultiExposureSignature(file.FilePath);
                
                // 检查是否已存在
                var existingRecord = DatabaseService.Instance.GetMediaFileRecordByPath(file.FilePath);
                
                var record = existingRecord ?? new Models.MediaFileRecord
                {
                    FeatureCode = featureCode,
                    FileName = file.FileName,
                    LocalPath = file.FilePath,
                    IsUploaded = false
                };
                
                // 更新特征码（如果重新计算）
                record.FeatureCode = featureCode;
                record.FileName = file.FileName;
                
                DatabaseService.Instance.SaveMediaFileRecord(record);
            }
            catch (Exception)
            {
                // 忽略单个文件记录保存失败
            }
        }
    }

    /// <summary>
    /// 加载最近打开的文件夹列表
    /// </summary>
    private void LoadRecentFolders()
    {
        try
        {
            var recentFolders = DatabaseService.Instance.GetRecentFolders(10);
            RecentFolders.Clear();
            foreach (var folder in recentFolders)
            {
                RecentFolders.Add(folder);
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
    /// 删除文件
    /// </summary>
    [RelayCommand]
    public void DeleteFile(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && System.IO.File.Exists(filePath))
        {
            try
            {
                // 先从数据库中查找并删除记录
                var record = DatabaseService.Instance.GetMediaFileRecordByPath(filePath);
                if (record != null)
                {
                    DatabaseService.Instance.DeleteMediaFileRecord(record.Id);
                }
                
                // 删除物理文件
                System.IO.File.Delete(filePath);
                
                // 从列表中移除
                var fileToRemove = MediaFiles.FirstOrDefault(f => f.FilePath == filePath);
                if (fileToRemove != null)
                {
                    MediaFiles.Remove(fileToRemove);
                }
                
                StatusMessage = $"已删除文件：{System.IO.Path.GetFileName(filePath)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"删除失败：{ex.Message}";
            }
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

                successCount++;
            }
            catch (Exception)
            {
                failCount++;
            }
        }

        // 从列表中移除已删除的文件
        foreach (var file in selectedFiles)
        {
            MediaFiles.Remove(file);
        }

        StatusMessage = $"批量删除完成：成功 {successCount} 个，失败 {failCount} 个";
    }

    /// <summary>
    /// 刷新当前文件夹
    /// </summary>
    [RelayCommand]
    public void RefreshFolder()
    {
        if (!string.IsNullOrWhiteSpace(SelectedFolderPath))
        {
            ScanFolder(SelectedFolderPath);
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

                return new MediaFileDateGroup
                {
                    Date = g.Key,
                    DateHeader = header,
                    Files = new ObservableCollection<MediaFile>(g.OrderByDescending(f => f.ModifiedTime))
                };
            });

        foreach (var group in groups)
        {
            DateGroups.Add(group);
        }
    }
}