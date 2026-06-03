using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly DatabaseService _databaseService;
    private readonly FFmpegService _ffmpegService;
    private readonly FileScanService _fileScanService;
    private CancellationTokenSource? _scanCancellationTokenSource;

    [ObservableProperty]
    private ObservableCollection<DirectoryNode> _directoryTree = new();

    [ObservableProperty]
    private ObservableCollection<MediaFile> _mediaFiles = new();

    [ObservableProperty]
    private DirectoryNode? _selectedDirectory;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private int _scanProgress;

    [ObservableProperty]
    private string _rootDirectoryPath = string.Empty;

    public MainWindowViewModel()
    {
        _databaseService = new DatabaseService();
        _ffmpegService = new FFmpegService();
        _fileScanService = new FileScanService(_databaseService, _ffmpegService);

        // 初始化时检查 FFmpeg 可用性（不等待，后台执行）
        _ = CheckFFmpegAvailabilityAsync();
    }

    /// <summary>
    /// 设置根目录并开始扫描
    /// </summary>
    public async Task SetRootDirectoryAndScanAsync(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            StatusMessage = "无效的目录路径";
            return;
        }

        RootDirectoryPath = rootPath;
        await LoadDirectoryTreeAsync(rootPath);
        await ScanSelectedDirectoryAsync(rootPath);
    }

    /// <summary>
    /// 加载目录树
    /// </summary>
    private async Task LoadDirectoryTreeAsync(string rootPath)
    {
        try
        {
            StatusMessage = "正在加载目录结构...";
            IsScanning = true;

            var rootNode = await _fileScanService.BuildDirectoryTreeAsync(rootPath);
            
            DirectoryTree.Clear();
            if (rootNode != null)
            {
                DirectoryTree.Add(rootNode);
                SelectedDirectory = rootNode;
            }

            StatusMessage = "目录结构加载完成";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载目录失败: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// 当选择的目录改变时触发
    /// </summary>
    partial void OnSelectedDirectoryChanged(DirectoryNode? value)
    {
        if (value != null)
        {
            _ = LoadMediaFilesForDirectoryAsync(value.FullPath);
        }
    }

    /// <summary>
    /// 加载指定目录的媒体文件
    /// </summary>
    private async Task LoadMediaFilesForDirectoryAsync(string directoryPath)
    {
        try
        {
            StatusMessage = $"正在加载目录: {Path.GetFileName(directoryPath)}";
            
            var mediaFiles = await _databaseService.GetMediaFilesByDirectoryAsync(directoryPath);
            
            MediaFiles.Clear();
            foreach (var file in mediaFiles)
            {
                MediaFiles.Add(file);
            }

            StatusMessage = $"已加载 {mediaFiles.Count} 个媒体文件";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载文件失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 扫描选中的目录
    /// </summary>
    private async Task ScanSelectedDirectoryAsync(string directoryPath)
    {
        if (_scanCancellationTokenSource != null)
        {
            await _scanCancellationTokenSource.CancelAsync();
            _scanCancellationTokenSource.Dispose();
        }

        _scanCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _scanCancellationTokenSource.Token;

        try
        {
            IsScanning = true;
            StatusMessage = "开始扫描目录...";
            ScanProgress = 0;

            await _fileScanService.ScanDirectoryAsync(
                directoryPath,
                progress =>
                {
                    // 更新进度信息（需要在 UI 线程执行）
                    StatusMessage = progress.StatusMessage;
                    if (progress.TotalFiles > 0)
                    {
                        ScanProgress = (int)((double)progress.CurrentFile / progress.TotalFiles * 100);
                    }
                },
                cancellationToken);

            StatusMessage = "扫描完成！";
            ScanProgress = 100;

            // 重新加载文件列表
            await LoadMediaFilesForDirectoryAsync(directoryPath);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = null;
        }
    }

    /// <summary>
    /// 取消扫描
    /// </summary>
    [RelayCommand]
    private void CancelScan()
    {
        _scanCancellationTokenSource?.Cancel();
    }

    /// <summary>
    /// 刷新当前目录
    /// </summary>
    [RelayCommand]
    private Task RefreshCurrentDirectoryAsync()
    {
        if (!string.IsNullOrEmpty(RootDirectoryPath))
        {
            return ScanSelectedDirectoryAsync(RootDirectoryPath);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 检查 FFmpeg 可用性
    /// </summary>
    private async Task CheckFFmpegAvailabilityAsync()
    {
        var isAvailable = await _ffmpegService.IsFFmpegAvailableAsync();
        if (!isAvailable)
        {
            StatusMessage = "警告: 未检测到 FFmpeg，缩略图功能将不可用。请安装 FFmpeg 并添加到系统 PATH。";
        }
    }

    public void Dispose()
    {
        _databaseService?.Dispose();
        _scanCancellationTokenSource?.Dispose();
    }
}