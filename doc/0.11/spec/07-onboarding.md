# 0.11 — 首次使用引导（Onboarding）

> 对应需求量文档 `doc/0.11/demand/07-general-improve.md` §「首次使用引导」。
> 核心改动：新用户（无项目 + 无照片）打开应用时，在 Gallery 空白页展示三步引导卡片，引导完成设置项目→扫描→可选备份。

---

## 1. 模块边界

```
MainWindow / GalleryView
  └─ App 启动 → Gallery 检测 HasNoProject || IsEmpty
       └─ 显示 OnboardingCard（覆盖在 Gallery 空态区域上方）
            ├─ Step 1: 设置项目目录（按钮 → 导航到 Settings）
            ├─ Step 2: 扫描媒体文件（从 Settings 返回后自动触发）
            └─ Step 3: 配置 OSS 备份（可选，引导跳转 Settings OSS Tab）
```

**依赖**：
- `GalleryViewModel.HasNoProject` / `IsEmpty`（已有）
- `ConfigService` 读/写 `onboarding_completed` 标志
- `SettingsViewModel` 导航（`MainWindowViewModel.NavigateTo("settings")`）

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Controls/OnboardingCard.axaml` | 引导卡片 UI | 新增 |
| `Controls/OnboardingCard.axaml.cs` | 步骤状态管理 + 按钮 Command | 新增 |
| `Views/GalleryView.axaml` | 在空态区域集成 OnboardingCard | 修改 |
| `ViewModels/GalleryViewModel.cs` | 添加 `IsOnboardingComplete` / `GoToSettingsCommand` | 修改 |
| `Services/ConfigKeys.cs` | 新增 `onboarding_v1` 配置键 | 修改 |

---

## 3. 引导卡片 UI

### 3.1 三步引导流程

```
┌──────────────────────────────────────────────────────────┐
│                                                          │
│                   🌌 欢迎使用星助                          │
│             三步开始管理你的天文摄影作品                    │
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │  ✅  第一步：设置项目目录                           │  │
│  │      选择存放天文照片的文件夹                        │  │
│  │                                        [去设置 →]  │  │
│  └────────────────────────────────────────────────────┘  │
│  ┌────────────────────────────────────────────────────┐  │
│  │  ○  第二步：扫描媒体文件                            │  │
│  │      自动扫描目录中的照片和视频                      │  │
│  │                              （完成第一步后自动触发） │  │
│  └────────────────────────────────────────────────────┘  │
│  ┌────────────────────────────────────────────────────┐  │
│  │  ○  第三步：配置云备份（可选）                      │  │
│  │      将照片备份到阿里云 OSS，手机可随时访问          │  │
│  │                                        [去配置 →]  │  │
│  └────────────────────────────────────────────────────┘  │
│                                                          │
│                            全部完成后此卡片自动消失       │
└──────────────────────────────────────────────────────────┘
```

### 3.2 步骤状态

| 状态 | 图标 | 样式 |
|---|---|---|
| 未完成 | ○ (空心圆) | 文字半透明，按钮可用 |
| 已完成 | ✅ (绿勾) | 文字正常，按钮隐藏/变灰 |
| 进行中 | 🔄 (旋转动画) | 适用于步骤 2 扫描中 |

### 3.3 步骤联动逻辑

```
Step 1 完成（用户点「去设置」→ 设置好项目目录 → 返回 Gallery）:
  ├─ 检测 HasNoProject == false
  ├─ Step 1 标记 ✅
  ├─ Step 2 自动触发扫描（RequestRefreshDebounced）
  └─ Step 2 状态 → 🔄

Step 2 扫描完成:
  ├─ 检测 IsEmpty == false（DateGroups.Count > 0）
  ├─ Step 2 标记 ✅
  └─ Step 3 仍显示 ○（可选步骤）

Step 3 完成（用户点「去配置」→ 配置好 OSS）:
  ├─ 检测 OSS 配置完整（BucketName + AccessKeyId 非空）
  └─ Step 3 标记 ✅

全部 3 步完成:
  └─ 3 秒后引导卡片淡出消失
```

---

## 4. OnboardingCard 控件

### 4.1 属性与事件

```csharp
public partial class OnboardingCard : UserControl
{
    // 绑定属性
    public static readonly StyledProperty<bool> Step1CompleteProperty = ...;
    public static readonly StyledProperty<bool> Step2CompleteProperty = ...;
    public static readonly StyledProperty<bool> Step3CompleteProperty = ...;
    public static readonly StyledProperty<bool> IsScanningProperty = ...;  // 步骤 2 进行中

    // 命令（由 GalleryViewModel 提供）
    public IRelayCommand? GoToSettingsCommand { get; set; }
    public IRelayCommand? GoToOssSettingsCommand { get; set; }

    // 全部步骤完成事件
    public event EventHandler? OnAllStepsComplete;
}
```

### 4.2 可见性

```xml
<!-- GalleryView.axaml 空态区域 -->
<StackPanel IsVisible="{Binding HasNoProject}">
    <!-- 引导卡片（Onboarding 未完成时替换原空态） -->
    <controls:OnboardingCard IsVisible="{Binding ShowOnboarding}"
                             Step1Complete="{Binding IsProjectSet}"
                             Step2Complete="{Binding IsNotEmpty}"
                             Step3Complete="{Binding IsOssConfigured}"
                             IsScanning="{Binding IsScanning}"
                             GoToSettingsCommand="{Binding GoToSettingsCommand}"
                             GoToOssSettingsCommand="{Binding GoToOssSettingsCommand}" />
</StackPanel>
```

> 引导完成后的空态回退到原有「暂无媒体」星座图标。

### 4.3 完成持久化

完成全部三步后，写入 `config.db`：

```
Key: "onboarding_v1"
Value: { "completed": true, "completed_at": "2026-07-14T10:30:00Z" }
```

应用启动时读取此标志，若 `completed == true` 则不再显示引导卡片，直接显示正常空态或照片网格。

---

## 5. GalleryViewModel 追加

```csharp
public partial class GalleryViewModel : ObservableObject
{
    // === 引导状态 ===
    [ObservableProperty]
    private bool _showOnboarding;       // 是否显示引导卡片

    [ObservableProperty]
    private bool _isProjectSet;         // Step 1 完成

    [ObservableProperty]
    private bool _isNotEmpty;           // Step 2 完成

    [ObservableProperty]
    private bool _isOssConfigured;      // Step 3 完成

    [ObservableProperty]
    private bool _isScanning;           // Step 2 进行中

    // 命令
    [RelayCommand]
    private void GoToSettings()
    {
        // 通过 MainWindowViewModel 导航到 settings 页
        _navigateToSettings?.Invoke();
    }

    [RelayCommand]
    private void GoToOssSettings()
    {
        _navigateToOssSettings?.Invoke();
    }

    // 初始化时检测引导状态
    private async Task CheckOnboardingStatusAsync()
    {
        var onboarding = await _configService.GetAsync<OnboardingState>(ConfigKeys.Onboarding);
        if (onboarding?.Completed == true)
        {
            ShowOnboarding = false;
            return;
        }

        ShowOnboarding = HasNoProject || IsEmpty;
        UpdateStepStates();
    }

    // 状态变化时更新步骤
    partial void OnProjectPathChanged(string? value)
    {
        UpdateStepStates();
    }

    private void UpdateStepStates()
    {
        IsProjectSet = !HasNoProject;
        IsNotEmpty = !IsEmpty;
        IsOssConfigured = CheckOssConfigured();

        // 全部完成 → 隐藏 + 持久化
        if (IsProjectSet && IsNotEmpty && IsOssConfigured)
        {
            ShowOnboarding = false;
            // 异步持久化（fire-and-forget）
            _ = _configService.SetAsync(ConfigKeys.Onboarding,
                new OnboardingState { Completed = true, CompletedAt = DateTime.UtcNow });
        }
    }

    private bool CheckOssConfigured()
    {
        var oss = _configService.GetOssConfigAsync().Result; // 简化，实际应异步
        return oss != null && !string.IsNullOrEmpty(oss.BucketName)
               && !string.IsNullOrEmpty(oss.AccessKeyId);
    }
}
```

---

## 6. 与已有功能的关系

| 功能 | 影响 |
|---|---|
| Gallery 空态 | 引导卡片替换原「请先选择项目目录」空态（引导完成后恢复） |
| Settings 导航 | 通过 `MainWindowViewModel.NavigateTo("settings")` 跳转 |
| 扫描刷新 | Step 2 自动调用 `RequestRefreshDebounced`，复用现有扫描 |
| config.db | 新增 `onboarding_v1` 键存储完成状态 |

---

## 7. 边界情况

| 场景 | 处理 |
|---|---|
| 用户已有项目目录（不是新用户） | 引导不显示（HasNoProject == false 且 IsEmpty == false 且 onboarding 未标记完成 → 也跳过） |
| 用户只完成步骤 1 和 2，跳过步骤 3 | 引导卡片始终显示，但步骤 1/2 为 ✅，步骤 3 为 ○，不强制关闭 |
| 用户手动关闭引导卡片 | Phase 1 不提供关闭按钮（引导流程不完成就一直显示） |
| 卸载重装 | 引导状态存在 `config.db` 中（`~/Application Support/StartTooler/config.db`），重装不丢 |
| 用户从 Settings 返回但未设置目录 | Step 1 仍为 ○，不触发 Step 2 |

---

## 8. 不做清单

| 内容 | 理由 |
|---|---|
| 独立欢迎页/窗口 | 需求明确「在 Gallery 空白页展示，非单独页面」 |
| 动画引导（指向设置按钮） | Phase 1 用静默卡片，动画增加实现复杂度且收益有限 |
| 强制完成所有步骤 | Step 3（OSS）可选，不强制 |
| 引导跳过按钮 | 引导步骤少且全完成后自动消失，无需跳过 |
