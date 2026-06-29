using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class UploadServerViewModel : ObservableObject, IDisposable
{
    private readonly GalleryViewModel _gallery;
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

    /// <summary>当前 QR/URL 是否指向公网地址（公网 relay 在跑）。</summary>
    [ObservableProperty] private bool _isPublicMode;

    [ObservableProperty] private PublicRelayViewModel publicRelayViewModel;

    public bool CanStart => !IsRunning;
    public bool CanStop => IsRunning;

    public UploadServerViewModel(GalleryViewModel gallery, PublicRelayViewModel publicRelayViewModel)
    {
        _gallery = gallery;
        PublicRelayViewModel = publicRelayViewModel;
        // 订阅公网代理状态/URL 变化，让二维码跟着切换
        publicRelayViewModel.PropertyChanged += OnPublicRelayPropertyChanged;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartServer()
    {
        ErrorMessage = null;
        StatusMessage = "正在启动...";

        try
        {
            _server = new UploadServerService(_gallery.ProjectPath ?? "");
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

            IsRunning = true;
            StatusMessage = "服务已启动";
            // 决定 QR 用哪个 URL：公网 relay 在跑就用公网，否则用局域网
            UpdateQrForMode();
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
        IsPublicMode = false;
        StatusMessage = "服务已停止";
        ErrorMessage = null;
    }

    private void OnPublicRelayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只在以下几种变化时刷新 QR：
        //   - relay 状态切换（Running ↔ Idle）
        //   - 公网 URL 组成字段（host/port）被改
        if (e.PropertyName != nameof(PublicRelayViewModel.RelayStateText)
            && e.PropertyName != nameof(PublicRelayViewModel.PublicHost)
            && e.PropertyName != nameof(PublicRelayViewModel.SshHost)
            && e.PropertyName != nameof(PublicRelayViewModel.HttpPort))
            return;

        Trace.WriteLine($"[UploadServerVM] QR refresh trigger: prop={e.PropertyName}, IsRunning={IsRunning}");

        // PropertyChanged 可能在后台线程触发（relay state 由 TCP loop 变更），统一 marshal 到 UI 线程
        Dispatcher.UIThread.Post(() =>
        {
            if (IsRunning) UpdateQrForMode();
        });
    }

    private void UpdateQrForMode()
    {
        if (_server == null) return;

        // 公网模式：relay 在跑 + 公网 URL 可拼出
        var publicUrl = PublicRelayViewModel.BuildPublicUploadUrl();
        var isPublic = PublicRelayViewModel.IsPublicRelayRunning && !string.IsNullOrEmpty(publicUrl);

        Trace.WriteLine($"[UploadServerVM] UpdateQrForMode: IsPublicRelayRunning={PublicRelayViewModel.IsPublicRelayRunning}, publicUrl={publicUrl ?? "<null>"}, isPublic={isPublic}");

        IsPublicMode = isPublic;
        var url = isPublic ? publicUrl! : _server.UploadUrl;
        UploadUrl = url;
        GenerateQrCode(url);
    }

    private void GenerateQrCode(string url)
    {
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrCodeData);
            var pngBytes = qrCode.GetGraphic(5);

            Trace.WriteLine($"[UploadServerVM] GenerateQrCode: url={url}, pngBytes={pngBytes.Length}");

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

    public async Task InitializeAsync()
    {
        await PublicRelayViewModel.InitializeAsync();
    }

    public void Dispose()
    {
        PublicRelayViewModel.PropertyChanged -= OnPublicRelayPropertyChanged;
        StopServer();
        _cts?.Dispose();
    }
}