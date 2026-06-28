using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class UploadServerViewModel : ObservableObject, IDisposable
{
    private readonly string _projectPath;
    private UploadServerService? _server;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private int _port = 8765;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopServerCommand))]
    private bool _isRunning;
    [ObservableProperty] private string? _uploadUrl;
    [ObservableProperty] private Bitmap? _qrCodeImage;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _recentUploadMessage;

    public bool CanStart => !IsRunning;
    public bool CanStop => IsRunning;

    public UploadServerViewModel(string projectPath)
    {
        _projectPath = projectPath;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartServer()
    {
        ErrorMessage = null;
        StatusMessage = "正在启动...";

        try
        {
            _server = new UploadServerService(_projectPath);
            _cts = new CancellationTokenSource();

            _server.OnUploadSuccess += path =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var fileName = Path.GetFileName(path);
                    RecentUploadMessage = $"✓ 已上传: {fileName}";
                    Trace.WriteLine($"[UploadServerVM] Upload success: {path}");
                });
            };

            _server.OnUploadError += err =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ErrorMessage = $"上传错误: {err}";
                    Trace.WriteLine($"[UploadServerVM] Upload error: {err}");
                });
            };

            await _server.StartAsync(Port, _cts.Token);

            UploadUrl = _server.UploadUrl;
            IsRunning = true;
            StatusMessage = "服务已启动";
            GenerateQrCode(_server.UploadUrl);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"启动失败: {ex.Message}";
            StatusMessage = "启动失败";
            Trace.WriteLine($"[UploadServerVM] Start error: {ex}");
            _server?.Dispose();
            _server = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopServer()
    {
        _cts?.Cancel();
        _server?.Stop();
        _server?.Dispose();
        _server = null;

        IsRunning = false;
        UploadUrl = null;
        QrCodeImage?.Dispose();
        QrCodeImage = null;
        StatusMessage = "服务已停止";
        ErrorMessage = null;
    }

    private void GenerateQrCode(string url)
    {
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrCodeData);
            var pngBytes = qrCode.GetGraphic(5);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                QrCodeImage?.Dispose();
                QrCodeImage = new Bitmap(new MemoryStream(pngBytes));
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[UploadServerVM] QR code error: {ex}");
        }
    }

    public void Dispose()
    {
        StopServer();
        _cts?.Dispose();
    }
}