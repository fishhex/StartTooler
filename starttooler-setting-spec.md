# StarTooler 设置页 — 界面实现规格

> 本规格文档是给实现 AI 的**唯一权威输入**。
> 实现者只需严格按照本文档执行，无需追问设计意图。
> 本规格基于设计图 + 当前 v2.3 项目现状。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 项目名 | StartTooler / 星助 |
| 文档版本 | **1.0**（设置页独立规格） |
| 父规格 | `star-helper-specv2.md` v2.3 |
| 适用页面 | SettingsView |
| 涉及 Tab | 通用、OSS配置 |

---

## 1. 设计图解读

### 1.1 整体结构

```
┌──────────────────────────────────────────────────┐
│  [● ● ●]                  星助                    │ 系统标题栏 38px
│  ═══════════════════════════════════════════════ │ ScanProgressBar (2px)
├──────┬───────────────────────────────────────────┤
│      │  工具栏                          [保存][返回]│ ← 每页工具栏
│      ├───────────────────────────────────────────┤
│ 媒体 │ ┌────────┐ ┌──────────┐                   │ Tab 栏
│      │ │  通用   │ │ OSS配置  │                   │
│ 本地 │ └────────┘ └──────────┘                   │
│      ├───────────────────────────────────────────┤
│ 服务 │  项目目录    [Select 框] [浏览...]         │ FormRow
│      │  主题模式    [Select 框]                   │ FormRow
│      │                                            │
│      │                                            │
│ 设置 │                                            │
└──────┴───────────────────────────────────────────┘
```

### 1.2 NavRail（左侧导航） — 2 项（v2.3 现状）

> **本次 v1.0 范围：忽略「本地」「服务」**。NavRail 保持 v2.3 现状 2 项。
> 未来如需扩展，再单独加 spec。

| 项 | 位置 | 状态 |
|------|------|------|
| 媒体 | 顶部 | v2.3 已实现 ✅ |
| 设置 | 底部 | v2.3 已实现 ✅ |

### 1.3 工具栏（设置页） — 右上角

| 按钮 | 样式 | 位置 | 行为 |
|------|------|------|------|
| **保存** | 次按钮（蓝边/蓝字）| 右上 | `IsEnabled="{Binding IsDirty}"` |
| **返回** | 次按钮（灰边/灰字）| 右上（最右）| 切回 Gallery 视图 |

### 1.4 Tab 栏

| Tab | 选中态（设计图）| 字段 |
|------|----------------|------|
| **通用** | 灰色背景 + 蓝色边框 | 项目目录 / 主题模式 |
| **OSS配置** | 灰色背景 + 蓝色边框 | 厂商 / Endpoint / Bucket / AccessKey / SecretKey / PathPrefix / UseHttps / EnableCdn |

---

## 2. 当前实现状态（v2.3）

| 模块 | 状态 | 备注 |
|------|------|------|
| NavRail 2 项 | ✅ 媒体 / 设置 | v1.0 保持 2 项，**忽略「本地」「服务」** |
| 工具栏保存/返回 | ⚠️ 在底部固定栏 | 需移到顶部 |
| Tab 通用/OSS | ⚠️ 名称「项目」+「OSS配置」 | 改名「通用」+「OSS配置」 |
| 通用 Tab 字段 | ✅ 项目目录 + 主题模式 | 已实现 |
| OSS Tab 字段 | ❌ 未实现 | 待新增 |
| 状态机 | ✅ Save-First + IsDirty | 已实现 |
| 单一启用控制 | ✅ 只 `IsEnabled="{Binding IsDirty}"` | 已实现 |

---

## 3. 差异清单（设计图 vs 现状）

### 3.1 必须修复的差异（v1.0 范围）

| # | 项 | 现状 | 设计图 | 优先级 |
|---|----|------|--------|--------|
| 1 | NavRail 项数 | 2 项 | 4 项 | **忽略**（保持 2 项） |
| 2 | 设置页工具栏 | 底部 | **顶部右侧** | P0 |
| 3 | Tab 默认名 | 「项目」 | **「通用」** | P1 |
| 4 | Tab 选中态 | 文字下划线 | **灰色底+蓝边框** | P1 |
| 5 | OSS Tab 字段 | 无 | **待规划** | P1 |

### 3.2 NavRail — 保持 2 项

**v1.0 不实现「本地」「服务」**。NavRail 保持 v2.3 现状 2 项（媒体 / 设置）。如未来需要扩展，单独开 spec。

### 3.3 颜色 — 全部走 token

> **铁律**：本规格所有颜色 / 间距 / 圆角 / 字号**全部走 Design Token**（`Colors.axaml`），
> 禁止硬编码任何颜色字面量。

颜色 token 一览（来自 `Themes/Colors.axaml`）：

| Token | Hex | 用途 |
|---|---|---|
| `Bg.Outer` | `#0A0E1A` | 窗口底色 / 标题栏 / NavRail / Sidebar |
| `Bg.Surface` | `#161B2E` | 工具栏 / 卡片 / 缩略图占位 |
| `Bg.SurfaceElevated` | `#1F2438` | 浮层 / Tooltip / NavItem 激活态 / **Tab 选中底色** |
| `Bg.Divider` | `#2A3050` | 分割线 / 边框弱态 |
| `Bg.Hover` | `#252B45` | 悬停态 |
| `Text.Primary` | `#E8EAF0` | 主要文字 |
| `Text.Secondary` | `#8892B0` | 次要文字 / 默认 Tab 文字 |
| `Text.Tertiary` | `#5A6280` | 三级文字 / 占位 |
| `Text.Disabled` | `#4A5273` | 禁用态 |
| `Accent.Stellar` | `#4FC3F7` | 选中 / 激活 / **Tab 选中边框** |
| `Accent.Nebula` | `#B388FF` | 主按钮渐变 |
| `State.Warning` | `#FFA726` | 警告 |
| `State.Danger` | `#FF5252` | 错误 / 危险 |
| `State.Success` | `#4DD0E1` | 成功 |

XAML 中**必须**用 `"{DynamicResource TokenName}"`，禁止写 `#FF4FC3F7` 这种字面量。

---

## 4. 详细规格

### 4.1 工具栏（设置页） — v1.0 重构

**位置**：MainWindow Row 1 全局工具栏中，**仅在设置页可见**。

```
工具栏布局（设置页）：
┌─────────────────────────────────────────────┐
│                                  [保存] [返回]│
└─────────────────────────────────────────────┘
```

**当前 MainWindow 全局工具栏只支持 Gallery 页**（v2.3 实现）。v1.0 需要扩展：
- 「多选 / 取消多选 / 批量上传 / 删除」仅 Gallery 页可见（v2.3 已实现）
- 「保存 / 返回」仅设置页可见（**v1.0 新增**）

**ViewModel 扩展**：

```csharp
public partial class MainWindowViewModel : ObservableObject
{
    // v2.3 已有
    public bool IsGalleryPage => CurrentPage == ViewPage.Gallery;

    // v1.0 新增
    public bool IsSettingsPageVisible => CurrentPage == ViewPage.Settings;
}
```

**XAML**：

```xml
<!-- MainWindow 全局工具栏, 追加设置页按钮组 -->
<StackPanel Orientation="Horizontal" Spacing="8" Margin="16,0,0,0"
            VerticalAlignment="Center"
            IsVisible="{Binding IsSettingsPageVisible}">
    <Button Classes="toolbar-button"
            Content="保存"
            IsEnabled="{Binding SettingsViewModel.IsDirty}"
            Command="{Binding SettingsViewModel.SaveCommand}"/>
    <Button Classes="toolbar-button"
            Content="返回"
            Command="{Binding NavigateToMediaCommand}"/>
</StackPanel>
```

**XAML 调整**：
- SettingsView 删除底部保存栏（v2.3 现状）
- 工具栏位置在 MainWindow Row 1（v2.3 全局工具栏位置）

### 4.2 Tab 栏 — v1.0 优化

**当前实现**（v2.3）：
```xml
<Button Content="项目" Classes="tab-item" IsEnabled="False"/>
<Button Content="OSS配置" Classes="tab-item"/>
```
- 选中态用文字下划线（`Accent.Stellar`）
- 「项目」Tab 直接 `IsEnabled="False"` 写死

**v1.0 调整**：
1. 改名「项目」→「通用」
2. 选中态改成**灰色底 + 蓝色边框**（与设计图一致）
3. 用 ViewModel `SelectedTab` 属性控制激活（**单一启用控制**原则）
4. 切换 Tab **不丢改动**（跨 Tab 状态保持）

**XAML**：

```xml
<Border Grid.Row="0" Background="{DynamicResource Bg.Outer}" Height="44">
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Spacing="24">
        <Button Content="通用" Classes="tab-item"
                Classes.active="{Binding SelectedTab, Converter={x:Static ObjectConverters.Equal}, ConverterParameter={x:Static vm:SettingsTab.General}}"
                Command="{Binding SelectTabCommand}"
                CommandParameter="{x:Static vm:SettingsTab.General}"/>
        <Button Content="OSS配置" Classes="tab-item"
                Classes.active="{Binding SelectedTab, Converter={x:Static ObjectConverters.Equal}, ConverterParameter={x:Static vm:SettingsTab.Oss}}"
                Command="{Binding SelectTabCommand}"
                CommandParameter="{x:Static vm:SettingsTab.Oss}}"/>
    </StackPanel>
</Border>
```

**ViewModel**：

```csharp
public enum SettingsTab { General, Oss }

[ObservableProperty] private SettingsTab _selectedTab = SettingsTab.General;

[RelayCommand]
private void SelectTab(SettingsTab tab)
{
    SelectedTab = tab;
    // 跨 Tab 状态保持: 不清 IsDirty
}
```

**Tab 样式**：

```xml
<Style Selector="Button.tab-item">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderBrush" Value="Transparent"/>
    <Setter Property="BorderThickness" Value="2"/>
    <Setter Property="Foreground" Value="{DynamicResource Text.Secondary}"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Padding" Value="16,4"/>
    <Setter Property="CornerRadius" Value="6"/>
    <Setter Property="Cursor" Value="Hand"/>
</Style>
<Style Selector="Button.tab-item:pointerover">
    <Setter Property="Foreground" Value="{DynamicResource Text.Primary}"/>
</Style>
<Style Selector="Button.tab-item.active">
    <Setter Property="Background" Value="{DynamicResource Bg.SurfaceElevated}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource Accent.Stellar}"/>
    <Setter Property="Foreground" Value="{DynamicResource Accent.Stellar}"/>
</Style>
```

### 4.3 通用 Tab 字段（v2.3 已实现 ✅）

| 字段 | 类型 | 绑定 | 行为 |
|------|------|------|------|
| 项目目录 | Select + Browse | `SelectedProjectDirectory` | 选目录 / 浏览 / 最近 |
| 主题模式 | ComboBox | `SelectedTheme` (0/1) | Deep Space / Red Night Vision |

**保持现状**。

### 4.4 OSS Tab 字段 — v1.0 新增

**字段列表**：

| # | 字段 | 类型 | 必填 | 备注 |
|---|------|------|------|------|
| 1 | 厂商 | Select (4 选 1) | ✅ | 阿里云 OSS / 腾讯云 COS / AWS S3 / 自定义 |
| 2 | Endpoint | TextBox | ✅ | 例如 `oss-cn-hangzhou.aliyuncs.com` |
| 3 | Bucket | TextBox | ✅ | 例如 `my-photos` |
| 4 | AccessKey | TextBox | ✅ | 访问密钥 |
| 5 | SecretKey | PasswordBox | ✅ | **脱敏显示** |
| 6 | PathPrefix | TextBox | ❌ | 例如 `astrophotos/`，默认空 |
| 7 | UseHttps | CheckBox | - | 默认 true |
| 8 | EnableCdn | CheckBox | - | 默认 false |
| 9 | CDN Domain | TextBox | ❌ | 仅 EnableCdn=true 时启用 |

**FormRow 布局**：

```xml
<Border Padding="40,16">
    <Grid ColumnDefinitions="160,*">
        <TextBlock Text="厂商" .../>
        <ComboBox Grid.Column="1" SelectedIndex="{Binding OssProvider}">
            <ComboBoxItem Content="阿里云 OSS"/>
            <ComboBoxItem Content="腾讯云 COS"/>
            <ComboBoxItem Content="AWS S3"/>
            <ComboBoxItem Content="自定义"/>
        </ComboBox>
    </Grid>
</Border>
<!-- 重复 8 个 FormRow -->
```

**ViewModel**：

```csharp
[ObservableProperty] private int _ossProvider;  // 0/1/2/3
[ObservableProperty] private string _endpoint = "";
[ObservableProperty] private string _bucket = "";
[ObservableProperty] private string _accessKey = "";
[ObservableProperty] private string _secretKey = "";
[ObservableProperty] private string _pathPrefix = "";
[ObservableProperty] private bool _useHttps = true;
[ObservableProperty] private bool _enableCdn = false;
[ObservableProperty] private string _cdnDomain = "";
```

**OssConfig POCO**（已存在 v2.3）：
```csharp
public class OssConfig
{
    public string Provider { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string AccessKeySecret { get; set; } = "";
    public string PathPrefix { get; set; } = "";
    public bool UseHttps { get; set; } = true;
    public bool EnableCdn { get; set; }
    public string CdnDomain { get; set; } = "";
}
```

**数据库**（v2.3 `Config` 表）：
- Key: `oss`，Value: `OssConfig` JSON

### 4.5 NavRail — 保持 2 项（v1.0 忽略）

**v1.0 不动 NavRail**，保持 v2.3 现状 2 项：

```xml
<Grid RowDefinitions="Auto,*">
    <Button Grid.Row="0" Content="媒体" Classes="nav-item" .../>
    <Button Grid.Row="1" Content="设置" Classes="nav-item" .../>
</Grid>
```

未来如需扩展（本地 / 服务），单独开 spec，不在 v1.0 范围。

---

## 5. 交互流程

### 5.1 设置页加载

```
NavigateToSettings
  → CurrentView = SettingsViewModel
  → CurrentPage = ViewPage.Settings
  → SettingsViewModel.InitializeAsync()
      → 读 ProjectConfig + AppConfig
      → 读 OssConfig
      → 快照 (lastSavedDirectory, lastSavedTheme, lastSavedOss)
      → 加载 UI
      → IsDirty = false
      → SelectedTab = General
```

### 5.2 切换 Tab

```
SelectTab(Oss)
  → SelectedTab = Oss
  → UI 切换显示 OSS 字段
  → 跨 Tab 状态保持:
      - 已输入的字段值不丢（仍是 IsDirty）
      - OssConfig 快照仍用同一个
```

### 5.3 编辑字段

```
OnSelectedProjectDirectoryChanged(value)
  → RecomputeDirty()
      → IsDirty = (SelectedProjectDirectory != lastSavedDirectory)
                || (SelectedTheme != lastSavedTheme)
                || (Oss 字段任意 != lastSavedOss)
  → IsDirty = true (如果改了)
```

### 5.4 保存

```
Save
  → IsSaving = true
  → 校验（项目目录存在、必填项非空）
  → SetAsync(ConfigKeys.Project, projectConfig)
  → SetAsync(ConfigKeys.App, appConfig)
  → SetAsync(ConfigKeys.Oss, ossConfig)
  → ApplyTheme
  → 刷新快照
  → IsDirty = false
  → IsSaving = false
  → StatusMessage = "已保存" (2-3s 后清空)
```

### 5.5 离开设置页（点返回 / 切其他 NavItem）

```
NavigateToMedia (或其他)
  → if IsDirty:
      → 弹确认对话框 "有未保存的修改, 离开将丢弃"
      → 用户取消 → 留在设置页
      → 用户确认 → DiscardChanges() + 跳转
  → else:
      → 直接跳转
```

---

## 6. 状态机

### 6.1 Tab 切换

```
General ⇄ Oss (NavItem 激活态自动响应 SelectedTab)
```

### 6.2 IsDirty 状态

```
Clean ──RecomputeDirty()──▶ Dirty
  ▲                          │
  │                          │ SaveAsync
  └──────────Save────────────┘
```

### 6.3 保存中

```
Idle → Saving → (Success → Idle | Failed → Idle)
```

---

## 7. 验收标准（DoD）

### 7.1 NavRail（v1.0 忽略）

- [x] 保持 2 项：媒体 / 设置（v2.3 现状）
- [ ] 激活态：左侧 2px 蓝色条 + 文字/图标变蓝（**走 `Accent.Stellar` token**）
- [ ] 默认显示在 Gallery 视图

### 7.2 工具栏

- [ ] 仅设置页可见「保存 / 返回」按钮
- [ ] 保存按钮在 `IsDirty` 时启用
- [ ] 返回按钮切回 Gallery 视图
- [ ] 「有未保存的修改」时返回弹确认对话框

### 7.3 Tab 栏

- [ ] 通用 / OSS配置 两个 Tab
- [ ] 选中态：灰色底 + 蓝色边框（**走 `Bg.SurfaceElevated` + `Accent.Stellar` token**）
- [ ] 切换 Tab 不丢已填字段
- [ ] 跨 Tab 状态保持（IsDirty 不重置）

### 7.4 通用 Tab

- [ ] 项目目录：Select + 浏览 + 最近目录
- [ ] 主题模式：ComboBox（Deep Space / Red Night Vision）
- [ ] 修改触发 IsDirty

### 7.5 OSS Tab

- [ ] 9 个字段全部可输入
- [ ] 厂商切换（4 选 1）
- [ ] SecretKey 脱敏（PasswordBox）
- [ ] EnableCdn=false 时 CDN Domain 禁用
- [ ] 保存到 Config 表 `oss` key

### 7.6 颜色 / Token

- [ ] **所有颜色**走 `"{DynamicResource ...}"` 引用 token
- [ ] **禁止** XAML 写 `#XXX` 颜色字面量
- [ ] **禁止** 硬编码字体 / 间距 / 圆角 / 字号

### 7.7 整体

- [ ] 设置页切换有未保存提示
- [ ] Save-First 主题（不预览）
- [ ] 跨平台表现一致

---

## 8. 涉及文件清单

### 8.1 新增

（v1.0 范围无新增文件）

### 8.2 修改

| 文件 | 改动 |
|------|------|
| `App/ViewModels/MainWindowViewModel.cs` | 新增 `IsSettingsPageVisible` |
| `App/ViewModels/SettingsViewModel.cs` | `SelectedTab` + Oss 字段 + 跨 Tab 状态保持 |
| `App/Views/SettingsView.axaml` | 删除底部保存栏；Tab 改 2 个（通用、OSS配置）；OSS Tab 字段；ViewModel SelectedTab 绑定 |
| `App/Views/MainWindow.axaml` | 全局工具栏加「保存/返回」（仅设置页可见） |
| `App/Themes/Styles.axaml` | tab-item 选中态改灰色底 + `Accent.Stellar` 边框 |

### 8.3 删除

| 文件 | 原因 |
|------|------|
| SettingsView 底部保存栏 XAML | 移到 MainWindow 工具栏 |

### 8.4 不动

| 文件 | 原因 |
|------|------|
| `App/Controls/NavRail.axaml` / `.cs` | v1.0 保持 2 项，忽略本地/服务扩展 |

---

## 9. 实施优先级

1. **P0**: 工具栏保存/返回移到顶部（仅设置页可见）
2. **P1**: Tab 改名为「通用」+ 选中态样式（灰色底 + 蓝色边框，走 token）
3. **P1**: OSS Tab 9 个字段
4. ~~P2: NavRail 4 项（v1.0 忽略，保持 2 项）~~
5. **P2**: 单元测试

---

**End of Spec v1.0**

> 本规格是设置页 v1.0，整合设计图反馈 + v2.3 现状 + 实施约束。
> 实现 AI 只需严格按照本文档执行，无需追问设计意图。
> **所有视觉决策、所有行为约束、所有架构铁律已闭环。**
