# 星助（StarHelper）— 界面实现规格 v1.0

> 本规格文档是给实现 AI 的**唯一权威输入**。
> 实现者只需严格按照本文档执行，无需追问设计意图。
> 所有视觉决策已在本文档闭环。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 项目名 | 星助 / StarHelper |
| 文档版本 | 1.0 |
| 目标用户 | 天文摄影爱好者（astrophotographer） |
| 技术栈 | **Avalonia UI 11.x** + C# + ReactiveUI / CommunityToolkit.Mvvm |
| 目标平台 | macOS（首要）、Windows 11、Linux（次要） |
| 文档语言 | 中文 |
| 设计风格 | Deep Space 深空主题（暗色优先） |

---

## 1. 产品概述

### 1.1 一句话定位

> 「天文摄影师的照片管家」—— 按时间归档、自动同步云端、保留本地副本。

### 1.2 用户画像

- **典型用户**：30-50 岁男性天文爱好者，拥有专业望远镜 + 赤道仪 + 单反/冷冻相机
- **使用场景**：
  - 户外拍摄后回到工作室导星、整理 RAW/FITS 文件
  - 长时间熬夜调参，眼睛对强光敏感
  - 跨设备查看照片（笔记本、台式机、iPad）
- **痛点**：
  - 文件太大（单张 RAW 50MB+），本地存不下
  - 拍摄记录需要按日期归档方便后期查阅
  - 网络上传中断后不知道哪些已传哪些没传

### 1.3 核心价值

1. **按拍摄日期自动归档**（左侧时间轴）
2. **直观看到每张照片的同步状态**（云图标 + 状态徽章）
3. **暗色界面保护夜视**（夜间模式：纯红界面）

---

## 2. 设计原则

按优先级排序，**任何视觉决策必须先过这五条**：

1. **暗夜优先**：默认深色界面。照片墙区域背景最深，让星空照片跳出来。
2. **照片为主角**：UI 元素全部低饱和、低亮度，不抢内容。
3. **状态用色不滥用**：颜色=语义，不用颜色装饰。
4. **拟真 macOS Chrome**：窗口采用 macOS 原生交通灯 + 38px 自绘标题栏。
5. **细节克制**：阴影、动效、渐变只在必要时使用。

---

## 3. 视觉设计系统（Design Tokens）

所有 token 必须落到 Avalonia `ResourceDictionary` 中，命名遵循 `类别.用途.变体` 三段式。

### 3.1 颜色（Colors）

#### 3.1.1 背景层（Bg.*）

| Token | Hex | 用途 |
|---|---|---|
| `Bg.Outer` | `#0A0E1A` | 窗口最外层底色 |
| `Bg.Surface` | `#161B2E` | 卡片/面板表面 |
| `Bg.SurfaceElevated` | `#1F2438` | 浮层、Tooltip、下拉面板 |
| `Bg.Divider` | `#2A3050` | 分割线、边框弱态 |
| `Bg.Hover` | `#252B45` | 悬停态背景 |

#### 3.1.2 文字层（Text.*）

| Token | Hex | 用途 |
|---|---|---|
| `Text.Primary` | `#E8EAF0` | 主要文字（柔白，不刺眼） |
| `Text.Secondary` | `#8892B0` | 次要文字、说明 |
| `Text.Tertiary` | `#5A6280` | 三级文字、占位 |
| `Text.Disabled` | `#4A5273` | 禁用态 |
| `Text.Inverse` | `#0A0E1A` | 浅色按钮上的文字 |

#### 3.1.3 强调色（Accent.*）

| Token | Hex | 用途 |
|---|---|---|
| `Accent.Stellar` | `#4FC3F7` | 恒星青蓝 — 选中/激活/主交互 |
| `Accent.Nebula` | `#B388FF` | 星云紫 — 高亮/焦点 |
| `Accent.Aurora` | `#4DD0E1` | 极光青 — 已上传/成功 |

#### 3.1.4 状态色（State.*）

| Token | Hex | 语义 | 出现位置 |
|---|---|---|---|
| `State.Quiet` | `#4A5273` | 静默、未操作 | 「未上传」图标、占位 |
| `State.Warning` | `#FFA726` | 待处理、提醒 | 「已上传但本地不存在」、进度提示 |
| `State.Danger` | `#FF5252` | 错误、危险操作 | 网络错误、保存失败 |
| `State.Success` | `#4DD0E1` | 成功 | 「已上传且本地存在」 |

> ⚠️ **关键规则**：三种上传状态中，**只有一种用红色**（State.Danger），其余两种分别是青色（成功）和金色（待处理），避免全部标红的视觉噪音。

#### 3.1.5 渐变（Gradient.*）

| Token | 定义 | 用途 |
|---|---|---|
| `Gradient.Header` | `Linear, 180deg, #0A0E1A → #161B2E` | 标题栏背景 |
| `Gradient.PrimaryButton` | `Linear, 135deg, #7E57C2 → #B388FF` | 主按钮（星云紫） |
| `Gradient.TimelineSelected` | `Radial, 0,0, #4FC3F7 0% → transparent 70%` | 选中时间点的光晕 |

### 3.2 字体（Typography）

| Token | 值 | 用途 |
|---|---|---|
| `Font.Family.Primary` | `"Inter", "PingFang SC", "Microsoft YaHei", sans-serif` | 全局 |
| `Font.Family.Mono` | `"JetBrains Mono", "SF Mono", Consolas` | 时间戳、文件路径 |

| 层级 | Size | Weight | LineHeight | 用途 |
|---|---|---|---|---|
| `Display` | 28 | 600 | 1.2 | 暂未使用，预留 |
| `Title` | 18 | 600 | 1.3 | 页面标题 |
| `Heading` | 15 | 600 | 1.4 | 区块标题 |
| `Body` | 13 | 400 | 1.5 | 正文 |
| `Caption` | 12 | 400 | 1.4 | 辅助说明 |
| `Label` | 11 | 500 | 1.2 | 徽章、Tag |
| `Timestamp` | 12 | 500 mono | 1.2 | 时间戳 |

### 3.3 间距（Spacing）

8 像素基准制：

| Token | 值 | 用途 |
|---|---|---|
| `Space.1` | 4 | 极小间距（图标内边距） |
| `Space.2` | 8 | 控件内元素 |
| `Space.3` | 12 | 控件之间 |
| `Space.4` | 16 | 区块内边距 |
| `Space.5` | 24 | 区块间距 |
| `Space.6` | 32 | 大区块 |
| `Space.7` | 48 | 页面边缘 |

### 3.4 圆角（Radius）

| Token | 值 | 用途 |
|---|---|---|
| `Radius.Small` | 4 | 标签、小按钮 |
| `Radius.Medium` | 8 | 输入框、缩略图 |
| `Radius.Large` | 12 | 卡片、面板 |
| `Radius.Pill` | 9999 | 徽章、Tag |

### 3.5 阴影与发光（Shadow/Glow）

| Token | 定义 | 用途 |
|---|---|---|
| `Shadow.Card` | `0 4px 12px rgba(0,0,0,0.4)` | 卡片浮起 |
| `Shadow.Popover` | `0 8px 24px rgba(0,0,0,0.6)` | 下拉、Tooltip |
| `Glow.Stellar` | `0 0 8px #4FC3F7` | 选中态发光 |
| `Glow.StellarStrong` | `0 0 16px #4FC3F7, 0 0 4px #E8EAF0` | 强焦点（如时间点激活） |
| `Glow.Nebula` | `0 0 12px #B388FF` | 主按钮 hover |

### 3.6 动效（Motion）

| Token | 值 | 用途 |
|---|---|---|
| `Duration.Fast` | 120ms | 悬停反馈 |
| `Duration.Normal` | 200ms | 状态切换 |
| `Duration.Slow` | 320ms | 页面切换、抽屉 |
| `Easing.Standard` | `cubic-bezier(0.4, 0, 0.2, 1)` | 默认 |
| `Easing.Decelerate` | `cubic-bezier(0, 0, 0.2, 1)` | 进入 |
| `Easing.Accelerate` | `cubic-bezier(0.4, 0, 1, 1)` | 退出 |

---

## 4. 组件库

每个组件给出**用途 / 外观 / 状态 / 交互 / 实现要点**五要素。

### C1 窗口外壳 `MacChromeWindow`

- **用途**：整个 App 的主窗口
- **外观**：
  - 标题栏高度 38px，使用 `Gradient.Header`
  - 左：macOS 交通灯（红 `#FF5F57` / 黄 `#FFBD2E` / 绿 `#28C940`），12×12px 圆点，间距 8px，距左 14px
  - 中：标题文字「星助」，13px SemiBold，居中
  - 右：「设置」文字按钮，hover 显示背景 `Bg.Hover`，圆角 6
- **状态**：无（固定 chrome）
- **交互**：交通灯使用 macOS 系统行为（关闭/最小化/全屏）
- **实现**：通过 `ExtendClientAreaToDecorationsHint="True"` + 自绘 Border

### C2 时间轴 `TimelineRail`

- **用途**：左侧按日期归档的导航
- **外观**：
  - 容器宽度 220px，背景 `Bg.Outer`
  - 内部垂直居中分布时间节点
  - 节点之间用 1px `Bg.Divider` 竖线连接（高度由 `Grid` 控制）
  - 每个节点：
    - 圆点 8×8px，**未选中**：`Bg.Divider` 实心；**选中**：`Accent.Stellar` + `Glow.Stellar`
    - 文字左对齐，距圆点 12px
    - **未选中文字**：`Text.Secondary`，12px FontFamily.Mono
    - **选中文字**：`Accent.Stellar`，12px FontFamily.Mono，加粗
- **状态**：默认 / 悬停 / 选中
- **交互**：单击切换选中节点；选中节点触发右侧网格重载
- **实现**：使用 `ItemsControl` + 自定义 `DataTemplate`

### C3 照片缩略图 `PhotoTile`

- **用途**：网格中单个照片
- **外观**：
  - 尺寸 160×120px，圆角 `Radius.Medium`，背景 `Bg.Surface`
  - 占位状态（无图时）：
    - 中心显示**星座连线 SVG**（Path），stroke=`Text.Disabled`，opacity=0.4
    - 禁用普通图片占位符
  - 有图时：直接显示照片，`Stretch="UniformToFill"`
  - 右下角徽章：
    - 仅当数量 > 1 时显示
    - 背景 `#CC1F2438`（半透明 SurfaceElevated）
    - 圆角 `Radius.Pill`，padding 8×2
    - 文字 `Text.Primary`，11px
    - 格式：「{N}张」
  - 右上角状态图标（详见 C4）
- **状态**：默认 / 悬停 / 按下 / 选中
- **交互**：
  - 单击：选中（用于多选）
  - 双击：在系统默认看图器打开
  - 右键：上下文菜单（查看详情 / 删除 / 重新上传）
  - hover：轻微放大（scale 1.02），时长 `Duration.Fast`
- **实现**：`Button` 自定义 Template；图片用 `Image` 控件，绑定到 `Bitmap`

### C4 状态徽章 `StatusBadge`

- **用途**：缩略图右上角表示照片同步状态
- **三种状态**：

| 状态 | 图标 | 颜色 | 含义 |
|---|---|---|---|
| 已上传且本地存在 | 云 ☁️ | `State.Success` | OK |
| 已上传但本地不存在 | 云 + ⚠️ | `State.Warning` | 云端有本地缺 |
| 未上传 | 云 + 🚫 | `State.Quiet` | 待上传 |

- **外观**：16×16px，置于缩略图右上角内边距 6px
- **交互**：hover 显示 Tooltip 详细说明（详见 C10）

### C5 主按钮 `PrimaryButton`

- **用途**：保存、确认等核心操作
- **外观**：
  - 高度 36px，圆角 `Radius.Medium`
  - 背景 `Gradient.PrimaryButton`（星云紫渐变）
  - 文字 `Text.Inverse`，13px SemiBold
  - Padding 水平 24px
- **状态**：
  - 默认：渐变 + `Shadow.Card`
  - Hover：渐变变亮 10% + `Glow.Nebula`
  - 按下：渐变变暗 10%
  - 禁用：渐变去饱和，文字 `Text.Disabled`
- **交互**：点击触发主操作

### C6 次按钮 `SecondaryButton`

- **用途**：取消、次要操作
- **外观**：透明背景 + 1px `Bg.Divider` 边框，文字 `Text.Primary`，其他同 C5

### C7 Tab 栏 `TabBar`

- **用途**：设置页的「项目 / oss配置」切换
- **外观**：
  - 高度 44px，背景 `Bg.Outer`
  - Tab 文字 13px
  - **未选中**：`Text.Secondary`
  - **选中**：`Accent.Stellar` + 底部 2px 下划线（`Accent.Stellar` 带 `Glow.Stellar`）
  - Tab 间距 24px，水平 padding 4px
- **状态**：默认 / 悬停 / 选中
- **交互**：点击切换 Tab，下划线有 `Duration.Normal` 滑动动画

### C8 下拉选择 `Select`

- **用途**：选择项目目录
- **外观**：
  - 高度 36px，最小宽度 240px
  - 背景 `Bg.Surface`，1px `Bg.Divider` 边框
  - 圆角 `Radius.Medium`
  - 左侧：当前选中文字，`Text.Primary`，13px
  - 右侧：chevron 向下箭头，`Text.Secondary`
  - Hover：边框变 `Accent.Stellar`
  - 打开：边框 `Accent.Stellar` + `Glow.Stellar` + 弹出下拉面板（`Bg.SurfaceElevated` + `Shadow.Popover`）
- **状态**：默认 / 悬停 / 打开 / 禁用
- **交互**：点击展开下拉，选中后收起并更新值

### C9 表单行 `FormRow`

- **用途**：设置页中标签 + 控件的组合
- **外观**：
  - 高度 56px
  - 左：标签文字，`Text.Primary`，13px，固定宽度 120px
  - 右：控件（如 C8 Select）
  - 垂直居中对齐
  - 底部分割线 1px `Bg.Divider`
- **状态**：默认 / 焦点（控件获焦时高亮分割线）

### C10 Tooltip `Tooltip`

- **用途**：hover 显示说明
- **外观**：
  - 背景 `Bg.SurfaceElevated`，圆角 `Radius.Small`
  - 1px `Bg.Divider` 边框
  - 文字 `Text.Primary`，12px
  - Padding 8×6
  - 阴影 `Shadow.Popover`
  - 出现动画：`Duration.Fast` 淡入 + 上移 4px
- **触发**：hover 300ms 后出现，鼠标离开 100ms 后消失

### C11 图标系统 `Iconography`

- **图标库**：使用 Lucide Icons（开源 SVG），通过 Avalonia 的 `Path` 渲染
- **尺寸规范**：
  - 小（inline）：12×12
  - 中（默认）：16×16
  - 大（功能按钮）：20×20
- **颜色规则**：图标颜色 = `Text.*` 或 `Accent.*` 或 `State.*`，不使用纯黑/纯白

---

## 5. 页面规格

### 5.1 页面 1：主相册页 `GalleryView`

#### 5.1.1 布局结构

```
┌────────────────────────────────────────────────────────────┐
│  MacChrome (38px)                                          │
├──────────┬─────────────────────────────────────────────────┤
│          │                                                  │
│ Timeline │           PhotoGrid (4列 × N行)                  │
│  (220px) │                                                  │
│          │                                                  │
│          │                                                  │
│          │                                                  │
│          │                                                  │
└──────────┴─────────────────────────────────────────────────┘
```

- 外层 `Grid`：`RowDefinitions="38,*"`、`ColumnDefinitions="220,*"`
- 标题栏跨两列
- 时间轴占左列
- 照片网格占右列

#### 5.1.2 数据模型

```csharp
public record Photo(
    string Id,
    DateTime ShotAt,         // 拍摄时间
    Bitmap Thumbnail,        // 缩略图
    SyncStatus Status        // 同步状态
);

public enum SyncStatus
{
    UploadedAndLocal,        // 已上传且本地存在
    UploadedButMissingLocal, // 已上传但本地不存在
    NotUploaded              // 未上传
}

public record TimelineEntry(DateTime Date, int PhotoCount);
```

#### 5.1.3 照片网格布局

- 容器：内边距 24px，水平方向
- 列：4 列等宽，间距 16px
- 行：自适应，每行 120px 高
- 使用 `WrapPanel` 或 `ItemsRepeater` + `UniformGridLayout`

#### 5.1.4 交互

| 操作 | 触发 | 结果 |
|---|---|---|
| 单击时间节点 | TimelineRail | 右侧网格重新加载该日期照片 |
| 悬停缩略图 | PhotoTile | scale 1.02 + 显示 Tooltip |
| 单击缩略图 | PhotoTile | 选中（多选） |
| 双击缩略图 | PhotoTile | 系统看图器打开 |
| 右键缩略图 | PhotoTile | 弹出上下文菜单 |

#### 5.1.5 状态（页面级）

| 状态 | 表现 |
|---|---|
| 加载中 | 网格区域显示 12 个骨架屏（占位 PhotoTile） |
| 空数据 | 居中显示「该日期暂无照片」+ 副标题「去拍摄或导入新照片」+ 主按钮「导入照片」 |
| 错误 | 顶部 Toast「加载失败」+ 重试按钮 |

### 5.2 页面 2：设置页 `SettingsView`

#### 5.2.1 布局结构

```
┌────────────────────────────────────────────────────────────┐
│  MacChrome (38px)                                          │
├────────────────────────────────────────────────────────────┤
│  TabBar (项目 | oss配置)                                   │
├────────────────────────────────────────────────────────────┤
│                                                             │
│   FormRow: 项目目录    [Select: 下拉菜单      ▼]          │
│                                                             │
│                                                             │
│   ──────────────────────────────────────────────            │
│                                              [保存]          │
└────────────────────────────────────────────────────────────┘
```

#### 5.2.2 数据模型

```csharp
public class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private string selectedProjectDirectory;
    [ObservableProperty] private ObservableCollection<string> availableDirectories;
    [ObservableProperty] private bool isDirty;  // 是否有未保存修改
}
```

#### 5.2.3 校验规则

- 「项目目录」必选，不能为空
- 目录必须存在且可读
- 切换 Tab 时若有未保存修改，弹出确认弹窗

#### 5.2.4 状态

| 状态 | 表现 |
|---|---|
| 干净（已保存） | 保存按钮禁用态 |
| 脏（修改未保存） | 保存按钮启用，按钮旁显示「有未保存的修改」小字 |
| 保存中 | 保存按钮显示 Spinner，禁用其他交互 |
| 保存成功 | Toast「已保存」2s 后消失 |
| 保存失败 | Toast「保存失败：{原因}」+ 重试 |

---

## 6. 交互流程

### 6.1 切换时间节点

```
用户点击 TimelineRail 中某节点
  → 选中样式切换（带 Glow 动画）
  → ViewModel 触发 Photos = LoadPhotos(date)
  → PhotoGrid 显示骨架屏
  → 数据到达后渲染缩略图
  → 状态徽章根据每张照片的 SyncStatus 渲染
```

### 6.2 主题切换

```
用户点击设置中的「主题模式」
  → 选择 Deep Space / Red Night Vision
  → ViewModel 更新 Theme 枚举
  → App.axaml 切换 RequestedThemeVariant
  → 全局颜色 token 替换，伴随 200ms 渐变过渡
```

### 6.3 照片上传（未来功能，预留接口）

```
用户拖拽照片到 PhotoGrid
  → 显示 DropOverlay（半透明 Accent.Stellar）
  → 释放后开始上传
  → 每个文件创建 Progress，绑定到对应 PhotoTile 的状态徽章
  → 上传中：徽章显示 Spinner
  → 成功：徽章变 State.Success
  → 失败：徽章变 State.Danger + Tooltip「点击重试」
```

---

## 7. 状态机

### 7.1 照片同步状态机

```
                    Upload
   NotUploaded ─────────────▶ UploadedAndLocal
        ▲                          │
        │ Delete Local             │ Delete Local
        │                          ▼
        └────────────── UploadedButMissingLocal

   UploadedButMissingLocal ──Download──▶ UploadedAndLocal
```

### 7.2 应用主题状态

```
   DeepSpace ◀──────────────┐
       │                    │
       │ User selects       │
       ▼                    │
   RedNightVision ──────────┘
       User selects
```

---

## 8. 主题模式

应用支持两种主题，运行时可切换：

### 8.1 Deep Space（默认）

按 §3.1 定义的所有 token 实施。

### 8.2 Red Night Vision（夜间红）

覆盖以下 token：

```xml
<!-- RedNightVision.axaml -->
<Color x:Key="Bg.Outer">#000000</Color>
<Color x:Key="Bg.Surface">#0A0000</Color>
<Color x:Key="Bg.SurfaceElevated">#140000</Color>
<Color x:Key="Bg.Divider">#2A0000</Color>
<Color x:Key="Bg.Hover">#1F0000</Color>

<Color x:Key="Text.Primary">#FF6B6B</Color>
<Color x:Key="Text.Secondary">#B53030</Color>
<Color x:Key="Text.Tertiary">#802020</Color>
<Color x:Key="Text.Disabled">#4D0000</Color>

<Color x:Key="Accent.Stellar">#FF3030</Color>
<Color x:Key="Accent.Nebula">#FF6060</Color>
<Color x:Key="Accent.Aurora">#FF8080</Color>

<Color x:Key="State.Success">#FF8080</Color>
<Color x:Key="State.Warning">#FFAA80</Color>
<Color x:Key="State.Danger">#FF3030</Color>
```

切换实现：`App.axaml.cs` 中通过 `Application.Current.Resources.MergedDictionaries` 替换。

---

## 9. 跨平台规范

| 平台 | 注意事项 |
|---|---|
| **macOS** | 交通灯使用系统原生（`ExtendClientAreaChromeHints="PreferSystemChrome"`）；菜单栏走 macOS 主菜单；字体优先 PingFang SC |
| **Windows 11** | 可选启用 Mica 效果；字体优先 Microsoft YaHei UI |
| **Linux** | 测试 GTK 与 XWayland 表现；字体优先 Noto Sans CJK SC |

---

## 10. 可访问性（A11y）

- 所有交互元素必须支持键盘导航（Tab / Shift+Tab / Enter / Esc）
- 图片缩略图必须有 `AutomationProperties.Name`
- 颜色对比度：文字与背景至少 4.5:1（AA 级）
- 不依赖单一颜色传达状态（图标 + 文字 + 颜色三重提示）
- 提供「减少动效」系统设置时，所有 Duration 缩短为 50%

---

## 11. 验收标准（DoD）

实现完成必须满足：

- [ ] §3 所有 token 全部实现并在 `Colors.axaml` 中可查
- [ ] §4 11 个组件全部有自定义样式，并支持列出的所有状态
- [ ] §5 两个页面布局与文档完全一致
- [ ] §6 三条核心交互流程跑通
- [ ] §7 状态机覆盖所有状态转换
- [ ] §8 两种主题可运行时切换，无残留样式
- [ ] §9 三个平台均可启动并运行
- [ ] §10 A11y 基础要求达成
- [ ] 所有交互元素在悬停 / 聚焦 / 按下时都有视觉反馈
- [ ] 字体、间距、圆角全部走 token，禁止硬编码

---

## 12. 技术约束

### 12.1 必须使用

- Avalonia UI 11.x
- C# 12+
- .NET 8
- ReactiveUI **或** CommunityToolkit.Mvvm（二选一）
- Lucide Icons SVG

### 12.2 禁止

- ❌ 硬编码颜色（必须用 token）
- ❌ 硬编码间距 / 圆角 / 字号
- ❌ 在 XAML 中写业务逻辑
- ❌ 使用 `Code Behind` 处理 UI 逻辑（除初始化）
- ❌ 引入未在本文档列出的第三方 UI 库

### 12.3 推荐

- ✅ 使用 `CompiledBindings`（X11 编译期绑定，性能更好）
- ✅ 使用 `x:DataType` 强类型绑定
- ✅ 自定义控件用 `TemplatedControl` 而非 `UserControl`
- ✅ 图片缩略图异步加载 + 内存缓存（LRU 100 张）

---

## 13. 项目结构（推荐）

```
StarHelper/
├── StarHelper.sln
├── src/
│   ├── StarHelper.App/
│   │   ├── Program.cs
│   │   ├── App.axaml / App.axaml.cs
│   │   ├── Views/
│   │   │   ├── MainWindow.axaml
│   │   │   ├── GalleryView.axaml
│   │   │   └── SettingsView.axaml
│   │   ├── ViewModels/
│   │   │   ├── MainWindowViewModel.cs
│   │   │   ├── GalleryViewModel.cs
│   │   │   └── SettingsViewModel.cs
│   │   └── Assets/
│   └── StarHelper.Core/
│       ├── Models/
│       └── Services/
└── Themes/
    └── Colors.axaml
    ├── DeepSpace.axaml
    └── RedNightVision.axaml
```

---

## 14. 实现优先级（建议执行顺序）

1. **脚手架**：项目结构 + `App.axaml` + `MainWindow.axaml` + `Colors.axaml`
2. **主题系统**：所有 token 落到 ResourceDictionary，验证 Deep Space 主题可见
3. **C1 MacChrome**：标题栏 + 交通灯 + 设置按钮
4. **C2 TimelineRail**：静态数据先跑通视觉
5. **C3 PhotoTile**：含 C4 状态徽章
6. **页面组装**：GalleryView 跑起来
7. **C5~C9 控件**：TabBar / Select / FormRow / Button
8. **SettingsView 页面**：跑通
9. **主题切换**：Deep Space ↔ Red Night Vision
10. **动效与微交互**：所有 Duration / Easing 应用
11. **A11y 收尾**

---

**End of Spec v1.0**