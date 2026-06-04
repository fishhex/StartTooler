using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "请选择一个文件夹开始扫描";

    [ObservableProperty]
    private ObservableCollection<RecentFolder> _recentFolders = new();

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

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
            
            // 异步生成所有文件的缩略图
            await GenerateThumbnailsAsync(files);
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
}