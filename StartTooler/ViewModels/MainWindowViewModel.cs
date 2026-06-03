using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly FileScanService _fileScanService;

    [ObservableProperty]
    private string _selectedFolderPath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<MediaFile> _mediaFiles = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "请选择一个文件夹开始扫描";

    public MainWindowViewModel()
    {
        _fileScanService = new FileScanService();
    }

    public void ScanFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

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

            StatusMessage = $"扫描完成，共找到 {files.Count} 个媒体文件";
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
}