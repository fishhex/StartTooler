using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Helpers;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public enum SettingsTab
{
    General,
    Oss,
    AI
}

/// <summary>连接测试状态（AI / OSS 复用）。驱动 View 里按钮文字 + 结果图标颜色。</summary>
public enum AITestState
{
    Idle,
    Running,
    Ok,
    Failed,
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly IDirectoryPickerService _directoryPicker;
    private readonly IFilePickerService _filePicker;
    private readonly IConfigService _configService;
    private readonly IOssStorageFactory _ossFactory;

    // General Tab 快照
    private string? _lastSavedDirectory;
    private int _lastSavedTheme;  // 0=DeepSpace, 1=RedNight
    private string? _lastSavedFfmpegPath;
    private string? _lastSavedFfprobePath;

    // OSS Tab 快照
    private OssConfig? _lastSavedOss;

    // AI Tab 快照
    private AIConfig? _lastSavedAI;

    private ProjectConfig? _projectConfig;
    private bool _isInitialized;

    /// <summary>
    /// v0.11 §5.1：AI 厂商切换确认框「不再提示」标记。
    /// 进程级 —— 重新启动回到默认行为（弹确认）。
    /// </summary>
    private bool _skipProviderSwitchWarning;

    [ObservableProperty] private SettingsTab selectedTab = SettingsTab.General;

    // General Tab 字段
    [ObservableProperty] private string? selectedProjectDirectory;
    [ObservableProperty] private ObservableCollection<string> recentDirectories;
    [ObservableProperty] private int selectedTheme;
    [ObservableProperty] private string? ffmpegPath;
    [ObservableProperty] private string? ffprobePath;

    // 通用 Tab 验证错误（spec §3.2）—— null 表示无错
    [ObservableProperty] private string? ffmpegPathError;
    [ObservableProperty] private string? ffprobePathError;

    // OSS Tab 字段
    [ObservableProperty] private string ossRegion = "";
    [ObservableProperty] private string ossBucket = "";
    [ObservableProperty] private string ossAccessKey = "";
    [ObservableProperty] private string ossSecretKey = "";
    [ObservableProperty] private string ossPathPrefix = "";

    // OSS Tab 验证错误
    [ObservableProperty] private string? ossRegionError;
    [ObservableProperty] private string? ossBucketError;
    [ObservableProperty] private string? ossAccessKeyError;
    [ObservableProperty] private string? ossSecretKeyError;

    /// <summary>OSS Secret 眼睛切换。spec §4.3 切 Tab 时 reset 到 false。</summary>
    [ObservableProperty] private bool isOssSecretKeyVisible;

    // OSS 连接测试状态（spec §4.2）
    [ObservableProperty] private AITestState ossTestState = AITestState.Idle;
    [ObservableProperty] private string? ossTestMessage;

    // AI Tab 字段
    [ObservableProperty] private AIProviderMeta aiProviderMeta = AIProviderCatalog.Get(AIProvider.Anthropic);
    [ObservableProperty] private string aiApiKey = "";
    [ObservableProperty] private string aiBaseUrl = "";
    [ObservableProperty] private string aiModel = "";
    [ObservableProperty] private string aiTestPrompt = "请分析这张天文照片的主体、质量和拍摄参数";

    /// <summary>
    /// AI 协议（"OpenAI" / "Anthropic"）。与厂商解耦 —— 切厂商不联动。
    /// 默认 "" 空串：强制用户显式选，不允许有静默默认值。
    /// 老 config.db 反序列化时 cfg.Protocol 缺失 → AIProtocol 保持空 → UI 强制让用户选。
    /// </summary>
    [ObservableProperty] private string aiProtocol = "";

    /// <summary>协议选项列表（驱动 UI ComboBox）。空串不列 —— 强制让用户选 OpenAI 或 Anthropic。</summary>
    public System.Collections.Generic.IReadOnlyList<string> AiProtocolOptions { get; }
        = new[] { "OpenAI", "Anthropic" };

    /// <summary>当前厂商的推荐模型列表，驱动 Model 下拉的 ItemsSource。</summary>
    [ObservableProperty] private System.Collections.Generic.IReadOnlyList<string> aiRecommendedModels
        = AIProviderCatalog.Get(AIProvider.Anthropic).RecommendedModels;

    /// <summary>对外暴露的当前厂商枚举（兼容 AIConfig 序列化、其它调用方）。</summary>
    public AIProvider AiProvider => AiProviderMeta?.Provider ?? AIProvider.Anthropic;

    /// <summary>所有厂商元数据列表，驱动厂商 ComboBox 的 ItemsSource。</summary>
    public System.Collections.Generic.IReadOnlyList<AIProviderMeta> AiProviders => AIProviderCatalog.All;

    // AI API Key 验证错误（spec §3.2）
    [ObservableProperty] private string? aiApiKeyError;

    // 状态
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private string? statusMessage;

    // 保存成功反馈（spec §3.3）
    [ObservableProperty] private string saveButtonText = "保存";

    /// <summary>
    /// 是否有任何验证错误（spec §3.2 + §8）—— 控制保存按钮 IsEnabled，
    /// 错误时直接置灰，不必等用户点保存才提示。
    /// </summary>
    public bool HasValidationErrors =>
        !string.IsNullOrEmpty(FfmpegPathError)
        || !string.IsNullOrEmpty(FfprobePathError)
        || !string.IsNullOrEmpty(OssRegionError)
        || !string.IsNullOrEmpty(OssBucketError)
        || !string.IsNullOrEmpty(OssAccessKeyError)
        || !string.IsNullOrEmpty(OssSecretKeyError)
        || !string.IsNullOrEmpty(AiApiKeyError);

    // AI 连接测试状态
    [ObservableProperty] private AITestState aiTestState = AITestState.Idle;
    [ObservableProperty] private string? aiTestMessage;

    /// <summary>API Key 显示/隐藏切换。默认 false = 隐藏（密码框）。不持久化 —— UI 临时状态。</summary>
    [ObservableProperty] private bool isAiApiKeyVisible;

    public SettingsViewModel(
        IDirectoryPickerService directoryPicker,
        IFilePickerService filePicker,
        IConfigService configService,
        IOssStorageFactory ossFactory)
    {
        _directoryPicker = directoryPicker;
        _filePicker = filePicker;
        _configService = configService;
        _ossFactory = ossFactory;
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

        // v0.11 §3.1: 目录历史优先读 ConfigKeys.ProjectHistory（spec §7 定义的独立 key），
        // fallback 到 ProjectConfig.RecentDirectories（老数据兼容）。
        var history = await _configService.GetAsync<System.Collections.Generic.List<string>>(ConfigKeys.ProjectHistory);
        if (history != null && history.Count > 0)
        {
            RecentDirectories.Clear();
            foreach (var dir in history) RecentDirectories.Add(dir);
        }
        else
        {
            RecentDirectories.Clear();
            foreach (var dir in _projectConfig.RecentDirectories) RecentDirectories.Add(dir);
        }

        // 加载 OSS 配置
        var ossConfig = await _configService.GetOrCreateAsync<OssConfig>(ConfigKeys.Oss);
        _lastSavedOss = ossConfig;
        LoadOssFromConfig(ossConfig);

        // 加载 AI 配置（v1 起从 "ai" key 读；旧 "anthropic" key 不再读）
        var aiConfig = await _configService.GetOrCreateAsync<AIConfig>(ConfigKeys.AI);
        _lastSavedAI = aiConfig;
        LoadAIFromConfig(aiConfig);

        // 最后才标记初始化完成
        _isInitialized = true;
        IsDirty = false;
        StatusMessage = null;

        // 通知保存按钮（HasValidationErrors 可能在 init 期间没触发）
        SaveCommand.NotifyCanExecuteChanged();
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

    private void LoadAIFromConfig(AIConfig cfg)
    {
        // Provider 字符串 → 枚举（兼容未来枚举值重排/废弃）
        AIProvider provider = AIProvider.Anthropic;
        if (!string.IsNullOrWhiteSpace(cfg.Provider)
            && System.Enum.TryParse<AIProvider>(cfg.Provider, ignoreCase: true, out var parsed))
        {
            provider = parsed;
        }
        var meta = AIProviderCatalog.Get(provider);

        AiProviderMeta = meta;
        AiApiKey = cfg.ApiKey ?? "";

        // BaseUrl：空 → 用厂商默认；非空 → 用保存值（用户可能填了私有化部署 / 代理）
        AiBaseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? meta.DefaultBaseUrl : cfg.BaseUrl;

        // Model：空 → 用厂商推荐第一个；非空 → 用保存值
        AiModel = string.IsNullOrWhiteSpace(cfg.Model) ? meta.DefaultModel : cfg.Model;

        // Protocol：老 JSON 缺失 / 空 → 保持空 → UI 强制让用户选（不联动厂商 meta.ProtocolKind）
        AiProtocol = cfg.Protocol ?? "";

        // v0.11 §5.2: 测试 prompt 持久化字段
        AiTestPrompt = string.IsNullOrWhiteSpace(cfg.TestPrompt)
            ? "请分析这张天文照片的主体、质量和拍摄参数"
            : cfg.TestPrompt;

        // 刷新推荐列表
        AiRecommendedModels = meta.RecommendedModels;
    }

    private AIConfig BuildAIConfigFromVm()
    {
        var meta = AiProviderMeta ?? AIProviderCatalog.Get(AIProvider.Anthropic);
        return new AIConfig
        {
            Provider = meta.Provider.ToString(),
            ApiKey = (AiApiKey ?? "").Trim(),
            BaseUrl = string.IsNullOrWhiteSpace(AiBaseUrl) ? meta.DefaultBaseUrl : AiBaseUrl.Trim(),
            Model = string.IsNullOrWhiteSpace(AiModel) ? meta.DefaultModel : AiModel.Trim(),
            Protocol = AiProtocol ?? "",
            TestPrompt = AiTestPrompt ?? "",
        };
    }

    [RelayCommand]
    private void SelectTab(SettingsTab tab)
    {
        SelectedTab = tab;
        // 跨 Tab 状态保持: 不清 IsDirty

        // spec §8 边界: Secret 切换时页面切换 → 复位到隐藏
        if (tab != SettingsTab.Oss)
        {
            IsOssSecretKeyVisible = false;
        }
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
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnFfprobePathChanged(string? value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
        StatusMessage = null;
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnOssRegionChanged(string value)
    {
        if (!_isInitialized) return;
        OssRegionError = string.IsNullOrWhiteSpace(value) ? "Region 不能为空" : null;
        RecomputeDirty();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnOssBucketChanged(string value)
    {
        if (!_isInitialized) return;
        OssBucketError = string.IsNullOrWhiteSpace(value) ? "Bucket 不能为空" : null;
        RecomputeDirty();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnOssAccessKeyChanged(string value)
    {
        if (!_isInitialized) return;
        OssAccessKeyError = string.IsNullOrWhiteSpace(value) ? "AccessKeyId 不能为空" : null;
        RecomputeDirty();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnOssSecretKeyChanged(string value)
    {
        if (!_isInitialized) return;
        OssSecretKeyError = string.IsNullOrWhiteSpace(value) ? "AccessKeySecret 不能为空" : null;
        RecomputeDirty();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnOssPathPrefixChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
    }

    partial void OnAiProviderMetaChanged(AIProviderMeta? oldValue, AIProviderMeta newValue)
    {
        if (!_isInitialized) return;
        if (oldValue == null || newValue == null) return;
        if (oldValue.Provider == newValue.Provider) return;

        // v0.11 §5.1: 切厂商弹三选确认 —— 用户选「不再提示」则后续跳过
        if (_skipProviderSwitchWarning)
        {
            ApplyProviderChange(newValue);
            return;
        }

        // 同步执行到 UI 线程弹窗（Avalonia dialog 必须 UI 线程）
        _ = ConfirmProviderSwitchAsync(oldValue, newValue);
    }

    private async Task ConfirmProviderSwitchAsync(AIProviderMeta oldMeta, AIProviderMeta newMeta)
    {
        var window = DialogHelper.GetMainWindow();
        if (window == null)
        {
            // 拿不到窗口时直接落 default（不卡 UI）
            ApplyProviderChange(newMeta);
            return;
        }

        var result = await DialogHelper.ShowChoiceAsync(
            window,
            title: "切换厂商",
            message: "将自动填入默认 Base URL 和推荐模型列表，API Key 保持不变。",
            primaryButtonText: "确认切换",
            secondaryButtonText: "取消",
            tertiaryButtonText: "不再提示");

        switch (result)
        {
            case DialogHelper.DialogChoice.Primary:
                // 确认切换：继续走 OnAiProviderMetaChanged 已经修改的 Meta → 走 apply
                ApplyProviderChange(newMeta);
                break;
            case DialogHelper.DialogChoice.Tertiary:
                // 不再提示：标记 + 走 apply
                _skipProviderSwitchWarning = true;
                ApplyProviderChange(newMeta);
                break;
            default:
                // 取消 / 关闭：回滚 AiProviderMeta 到 old（但 OnAiProviderMetaChanged 已改完）
                // → 静默写回老值；这会再触发一次 OnAiProviderMetaChanged(old, new=old)
                //   因为 Provider 相同 → 走 early return，不会再弹窗
                AiProviderMeta = oldMeta;
                break;
        }
    }

    private void ApplyProviderChange(AIProviderMeta meta)
    {
        // 切换厂商：刷新推荐列表 + 同步默认 BaseUrl + Model
        AiRecommendedModels = meta.RecommendedModels;

        // BaseUrl：自定义厂商不强制填；其它厂商一律同步默认
        if (meta.Provider != AIProvider.Custom)
        {
            AiBaseUrl = meta.DefaultBaseUrl;
        }
        else
        {
            AiBaseUrl = "";
        }

        // Model 同步到推荐列表第一个
        AiModel = meta.DefaultModel;

        // Protocol **不联动**：保留用户已选；未选保持空
        RecomputeDirty();
    }

    partial void OnAiApiKeyChanged(string value)
    {
        if (!_isInitialized) return;
        // 非阻塞性前缀提示（spec §3.2 + §8 不强制）
        AiApiKeyError = string.IsNullOrWhiteSpace(value)
            ? "API Key 不能为空"
            : RecommendApiKeyPrefixHint(value);
        RecomputeDirty();
        TestConnectionCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static string? RecommendApiKeyPrefixHint(string key)
    {
        // 不强制阻塞保存，只在「完全不像」时给提示。常见前缀：sk- / sat- (Zhipu) 等。
        // 已有合法前缀 → 不提示。
        var trimmed = key.TrimStart();
        if (trimmed.StartsWith("sk-", StringComparison.Ordinal)
            || trimmed.StartsWith("sat-", StringComparison.Ordinal))
        {
            return null;
        }
        return "建议以 'sk-' 开头（OpenAI / DeepSeek / Moonshot）或 'sat-'（智谱）";
    }

    partial void OnAiBaseUrlChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnAiModelChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnAiProtocolChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnAiTestPromptChanged(string value)
    {
        if (!_isInitialized) return;
        RecomputeDirty();
    }

    partial void OnAiRecommendedModelsChanged(System.Collections.Generic.IReadOnlyList<string> value)
    {
        // 推荐列表变了，触发 IsDirty 重算（因为 Dirty 比的是 BaseUrl/Model 等字段值）
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

        var currentAI = BuildAIConfigFromVm();
        var aiDirty = !AIConfigEquals(currentAI, _lastSavedAI);

        var newValue = generalDirty || ossDirty || aiDirty;
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

    private static bool AIConfigEquals(AIConfig a, AIConfig? b)
    {
        if (b == null) return false;
        return a.Provider == b.Provider
            && a.ApiKey == b.ApiKey
            && a.BaseUrl == b.BaseUrl
            && a.Model == b.Model
            && a.Protocol == b.Protocol
            && a.TestPrompt == b.TestPrompt;
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

        // 恢复 AI
        if (_lastSavedAI != null)
        {
            LoadAIFromConfig(_lastSavedAI);
        }

        IsDirty = false;
        StatusMessage = null;

        // 验证错误清掉 + 保存按钮刷新
        FfmpegPathError = null;
        FfprobePathError = null;
        OssRegionError = null;
        OssBucketError = null;
        OssAccessKeyError = null;
        OssSecretKeyError = null;
        AiApiKeyError = null;
        SaveCommand.NotifyCanExecuteChanged();
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

    /// <summary>
    /// v0.11 §3.2 边界：FFmpeg / FFprobe 路径在 LostFocus 时由 View 调用做文件存在性检查。
    /// 空 = 走系统 PATH（合法），非空但不存在 → 警告（不阻塞保存）。
    /// </summary>
    public void ValidateFfmpegPath()
    {
        var p = FfmpegPath;
        if (string.IsNullOrWhiteSpace(p))
        {
            FfmpegPathError = null;
            return;
        }
        FfmpegPathError = File.Exists(p) ? null : "文件不存在";
        SaveCommand.NotifyCanExecuteChanged();
    }

    public void ValidateFfprobePath()
    {
        var p = FfprobePath;
        if (string.IsNullOrWhiteSpace(p))
        {
            FfprobePathError = null;
            return;
        }
        FfprobePathError = File.Exists(p) ? null : "文件不存在";
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// v0.11 §4.4 边界：OssPathPrefix 失焦时若非空且不以 / 结尾，自动补 /。
    /// </summary>
    public void NormalizeOssPathPrefix()
    {
        var p = OssPathPrefix;
        if (string.IsNullOrWhiteSpace(p)) return;
        if (p.EndsWith("/", StringComparison.Ordinal)) return;
        OssPathPrefix = p + "/";
    }

    private void AddToRecentDirectories(string directory)
    {
        if (RecentDirectories.Contains(directory))
            RecentDirectories.Remove(directory);

        RecentDirectories.Insert(0, directory);

        while (RecentDirectories.Count > 10)
            RecentDirectories.RemoveAt(RecentDirectories.Count - 1);
    }

    /// <summary>
    /// 切换 API Key 明文/隐藏。纯 UI 状态，不进 Config，不进 dirty 计算。
    /// 每次切换焦点不丢：直接 toggle PasswordChar 不重建 TextBox。
    /// </summary>
    [RelayCommand]
    private void ToggleAiApiKeyVisibility() => IsAiApiKeyVisible = !IsAiApiKeyVisible;

    /// <summary>
    /// v0.11 §4.3: 切换 OSS Secret 显示 / 隐藏。
    /// </summary>
    [RelayCommand]
    private void ToggleOssSecretKeyVisibility() => IsOssSecretKeyVisible = !IsOssSecretKeyVisible;

    /// <summary>
    /// v0.11 §4.2: 测试当前 OSS 配置是否打通真服务。DoesBucketExistAsync 验证
    ///   - Endpoint 可达
    ///   - 凭据有效
    ///   - Bucket 存在
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestOssConnection))]
    private async Task TestOssConnectionAsync()
    {
        // 基础校验 —— 4 个字段缺一不可
        if (string.IsNullOrWhiteSpace(OssRegion)
            || string.IsNullOrWhiteSpace(OssBucket)
            || string.IsNullOrWhiteSpace(OssAccessKey)
            || string.IsNullOrWhiteSpace(OssSecretKey))
        {
            OssTestState = AITestState.Failed;
            OssTestMessage = "Region / Bucket / AccessKey / SecretKey 均为必填";
            return;
        }

        OssTestState = AITestState.Running;
        OssTestMessage = "测试中…";

        try
        {
            // 构造一个临时 OssConfig（按 UI 当前值），不污染 _lastSavedOss
            var tmpConfig = BuildOssConfigFromVm();
            using var storage = new AliyunOssStorage(tmpConfig);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ok = await storage.TestConnectionAsync(cts.Token);

            OssTestState = ok ? AITestState.Ok : AITestState.Failed;
            OssTestMessage = ok ? "连接成功" : "连接失败（Bucket 不存在或凭据错误）";
        }
        catch (OperationCanceledException)
        {
            OssTestState = AITestState.Failed;
            OssTestMessage = "连接超时（15s）";
        }
        catch (Exception ex)
        {
            OssTestState = AITestState.Failed;
            OssTestMessage = $"测试异常：{ex.Message}";
        }
        finally
        {
            TestOssConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanTestOssConnection()
        => _isInitialized
           && OssTestState != AITestState.Running
           && !string.IsNullOrWhiteSpace(OssRegion)
           && !string.IsNullOrWhiteSpace(OssBucket)
           && !string.IsNullOrWhiteSpace(OssAccessKey)
           && !string.IsNullOrWhiteSpace(OssSecretKey);

    partial void OnOssTestStateChanged(AITestState value)
    {
        TestOssConnectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 测试当前 AI 配置是否打通真服务。发一个极小请求验证：
    ///   BaseUrl 可达 + 鉴权通过 + Model 存在。
    /// 跑测试时按钮 disabled，避免重复点。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        var meta = AiProviderMeta;
        if (meta == null) return;

        AiTestState = AITestState.Running;
        AiTestMessage = "测试中…";

        try
        {
            // v0.11 §5.2: prompt 走 UI 当前值
            var result = await AITester.TestAsync(
                AiProtocol ?? "",
                AiApiKey ?? "",
                AiBaseUrl ?? "",
                AiModel ?? "",
                AiTestPrompt ?? "");

            AiTestState = result.Success ? AITestState.Ok : AITestState.Failed;
            AiTestMessage = result.Success
                ? (string.IsNullOrEmpty(result.ProtocolNote) ? result.Message : $"{result.Message}（{result.ProtocolNote}）")
                : result.Message;
        }
        catch (Exception ex)
        {
            AiTestState = AITestState.Failed;
            AiTestMessage = $"测试异常：{ex.Message}";
        }
        finally
        {
            TestConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanTestConnection()
        => _isInitialized
           && AiTestState != AITestState.Running
           && !string.IsNullOrWhiteSpace(AiApiKey)
           && !string.IsNullOrWhiteSpace(AiBaseUrl)
           && !string.IsNullOrWhiteSpace(AiModel)
           && !string.IsNullOrWhiteSpace(AiProtocol);

    partial void OnAiTestStateChanged(AITestState value)
    {
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    // ============================================================
    // 导入 / 导出配置 (v0.11 §3.4)
    // ============================================================

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        var path = await _filePicker.SaveFileAsync("导出配置", "starttooler-config.json", "json");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            await using var fs = File.Create(path);
            // v0.11: 允许导出密钥 —— 用户主动备份场景下不想手动重填
            var count = await _configService.ExportToJsonAsync(fs, redactSecrets: false);
            StatusMessage = $"已导出 {count} 项配置到 {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        var path = await _filePicker.PickFileAsync("导入配置", "json");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            await using var fs = File.OpenRead(path);
            var count = await _configService.ImportFromJsonAsync(fs);
            StatusMessage = $"已导入 {count} 项配置（含密钥），请检查后保存";

            // 导入后重新加载 VM（让 UI 反映新值）
            await ReloadFromConfigAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 从 config.db 重新加载所有字段。导入完成后让 UI 反映新值用。
    /// </summary>
    private async Task ReloadFromConfigAsync()
    {
        _isInitialized = false;
        try
        {
            _projectConfig = await _configService.GetAsync<ProjectConfig>(ConfigKeys.Project) ?? new ProjectConfig();
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
            _lastSavedDirectory = _projectConfig.CurrentDirectory;
            SelectedProjectDirectory = _projectConfig.CurrentDirectory;

            var history = await _configService.GetAsync<System.Collections.Generic.List<string>>(ConfigKeys.ProjectHistory);
            RecentDirectories.Clear();
            if (history != null)
                foreach (var dir in history) RecentDirectories.Add(dir);

            var ossConfig = await _configService.GetAsync<OssConfig>(ConfigKeys.Oss) ?? new OssConfig();
            _lastSavedOss = ossConfig;
            LoadOssFromConfig(ossConfig);

            var aiConfig = await _configService.GetAsync<AIConfig>(ConfigKeys.AI) ?? new AIConfig();
            _lastSavedAI = aiConfig;
            LoadAIFromConfig(aiConfig);

            IsDirty = false;
        }
        finally
        {
            _isInitialized = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        var hasDirectory = !string.IsNullOrEmpty(SelectedProjectDirectory);

        // v0.11 §3.2: 保存时再次跑一遍 FFmpeg / FFprobe 路径校验 —— 防止初始化后路径被改但验证未触发
        if (!string.IsNullOrWhiteSpace(FfmpegPath) && !File.Exists(FfmpegPath))
        {
            StatusMessage = $"FFmpeg 文件不存在：{FfmpegPath}";
            FfmpegPathError = "文件不存在";
            SaveCommand.NotifyCanExecuteChanged();
            return;
        }
        if (!string.IsNullOrWhiteSpace(FfprobePath) && !File.Exists(FfprobePath))
        {
            StatusMessage = $"FFprobe 文件不存在：{FfprobePath}";
            FfprobePathError = "文件不存在";
            SaveCommand.NotifyCanExecuteChanged();
            return;
        }

        if (hasDirectory && !Directory.Exists(SelectedProjectDirectory))
        {
            StatusMessage = $"目录不存在：{SelectedProjectDirectory}";
            return;
        }

        // AI Protocol 校验：强制用户显式选，不允许空保存（防止老 config.db 缺字段时静默默认值）
        if (string.IsNullOrWhiteSpace(AiProtocol))
        {
            StatusMessage = "请选择 AI 协议（OpenAI 或 Anthropic）";
            return;
        }

        // 验证 OSS 必填字段
        if (string.IsNullOrWhiteSpace(OssRegion) || string.IsNullOrWhiteSpace(OssBucket)
            || string.IsNullOrWhiteSpace(OssAccessKey) || string.IsNullOrWhiteSpace(OssSecretKey))
        {
            StatusMessage = "请填写 OSS Region / Bucket / AccessKey / SecretKey";
            return;
        }

        // 验证 AI API Key 必填
        if (string.IsNullOrWhiteSpace(AiApiKey))
        {
            StatusMessage = "请填写 AI API Key";
            return;
        }

        IsSaving = true;
        StatusMessage = null;

        try
        {
            // v0.11 §3.1: Save 成功后把当前目录追加到 ProjectHistory 头部（去重 + max 10）
            if (hasDirectory)
            {
                AddToRecentDirectories(SelectedProjectDirectory!);
            }

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

            // v0.11 §3.1: 同步写 ProjectHistory（spec 要求的独立 key）—— 与 ProjectConfig.RecentDirectories 保持一致
            await _configService.SetAsync(ConfigKeys.ProjectHistory, RecentDirectories.ToList());

            // 保存 App 配置（主题 + FFmpeg / FFprobe 路径）
            var theme = SelectedTheme == 1 ? "RedNight" : "DeepSpace";
            var trimmedFfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath.Trim();
            var trimmedFfprobePath = string.IsNullOrWhiteSpace(FfprobePath) ? null : FfprobePath.Trim();
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

            // 保存 AI 配置
            var aiConfig = BuildAIConfigFromVm();
            await _configService.SetAsync(ConfigKeys.AI, aiConfig);

            // 刷新快照
            _lastSavedDirectory = SelectedProjectDirectory;
            _lastSavedTheme = SelectedTheme;
            _lastSavedFfmpegPath = trimmedFfmpegPath;
            _lastSavedFfprobePath = trimmedFfprobePath;
            _lastSavedOss = ossConfig;
            _lastSavedAI = aiConfig;

            IsDirty = false;
            StatusMessage = "已保存";

            // v0.11 §3.3: 保存成功反馈 —— 按钮文字 1.5s 后恢复
            _ = ShowSaveConfirmationAsync();
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

    private bool CanSave()
        => _isInitialized
           && !IsSaving
           && !HasValidationErrors;

    partial void OnIsSavingChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
    }

    private async Task ShowSaveConfirmationAsync()
    {
        SaveButtonText = "已保存 ✓";
        try
        {
            await Task.Delay(1500);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Settings] ShowSaveConfirmation 延时异常: {ex.Message}");
        }
        // 期间用户可能切换了 Tab / 触发再次保存 → 简单保护：只在当前还是「已保存」状态时重置
        if (SaveButtonText == "已保存 ✓")
        {
            SaveButtonText = "保存";
        }
    }
}
