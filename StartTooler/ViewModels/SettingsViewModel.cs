using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public enum SettingsTab
{
    General,
    Oss
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly IDirectoryPickerService _directoryPicker;
    private readonly IFilePickerService _filePicker;
    private readonly IConfigService _configService;

    // General Tab 快照
    private string? _lastSavedDirectory;
    private int _lastSavedTheme;  // 0=DeepSpace, 1=RedNight
    private string? _lastSavedFfmpegPath;
    private string? _lastSavedFfprobePath;

    // OSS Tab 快照
    private OssConfig? _lastSavedOss;

    private ProjectConfig? _projectConfig;
    private bool _isInitialized;

    [ObservableProperty] private SettingsTab selectedTab = SettingsTab.General;

    // General Tab 字段
    [ObservableProperty] private string? selectedProjectDirectory;
    [ObservableProperty] private ObservableCollection<string> recentDirectories;
    [ObservableProperty] private int selectedTheme;
    [ObservableProperty] private string? ffmpegPath;
    [ObservableProperty] private string? ffprobePath;

    // OSS Tab 字段
    // OssProvider 是 UI 占位字段，目前只支持 Aliyun (index 0)，为未来扩展留入口。
    // 不参与 dirty 计算，不持久化到 Config（BuildOssConfigFromVm 硬编码 Provider = "Aliyun"）。
    [ObservableProperty] private int ossProvider = 0;
    [ObservableProperty] private string ossRegion = "";
    [ObservableProperty] private string ossBucket = "";
    [ObservableProperty] private string ossAccessKey = "";
    [ObservableProperty] private string ossSecretKey = "";
    [ObservableProperty] private string ossPathPrefix = "";

    // 状态
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private string? statusMessage;

    public SettingsViewModel(IDirectoryPickerService directoryPicker, IFilePickerService filePicker, IConfigService configService)
    {
        _directoryPicker = directoryPicker;
        _filePicker = filePicker;
        _configService = configService;
        RecentDirectories = new ObservableCollection<string>();
        IsDirty = false;
        IsSaving = false;
        SelectedTheme = 0;
        _isInitialized = false;
    }

    public async Task InitializeAsync()
    {
        _projectConfig = await _configService.GetOrCreateAsync<ProjectConfig>(ConfigKeys.Project);

        // 加载保存的主题
        var appConfig = await _configService.GetAsync<AppConfig>(ConfigKeys.App);
        if (appConfig != null)
        {
            _lastSavedTheme = appConfig.Theme == "RedNight" ? 1 : 0;
            SelectedTheme = _lastSavedTheme;

            _lastSavedFfmpegPath = appConfig.FFmpegPath;
            FfmpegPath = appConfig.FFmpegPath;
            _lastSavedFfprobePath = appConfig.FFprobePath;
            FfprobePath = appConfig.FFprobePath;
        }

        // 先设置 _lastSavedDirectory
        _lastSavedDirectory = _projectConfig.CurrentDirectory;
        SelectedProjectDirectory = _projectConfig.CurrentDirectory;

        RecentDirectories.Clear();
        foreach (var dir in _projectConfig.RecentDirectories)
        {
            RecentDirectories.Add(dir);
        }

        // 加载 OSS 配置
        var ossConfig = await _configService.GetOrCreateAsync<OssConfig>(ConfigKeys.Oss);
        _lastSavedOss = ossConfig;
        LoadOssFromConfig(ossConfig);

        // 最后才标记初始化完成
        _isInitialized = true;
        IsDirty = false;
        StatusMessage = null;
    }

    private void LoadOssFromConfig(OssConfig cfg)
    {
        OssRegion = cfg.Region ?? "";
        OssBucket = cfg.Bucket ?? "";
        OssAccessKey = cfg.AccessKeyId ?? "";
        OssSecretKey = cfg.AccessKeySecret ?? "";
        OssPathPrefix = cfg.PathPrefix ?? "";
    }

    private OssConfig BuildOssConfigFromVm()
    {
        return new OssConfig
        {
            Provider = "Aliyun",
            Region = OssRegion ?? "",
            Bucket = OssBucket ?? "",
            AccessKeyId = OssAccessKey ?? "",
            AccessKeySecret = OssSecretKey ?? "",
            PathPrefix = OssPathPrefix ?? ""
        };
    }

    [RelayCommand]
    private void SelectTab(SettingsTab tab)
    {
        SelectedTab = tab;
        // 跨 Tab 状态保持: 不清 IsDirty
    }

    partial void OnSelectedProjectDirectoryChanged(string? value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
        StatusMessage = null;
    }

    partial void OnSelectedThemeChanged(int value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
    }

    partial void OnFfmpegPathChanged(string? value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
        StatusMessage = null;
    }

    partial void OnFfprobePathChanged(string? value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
        StatusMessage = null;
    }

    partial void OnOssRegionChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
    }

    partial void OnOssBucketChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
    }

    partial void OnOssAccessKeyChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
    }

    partial void OnOssSecretKeyChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
    }

    partial void OnOssPathPrefixChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
    }

    private void RecomputeDirty()
    {
        if (!_isInitialized) return;

        var generalDirty = SelectedProjectDirectory != _lastSavedDirectory
                        || SelectedTheme != _lastSavedTheme
                        || FfmpegPath != _lastSavedFfmpegPath
                        || FfprobePath != _lastSavedFfprobePath;

        var currentOss = BuildOssConfigFromVm();
        var ossDirty = !OssConfigEquals(currentOss, _lastSavedOss);

        var newValue = generalDirty || ossDirty;
        if (IsDirty != newValue)
        {
            IsDirty = newValue;
        }
    }

    private static bool OssConfigEquals(OssConfig a, OssConfig? b)
    {
        if (b == null) return false;
        return a.Provider == b.Provider
            && a.Region == b.Region
            && a.Bucket == b.Bucket
            && a.AccessKeyId == b.AccessKeyId
            && a.AccessKeySecret == b.AccessKeySecret
            && a.PathPrefix == b.PathPrefix;
    }

    public void DiscardChanges()
    {
        // 恢复 General
        SelectedProjectDirectory = _lastSavedDirectory;
        SelectedTheme = _lastSavedTheme;
        FfmpegPath = _lastSavedFfmpegPath;
        FfprobePath = _lastSavedFfprobePath;

        RecentDirectories.Clear();
        if (_projectConfig != null)
        {
            foreach (var dir in _projectConfig.RecentDirectories)
            {
                RecentDirectories.Add(dir);
            }
        }

        // 恢复 OSS
        if (_lastSavedOss != null)
        {
            LoadOssFromConfig(_lastSavedOss);
        }

        IsDirty = false;
        StatusMessage = null;
    }

    [RelayCommand]
    private async Task BrowseDirectory()
    {
        var folder = await _directoryPicker.PickFolderAsync("选择项目目录");
        if (string.IsNullOrEmpty(folder))
            return;

        SelectedProjectDirectory = folder;
        AddToRecentDirectories(folder);
    }

    [RelayCommand]
    private void SelectRecentDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return;

        SelectedProjectDirectory = directory;
        AddToRecentDirectories(directory);
    }

    [RelayCommand]
    private void ClearRecentDirectories()
    {
        RecentDirectories.Clear();
        RecomputeDirty();
    }

    [RelayCommand]
    private async Task BrowseFFmpeg()
    {
        // 跨平台扩展名：Windows 走 .exe，macOS/Linux 走无扩展名 / .app 内嵌
        var ext = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? new[] { "exe" }
            : null;

        var file = await _filePicker.PickFileAsync("选择 ffmpeg 可执行文件", ext);
        if (string.IsNullOrEmpty(file))
            return;

        FfmpegPath = file;
    }

    [RelayCommand]
    private async Task BrowseFFprobe()
    {
        var ext = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? new[] { "exe" }
            : null;

        var file = await _filePicker.PickFileAsync("选择 ffprobe 可执行文件", ext);
        if (string.IsNullOrEmpty(file))
            return;

        FfprobePath = file;
    }

    private void AddToRecentDirectories(string directory)
    {
        if (RecentDirectories.Contains(directory))
            RecentDirectories.Remove(directory);

        RecentDirectories.Insert(0, directory);

        while (RecentDirectories.Count > 10)
            RecentDirectories.RemoveAt(RecentDirectories.Count - 1);
    }

    [RelayCommand]
    private async Task Save()
    {
        var hasDirectory = !string.IsNullOrEmpty(SelectedProjectDirectory);

        if (hasDirectory && !Directory.Exists(SelectedProjectDirectory))
        {
            StatusMessage = $"目录不存在：{SelectedProjectDirectory}";
            return;
        }

        // FFmpeg 路径校验：空 = 走系统 PATH；非空必须是「文件」而不是「目录」。
        var trimmedFfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath.Trim();
        if (!string.IsNullOrEmpty(trimmedFfmpegPath))
        {
            // 先判目录（用户最常见的错：把 ffmpeg 所在目录选进来了）
            if (Directory.Exists(trimmedFfmpegPath))
            {
                StatusMessage = $"FFmpeg 路径不能是目录：{trimmedFfmpegPath}";
                return;
            }
            // 再判文件存在
            if (!File.Exists(trimmedFfmpegPath))
            {
                StatusMessage = $"FFmpeg 文件不存在：{trimmedFfmpegPath}";
                return;
            }
        }

        // FFprobe 路径校验：规则跟 ffmpeg 一致
        var trimmedFfprobePath = string.IsNullOrWhiteSpace(FfprobePath) ? null : FfprobePath.Trim();
        if (!string.IsNullOrEmpty(trimmedFfprobePath))
        {
            if (Directory.Exists(trimmedFfprobePath))
            {
                StatusMessage = $"FFprobe 路径不能是目录：{trimmedFfprobePath}";
                return;
            }
            if (!File.Exists(trimmedFfprobePath))
            {
                StatusMessage = $"FFprobe 文件不存在：{trimmedFfprobePath}";
                return;
            }
        }

        IsSaving = true;
        StatusMessage = null;

        try
        {
            // 保存项目配置
            if (hasDirectory)
            {
                _projectConfig ??= new ProjectConfig();
                _projectConfig.CurrentDirectory = SelectedProjectDirectory;
                _projectConfig.RecentDirectories.Clear();
                foreach (var dir in RecentDirectories)
                {
                    _projectConfig.RecentDirectories.Add(dir);
                }

                await _configService.SetAsync(ConfigKeys.Project, _projectConfig);
            }

            // 保存 App 配置（主题 + FFmpeg / FFprobe 路径）
            var theme = SelectedTheme == 1 ? "RedNight" : "DeepSpace";
            var appConfig = new AppConfig
            {
                Theme = theme,
                FFmpegPath = trimmedFfmpegPath,
                FFprobePath = trimmedFfprobePath,
            };
            await _configService.SetAsync(ConfigKeys.App, appConfig);
            ThemeManager.SetTheme(SelectedTheme == 1);

            // 立即把 FfmpegPath / FfprobePath 应用到 FFmpegConfigurator，不需要重启
            FFmpegConfigurator.Apply(trimmedFfmpegPath, trimmedFfprobePath);

            // 把 trim 后的值同步回 UI（用户输入带首尾空格的场景）
            if (FfmpegPath != trimmedFfmpegPath)
            {
                FfmpegPath = trimmedFfmpegPath;
            }
            if (FfprobePath != trimmedFfprobePath)
            {
                FfprobePath = trimmedFfprobePath;
            }

            // 保存 OSS 配置
            var ossConfig = BuildOssConfigFromVm();
            await _configService.SetAsync(ConfigKeys.Oss, ossConfig);

            // 刷新快照
            _lastSavedDirectory = SelectedProjectDirectory;
            _lastSavedTheme = SelectedTheme;
            _lastSavedFfmpegPath = trimmedFfmpegPath;
            _lastSavedFfprobePath = trimmedFfprobePath;
            _lastSavedOss = ossConfig;

            IsDirty = false;
            StatusMessage = "已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }
}
