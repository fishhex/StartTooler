using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using StartTooler.Services;

namespace StartTooler.ViewModels;

/// <summary>
/// 上传历史条目：每次 OnUploadSuccess 追加一条，UI 在 ScrollViewer 里滚动展示。
/// 故意不做持久化（重启清空），避免 LocalAddress/路径等历史信息误导。
/// </summary>
public class UploadHistoryEntry
{
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsSuccess { get; set; }
}

public partial class UploadServerViewModel : ObservableObject, IDisposable
{
    private readonly GalleryViewModel _gallery;
    private UploadServerService? _server;
    private CancellationTokenSource? _cts;
    private string? _lastProjectPath;  // 监听项目目录变化，自动停服

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

    /// <summary>
    /// v0.11: 状态消息非冲突态时是否显示（绿）。避免绿色 TextBlock 在冲突态或空消息时残留。
    /// </summary>
    public bool ShowSuccessStatus => !IsPortConflict && !string.IsNullOrEmpty(StatusMessage);

    // v0.11 上传历史 + 复制链接
    public ObservableCollection<UploadHistoryEntry> UploadHistory { get; } = new();
    /// <summary>历史非空时显示 ScrollViewer 区块。</summary>
    public bool HasUploadHistory => UploadHistory.Count > 0;
    [ObservableProperty] private string _copyButtonText = "已复制";  // v0.11: 默认空，按钮渲染 Icon.Copy（XAML 端），执行后变 "已复制"

    // v0.11 端口冲突（State.Danger 颜色 + 建议空闲端口）
    [ObservableProperty] private bool _isPortConflict;
    [ObservableProperty] private List<int> _suggestedPorts = new();

    // v0.11 多 IP 列表（StartServer 成功后从 Dns 拿，运行时可复制）
    [ObservableProperty] private ObservableCollection<string> _localAddresses = new();
    /// <summary>多 IP 列表非空时显示区块（至少有一个 IPv4 才显示）。</summary>
    public bool HasLocalAddresses => LocalAddresses.Count > 0;
    /// <summary>左侧卡片展示的首选 IP；无 IPv4 时显示 "-"。</summary>
    public string PreferredLocalAddress => LocalAddresses.Count > 0 ? LocalAddresses[0] : "-";

    [ObservableProperty] private PublicRelayViewModel publicRelayViewModel;

    public bool CanStart => !IsRunning;
    public bool CanStop => IsRunning;

    public UploadServerViewModel(GalleryViewModel gallery, PublicRelayViewModel publicRelayViewModel)
    {
        _gallery = gallery;
        PublicRelayViewModel = publicRelayViewModel;
        // 订阅公网代理状态/URL 变化，让二维码跟着切换
        publicRelayViewModel.PropertyChanged += OnPublicRelayPropertyChanged;
        // 监听项目目录变化：切项目时停服（路径已变，上传的文件会落错位置）
        _gallery.PropertyChanged += OnGalleryPropertyChanged;
        // v0.11: 集合增删要通知 HasXxx 派生属性
        UploadHistory.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasUploadHistory));
        LocalAddresses.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasLocalAddresses));
            OnPropertyChanged(nameof(PreferredLocalAddress));
        };
    }

    private void OnGalleryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GalleryViewModel.ProjectPath)) return;
        var newPath = _gallery.ProjectPath;
        if (IsRunning && !string.IsNullOrEmpty(_lastProjectPath)
            && !string.Equals(newPath, _lastProjectPath, StringComparison.Ordinal))
        {
            Trace.WriteLine($"[UploadServerVM] ProjectPath changed {_lastProjectPath} -> {newPath}, auto stopping server");
            StopServer();
        }
    }

    // v0.11: 状态消息/冲突态变化时通知 ShowSuccessStatus
    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(ShowSuccessStatus));
    partial void OnIsPortConflictChanged(bool value) => OnPropertyChanged(nameof(ShowSuccessStatus));

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartServer()
    {
        // 重置冲突态（重新启动时清掉上次的红色提示）
        IsPortConflict = false;
        SuggestedPorts = new List<int>();
        ErrorMessage = null;
        StatusMessage = "正在启动...";

        try
        {
            _server = new UploadServerService(_gallery.ProjectPath ?? "");
            _cts = new CancellationTokenSource();
            _lastProjectPath = _gallery.ProjectPath;

            _server.OnUploadSuccess += path =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var fileName = Path.GetFileName(path);
                    long size = 0;
                    try { size = new FileInfo(path).Length; } catch { /* 文件可能已移走 */ }

                    RecentUploadMessage = $"✓ 已上传: {fileName}";
                    UploadHistory.Insert(0, new UploadHistoryEntry
                    {
                        FileName = fileName,
                        FileSize = size,
                        Timestamp = DateTime.Now,
                        IsSuccess = true,
                    });
                    // 限制最多保留 50 条（避免极端场景内存膨胀；UI MaxHeight=200 也基本只能看 10 来条）
                    while (UploadHistory.Count > 50)
                        UploadHistory.RemoveAt(UploadHistory.Count - 1);

                    // v0.11: 上传完自动刷新媒体（spec §3 延伸），
                    // 2s 防抖把连拍多张合成一次扫描
                    _gallery.RequestRefreshDebounced();

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

            // 拉取所有本机 IPv4（多网卡 / VPN / 虚拟机都可能给多个 IP；loopback 也包含便于本地调试）
            var addrs = Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => ip.ToString())
                .ToList();
            LocalAddresses.Clear();
            foreach (var a in addrs) LocalAddresses.Add(a);
            Trace.WriteLine($"[UploadServerVM] LocalAddresses: {string.Join(",", LocalAddresses)}");

            // 决定 QR 用哪个 URL：公网 relay 在跑就用公网，否则用局域网
            UpdateQrForMode();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[UploadServerVM] Start error: {ex}");
            HandleStartError(ex);
            _server?.Dispose();
            _server = null;
        }
    }

    /// <summary>
    /// 启动失败分类：端口冲突（HttpListenerException + "address already in use"）→ 红色提示 + 建议空闲端口；
    /// 其他 → 通用错误。spec §3.3。
    /// </summary>
    private void HandleStartError(Exception ex)
    {
        var msg = ex.Message ?? "";
        var isConflict = ex is HttpListenerException
            || msg.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("无法绑定", StringComparison.OrdinalIgnoreCase);

        if (isConflict)
        {
            IsPortConflict = true;
            var free = FindFreePorts(3);
            SuggestedPorts = free;
            StatusMessage = "端口冲突";
            ErrorMessage = free.Count == 0
                ? $"端口 {Port} 被占用，且未找到可用端口（建议手动改 8765-65535 范围）"
                : $"端口 {Port} 被占用，建议改用 {string.Join(" / ", free)} 之一";
        }
        else
        {
            IsPortConflict = false;
            ErrorMessage = $"启动失败: {msg}";
            StatusMessage = "启动失败";
        }
    }

    /// <summary>
    /// 扫连续端口找 N 个空闲的。返回 0~N 个（极端情况可能 0 个）。
    /// 失败 cost 是一次 TcpListener bind/unbind，开销 O(N)；N 默认 3 完全可接受。
    /// </summary>
    private static List<int> FindFreePorts(int count)
    {
        var ports = new List<int>();
        for (int p = 8765; p <= 65535 && ports.Count < count; p++)
        {
            try
            {
                using var s = new TcpListener(IPAddress.Loopback, p);
                s.Start();
                s.Stop();
                ports.Add(p);
            }
            catch
            {
                // 端口被占用，跳过
            }
        }
        return ports;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void UseRandomPort(int? pickPort = null)
    {
        if (pickPort is int p && p > 0)
        {
            Port = p;
        }
        else
        {
            var free = FindFreePorts(1);
            if (free.Count > 0) Port = free[0];
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopServer()
    {
        _cts?.Cancel();
        _server?.Stop();
        _server?.Dispose();
        _server = null;
        _lastProjectPath = null;

        IsRunning = false;
        UploadUrl = null;
        QrCodeImage?.Dispose();
        QrCodeImage = null;
        IsPublicMode = false;
        IsPortConflict = false;
        SuggestedPorts = new List<int>();
        LocalAddresses.Clear();
        StatusMessage = "服务已停止";
        ErrorMessage = null;
    }

    /// <summary>
    /// 复制 UploadUrl 到剪贴板，按钮短暂变 "已复制" 反馈。spec §3.2。
    /// 走 <see cref="ClipboardService"/>（启动时 App 把 MainWindow.Clipboard 绑进来）。
    /// v0.11: 默认 CopyButtonText="已复制" 占位（XAML 端 IsNullOrEmpty 才显示 Icon.Copy）；
    /// 点击后设 "已复制" 显示文字 → 1.5s 后清空回 Icon。
    /// </summary>
    [RelayCommand]
    private async Task CopyUrl()
    {
        if (string.IsNullOrEmpty(UploadUrl)) return;
        await ClipboardService.SetTextAsync(UploadUrl);
        CopyButtonText = "已复制";
        await Task.Delay(1500);
        CopyButtonText = "";  // v0.11: 清空让 XAML 重新显示 Icon.Copy
    }

    /// <summary>
    /// 复制单个 IP（多网卡列表里每行一个复制按钮）。CommandParameter 传 IP 字符串。
    /// </summary>
    [RelayCommand]
    private async Task CopyAddress(string? address)
    {
        if (string.IsNullOrEmpty(address)) return;
        await ClipboardService.SetTextAsync(address);
    }

    /// <summary>
    /// v0.11: 清除上传历史（spec §13.2）。
    /// 用户拍一下就把最近上传列表清空；不影响未来的上传（不持久化）。
    /// </summary>
    [RelayCommand]
    private void ClearUploadHistory()
    {
        UploadHistory.Clear();
        Trace.WriteLine("[UploadServerVM] ClearUploadHistory: 清空上传历史");
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
        _gallery.PropertyChanged -= OnGalleryPropertyChanged;
        PublicRelayViewModel.PropertyChanged -= OnPublicRelayPropertyChanged;
        StopServer();
        _cts?.Dispose();
    }
}
