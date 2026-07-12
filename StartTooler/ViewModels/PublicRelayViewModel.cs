using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public enum SshAuthMethod
{
    Password,
    Key,
}

public partial class PublicRelayViewModel : ObservableObject, IDisposable
{
    private readonly IConfigService _configService;
    private readonly PublicRelayService _relayService;
    private readonly IFilePickerService _filePicker;
    private readonly GalleryViewModel _gallery;

    // 快照（dirty tracking）
    private PublicRelayConfig? _lastSaved;
    private bool _isInitialized;

    // 认证方式
    [ObservableProperty] private SshAuthMethod authMethod = SshAuthMethod.Password;

    // 配置字段
    [ObservableProperty] private string sshHost = "";
    [ObservableProperty] private int sshPort = 22;
    [ObservableProperty] private string sshUser = "";
    [ObservableProperty] private string? sshPassword;
    [ObservableProperty] private string? sshKeyPath;
    [ObservableProperty] private string sshRemotePath = "~/starttooler";
    [ObservableProperty] private int httpPort = 8765;
    [ObservableProperty] private int tcpPort = 8766;
    [ObservableProperty] private string? publicHost;

    // VPS 架构：auto / amd64 / arm64
    [ObservableProperty] private string remoteArch = RelayArch.Auto;

    // 状态
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private string? relayStateText;
    [ObservableProperty] private string? lastError;
    [ObservableProperty] private string? lastLog;
    [ObservableProperty] private bool isProjectPathSet;

    // v0.11 分步向导：spec §4.1
    [ObservableProperty] private bool _step1Done;   // Save 成功后 = true；下次再改 dirty 时不变（RecomputeDirty 单独管）
    [ObservableProperty] private bool _step2Done;   // Deploy 成功后 = true
    [ObservableProperty] private bool _canDeploy;   // Step1Done && !IsBusy
    [ObservableProperty] private bool _canStart;    // Step2Done && !IsBusy && IsProjectPathSet

    // v0.11 多行日志：spec §4.3。保留最近 100 行（含时间戳）。
    [ObservableProperty] private string _logText = "";
    private const int LogMaxLines = 100;

    /// <summary>sync_for_vps_task 中 Pending 行数（UI 显示用）。</summary>
    [ObservableProperty] private int pendingCount;

    // 认证方式切换（用于 XAML IsVisible 绑定）
    [ObservableProperty] private bool showPasswordFields = true;
    [ObservableProperty] private bool showKeyFields;

    public int AuthMethodIndex
    {
        get => (int)AuthMethod;
        set
        {
            if (value < 0 || value > 1) return;
            AuthMethod = (SshAuthMethod)value;
        }
    }

    /// <summary>
    /// ComboBox 用：0=Auto, 1=amd64, 2=arm64。
    /// </summary>
    public int RemoteArchIndex
    {
        get
        {
            var v = (RemoteArch ?? RelayArch.Auto).Trim().ToLowerInvariant();
            return v switch
            {
                RelayArch.Amd64 => 1,
                RelayArch.Arm64 => 2,
                _ => 0,
            };
        }
        set
        {
            var next = value switch
            {
                1 => RelayArch.Amd64,
                2 => RelayArch.Arm64,
                _ => RelayArch.Auto,
            };
            if (!string.Equals(next, RemoteArch, StringComparison.OrdinalIgnoreCase))
                RemoteArch = next;
        }
    }

    public PublicRelayViewModel(
        IConfigService configService,
        PublicRelayService relayService,
        IFilePickerService filePicker,
        GalleryViewModel gallery)
    {
        _configService = configService;
        _relayService = relayService;
        _filePicker = filePicker;
        _gallery = gallery;

        _gallery.PropertyChanged += OnGalleryPropertyChanged;
        _relayService.StateChanged += OnRelayStateChanged;
        _relayService.PendingCountChanged += OnRelayPendingCountChanged;
        // v0.11: WAN scp 落盘完也触发 gallery 自动刷新（和 LAN OnUploadSuccess 走同一条防抖路径）
        _relayService.FileReceived += OnRelayFileReceived;
        RefreshProjectPathSet();
    }

    private void OnGalleryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.ProjectPath))
        {
            RefreshProjectPathSet();
        }
    }

    private void RefreshProjectPathSet()
    {
        var newValue = !string.IsNullOrEmpty(_gallery.ProjectPath);
        if (IsProjectPathSet != newValue)
        {
            IsProjectPathSet = newValue;
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnIsProjectPathSetChanged(bool value)
    {
        // v0.11: CanStart 也依赖 IsProjectPathSet，要重算 step 链 + 通知 Start
        RecomputeStepEnables();
    }

    public async Task InitializeAsync()
    {
        var cfg = await _configService.GetOrCreateAsync<PublicRelayConfig>(ConfigKeys.PublicRelay);
        LoadFromConfig(cfg);
        _lastSaved = CloneConfig(cfg);
        _isInitialized = true;
        IsDirty = false;

        // v0.11: 已有配置视为 Step1 已完成（用户没动 → 无需重 Save）
        Step1Done = HasMeaningfulConfig(cfg);
        RecomputeStepEnables();

        RefreshProjectPathSet();
        UpdateRelayStateText();
    }

    /// <summary>SSH Host/Port/User 至少填了 Host 就认为 Step1 可推进。其它字段有默认。</summary>
    private static bool HasMeaningfulConfig(PublicRelayConfig cfg)
    {
        return !string.IsNullOrWhiteSpace(cfg.SshHost) && !string.IsNullOrWhiteSpace(cfg.SshUser);
    }

    private void LoadFromConfig(PublicRelayConfig cfg)
    {
        SshHost = cfg.SshHost ?? "";
        SshPort = cfg.SshPort;
        SshUser = cfg.SshUser ?? "";
        SshPassword = cfg.SshPassword;
        SshKeyPath = cfg.SshKeyPath;
        AuthMethod = !string.IsNullOrEmpty(cfg.SshKeyPath) ? SshAuthMethod.Key : SshAuthMethod.Password;
        SshRemotePath = string.IsNullOrEmpty(cfg.SshRemotePath) ? "~/starttooler" : cfg.SshRemotePath;
        HttpPort = cfg.HttpPort;
        TcpPort = cfg.TcpPort;
        PublicHost = cfg.PublicHost;
        RemoteArch = string.IsNullOrWhiteSpace(cfg.RemoteArch) ? RelayArch.Auto : cfg.RemoteArch.Trim().ToLowerInvariant();
    }

    private PublicRelayConfig BuildConfigFromVm() => new()
    {
        SshHost = SshHost ?? "",
        SshPort = SshPort,
        SshUser = SshUser ?? "",
        SshPassword = AuthMethod == SshAuthMethod.Password ? SshPassword : null,
        SshKeyPath = AuthMethod == SshAuthMethod.Key ? SshKeyPath : null,
        SshRemotePath = string.IsNullOrWhiteSpace(SshRemotePath) ? "~/starttooler" : SshRemotePath,
        HttpPort = HttpPort,
        TcpPort = TcpPort,
        PublicHost = string.IsNullOrWhiteSpace(PublicHost) ? null : PublicHost,
        RemoteArch = string.IsNullOrWhiteSpace(RemoteArch) ? RelayArch.Auto : RemoteArch.Trim().ToLowerInvariant(),
    };

    private static PublicRelayConfig CloneConfig(PublicRelayConfig c) => new()
    {
        SshHost = c.SshHost,
        SshPort = c.SshPort,
        SshUser = c.SshUser,
        SshPassword = c.SshPassword,
        SshKeyPath = c.SshKeyPath,
        SshRemotePath = c.SshRemotePath,
        HttpPort = c.HttpPort,
        TcpPort = c.TcpPort,
        PublicHost = c.PublicHost,
        RemoteArch = c.RemoteArch,
    };

    private void RecomputeDirty()
    {
        if (!_isInitialized || _lastSaved == null) return;
        var current = BuildConfigFromVm();
        var s = _lastSaved;
        var dirty = current.SshHost != s.SshHost
                  || current.SshPort != s.SshPort
                  || current.SshUser != s.SshUser
                  || current.SshPassword != s.SshPassword
                  || current.SshKeyPath != s.SshKeyPath
                  || current.SshRemotePath != s.SshRemotePath
                  || current.HttpPort != s.HttpPort
                  || current.TcpPort != s.TcpPort
                  || current.PublicHost != s.PublicHost
                  || current.RemoteArch != s.RemoteArch;
        if (IsDirty != dirty) IsDirty = dirty;
    }

    partial void OnSshHostChanged(string value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnSshPortChanged(int value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnSshUserChanged(string value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnSshPasswordChanged(string? value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnSshKeyPathChanged(string? value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnSshRemotePathChanged(string value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnHttpPortChanged(int value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnTcpPortChanged(int value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnPublicHostChanged(string? value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnRemoteArchChanged(string value) { if (_isInitialized) RecomputeDirty(); }
    partial void OnAuthMethodChanged(SshAuthMethod value)
    {
        ShowPasswordFields = value == SshAuthMethod.Password;
        ShowKeyFields = value == SshAuthMethod.Key;
        if (_isInitialized) RecomputeDirty();
    }

    // ============================================================
    // 命令
    // ============================================================

    [RelayCommand]
    private async Task BrowseKey()
    {
        var file = await _filePicker.PickFileAsync("选择 SSH 私钥文件", null);
        if (!string.IsNullOrEmpty(file)) SshKeyPath = file;
    }

    [RelayCommand]
    private async Task Save()
    {
        var cfg = BuildConfigFromVm();
        await _configService.SetAsync(ConfigKeys.PublicRelay, cfg);
        _lastSaved = CloneConfig(cfg);
        IsDirty = false;
        StatusMessage = "已保存";
        // v0.11: Save 成功 → Step1Done 翻 true，线性按钮组向前推进
        Step1Done = HasMeaningfulConfig(cfg);
        RecomputeStepEnables();
    }

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private async Task Deploy()
    {
        await RunWithBusyAsync("部署中...", async () =>
        {
            var cfg = BuildConfigFromVm();
            var progress = new Progress<string>(s => AppendLog(s));

            // 先解析目标架构（auto 会 SSH uname -m）
            var arch = await _relayService.ResolveArchAsync(cfg, progress, default);

            // 直接部署单个二进制（HTML 已嵌入）
            await _relayService.DeployAsync(cfg, arch, progress, default);
            StatusMessage = "部署完成";
            // v0.11: 部署成功 → Step2Done 翻 true，Start 按钮可点
            Step2Done = true;
            RecomputeStepEnables();
        });
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        await RunWithBusyAsync("启动中...", async () =>
        {
            var cfg = BuildConfigFromVm();
            var progress = new Progress<string>(s => AppendLog(s));

            // 先解析 arch（auto 会 SSH uname -m）
            var arch = await _relayService.ResolveArchAsync(cfg, progress, default);

            // 先保证部署了（失败也不阻塞启动 —— 允许 VPS 上已有二进制）
            try { await _relayService.DeployAsync(cfg, arch, progress, default); }
            catch (Exception ex) { AppendLog("部署步骤失败：" + ex.Message); }

            await _relayService.StartRemoteAsync(cfg, arch, progress, default);
            var projectPath = _gallery.ProjectPath ?? "";
            _relayService.StartClient(cfg, projectPath);
            StatusMessage = "公网代理已启动";
            UpdateRelayStateText();
        });
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        await RunWithBusyAsync("停止中...", async () =>
        {
            await _relayService.StopClientAsync();
            var cfg = BuildConfigFromVm();
            var progress = new Progress<string>(s => AppendLog(s));
            try
            {
                await _relayService.StopRemoteAsync(cfg, progress, default);
            }
            catch (Exception ex)
            {
                AppendLog("远端停止失败：" + ex.Message);
            }
            StatusMessage = "已停止";
            UpdateRelayStateText();
        });
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogText = "";
    }

    /// <summary>
    /// 追加一行到多行日志 + 更新 LastLog（保留单行预览属性以备旧绑定处引用）。
    /// spec §4.3：保留最近 100 行。
    /// </summary>
    private void AppendLog(string msg)
    {
        // Progress<string> 可能在后台线程触发，统一 marshal 到 UI 线程
        var ts = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{ts}] {msg}";
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LastLog = msg;
            LogText += line + "\n";
            var lines = LogText.Split('\n', StringSplitOptions.None);
            // 末尾 Split 会多一个空字符串，lines.Length > LogMaxLines+1 才截
            if (lines.Length > LogMaxLines + 1)
            {
                LogText = string.Join('\n', lines, lines.Length - LogMaxLines - 1, LogMaxLines);
            }
        });
    }

    /// <summary>
    /// v0.11 按钮链启用条件：spec §4.1
    ///   CanDeploy = Step1Done && !IsBusy
    ///   CanStart  = Step2Done && !IsBusy && IsProjectPathSet
    /// 任何一边的状态变化都要重算（partial OnXxxChanged 钩子触发）。
    /// </summary>
    private void RecomputeStepEnables()
    {
        CanDeploy = Step1Done && !IsBusy;
        CanStart = Step2Done && !IsBusy && IsProjectPathSet;
        DeployCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => RecomputeStepEnables();
    partial void OnStep1DoneChanged(bool value) => RecomputeStepEnables();
    partial void OnStep2DoneChanged(bool value) => RecomputeStepEnables();

    private bool CanStop() => !IsBusy && _relayService.State == PublicRelayService.RelayState.Running;

    private async Task RunWithBusyAsync(string busyText, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyChanged();
        StatusMessage = busyText;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = "失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyChanged();
            UpdateRelayStateText();
        }
    }

    private void BusyChanged()
    {
        DeployCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void OnRelayStateChanged()
    {
        UpdateRelayStateText();
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPublicRelayRunning));
        // v0.11: 状态色随 RelayState 变化，UI 折叠态也能跟到（OnPropertyChanged 兜底）
        OnPropertyChanged(nameof(RelayStateColor));
    }

    private void OnRelayPendingCountChanged(int count)
    {
        // PendingCountChanged 是从 background task 触发的，跨线程 → marshal 到 UI 线程
        Avalonia.Threading.Dispatcher.UIThread.Post(() => PendingCount = count);
    }

    /// <summary>
    /// v0.11: WAN scp 落盘成功回调。文件已写到项目目录的 YYYY-MM-DD/，触发 gallery 自动刷新。
    /// FileReceived 在 PublicRelayService 的 Poller 线程触发，统一 marshal 到 UI 线程。
    /// </summary>
    private void OnRelayFileReceived(string localPath)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Trace.WriteLine($"[PublicRelayVM] FileReceived: {localPath} → request gallery refresh");
            _gallery.RequestRefreshDebounced();
            // 单行日志也补一笔（用户切到 gallery 之前能在这看到）
            AppendLog($"✅ 已下载到 {Path.GetFileName(localPath)}");
        });
    }

    private void UpdateRelayStateText()
    {
        RelayStateText = _relayService.State switch
        {
            PublicRelayService.RelayState.Idle => "未启用",
            PublicRelayService.RelayState.Running => "运行中",
            PublicRelayService.RelayState.Deploying => "部署中...",
            PublicRelayService.RelayState.Starting => "启动中...",
            PublicRelayService.RelayState.Stopping => "停止中...",
            PublicRelayService.RelayState.Error => "错误",
            _ => _relayService.State.ToString(),
        };
        LastError = _relayService.LastError;
    }

    /// <summary>远端 relay 进程是否在跑。</summary>
    public bool IsPublicRelayRunning => _relayService.State == PublicRelayService.RelayState.Running;

    /// <summary>
    /// v0.11 状态指示器颜色：spec §4.4
    ///   Running → 绿
    ///   过渡态（Deploying/Starting/Stopping）→ 橙
    ///   Error → 红
    ///   Idle/Stopped → 灰
    /// </summary>
    public IBrush RelayStateColor => _relayService.State switch
    {
        PublicRelayService.RelayState.Running => Brushes.LimeGreen,
        PublicRelayService.RelayState.Deploying => Brushes.Orange,
        PublicRelayService.RelayState.Starting => Brushes.Orange,
        PublicRelayService.RelayState.Stopping => Brushes.Orange,
        PublicRelayService.RelayState.Error => Brushes.Red,
        _ => Brushes.Gray,
    };

    /// <summary>
    /// 拼出公网上传 URL（用于二维码显示）。
    /// 优先级：PublicHost → SshHost。任何一项缺失返回 null。
    /// </summary>
    public string? BuildPublicUploadUrl()
    {
        var host = !string.IsNullOrWhiteSpace(PublicHost) ? PublicHost : SshHost;
        if (string.IsNullOrWhiteSpace(host)) return null;
        return $"http://{host}:{HttpPort}/upload";
    }

    /// <summary>退出时确保远端进程被杀（不抛异常）。</summary>
    public async Task EnsureRemoteKilledOnExitAsync()
    {
        try
        {
            await _relayService.StopClientAsync();
            var cfg = await _configService.GetAsync<PublicRelayConfig>(ConfigKeys.PublicRelay);
            if (cfg == null || string.IsNullOrEmpty(cfg.SshHost)) return;
            await _relayService.StopRemoteAsync(cfg, null, default);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[PublicRelay] exit kill failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _gallery.PropertyChanged -= OnGalleryPropertyChanged;
        _relayService.StateChanged -= OnRelayStateChanged;
        _relayService.PendingCountChanged -= OnRelayPendingCountChanged;
        _relayService.FileReceived -= OnRelayFileReceived;
        _relayService.Dispose();
    }
}