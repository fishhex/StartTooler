# 0.11 — Settings（设置）UI & 交互改进

> 对应需求量文档 `doc/demand/03-settings-improve.md`。
> 核心改动：通用 Tab（目录历史、表单验证、保存反馈、导入导出）、OSS Tab（供应商显示、连接测试、Secret 安全）、AI Tab（切换提示、测试 prompt 自定义）、全局（三选导航守卫）。

---

## 1. 模块边界

```
SettingsView.axaml
  ├─ 通用 Tab → SettingsViewModel properties + commands
  │    ├─ 项目目录：Flyout 内 MenuItem 从 ConfigService 读历史
  │    ├─ 表单验证：LostFocus → 校验 → 红框 + 错误提示
  │    ├─ 保存成功反馈：按钮文字 "已保存 ✓" 1.5s 恢复
  │    └─ 导入/导出：ConfigService.ExportToJson / ImportFromJson
  ├─ OSS Tab
  │    ├─ 供应商 → TextBlock "阿里云 OSS"（替换不可交互 ComboBox）
  │    ├─ 连接测试按钮 + AITester.ListObjects 或 HeadBucket
  │    └─ Secret 眼睛切换（复用 BoolToPasswordCharConverter）
  ├─ AI Tab
  │    ├─ 切换厂商弹出确认提示（"不再提示"复选框）
  │    ├─ 可编辑测试 prompt TextBox
  │    └─ 模型字段下方说明文字
  └─ 全局：三选导航守卫 → DialogHelper.ShowChoiceAsync

依赖链：
  SettingsViewModel
    ├─ ConfigService（读写历史、导入导出）
    ├─ IDirectoryPickerService / IFilePickerService（浏览目录、选择文件）
    ├─ AIProviderLoader（厂商列表）
    └─ AITester（OSS 连接测试、AI 测试 prompt 自定义）
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Services/IOssStorage.cs` | 新增 `TestConnectionAsync` 接口方法 | 修改 |
| `Services/AliyunOssStorage.cs` | 实现 `TestConnectionAsync` | 修改 |
| `Converters/SettingsConverters.cs` | 新增 `ErrorToBorderBrush` / `IsNotEmptyToBool` converter | 修改 |

**修改文件**：

| 文件 | 改动 |
|------|------|
| `ViewModels/SettingsViewModel.cs` | 目录历史、表单验证、保存反馈、导入导出、OSS 连接测试、AI 测试 prompt、厂商切换提示 |
| `Views/SettingsView.axaml` | 最近目录动态 MenuItem、红框验证、保存反馈按钮文字、OSS 供应商 TextBlock、OSS 连接测试、OSS Secret 眼睛、AI 测试 prompt、导出/导入按钮 |
| `ViewModels/MainWindowViewModel.cs` | `NavigateToGallery` 改为三选对话框 |
| `Helpers/DialogHelper.cs` | 无改动（已存在 `ShowChoiceAsync`，直接复用） |
| `Services/ConfigService.cs` | 新增 `ExportToJsonAsync` / `ImportFromJsonAsync`、目录历史存取 |
| `Services/ConfigKeys.cs` | 新增 `ProjectHistory` key |
| `Resources/ai-providers.default.toml` | 无改动 |

> **不引入新 NuGet 包。**

---

## 3. 通用 Tab 改进

### 3.1 目录历史（RecentDirectories）

**存储**：`ConfigService` 中读写 `ConfigKeys.ProjectHistory` → `List<string>`（最多 10 条，新条目插队首，去重）。

**UI**：`SettingsView` `OnSelectFlyoutOpened` 中动态生成 `MenuItem`：

```csharp
// 在 flyout 打开时清空旧的动态 MenuItem，从 ViewModel.RecentDirectories 重新生成
var recentItems = DataContext is SettingsViewModel vm ? vm.RecentDirectories : [];
foreach (var dir in recentItems)
{
    var item = new MenuItem { Header = dir };
    item.Click += (s, e) => { vm.SelectedProjectDirectory = dir; };
    flyout.Items.Insert(beforeSeparatorIndex, item);
}
```

**初始化**：`SettingsViewModel.InitializeAsync` 中从 `ConfigService.GetAsync<List<string>>(ConfigKeys.ProjectHistory)` 加载。

**写入**：`SaveAsync` 成功后把当前 `SelectedProjectDirectory` 追加到历史列表头部。

### 3.2 表单验证

| 字段 | 规则 | 触发方式 |
|------|------|---------|
| 项目目录 | 非空（不可输入，由浏览选择，实际无需校验） | — |
| FFmpeg 路径 | 文件不存在时警告 | LostFocus |
| FFprobe 路径 | 文件不存在时警告 | LostFocus |
| OSS Region | 非空 | LostFocus |
| OSS Bucket | 非空 | LostFocus |
| OSS AccessKeyId | 非空 | LostFocus |
| OSS AccessKeySecret | 非空 | LostFocus |
| AI API Key | 非空 + 推荐前缀检查（`sk-` / `sat-` 等）| LostFocus |

**ViewModel 新增属性**：

```csharp
// 每个字段对应一对错误状态
[ObservableProperty] private string? _ffmpegPathError;     // null = 无错误
[ObservableProperty] private string? _ffprobePathError;
[ObservableProperty] private string? _ossRegionError;
[ObservableProperty] private string? _ossBucketError;
[ObservableProperty] private string? _ossAccessKeyError;
[ObservableProperty] private string? _ossSecretKeyError;
[ObservableProperty] private string? _aiApiKeyError;

// 是否有任何验证错误（控制保存按钮）
public bool HasValidationErrors => ...;
```

**UI**：TextBox `Classes.error` 样式切换（`BorderBrush="{DynamicResource State.Danger}"`），下方显示 `TextBlock` 错误文本。

**API Key 前缀校验**：非强制（不阻塞保存），仅 LossFocus 时做友好提示："建议以 'sk-' 开头"。

### 3.3 保存成功反馈

```csharp
[ObservableProperty] private string _saveButtonText = "保存";
[ObservableProperty] private bool _saveJustCompleted;  // 控制 "已保存 ✓" 显示

// SaveAsync 成功后：
SaveButtonText = "已保存 ✓";
SaveJustCompleted = true;
await Task.Delay(1500);
SaveButtonText = "保存";
SaveJustCompleted = false;
```

### 3.4 导入/导出配置

**导出**：按钮 → `FilePickerService.SaveFileAsync(json)` → `ConfigService.ExportToJsonAsync(stream)`：
- 遍历所有 config key，序列化为 `Dictionary<string, object>` JSON
- 密钥字段（`AiApiKey` / `AccessKeySecret`）导出时替换为 `<请在导入后手动填写>`

**导入**：按钮 → `FilePickerService.OpenFileAsync(json)` → `ConfigService.ImportFromJsonAsync(stream)`：
- 反序列化 JSON → 逐个 key 写入 config.db（密钥字段跳过空占位符，保留现有值）

---

## 4. OSS Tab 改进

### 4.1 供应商 TextBlock

```xml
<!-- 替换 ComboBox -->
<TextBlock Grid.Column="1" Text="阿里云 OSS" FontSize="13"
           Foreground="{DynamicResource Text.Primary}" VerticalAlignment="Center"/>
```

### 4.2 连接测试

新增 `IOssStorage.TestConnectionAsync(CancellationToken ct)` 方法 → `AliyunOssStorage` 实现：`client.DoesBucketExistAsync(bucketName)`（阿里云 SDK `IOss`）。

```csharp
// AliyunOssStorage.cs
public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
{
    var client = new OssClient(_endpoint, _config.AccessKeyId, _config.AccessKeySecret);
    return await client.DoesBucketExistAsync(_config.Bucket);
}
```

**UI**：SettingsViewModel 新增：
- `OssTestState`（Idle / Running / Ok / Failed）枚举，复用 `AITestState` 的 converter 逻辑
- `TestOssConnectionCommand` → 构造临时 `OssStorage` → 调 `TestConnectionAsync`
- `OssTestMessage` 结果文本

### 4.3 Secret 眼睛切换

```xml
<!-- Secret 字段复用 BoolToPasswordCharConverter + 眼睛按钮 -->
<TextBox Text="{Binding OssSecretKey, Mode=TwoWay}"
         PasswordChar="{Binding IsOssSecretKeyVisible, Converter={StaticResource BoolToPasswordChar}}" />
<Button Command="{Binding ToggleOssSecretKeyVisibilityCommand}">
  <!-- Eye / EyeOff 图标同上 -->
</Button>
```

### 4.4 路径前缀自动补 `/`

`Watermark` 改为 `astrophotos/`（已有）。失焦时若值非空且不以 `/` 结尾，自动追加 `/`。

---

## 5. AI Tab 改进

### 5.1 厂商切换提示

厂商 ComboBox 通过 `SelectionChanged` 或 ViewModel property change 触发：

```csharp
partial void OnAiProviderMetaChanged(AIProviderMeta? oldValue, AIProviderMeta? newValue)
{
    if (oldValue == null || newValue == null || oldValue.Provider == newValue.Provider) return;

    // 如果用户勾选了「不再提示」，直接切换
    if (_skipProviderSwitchWarning) return;

    // 弹出确认框
    var skipWarning = false;
    // 三选对话框 → 确认切换 / 取消
    var result = await DialogHelper.ShowChoiceAsync(owner,
        "切换厂商",
        "将自动填入默认 Base URL 和推荐模型列表，API Key 保持不变",
        "确认切换", "取消", "不再提示");
    if (result == Cancelled) { /* 回滚 */ }
    if (result == Tertiary) { _skipProviderSwitchWarning = true; }
}
```

### 5.2 可编辑测试 Prompt

新增属性 `string AiTestPrompt = "请分析这张天文照片的主体、质量和拍摄参数"`，绑定到 `TextBox`（`AcceptsReturn="True"`）。传给 `AITester.TestAsync` 作为自定义 prompt。

### 5.3 模型字段说明

```xml
<TextBlock Text="可从下拉列表选择推荐模型，也可直接输入任意模型名"
           FontSize="11" Foreground="{DynamicResource Text.Tertiary}" />
```

---

## 6. 全局：三选导航守卫

`MainWindowViewModel.NavigateToGallery` 从两选改为三选：

```csharp
if (IsSettingsPage && SettingsViewModel.IsDirty)
{
    var result = await DialogHelper.ShowChoiceAsync(
        window, "有未保存的修改",
        "是否保存后再离开？",
        primaryButtonText: "保存并离开",
        secondaryButtonText: "放弃更改",
        tertiaryButtonText: "取消");

    switch (result)
    {
        case DialogChoice.Primary:   // 保存并离开
            await SettingsViewModel.SaveCommand.ExecuteAsync(null);
            break;
        case DialogChoice.Secondary: // 放弃更改
            SettingsViewModel.DiscardChanges();
            break;
        default:                     // 取消
            return;
    }
}
```

> `DialogHelper.ShowChoiceAsync` 已存在（v0.8 引入），直接复用，不需要新增。

---

## 7. ConfigKeys 新增

```csharp
public const string ProjectHistory = "project_history";   // List<string>
```

---

## 8. 边界情况

| 场景 | 处理 |
|------|------|
| 目录历史为空 | "最近使用" 分组 + 所有 MenuItem 不显示，`StackPanel.IsVisible="{Binding RecentDirectories.Count}"` 控制 |
| 导出时无配置 | 默认导出空 JSON `{}`（至少包含所有 key 占位） |
| 导入时 JSON 格式错误 | catch → StatusMessage 显示 "导入失败：格式错误" |
| OSS 连接测试网络超时 | 15s timeout，Failed 状态 + "连接超时" 提示 |
| 厂商切换确认框被 Esc 关闭 | DialogChoice.Cancelled → 回滚 `AiProviderMeta` 到旧值 |
| Secret 切换时页面切换 | 打码状态复位到隐藏（通过 `SelectedTab` 变化时重置 `IsOssSecretVisible = false`） |
| 路径前缀失焦自动补 `/` | 仅当非空且末字符非 `/` 时追加 |
| 保存按钮在验证错误时 | `HasValidationErrors` → `IsEnabled = false`，不必等到点击保存才提示 |
| FFmpeg 路径不存在 | Load 时检测已安装 FFmpeg → 无警告；手动输入后 LostFocus → 文件不存在 → 黄框警告 + 文字 "文件不存在" | 

---

## 9. 与现有系统的关系

### 9.1 不影响 Gallery / Upload / Trash

Settings 改进仅影响设置页内部，不修改 `MediaFile`、`UploadJob` 等数据结构。

### 9.2 OSS TestConnection 不影响现有上传

`TestConnectionAsync` 是新增只读方法，`IOssStorage` 接口新增方法需要 `AliyunOssStorage` 实现，现有的 `UploadAsync` / `DownloadAsync` 不受影响。
