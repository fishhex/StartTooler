# 06 — 设置页与配置校验

> 对应代码：`ViewModels/SettingsViewModel.cs`、`Views/SettingsView.axaml`，以及 `Services/AppConfig.cs`、`ProjectConfig.cs`、`OssConfig.cs`、`ConfigKeys.cs`、`ConfigService.cs`、`ThemeManager.cs`、`FFmpegConfigurator.cs`。

---

## 1. 模块边界

```
SettingsViewModel
  ├─ IDirectoryPickerService    (Avalonia.StorageProvider.OpenFolderPicker)
  ├─ IFilePickerService         (OpenFilePicker 给 FFmpeg)
  └─ IConfigService             (读 + 写 ConfigDb)

UI:
  Views/SettingsView.axaml (General / OSS 两个 Tab)

宿主：
  MainWindowViewModel.NavigateToSettings 切到 SettingsVM
  MainWindowVM 拦截 NavigateToGallery（IsDirty 时弹 Discard）
```

---

## 2. SettingsViewModel 字段模型

### 2.1 持久化字段（按 Tab）

| Tab | 字段 | 类型 | 持久化到 |
|---|---|---|---|
| General | `SelectedProjectDirectory` | `string?` | `ProjectConfig.CurrentDirectory` + RecentDirs |
| General | `RecentDirectories` | `ObservableCollection<string>` (max 10) | `ProjectConfig.RecentDirectories` |
| General | `SelectedTheme` | `int` (0=DeepSpace, 1=RedNight) | `AppConfig.Theme` |
| General | `FFmpegPath` | `string?` | `AppConfig.FFmpegPath` |
| General | `FFprobePath` | `string?` | `AppConfig.FFprobePath` |
| OSS | `OssProvider` | `int` (UI 占位，当前 0=Aliyun 唯 1) | **不持久化**，硬编码 "Aliyun" |
| OSS | `OssRegion` | `string` | `OssConfig.Region` |
| OSS | `OssBucket` | `string` | `OssConfig.Bucket` |
| OSS | `OssAccessKey` | `string` | `OssConfig.AccessKeyId` |
| OSS | `OssSecretKey` | `string` | `OssConfig.AccessKeySecret` |
| OSS | `OssPathPrefix` | `string` | `OssConfig.PathPrefix` |
| AI (Anthropic) | `AnthropicApiKey` | `string` (UI PasswordChar 隐藏) | `AnthropicConfig.ApiKey` |
| AI (Anthropic) | `AnthropicBaseUrl` | `string` (默认 `https://api.anthropic.com`) | `AnthropicConfig.BaseUrl` |
| AI (Anthropic) | `AnthropicModel` | `string` (默认 `claude-3-5-sonnet-latest`，ComboBox 可编辑) | `AnthropicConfig.Model` |

> `OssProvider` 在 UI 占位 `ComboBox` 渲染 "Aliyun" 那一项，**不参与 dirty 不参与持久化** —— `BuildOssConfigFromVm` 硬编码 `Provider = "Aliyun"`（`SettingsViewModel.cs:121`）。

### 2.2 状态字段

| 字段 | 类型 | 含义 |
|---|---|---|
| `SelectedTab` | `SettingsTab { General, Oss, Anthropic }` | 当前 Tab |
| `IsDirty` | `bool` | 与 `_lastSaved*` 快照不一致 |
| `IsSaving` | `bool` | Save 进行中（按钮 disabled） |
| `StatusMessage` | `string?` | "已保存" / "FFmpeg 文件不存在" 等反馈 |

### 2.3 快照（dirty tracking）

```csharp
// General
private string? _lastSavedDirectory;
private int _lastSavedTheme;
private string? _lastSavedFfmpegPath;
private string? _lastSavedFfprobePath;

// OSS
private OssConfig? _lastSavedOss;

// Anthropic
private AnthropicConfig? _lastSavedAnthropic;
```

`OnSelectedProjectDirectoryChanged` 等 13 个 `[NotifyPropertyChangedFor]` 触发 `RecomputeDirty()`，逐字段对比 `_lastSaved*`。

---

## 3. 生命周期

### 3.1 启动（`InitializeAsync` — `SettingsViewModel.cs:71-107`）

```
MainWindowVM 构造 ctor → settings = new SettingsViewModel(...)
MainWindowVM.InitializeAsync() → await settings.InitializeAsync()
  ├─ _projectConfig = _configService.GetOrCreateAsync<ProjectConfig>(ConfigKeys.Project)
  ├─ appConfig = _configService.GetAsync<AppConfig>(ConfigKeys.App)
  │     └─ 加载主题 + FFmpegPath + FFprobePath
  ├─ _lastSavedDirectory = _projectConfig.CurrentDirectory
  │     SelectedProjectDirectory = 同上
  ├─ RecentDirectories.Clear() + foreach add
  ├─ ossConfig = _configService.GetOrCreateAsync<OssConfig>(ConfigKeys.Oss)
  │     └─ _lastSavedOss = ossConfig; LoadOssFromConfig(ossConfig)
  ├─ anthropicConfig = _configService.GetOrCreateAsync<AnthropicConfig>(ConfigKeys.Anthropic)
  │     └─ _lastSavedAnthropic = anthropicConfig; LoadAnthropicFromConfig(anthropicConfig)
  └─ _isInitialized = true; IsDirty = false; StatusMessage = null
```

> **顺序关键**：先读各个配置，**最后**才 `_isInitialized = true` —— dirty 计算依赖 `_isInitialized` 守门，未初始完时改字段不触发 dirty。

### 3.2 字段修改 → 触发 dirty

```csharp
partial void OnSelectedProjectDirectoryChanged(string? value) {
    if (!_isInitialized) return;
    RecomputeDirty();
    StatusMessage = null;          // 清空旧 toast
}
```

`RecomputeDirty`（`SettingsViewModel.cs:195-212`）：
```csharp
var generalDirty = SelectedProjectDirectory != _lastSavedDirectory
                || SelectedTheme != _lastSavedTheme
                || FfmpegPath != _lastSavedFfmpegPath
                || FfprobePath != _lastSavedFfprobePath;
var currentOss = BuildOssConfigFromVm();
var ossDirty = !OssConfigEquals(currentOss, _lastSavedOss);
var currentAnthropic = BuildAnthropicConfigFromVm();
var anthropicDirty = !AnthropicConfigEquals(currentAnthropic, _lastSavedAnthropic);
var newValue = generalDirty || ossDirty || anthropicDirty;
if (IsDirty != newValue) IsDirty = newValue;
```

> 跨 Tab 状态保持：`SelectTab` 只切 `SelectedTab`，**不清 IsDirty** —— 用户在 OSS Tab 改了东西切去 General 还能看到「已修改」（`SettingsViewModel.cs:131-136` 注释明确）。

### 3.3 切到其他页面 → Discard 弹框

`MainWindowViewModel.NavigateToGallery` 检查 `settings.IsDirty`：
- 是 → `DialogHelper.ShowConfirmAsync(window, title, message, "丢弃", "取消")`
- 用户点「丢弃」→ `SettingsViewModel.DiscardChanges()` → 字段全部复位

`DiscardChanges`（`SettingsViewModel.cs:225-250`）：从 `_lastSaved*` 还原所有字段（General + OSS + Anthropic）。

### 3.4 Browse 命令

| 命令 | 行为 |
|---|---|
| `BrowseDirectory` | `IDirectoryPickerService.PickFolderAsync("选择项目目录")` → 选中后 add 到 Recent |
| `BrowseFFmpeg` | `PickFileAsync("选择 ffmpeg 可执行文件", { "exe" } 或 null)` — Windows 限定 .exe |
| `BrowseFFprobe` | 同上 |
| `SelectRecentDirectory(dir)` | 点 Recent 列表复用 |
| `ClearRecentDirectories` | 清空 list + 触发 dirty |

### 3.5 `AddToRecentDirectories`（去重 + LRU + 上限 10）

```csharp
private void AddToRecentDirectories(string directory) {
    if (RecentDirectories.Contains(directory)) RecentDirectories.Remove(directory);
    RecentDirectories.Insert(0, directory);            // 移到最前
    while (RecentDirectories.Count > 10) RecentDirectories.RemoveAt(RecentDirectories.Count - 1);
}
```

### 3.6 Save（`SaveAsync` — `SettingsViewModel.cs:322-432`）

```
Save (RelayCommand)
  ├─ hasDirectory = !string.IsNullOrEmpty(SelectedProjectDirectory)
  ├─ 校验：if hasDirectory && !Directory.Exists(selected) → StatusMessage 红字 + return
  ├─ 校验 FFmpeg 路径：
  │     ├─ if Directory.Exists(path) → "FFmpeg 路径不能是目录: <path>" + return
  │     └─ if !File.Exists(path)    → "FFmpeg 文件不存在: <path>" + return
  ├─ 校验 FFprobe 路径（同样）
  ├─ IsSaving = true
  ├─ if hasDirectory:
  │     _projectConfig ??= new();
  │     _projectConfig.CurrentDirectory = SelectedProjectDirectory;
  │     _projectConfig.RecentDirectories = new(RecentDirectories);
  │     await _configService.SetAsync(ConfigKeys.Project, _projectConfig);
  ├─ theme = SelectedTheme == 1 ? "RedNight" : "DeepSpace";
  │     await _configService.SetAsync(ConfigKeys.App, new AppConfig { Theme, FFmpegPath, FFprobePath });
  │     ThemeManager.SetTheme(SelectedTheme == 1);              ← 立刻生效（save-first）
  │     FFmpegConfigurator.Apply(trimmedFfmpegPath, trimmedFfprobePath);  ← 立刻生效
  │     if FfmpegPath != trimmed → FfmpegPath = trimmed; (UI 同步 trim)
  ├─ await _configService.SetAsync(ConfigKeys.Oss, BuildOssConfigFromVm());
  ├─ 刷新 _lastSaved* 快照
  └─ IsDirty = false; StatusMessage = "已保存"
```

> **Save-First 主题切换契约**（`00-project.md` §2.6 + `01-app-bootstrap.md` §2.6）：UI 上切 radio **不立即生效**，必须 Save 成功后 `ThemeManager.SetTheme(...)` 才动资源。
> FFmpeg/FFprobe 路径同理：UI 上点 path 文件选择器，**Save 时** 调 Apply 才生效（不需要重启）。

---

## 4. 校验项细节

| 校验 | 文件 / 函数 | 触发 | 错误信息 |
|---|---|---|---|
| 项目目录存在 | `SettingsViewModel.cs:327-331` | Save 时 | `"目录不存在：{path}"` |
| FFmpeg 不能是目录 | `SettingsViewModel.cs:336-342` | Save 时 | `"FFmpeg 路径不能是目录：{path}"` |
| FFmpeg 文件存在 | `SettingsViewModel.cs:344-349` | Save 时 | `"FFmpeg 文件不存在：{path}"` |
| FFprobe 不能是目录 | `SettingsViewModel.cs:353-358` | Save 时 | `"FFprobe 路径不能是目录：{path}"` |
| FFprobe 文件存在 | `SettingsViewModel.cs:360-365` | Save 时 | `"FFprobe 文件不存在：{path}"` |
| OSS 配置完整性 | `OssStorageFactory.IsConfigured()` | 上传时（不通过 Settings 路径）| 触发 `OnOssNotConfigured` 弹框 |

> 故意不校验 OSS 字段（为空时直接允许保存但下次上传会引导用户）

---

## 5. 关键交互模型

### 5.1 「保存」按钮禁用逻辑

```xml
<Button Classes="secondary-button"
        Content="保存"
        Command="{Binding SettingsViewModel.SaveCommand}"
        IsEnabled="{Binding SettingsViewModel.IsDirty}"
        IsVisible="{Binding IsSettingsPageVisible}" />
```

- `SettingsViewModel.IsDirty == true` 才可点
- `SettingsViewModel.IsSaving` 时无额外禁用 —— SaveCommand 当前没用 `CanExecute`，假定 UI 短点击不会重叠

### 5.2 Tab 切换不重置 dirty

```csharp
[RelayCommand]
private void SelectTab(SettingsTab tab) {
    SelectedTab = tab;
    // 跨 Tab 状态保持: 不清 IsDirty
}
```

避免「OSS 改了字段，切去 General，看到保存按钮禁用而误以为没改」。

### 5.3 OSS Provider 占位

```csharp
[ObservableProperty] private int ossProvider = 0;  // 0 = Aliyun（唯一实现）
```

`BuildOssConfigFromVm`（`SettingsViewModel.cs:118-129`）硬编码 `Provider = "Aliyun"`，**完全忽视 `OssProvider`** —— UI 留口子给未来扩展。

---

## 6. 修改这个模块前的检查清单

| 改动 | 涉及 | 验证 |
|---|---|---|
| 加新设置项（例：UI 字号） | `AppConfig` 加字段 + `LoadOssFromConfig` / `BuildConfigFromVm` + dirty 字段 + Save 路径 | 启动 / 修改 / 保存 / 重启都生效 |
| 新增校验 | `Save` 函数加 if 分支 | 红字反馈 + 不写 DB |
| 改 Theme 自动 Save | ❌ 不改，保留 Save-First | 一旦改成实时切换，UI 行为会变 |
| 改 OSS Provider UI | `SettingsView.axaml` `OssProvider` 加项 + `BuildOssConfigFromVm` 接收到新值 | Factory 也得新加 Provider 实现 |
| 加立即生效的字段 | 例：开发期 dev path | 需要重启，会让用户困惑——保持 Save-First |

---

## 7. 已知陷阱（详见 `10-trap-book.md`）

- **`_isInitialized` 守门**：所有 `On*Changed` partial 必须先 if (!_isInitialized) return，否则初始化读 config → UI set → 触发 dirty → IsDirty=true 的悖论（已固化）
- **`Provider` 误设为空**：`BuildOssConfigFromVm` 硬编码 "Aliyun"，永远不写 Provider=用户选的那个值（现状：UI 单选项）
- **`IsSaving` 没用 CanExecute**：Save 同时点两次会并发写 ConfigDb（race）；生产环境应该加 `SaveCommand.CanExecute = !IsSaving`
- **`FFmpegPath` 首尾空格未 trim 前显示**：Save 时 trim 后才回写到 `FfmpegPath`（避免 UI 显示不一致）
- **OSX 上 `/` 是合法的空 PATH**：`OperatingSystem.IsMacOS()` 不会触发；`ResolveFromPath` 走 `PATH` 环境变量，install 时手动配
- **`RecentDirectories` 删项目的 case**：`Path.GetFullPath` 没有标准化，可能 `~/shots` vs `~/shots/` 重复——目前用 `Contains` 字符串匹配，可能误判
