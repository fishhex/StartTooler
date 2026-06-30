# 星助（StarHelper）— 界面实现规格 v2.3

> 本规格文档是给实现 AI 的**唯一权威输入**。
> 实现者只需严格按照本文档执行，无需追问设计意图。
> 所有视觉决策、行为约束、踩坑教训已闭环。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 项目名 | 星助 / StartTooler |
| 文档版本 | **2.3**（从 v2.2 升级；v2.3 补充多选模式、批量操作、右键菜单的实现约束） |
| 目标用户 | 天文摄影爱好者（astrophotographer） |
| 技术栈 | **Avalonia UI 11.x** + C# 12 + **.NET 9** + **SQLite** + CommunityToolkit.Mvvm |
| 目标平台 | macOS（首要）、Windows 11、Linux（次要） |
| 文档语言 | 中文 |
| 设计风格 | Deep Space 深空主题（暗色优先）+ Red Night Vision 夜间红（次要） |
| 项目结构 | **单项目**（StartTooler/StartTooler/） |
| 字数限制 | 本版本忽略字数限制，作为权威参考 |

### v2.3 相对 v2.2 的变更摘要

| 类别 | 变更 |
|------|------|
| **工具栏** | 改为「全局工具栏」（标题栏下方），不是每页独立 |
| **工具栏按钮** | 新增「多选 / 取消多选 / 批量上传 / 删除」4 个按钮 |
| **多选模式** | 默认只显示「多选 / 刷新」；进入多选态后显示「取消多选 / 批量上传 / 删除」 |
| **批量操作** | 上传 / 删除仅做 UI 反馈（Toast），不执行真实 IO |
| **右键菜单** | 新增：上传 / 删除 / 彻底删除 / 文件夹中打开 / 查看 |
| **系统 API** | 「在文件夹中打开」跨平台调用系统 Shell |
| **UI 状态机** | 单张照片：未选择 / 选中 / 进行中 三态可视化 |
| **NavRail** | 4 项：媒体 / 本地 / 服务 / 设置（v2.2 只有 2 项，更新） |

### v2.2 相对 v2.1 的变更摘要

| 类别 | 变更 |
|------|------|
| **技术栈** | .NET 8 → .NET 9 |
| **项目结构** | src/ 子项目 → 单项目（StartTooler/StartTooler/） |
| **ScanProgressBar** | Gallery 页内 → MainWindow 标题栏区域 |
| **GalleryView 工具栏** | 高度 40px，有 "Gallery" 文字 |
| **时间轴宽度** | 220px → 180px |
| **NavRail** | 图标+文字 → **只有文字**（垂直排列） |
| **TimelineEntry** | Count 属性 → **PhotoCount** 属性 |
| **数据库表名** | `config` → `Config`（大写） |
| **数据库路径** | `~/Library/Application Support/` → `Environment.SpecialFolder.ApplicationData` |

### v2.1 相对 v2.0 的变更摘要（历史）

| 类别 | 变更 |
|------|------|
| **组件库** | C1-C11 基础组件 + **C12 RefreshButton** + **C13 VideoBadge** + **C14 ScanProgressBar** + **C15 NavRail** |
| **数据层** | 拆分：设置走 `config` 表（JSON）+ 业务数据走 `media_files` 独立表 |
| **媒体层** | 新增 media_files 表、IMediaScanner、IThumbnailGenerator、IFfmpegLocator |
| **UI 数据接入** | 完整 GalleryViewModel 状态机 |
| **跨模块铁律** | 8 条沉淀（颜色/状态/Flyout/Radio/Icon/Converter/故障隔离/缩略图） |
| **已知陷阱** | 8 个迭代中踩过的坑 + 解决方案 |
| **MainWindow 布局** | **三区域模式**（标题栏 / 菜单栏 / 每页自带工具栏+内容） |

### v2.0 核心架构决策

| 决策 | 说明 |
|------|------|
| 三层顶部结构 | 标题栏（仅标题） / 工具栏（仅刷新） / 主内容（Timeline + Grid） |
| 左侧 NavRail 80px | 媒体 + 设置 页面导航 |
| **不**保留右侧图例 | 原版原型没有，移除以对齐 |
| 单表 JSON 存设置 | `config` 表，4 个分区共享 |
| 业务数据独立表 | `media_files` 等 |
| 单一启用控制 | `IsEnabled` 与 `CanExecute` 二选一 |
| Save-First 主题 | 主题切换不预览，保存才应用 |
| 故障隔离 | 扫描循环必须 try/catch |

---

## 1. 产品概述

### 1.1 一句话定位

> 「天文摄影师的照片管家」—— 按时间归档，递归扫描，自动同步云端，保留本地副本。

### 1.2 用户画像

- **典型用户**：30-50 岁男性天文爱好者，望远镜 + 赤道仪 + 单反/冷冻相机
- **使用场景**：
  - 户外拍摄后回工作室导星、整理 RAW/FITS 文件
  - 长时间熬夜调参，眼睛对强光敏感
  - 跨设备查看照片
- **痛点**：
  - 文件太大（单张 RAW 50MB+），本地存不下
  - 拍摄记录需要按日期归档方便后期查阅
  - 网络上传中断后不知道哪些已传哪些没传
  - 不知道视频文件怎么预览

### 1.3 核心价值

1. **按拍摄日期自动归档**（左侧时间轴）
2. **递归扫描 + 缩略图**（工具栏刷新）
3. **直观看到每张照片的同步状态**（云图标 + 状态徽章）
4. **暗色界面保护夜视**（夜间红模式）

---

## 2. 设计原则

按优先级排序，**任何视觉决策必须先过这 5 条**：

1. **暗夜优先** — 默认深色界面。照片墙区域背景最深，让星空照片跳出来。
2. **照片为主角** — UI 元素全部低饱和、低亮度，不抢内容。
3. **状态用色不滥用** — 颜色=语义，不用颜色装饰。
4. **拟真 macOS Chrome** — 窗口采用 macOS 原生交通灯 + 38px 自绘标题栏。
5. **细节克制** — 阴影、动效、渐变只在必要时使用。

加上 6 条**跨模块铁律**（详见 §20）：

6. **Token 严格** — 颜色/间距/圆角走 token，禁硬编码
7. **单一启用控制** — `IsEnabled` 与 `CanExecute` 二选一
8. **Save-First 主题** — 主题切换不预览，保存才生效
9. **图标用 StreamGeometry** — 不用 PathGeometry（Bounds 延迟）
10. **状态可追踪** — 显式 IsDirty、显式快照
11. **故障隔离** — 扫描循环必须 try/catch，单文件失败不让 App 崩

---

## 3. 视觉设计系统（Design Tokens）

所有 token 必须落到 Avalonia `ResourceDictionary` 中，命名遵循 `类别.用途.变体` 三段式。

### 3.1 颜色（Colors）

#### 3.1.1 Deep Space（默认主题）

**背景层**：

| Token | Hex | 用途 |
|---|---|---|
| `Bg.Outer` | `#0A0E1A` | 窗口底色、标题栏、NavRail、Sidebar |
| `Bg.Surface` | `#161B2E` | 工具栏、卡片/面板表面、缩略图占位 |
| `Bg.SurfaceElevated` | `#1F2438` | 浮层、Tooltip、下拉面板、NavItem 激活态、星形占位 |
| `Bg.Divider` | `#2A3050` | 分割线、边框弱态 |
| `Bg.Hover` | `#252B45` | 悬停态背景 |

**文字层**：

| Token | Hex | 用途 |
|---|---|---|
| `Text.Primary` | `#E8EAF0` | 主要文字（柔白，不刺眼） |
| `Text.Secondary` | `#8892B0` | 次要文字、说明、NavItem 非激活态 |
| `Text.Tertiary` | `#5A6280` | 三级文字、占位、辅助 |
| `Text.Disabled` | `#4A5273` | 禁用态、星形占位弱化色 |
| `Text.Inverse` | `#0A0E1A` | 浅色按钮上的文字 |

**强调色**：

| Token | Hex | 用途 |
|---|---|---|
| `Accent.Stellar` | `#4FC3F7` | 恒星青蓝 — 选中/激活/NavItem 激活文字 |
| `Accent.Nebula` | `#B388FF` | 星云紫 — 高亮/焦点、PrimaryButton 渐变 |
| `Accent.Aurora` | `#4DD0E1` | 极光青 — 已上传/成功 |

**状态色**：

| Token | Hex | 语义 | 出现位置 |
|---|---|---|---|
| `State.Quiet` | `#4A5273` | 静默、未操作 | 「未上传」图标 |
| `State.Warning` | `#FFA726` | 待处理、提醒 | 「已上传但本地不存在」、扫描中文字 |
| `State.Danger` | `#FF5252` | 错误、危险 | 网络错误、保存失败 |
| `State.Success` | `#4DD0E1` | 成功 | 「已上传且本地存在」 |

**渐变**：

| Token | 定义 | 用途 |
|---|---|---|
| `Gradient.Header` | `Linear, 180deg, #0A0E1A → #161B2E` | 标题栏背景 |
| `Gradient.PrimaryButton` | `Linear, 135deg, #7E57C2 → #B388FF` | 主按钮（星云紫渐变） |
| `Gradient.TimelineSelected` | `Radial, 0,0, #4FC3F7 0% → transparent 70%` | 选中时间点的光晕 |

#### 3.1.2 Red Night Vision（夜间红主题）

> 户外拍星时使用，保护暗适应。

```xml
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

### 3.2 字体（Typography）

| Token | 值 | 用途 |
|---|---|---|
| `Font.Family.Primary` | `"Inter", "PingFang SC", "Microsoft YaHei", sans-serif` | 全局 |
| `Font.Family.Mono` | `"JetBrains Mono", "SF Mono", Consolas` | 时间戳、文件路径 |

| 层级 | Size | Weight | LineHeight | 用途 |
|---|---|---|---|---|
| `Display` | 28 | 600 | 1.2 | 预留 |
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
| `Space.4` | 16 | 区块内边距、FormRow 间距 |
| `Space.5` | 24 | 区块间距 |
| `Space.6` | 32 | 大区块 |
| `Space.7` | 48 | 页面边缘 |

### 3.4 圆角（Radius）

| Token | 值 | 用途 |
|---|---|---|
| `Radius.Small` | 4 | 标签、小按钮 |
| `Radius.Medium` | 8 | 输入框、缩略图、Select 按钮 |
| `Radius.Large` | 12 | 卡片、面板 |
| `Radius.Pill` | 9999 | 徽章、Tag |

### 3.5 阴影与发光（Shadow/Glow）

| Token | 定义 | 用途 |
|---|---|---|
| `Shadow.Card` | `0 4px 12px rgba(0,0,0,0.4)` | 卡片浮起 |
| `Shadow.Popover` | `0 8px 24px rgba(0,0,0,0.6)` | 下拉、Tooltip |
| `Glow.Stellar` | `0 0 8px #4FC3F7` | 选中态发光 |
| `Glow.StellarStrong` | `0 0 16px #4FC3F7, 0 0 4px #E8EAF0` | 强焦点 |
| `Glow.Nebula` | `0 0 12px #B388FF` | 主按钮 hover |

### 3.6 动效（Motion）

| Token | 值 | 用途 |
|---|---|---|
| `Duration.Fast` | 120ms | 悬停反馈、缩略图淡入 |
| `Duration.Normal` | 200ms | 状态切换、页面切换 |
| `Duration.Slow` | 320ms | 抽屉、滑入滑出 |
| `Easing.Standard` | `cubic-bezier(0.4, 0, 0.2, 1)` | 默认 |
| `Easing.Decelerate` | `cubic-bezier(0, 0, 0.2, 1)` | 进入 |
| `Easing.Accelerate` | `cubic-bezier(0.4, 0, 1, 1)` | 退出 |

---

## 4. 组件库

### 4.1 组件清单（v2.2 实际实现）

| 编号 | 名称 | 用途 | 状态 |
|------|------|------|------|
| C1 | MacChrome | 窗口标题栏 | ✅ |
| C2 | TimelineRail | 时间轴 | ✅ |
| C3 | MediaFileTile | 媒体缩略图（图片+视频） | ✅ |
| C4 | StatusBadge | 同步状态徽章（三态） | ✅ |
| C5 | PrimaryButton | 主按钮（紫渐变） | ✅ |
| C6 | SecondaryButton | 次按钮 | ✅ |
| C7 | TabBar | Tab 切换 | ✅ |
| C8 | Select | 选择器（含 Flyout） | ✅ |
| C9 | FormRow | 表单行 | ✅ |
| C10 | Tooltip | 悬浮提示 | ✅ |
| C11 | Iconography | 图标系统（StreamGeometry） | ✅ |
| C12 | RefreshButton | 工具栏刷新按钮（4 状态+旋转动画） | ✅ |
| C13 | VideoBadge | 视频角标 ▶ | ✅ |
| C14 | ScanProgressBar | **标题栏底部**进度条 | ✅ |
| C15 | NavRail | 左侧导航 rail（**只有文字**） | ✅ |
| C16 | StringToBitmapConverter | 文件路径 → Bitmap 转换器 | ✅（FilePathToBitmapConverter）|

### C1 MacChrome

**标题栏（38px）**：
- 背景 `Bg.Outer`
- 底边框 1px `Bg.Divider`
- 左侧：交通灯 3 个圆点（12×12，间距 8px，margin-left 14px）
- 中央：星助 13px SemiBold `Text.Primary`
- **不**放任何按钮

### C2 TimelineRail

- 宽度 220px
- 节点：圆点 8×8 + 文字 12px mono + 数量徽章（条件显示）
- 选中态：`Accent.Stellar` 圆点 + `Glow.Stellar` + 文字 `Accent.Stellar`
- 状态：默认 / Hover / 选中 / 空态 / 加载骨架

### C3 MediaFileTile（核心组件）

> 绑定 `MediaFile` 模型。

**外观**：
- 尺寸 160×120px
- 圆角 `Radius.Medium`
- 背景 `Bg.Surface`

**内部结构（Panel）**：

```
┌─────────────────────────┐
│ [Image or Star]         │ ← 缩略图层
│                    [▶]  │ ← 视频徽章（条件）
│                    [☁] │ ← 状态徽章
└─────────────────────────┘
```

**绑定字段**：

| 元素 | 绑定 | Converter | 条件 |
|------|------|-----------|------|
| Image | `ThumbnailPath` | `StringToBitmap` (C16) | `IsNotNullOrEmpty` |
| Star | `Icon.StarOutline` | — | `IsNullOrEmpty` |
| StatusBadge | `IsUploaded + LocalExists` | `MediaFileToStatus` | 总是 |
| VideoBadge | `MediaType` | `MediaTypeToVideo` | `MediaType == Video` |

**状态**：
- 默认：占位/图片 + 徽章
- Hover：scale 1.02 + `Glow.Stellar` 边框
- 选中：实色 `Accent.Stellar` 边框 + 强发光
- 加载中：骨架屏

### C4 StatusBadge

**位置**：缩略图右上角 8px 内边距。

**三种状态**：

| `IsUploaded` | `LocalExists` | 图标 | 颜色 | 含义 |
|--------------|---------------|------|------|------|
| true | true | `Icon.Cloud` | `State.Success` | 已上传且本地有 |
| true | false | `Icon.AlertTriangle` | `State.Warning` | 云端有本地缺 |
| false | * | `Icon.CloudOff` | `State.Quiet` | 未上传 |

**结构**：半透明圆角背景（`#CC0A0E1A`）+ 12×12 图标。

### C5 PrimaryButton

- 背景 `Gradient.PrimaryButton`
- 文字 `Text.Inverse` 13px SemiBold
- 圆角 `Radius.Medium`
- 阴影 `0 4 12 0 #40000000`
- Hover：`Glow.Nebula` + 阴影增强

### C7 TabBar

- 高度 44px
- Tab 文字 13px
- 选中：`Accent.Stellar` 文字 + 底部 2px 下划线 + `Glow.Stellar`
- 间距 24px

### C8 Select

**关键规则**：
- 内部用 **Grid**（不是 Panel）
- 单个 TextBlock + Converter 切换占位/值
- 右侧 chevron `Icon.ChevronDown` 12×12

**Flyout 规则**：
- `Placement="BottomEdgeAlignedLeft"`
- **Flyout 宽度 = 按钮宽度**：用 FlyoutOpened 事件 + 代码后置同步
- ❌ **禁用** `MinWidth` 绑定 `$parent[Button].Bounds.Width`（时序不可靠）

### C9 FormRow

- 高度 64px
- padding `40,16`
- 底分割线 1px `Bg.Divider`
- 三列 Grid：`160px * Auto`

### C12 RefreshButton（5 状态）

| 状态 | 触发 | 表现 |
|------|------|------|
| 默认 | `!IsScanning && HasProject` | 文字「刷新」+ 静态 `Icon.Refresh`，背景 `Bg.Outer` |
| Hover | pointer over | 文字+图标变 `Accent.Stellar` |
| Pressed | 按下 | 文字+图标变 `Accent.Nebula` |
| Scanning | `IsScanning` | 文字「停止」+ 图标**旋转动画** + 文字 `State.Warning` |
| Disabled | `!HasProject` | opacity 0.4，文字「请先选择项目」 |

**旋转动画**：1.5s/cycle 无限循环。

**位置**：Gallery 页工具栏右侧（详见 §5.2）。

### C13 VideoBadge

- 位置：缩略图左上角 6px 内边距
- 22×22 圆形 + `Icon.Play` 12×12
- 背景：`#CC0A0E1A`
- 阴影：`0 2 6 0 #80000000`
- 可见性：`IsVisible="{Binding MediaType, Converter={x:Static ...}}"`

### C14 ScanProgressBar

**位置**：MainWindow **标题栏底部**（Row 0 底部），全宽 2px。

**v2.2 实际实现**：
- 位于 MainWindow.axaml 的 Grid Row 0 内
- Bottom 对齐，紧贴标题栏下方
- DataContext 绑定到 `GalleryViewModel`

**状态**：

| 状态 | 表现 |
|------|------|
| 未扫描 | 完全隐藏（`IsVisible=False`） |
| Scanning-Indeterminate | 流动条纹动画 |
| Scanning-Determinate | 按 `Processed/Total` 比例填充 |
| Completed | 100% 停留 200ms → **渐隐**（opacity 动画） |

**下方文字行**（条件显示）：
- 扫描中：「扫描中 123 / 456 · 当前文件：DSC_0001.NEF」
- 完成后：「扫描完成 · 共 456 个文件」

> **注意**：v2.2 ScanProgressBar 在 MainWindow 内，不在 GalleryView 内。这样扫描状态在切换页面时也可见。

### C15 NavRail（左侧导航）

| 属性 | 值 |
|------|-----|
| 宽度 | 80px 固定 |
| 背景 | `Bg.Outer` |
| 右边框 | 1px `Bg.Divider` |
| 高度 | 主内容区全高 |

**v2.2 实现**：使用 Grid 布局
- Row 0：媒体 NavItem（顶部对齐）
- Row 1：设置 NavItem（底部对齐）

**NavItem 视觉**（v2.2）：
- 高度 48px
- **只有文字**，垂直排列
- 水平居中
- Padding 0（文字内边距）

**NavItem 4 状态**（v2.2 实际）：

| 状态 | 表现 |
|------|------|
| 默认 | 透明背景，`Text.Secondary` 文字 |
| Hover | `Bg.Hover` 背景，`Text.Primary` |
| **激活** | `Bg.SurfaceElevated` 背景，`Accent.Stellar` 文字 + **左侧 2px `Accent.Stellar` 边框** |
| Pressed | `Bg.Divider` 背景 |

> **注意**：v2.2 实现只有文字，**没有图标**。如需添加图标，见 §21.1 涉及文件清单。

### C16 FilePathToBitmapConverter（v2.2 实际实现）

> **Avalonia 11 的 `Image.Source` 不接受 `string`**（不像 WPF 自动转）。

**v2.2 实现**（`Converters/FilePathToBitmapConverter.cs`）：

```csharp
public class FilePathToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path)) return null;
        try
        {
            return new Bitmap(File.OpenRead(path));
        }
        catch { return null; }
    }
    
    public object? ConvertBack(...) => throw new NotSupportedException();
}
```

**使用**：
```xml
<Image Source="{Binding ThumbnailPath, Converter={StaticResource FilePathToBitmap}}"
       Stretch="UniformToFill" />
```

> **注意**：v2.2 实现没有 LRU 缓存，直接加载。性能优化可在后续迭代中添加。

---

## 5. 页面规格

### 5.1 MainWindow（整体布局 — v2.2 实际实现）

```
┌──────────────────────────────────────────────────┐
│  [● ● ●]                  星助                    │ 系统标题栏 38px
│  ═══════════════════════════════════════════════ │ ScanProgressBar (2px)
├──────┬───────────────────────────────────────────┤
│      │  [当前页面的工具栏]                        │ ← 每页独立
│      │  ──────────────────────────────────────   │
│ 菜单 │                                            │
│ 栏   │  [当前内容]                                │ ← 每页不同
│      │                                            │
│      │                                            │
│      │                                            │
│      │                                            │
└──────┴────────────────────────────────────────────┘
   80px                    *
```

**外层 Grid**：
- `ColumnDefinitions="80,*"`（两列）
- `RowDefinitions="38,*"`（标题栏 / 主内容）

**三区域职责**：

| 区域 | 位置 | 职责 | 跨页 |
|------|------|------|------|
| **系统标题栏** | Row 0 跨两列 | 窗口标题（星助）+ **ScanProgressBar** | 是 |
| **菜单栏** | Col 0 Row 1 | 页面导航（媒体 / 设置） | 是 |
| **页面工具栏** | Col 1 Row 1 顶部 | **每页独立**的动作 | ❌ 否 |
| **页面内容** | Col 1 Row 1 下半 | 当前页的具体内容 | ❌ 否 |

**v2.2 关键差异**：
- **ScanProgressBar 位于标题栏底部**（不是 GalleryView 内）
- 标题栏内放置进度条，扫描状态全局可见
- Grid.Row 0 内包含：标题文字 + ScanProgressBar（Bottom 对齐）

**GalleryView 工具栏**：
- 高度 **40px**（不是 48px）
- 包含 "Gallery" 标题文字 + 刷新按钮

**时间轴宽度**：
- **180px**（不是 220px）

### 5.2 GalleryView（页面 1：媒体）

**结构**（v2.2 实际实现）：
```
┌─────────────────────────────────────────────┐
│  Gallery    [刷新]                           │  ← 工具栏 40px
├──────────┬──────────────────────────────────┤
│ Timeline │                                  │
│  (180px) │  PhotoGrid                       │
│          │                                  │
└──────────┴──────────────────────────────────┘
```

**布局**（v2.2）：
- 顶部 **40px** 工具栏：左侧 "Gallery" 标题 + 右侧「刷新」按钮
- 下方两列 Grid：**180px** TimelineRail / `*` PhotoGrid
- PhotoGrid 使用 WrapPanel（ItemWidth=160, ItemHeight=120）

**GalleryViewModel 接口**（v2.2 实际实现）：

```csharp
public partial class GalleryViewModel : ObservableObject
{
    // === 数据源 ===
    public ObservableCollection<TimelineEntry> DateGroups { get; }
    public ObservableCollection<MediaFile> CurrentMediaFiles { get; }

    // === 选中态 ===
    [ObservableProperty] private TimelineEntry? _selectedDate;

    // === 加载状态 ===
    [ObservableProperty] private bool _isLoadingDateGroups;
    [ObservableProperty] private bool _isLoadingMedia;
    [ObservableProperty] private string? _loadErrorMessage;

    // === 扫描进度状态 ===
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private ScanProgress? _scanProgress;
    [ObservableProperty] private string? _scanStatusMessage;
    [ObservableProperty] private RefreshState _refreshState = RefreshState.Idle;

    // === 命令 ===
    [RelayCommand] private async Task SelectAsync(TimelineEntry? entry)
    [RelayCommand] private async Task ReloadAsync()
    [RelayCommand] private async Task RefreshAsync()

    // === 生命周期 ===
    Task InitializeAsync()
    private async Task LoadDateAsync(TimelineEntry entry, CancellationToken ct)
}
```

**TimelineEntry 模型**（v2.2）：
```csharp
public partial class TimelineEntry : ObservableObject
{
    public DateTime Date { get; }
    public int PhotoCount { get; }        // 注意：是 PhotoCount 不是 Count
    [ObservableProperty] private bool isSelected;
}
```

**Page Toolbar 规格**（v2.2）：
- 高度 **40px**（不是 48px）
- 背景 `Bg.Surface`，底 1px `Bg.Divider`
- 左侧 "Gallery" 标题文字
- 右侧「刷新」按钮
- 状态：默认 / Hover / Pressed / Scanning（旋转动画）/ Disabled

### 5.3 SettingsView（页面 2：设置）

**结构**：
```
┌─────────────────────────────────────────────┐
│  [Page Toolbar 48px]    [保存] [返回]         │  ← 每页工具栏
├─────────────────────────────────────────────┤
│  [项目]  OSS配置     ← Tab 栏                  │
│  ──────────────────────────────────────────   │
│  FormRow: 项目目录 + Select + 浏览           │
│  FormRow: 主题模式 + Select                  │
│  FormRow: ...                                │
└─────────────────────────────────────────────┘
```

**Page Toolbar 规格**：
- 高度 48px，背景 `Bg.Surface`，底 1px `Bg.Divider`
- 右侧 margin 14px
- 按钮顺序：`[返回] [保存]`（保存靠最右）
- 「保存」按钮：主色（`Accent.Stellar`），启用条件 `IsDirty && !IsSaving`
- 「返回」按钮：次按钮（透明 + 边框），无启用条件

**表单规格**（详见 `specs/settings-page-v1.md`）：
- 通用 Tab：项目目录 / 主题模式
- OSS Tab：厂商（4 选 1）+ Endpoint / Bucket / Access Key / Secret Key / PathPrefix / UseHttps / EnableCdn

**关键规则**：
- 单一启用控制：只 `IsEnabled="{Binding IsDirty}"`
- Save-First 主题：切 theme 不预览，保存才 `ApplyTheme`
- OnSelectedThemeChanged **只**调 `RecomputeDirty`，不预览
- 跨 Tab 状态保持：切 Tab 不丢改动

**SettingsViewModel 接口**（v2.2 实际实现）：

```csharp
public partial class SettingsViewModel : ObservableObject
{
    private readonly IDirectoryPickerService _directoryPicker;
    private readonly IConfigService _configService;
    private string? _lastSavedDirectory;
    private int _lastSavedTheme;  // 0=DeepSpace, 1=RedNight

    [ObservableProperty] private string? selectedProjectDirectory;
    [ObservableProperty] private ObservableCollection<string> recentDirectories;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private int selectedTheme;  // 0 或 1

    [RelayCommand] private async Task BrowseDirectory()
    [RelayCommand] private void SelectRecentDirectory(string? directory)
    [RelayCommand] private void ClearRecentDirectories()
    [RelayCommand] private async Task Save()
    public void DiscardChanges()
}
```

**v2.2 特点**：
- 主题用 `int`（0=DeepSpace, 1=RedNight），不是枚举
- 保存时调用 `ThemeManager.SetTheme()` 应用主题
- 使用快照（`_lastSavedDirectory`, `_lastSavedTheme`）判断 `IsDirty`
- `InitializeAsync()` 最后才标记 `_isInitialized = true`，避免初始化时触发脏检查

### 5.4 MainWindowViewModel（v2.2 实际实现）

```csharp
public enum ViewPage { Gallery, Settings }

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private GalleryViewModel galleryViewModel;
    [ObservableProperty] private SettingsViewModel settingsViewModel;
    [ObservableProperty] private object currentView;
    [ObservableProperty] private string title = "星助";
    [ObservableProperty] private bool isSettingsPage;
    [ObservableProperty] private ViewPage currentPage = ViewPage.Gallery;

    public bool HasProject => !string.IsNullOrEmpty(GalleryViewModel?.ProjectPath);
    public bool IsMediaActive => CurrentPage == ViewPage.Gallery;
    public bool IsSettingsActive => CurrentPage == ViewPage.Settings;

    [RelayCommand] private async Task NavigateToGallery()
    [RelayCommand] private void NavigateToSettings()
    [RelayCommand] private void NavigateToMedia()
    [RelayCommand] private async Task Refresh()
}
```

**v2.2 特点**：
- 直接创建子 ViewModel（不使用 DI 容器）
- 包含 `GalleryViewModel` 和 `SettingsViewModel` 实例
- `CurrentView` 控制 ContentControl 显示哪个页面
- 导航到设置页时检查 `IsDirty`，有未保存修改则弹出确认对话框

---

## 6. 交互流程

### 6.1 启动

```
App 启动
  → MainWindow 加载
  → GalleryView 实例化
  → GalleryViewModel.InitializeAsync()
      1. 读 ProjectConfig.CurrentDirectory
      2. 有值 → 调 GetDateGroupsAsync → DateGroups
      3. 无值 → 显示「请先选择项目」空态
  → 选中 DateGroups[0]
  → 调 GetByDateAsync → CurrentMediaFiles
```

### 6.2 切换日期

```
用户点击 TimelineRail 节点
  → SelectedDate = dateCount
  → OnSelectedDateChanged → LoadDateAsync(SelectedDate)
  → IsLoadingMedia = true
  → 调 GetByDateAsync
  → 填 CurrentMediaFiles
  → IsLoadingMedia = false
```

### 6.3 扫描流程

```
点击刷新按钮
  → IsScanning = true
  → IsIndeterminate = true
  → 递归遍历项目目录（带 CancellationToken）
  → 收集文件列表
  → IsIndeterminate = false, Total = count
  → for each file:
      try:
        若是图片：Bitmap.GenerateAsync
        若是视频：ffmpeg 抽帧
        落库 media_files
        progress.Report(Processed++, CurrentFile)
      catch (not OperationCanceledException):
        计入 Failed，记日志，继续下一个
  → IsScanning = false
  → RefreshState = Completed
  → 触发 GalleryViewModel.ReloadAsync
```

### 6.4 主题切换（Save-First）

```
用户点 RadioButton (Deep Space / Red Night Vision)
  → bool? 属性 setter 触发
  → ViewModel.SelectedTheme = newValue
  → OnSelectedThemeChanged:
      RecomputeDirty()  // 仅此一行
  → 主题颜色**不变**（save-first）
  → IsDirty = true
  → 保存按钮亮

点保存
  → SaveAsync
  → 写 AppConfig
  → _themeService.ApplyTheme(app.Theme)  // 主题此时才应用
  → IsDirty = false
```

### 6.5 取消扫描

```
用户点扫描中按钮（变「停止」）
  → CancelScanCommand
  → CancellationTokenSource.Cancel()
  → 循环检查 token
  → 已扫描文件保留入库
  → IsScanning = false
  → 状态：「已停止，扫描了 X / Y」
```

---

## 7. 状态机

### 7.1 照片同步状态

```
            Upload
NotUploaded ─────────▶ UploadedAndLocal
    ▲                       │
    │ Delete Local          │ Delete Local
    │                       ▼
    └───────── UploadedButMissingLocal

UploadedButMissingLocal ──Download──▶ UploadedAndLocal
```

### 7.2 应用主题状态

```
   DeepSpace ◀────────────┐
       │                  │
       │ User selects     │
       ▼                  │
   RedNightVision ────────┘
       User selects (after save)
```

### 7.3 GalleryView 页面级

```
                            ┌────────────┐
                            │ NoProject  │ ← _projectPath 空
                            └─────┬──────┘
                                  │ 选了项目
                                  ▼
                            ┌────────────┐
              ┌─────────── │ Loading    │
              │            └─────┬──────┘
              │                  │ 完成
              │                  ▼
              │            ┌────────────┐
              │            │ Empty      │ ← DateGroups.Count==0
              │            └─────┬──────┘
              │                  │ 扫描到媒体
              │                  ▼
              │            ┌────────────┐
              ├─────────── │ Loaded     │
              │            └─────┬──────┘
              │                  │ 扫描完成
              │                  │ → ReloadAsync
              │                  ▼
              │            (回到 Loaded/Empty)
              │
              │ 任意时刻出错
              ▼
    ┌────────────┐
    │ Error      │ ← LoadErrorMessage 非空
    └────────────┘
              │ 重试
              ▼
        (回到 Loading)
```

### 7.4 媒体项状态

```
[IsUploaded, LocalExists]
  [true,  true]  → 已上传且本地有（云图标，State.Success）
  [true,  false] → 已上传但本地缺（警告图标，State.Warning）
  [false, *]     → 未上传（禁云图标，State.Quiet）
```

### 7.5 扫描状态

```
Idle → Scanning → (Completed | Stopping → Idle | Failed → Idle)
```

### 7.6 当前页状态

```
Gallery ⇄ Settings   (NavItem 激活态自动响应 CurrentPage)
```

---

## 8. 主题模式

### 8.1 Deep Space（默认）

按 §3.1.1 token 实施。

### 8.2 Red Night Vision

按 §3.1.2 token 实施。

### 8.3 切换实现

- **App 启动**：读 AppConfig.Theme → 调 `IThemeService.ApplyTheme`
- **运行时切换**（用户保存后）：见 §6.4
- 切换机制：替换 `MergedDictionaries` 中的主题字典

### 8.4 主题服务接口

```csharp
public interface IThemeService
{
    ThemeMode CurrentTheme { get; }
    void ApplyTheme(ThemeMode mode);
    event EventHandler<ThemeMode>? ThemeChanged;
}
```

---

## 9. 跨平台规范

| 平台 | 注意事项 |
|------|---------|
| **macOS** | 交通灯使用系统原生（`ExtendClientAreaChromeHints="PreferSystemChrome"`）；菜单栏走 macOS 主菜单；字体优先 PingFang SC |
| **Windows 11** | 可选启用 Mica 效果；字体优先 Microsoft YaHei UI |
| **Linux** | 测试 GTK 与 XWayland 表现；字体优先 Noto Sans CJK SC |

### 跨平台目录分隔符

**必须**用 `Path.Combine`，**禁止**字符串拼接。

### ffmpeg 跨平台

- 路径解析顺序：`STARHELPER_FFMPEG_PATH` → `FFMPEG_PATH` → `FFMPEG_HOME/bin/ffmpeg` → 系统 PATH
- 三平台走系统 PATH 查找

---

## 10. 可访问性（A11y）

- 所有交互元素必须支持键盘导航（Tab / Shift+Tab / Enter / Esc）
- 图片缩略图必须有 `AutomationProperties.Name`
- 颜色对比度：文字与背景至少 4.5:1（AA 级）
- 不依赖单一颜色传达状态（图标 + 文字 + 颜色三重提示）
- 提供「减少动效」系统设置时，所有 Duration 缩短为 50%

---

## 11. 验收标准（DoD）

### 11.1 视觉

- [ ] §3 所有 token 全部实现并在 `Colors.axaml` 中可查
- [ ] §4 15 个组件全部有自定义样式
- [ ] §5 三个页面布局与文档完全一致
- [ ] §8 两种主题可运行时切换，无残留样式

### 11.2 行为

- [ ] §6 五条核心交互流程跑通
- [ ] §7 状态机覆盖所有状态转换
- [ ] 主题切换不预览，保存后才 ApplyTheme
- [ ] 扫描可取消，已扫描结果保留

### 11.3 跨平台

- [ ] macOS / Windows / Linux 三个平台均可启动并运行
- [ ] 文件夹选择器走系统原生
- [ ] ffmpeg 跨平台定位

### 11.4 数据

- [ ] §15 持久化全部实现（4 个配置分区、UPSERT、缓存）
- [ ] §17 media_files 表 + 索引齐全
- [ ] §18 GalleryViewModel 接 Repository
- [ ] 关闭重开数据恢复

### 11.5 铁律

- [ ] §20 所有跨模块规则被遵守
- [ ] 字体、间距、圆角全部走 token，禁止硬编码
- [ ] 单元测试覆盖核心服务（SqliteConfigService + MediaScanner + ThumbnailGenerator）

### 11.6 A11y

- [ ] §10 A11y 基础要求达成
- [ ] 所有交互元素在悬停 / 聚焦 / 按下时都有视觉反馈

---

## 12. 技术约束

### 12.1 必须使用

- Avalonia UI 11.x
- C# 12+
- .NET 8
- CommunityToolkit.Mvvm（或 ReactiveUI）
- Lucide Icons（用 StreamGeometry 实现）
- **Microsoft.Data.Sqlite**（数据持久化唯一依赖）
- `SixLabors.ImageSharp`（图片缩略图生成）

### 12.2 禁止

- ❌ 硬编码颜色（必须用 token）
- ❌ 硬编码间距 / 圆角 / 字号
- ❌ 在 XAML 中写业务逻辑
- ❌ 使用 `Code Behind` 处理 UI 逻辑（除初始化）
- ❌ 引入未在本文档列出的第三方 UI 库
- ❌ JSON / INI / XML 文件存配置
- ❌ 注册表 / plist / Keychain 直存
- ❌ EntityFramework Core / Dapper / 任何 ORM
- ❌ 除 SQLite 外的任何数据库引擎
- ❌ `PathGeometry` 图标（用 `StreamGeometry`）
- ❌ `Image.Source` 直接绑 `string` 路径（用 `StringToBitmapConverter`）
- ❌ Flyout 宽度用 `MinWidth` 绑定（用代码后置同步）
- ❌ `EnumToBoolConverter` 双向绑定 RadioButton（用 `bool?` 属性）
- ❌ 扫描循环不 try/catch（**单文件失败不能让 App 崩**）
- ❌ 业务数据塞进 `config` 表 JSON（用独立表）
- ❌ 全局工具栏（**应每页自带**）
- ❌ 工具栏跨页复用（不同页动作不同）
- ❌ 工具栏放「媒体」标签（设计冗余）
- ❌ 标题栏塞按钮

---

## 13. 项目结构（v2.2 实际实现）

```
StartTooler/                          ← 解决方案根目录
├── StartTooler.sln
├── StartTooler/                      ← 单项目（不是 src/ 子项目）
│   ├── Program.cs
│   ├── StartTooler.csproj
│   ├── App.axaml / App.axaml.cs
│   ├── Views/
│   │   ├── MainWindow.axaml / .cs
│   │   ├── GalleryView.axaml / .cs
│   │   └── SettingsView.axaml / .cs
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs
│   │   ├── GalleryViewModel.cs
│   │   └── SettingsViewModel.cs
│   ├── Controls/
│   │   ├── NavRail.axaml / .cs
│   │   └── StatusLegend.axaml / .cs
│   ├── Components/
│   │   └── ScanProgressBar.axaml / .cs   (C14)
│   ├── Converters/
│   │   ├── FilePathToBitmapConverter.cs
│   │   ├── MediaFileConverters.cs
│   │   ├── MediaTypeConverters.cs
│   │   ├── RefreshStateConverters.cs
│   │   ├── ScanProgressConverters.cs
│   │   ├── SettingsConverters.cs
│   │   ├── SyncStatusConverters.cs
│   │   └── TimelineBoolConverters.cs
│   ├── Services/
│   │   ├── ConfigService.cs
│   │   ├── IConfigService.cs
│   │   ├── DirectoryPickerService.cs
│   │   ├── IDirectoryPickerService.cs
│   │   ├── ThemeManager.cs
│   │   ├── ThumbnailService.cs
│   │   ├── ImageCacheService.cs
│   │   ├── AppConfig.cs
│   │   ├── ProjectConfig.cs
│   │   └── ConfigKeys.cs
│   ├── Data/
│   │   ├── MediaFile.cs
│   │   ├── MediaType.cs
│   │   ├── DateCount.cs
│   │   ├── IMediaRepository.cs
│   │   └── MediaRepository.cs
│   ├── Models/
│   │   └── Models.cs                   (TimelineEntry, SyncStatus, RefreshState, ScanProgress)
│   ├── Themes/
│   │   ├── Colors.axaml
│   │   ├── Icons.axaml
│   │   ├── Styles.axaml
│   │   └── RedNightVision.axaml
│   └── Assets/
│       └── ...
├── dist/
├── publish/
└── obj/
```

> **注意**：v2.2 使用单项目结构，所有代码在同一目录下。

---

## 14. 实施优先级

1. **数据层**（§15）：IConfigService + SqliteConfigService + 4 个 POCO + 单元测试
2. **服务层**：IThemeService / IDirectoryPickerService / IFfmpegLocator
3. **媒体层**（§17）：MediaFile 模型 + MediaRepository（DDL + 索引）+ IMediaScanner + IThumbnailGenerator
4. **主题系统**：所有 token 落到 ResourceDictionary
5. **MainWindow 框架**（§5.1）：三区域模式（标题栏 / 菜单栏 / 主内容）
6. **C1 MacChrome + C15 NavRail**
7. **每页工具栏**（Gallery: 刷新 / Settings: 保存+返回）
8. **MainWindowViewModel + 页面导航**
9. **GalleryView + GalleryViewModel**（§5.2，含 Page Toolbar + Converter）
10. **SettingsView + SettingsViewModel**（§5.3，含 Page Toolbar + OSS Tab）
11. **主题切换集成**（启动时 + 保存后）
12. **单元测试**（数据层 + 媒体层 + 服务层）
13. **A11y 收尾**

---

## 15. 数据持久化（SQLite 设置层）

### 15.1 范围限定

> 本节定义**设置（Settings）**的存储方案。
> **业务数据**走独立关系表，详见 §17。

### 15.2 存储选型

**SQLite**，单文件本地数据库。

**禁止**：JSON / INI / 注册表 / plist / EF Core / Dapper / 任何 ORM。

### 15.3 表结构（设置表，唯一一张）

**v2.2 实际实现**：

```sql
CREATE TABLE IF NOT EXISTS Config (
    Key        TEXT PRIMARY KEY,
    Value      TEXT NOT NULL,           -- JSON
    UpdatedAt  TEXT NOT NULL            -- ISO 8601 UTC
);
```

> **注意**：v2.2 表名为 `Config`（大写），没有 `version` 字段。

> ⚠️ **铁律**：无论未来增加多少配置分区，**始终只有这一张 `config` 表**。

### 15.4 四个配置分区

| Key | 类型 | 字段 |
|------|------|------|
| `app` | `AppConfig` | Theme / Language / HardwareAcceleration |
| `project` | `ProjectConfig` | CurrentDirectory / RecentDirectories / AutoSync / ConflictPolicy |
| `oss` | `OssConfig` | Endpoint / Bucket / AccessKeyId / AccessKeySecret / ... |
| `window` | `WindowConfig` | Width / Height / X / Y / IsMaximized / LastView |

### 15.5 服务接口

```csharp
public interface IConfigService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;
    Task SetAsync<T>(string key, T value, CancellationToken ct = default) where T : class;
    Task<bool> ContainsAsync(string key, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAllKeysAsync(CancellationToken ct = default);
    Task ReloadAsync(CancellationToken ct = default);
}
```

### 15.6 实现约束

- 写入用 UPSERT（`ON CONFLICT(key) DO UPDATE SET version = version + 1`）
- 内存缓存 `ConcurrentDictionary`
- 启动时全量加载
- 写时同步更新缓存

### 15.7 数据库路径

**v2.2 实际实现**：统一使用 `Environment.SpecialFolder.ApplicationData`

```csharp
// C# 实现
var appDataPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "StartTooler");
var dbPath = Path.Combine(appDataPath, "config.db");
```

| 平台 | 实际路径 |
|------|---------|
| macOS | `~/Library/Application Support/StartTooler/config.db` |
| Windows | `%APPDATA%\StartTooler\config.db` |
| Linux | `~/.config/StartTooler/config.db` |

> **注意**：v2.2 使用 `ApplicationData`（漫游），不是 `LocalApplicationData`。

### 15.8 敏感字段

- `OssConfig.AccessKeySecret` **必须**加密存储
- 加密方案（v1.2）：AES-256-GCM + 机器指纹
- v1.0 可临时明文存，但 DB 文件权限必须 0600

### 15.9 性能

| 操作 | 目标 |
|------|------|
| 启动加载 | < 50ms |
| 读命中缓存 | < 1ms |
| 单次写 | < 5ms |
| 配置文件大小 | < 100KB |

### 15.10 单元测试（必跑 5 个）

1. RoundTrip（Set → Get 一致）
2. GetNonExistent（返回 null）
3. Delete（删除后 Contains=false）
4. CacheSurvives（多次读不重复查 DB）
5. Reload（外部修改后 ReloadAsync 拉到新值）

### 15.11 DI 注册

```csharp
services.AddSqliteConfig();  // 默认路径
```

### 15.12 调试

```bash
sqlite3 ~/Library/Application\ Support/StarHelper/config.db
sqlite> .schema config
sqlite> SELECT key, length(value), updated_at, version FROM config;
```

### 15.13 数据 vs 设置（范围边界）

> **关键**：「单表 + JSON」是**设置层**专用。**业务数据必须走独立表**。

#### 数据类型分流

| 数据类型 | 存储方式 |
|---------|---------|
| 用户偏好 / OSS / 窗口 / MRU | `config.*` JSON |
| 照片元数据 | 独立表 `media_files` |
| 缩略图缓存 | 独立表 `thumbnails` |
| 时间轴索引 | 派生自 `media_files.shot_at` |
| 上传任务 | 独立表 `upload_jobs` |
| EXIF 缓存 | 独立表 `exif_cache` |

#### 反模式黑名单

- ❌ 把照片列表塞进 `config.photos` JSON
- ❌ 用 `config.thumbnails` 存 base64
- ❌ 把上传任务塞进 `config.jobs` JSON
- ❌ 用一张 `kv_store` 表代替所有业务表

---

## 16. 文件 IO 与 ffmpeg 集成

### 16.1 文件夹选择器

```csharp
public interface IDirectoryPickerService
{
    Task<string?> PickFolderAsync(string title = "选择文件夹");
}
```

基于 Avalonia `IStorageProvider.OpenFolderPickerAsync`，三平台走系统原生。

### 16.2 缩略图缓存目录

```
%LocalAppData%/StarHelper/
├── config.db                ← 设置库
├── thumbnails/              ← 缩略图缓存
│   └── {hash}.jpg
└── logs/
```

命名：SHA256(absPath).Substring(0, 16).jpg

### 16.3 缩略图生成

**图片**：`Bitmap.DecodeToWidth(stream, 320)`，JPEG 85%。

**视频**：调 ffmpeg 抽帧：
```
ffmpeg -i {input} -ss 00:00:01 -vframes 1 -vf scale=320:-1 {output} -y
```

- 超时 30s
- 失败 → 用 StarOutline 占位
- Process 必须 `WaitForExit(timeout)` 防卡死
- 必须 `try/catch` + `Kill(entireProcessTree: true)`

### 16.4 ffmpeg 路径解析

```csharp
public interface IFfmpegLocator
{
    string? Locate();
}
```

解析顺序：
1. `STARHELPER_FFMPEG_PATH` 环境变量
2. `FFMPEG_PATH` 环境变量
3. `FFMPEG_HOME/bin/ffmpeg`
4. 系统 PATH 查找
5. 找不到 → 视频用 StarOutline 占位

---

## 17. 媒体索引与扫描（业务数据层）

### 17.1 media_files 表（业务数据，**独立表**）

```sql
CREATE TABLE media_files (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    project_path    TEXT    NOT NULL,
    relative_path   TEXT    NOT NULL,
    file_name       TEXT    NOT NULL,
    media_type      TEXT    NOT NULL CHECK(media_type IN ('image', 'video')),
    file_size       INTEGER NOT NULL,
    last_modified   INTEGER NOT NULL,                          -- Unix 秒
    shot_at         INTEGER,                                    -- EXIF 拍摄时间
    is_uploaded     INTEGER NOT NULL DEFAULT 0,
    local_exists    INTEGER NOT NULL DEFAULT 1,
    thumbnail_path  TEXT,
    remote_url      TEXT,
    uploaded_at     INTEGER,
    scanned_at      INTEGER NOT NULL,
    UNIQUE(project_path, relative_path)
);

CREATE INDEX idx_media_project ON media_files(project_path);
CREATE INDEX idx_media_shot_at ON media_files(shot_at);
CREATE INDEX idx_media_uploaded ON media_files(is_uploaded);
CREATE INDEX idx_media_local ON media_files(local_exists);
CREATE INDEX idx_media_type ON media_files(media_type);
```

### 17.2 MediaFile 模型

```csharp
public sealed class MediaFile
{
    public long Id { get; set; }
    public string ProjectPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public MediaType MediaType { get; set; }
    public long FileSize { get; set; }
    public long LastModified { get; set; }
    public long? ShotAt { get; set; }
    public bool IsUploaded { get; set; }
    public bool LocalExists { get; set; } = true;
    public string? ThumbnailPath { get; set; }
    public string? RemoteUrl { get; set; }
    public long? UploadedAt { get; set; }
    public long ScannedAt { get; set; }
}

public enum MediaType { Image, Video }
```

### 17.3 DateCount 模型

```csharp
public sealed class DateCount
{
    public DateTime Date { get; init; }
    public int Count { get; init; }
}
```

### 17.4 IMediaRepository

```csharp
public interface IMediaRepository
{
    Task UpsertAsync(MediaFile file, CancellationToken ct = default);
    Task BulkUpsertAsync(IEnumerable<MediaFile> files, CancellationToken ct = default);
    Task MarkMissingAsync(string projectPath, IEnumerable<string> existingRelativePaths, CancellationToken ct = default);
    Task<IReadOnlyList<MediaFile>> GetByDateAsync(string projectPath, DateTime date, CancellationToken ct = default);
    Task<IReadOnlyList<DateCount>> GetDateGroupsAsync(string projectPath, CancellationToken ct = default);
}
```

**实现位置**：`Core/Data/MediaRepository.cs`
**走独立 SqliteConnection**（不与 `SqliteConfigService` 共享）

### 17.5 IMediaScanner

```csharp
public interface IMediaScanner
{
    Task<ScanResult> ScanAsync(
        string projectPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ScanResult
{
    public int TotalFound { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int MarkedMissing { get; init; }
    public int Failed { get; init; }
    public TimeSpan Elapsed { get; init; }
}
```

### 17.6 IThumbnailGenerator

```csharp
public interface IThumbnailGenerator
{
    Task<string?> GenerateAsync(string absolutePath, MediaType type, CancellationToken ct = default);
    string? FindExisting(string absolutePath);
}
```

### 17.7 ScanProgress

```csharp
public sealed class ScanProgress
{
    public int Total { get; set; }              // 0 = indeterminate
    public int Processed { get; set; }
    public int Failed { get; set; }
    public string? CurrentFile { get; set; }
}

public enum RefreshState { Idle, Scanning, Stopping, Completed, Failed }
```

### 17.8 扫描流程（带故障隔离）

```
点击刷新
  → IsScanning = true
  → 遍历目录（带 CancellationToken）
  → 收集文件列表
  → for each file:
      try:
        生成缩略图（图片 / ffmpeg 抽视频）
        落库 UPSERT
        progress.Report
      catch (not OperationCanceledException):
        _logger.Warn(ex, "Failed: {File}", file)
        Failed++
        // 不 throw，继续
  → IsScanning = false
  → RefreshState = Completed
  → 触发 GalleryViewModel.ReloadAsync
```

### 17.9 支持的文件扩展名

**图片**：`.jpg .jpeg .png .webp .tif .tiff .bmp .heic .cr2 .cr3 .nef .arw .dng .raf .orf .rw2 .pef`

**视频**：`.mp4 .mov .avi .mkv .webm .m4v .mpg .mpeg`

### 17.10 错误处理

| 场景 | 行为 |
|------|------|
| 文件锁住 | skip + 计入 Failed + 记日志 |
| EXIF 解析失败 | `shot_at` NULL，回退 `last_modified` |
| ffmpeg 找不到 | 视频用 StarOutline 占位 + 警告日志 |
| ffmpeg 超时 | skip + 计入 Failed |
| 数据库写入失败 | 中止扫描 + 回滚已写事务 |
| 用户取消 | 保留已扫描结果 + 显示「已停止 X/Y」 |

### 17.11 性能

| 指标 | 目标 |
|------|------|
| 100 张图片扫描 + 缩略图 | < 30s |
| 1000 张图片扫描 | < 3min（首次），< 10s（增量） |
| 单张图片缩略图 | < 500ms |
| 单张视频缩略图 | < 5s |
| 10000 行查询 | < 50ms |

---

## 18. 界面数据接入

### 18.1 改造范围

- **删除**：`Photo` / `SyncStatus` 模型
- **改造**：`MediaFileTile` 绑 `MediaFile`
- **改造**：`GalleryViewModel` 接 `IMediaRepository`
- **改造**：TimelineRail 接 `GetDateGroupsAsync`
- **改造**：PhotoGrid 接 `GetByDateAsync`

### 18.2 GalleryViewModel 状态机（见 §7.3）

```csharp
[ObservableProperty] DateCount? _selectedDate;
[ObservableProperty] bool _isLoadingDateGroups;
[ObservableProperty] bool _isLoadingMedia;
[ObservableProperty] string? _loadErrorMessage;
[ObservableProperty] string? _projectPath;

public bool IsEmpty => !IsLoadingDateGroups && DateGroups.Count == 0;
public bool HasNoProject => string.IsNullOrEmpty(ProjectPath);
```

### 18.3 初始化流程

```
InitializeAsync:
  1. 读 ProjectConfig.CurrentDirectory
  2. 空 → DateGroups.Clear()，显示「请先选择项目」空态
  3. 非空 → 调 GetDateGroupsAsync
  4. Count==0 → 显示「未发现媒体」空态
  5. Count>0 → 选 DateGroups[0] → 触发 LoadDateAsync
```

### 18.4 切换日期

```
OnSelectedDateChanged:
  → LoadDateAsync(SelectedDate)
  → IsLoadingMedia = true
  → 清空 CurrentMediaFiles
  → 调 GetByDateAsync
  → 填 CurrentMediaFiles
  → IsLoadingMedia = false
```

### 18.5 扫描完成自动刷新

```
OnRefreshStateChanged(value):
  if (value == RefreshState.Completed)
    ReloadAsync:
      保存当前 SelectedDate
      重新调 GetDateGroupsAsync
      若旧日期仍存在 → 保持选中 + 调 GetByDateAsync
      若不存在 → 选最新日期
      若全空 → 清空 + 显示空态
```

### 18.6 状态展示

| 场景 | 表现 |
|------|------|
| 无项目目录 | 「请先在设置中选择项目目录」 |
| 无媒体 | 「未发现媒体文件，请点刷新扫描」 |
| 选中日期无文件 | 「该日期无媒体」 |
| 加载中 | 骨架屏（3 行 placeholder / 12 个占位 tile） |
| DB 失败 | Toast「加载失败」+ 重试按钮 |

### 18.7 持久化恢复

- 关闭重开 App，TimelineRail 和 PhotoGrid **从 DB 加载**（不重扫）
- 不需要重新扫描就能看到上次扫描结果

---

## 19. 已知陷阱（沉淀复用）

### 19.1 PathGeometry 图标显示成横条

**症状**：图标显示成彩色短横条。

**根因**：`PathGeometry` Bounds 计算延迟，`Stretch="Uniform"` 缩放压扁。

**修复**：改用 `StreamGeometry`。

### 19.2 主题实时预览破坏 save-first

**症状**：切 radio 立即换肤 + 保存按钮不亮。

**根因**：`OnSelectedThemeChanged` 既预览又调脏检查，或重复签名冲突。

**修复**：
- 主题切换**只**调 `RecomputeDirty()`
- 预览**只**在 `SaveAsync` 成功后调
- 合并 `OnSelectedThemeChanged` 为单段 partial

### 19.3 双启用控制（IsEnabled + CanExecute）

**症状**：IsDirty 显示正确但保存按钮禁用。

**根因**：`[RelayCommand(CanExecute = nameof(CanSave))]` + `IsEnabled` 双控。

**修复**：
- 只用 XAML `IsEnabled="{Binding IsDirty}"`
- 删 `[RelayCommand]` 的 `CanExecute` 参数
- 删 `OnIsDirtyChanged.NotifyCanExecuteChanged`

### 19.4 RadioButton + Enum 双向绑定失效

**症状**：radio 视觉切换但 ViewModel 字段不变。

**根因**：`EnumToBoolConverter` 双向绑定 + `GroupName` 在 Avalonia 11 行为不稳定。

**修复**：用 `bool?` 属性绕开：

```csharp
public bool? IsRedNightVisionSelected
{
    get => SelectedTheme == ThemeMode.RedNightVision;
    set { if (value == true) SelectedTheme = ThemeMode.RedNightVision; }
}
```

```xml
<RadioButton IsChecked="{Binding IsRedNightVisionSelected}" />
```

### 19.5 Flyout 宽度不对齐

**症状**：Flyout 弹出后宽度 ≠ 按钮宽度。

**根因**：`$parent[Button].Bounds.Width` 绑定时序不可靠。

**修复**：FlyoutOpened 事件 + 代码后置同步 `Width`：

```csharp
private void OnFlyoutOpened(object? s, EventArgs e)
{
    if (Flyout is PopupFlyoutBase pf && pf.Popup is Popup popup)
    {
        popup.Width = Bounds.Width;
    }
}
```

### 19.6 Image.Source 不接受 string

**症状**：缩略图绑了路径但不显示。

**根因**：Avalonia 11 `Image.Source` 只接受 `IImage` / `Uri`（不像 WPF 自动转）。

**修复**：用 `StringToBitmapConverter`（C16）。

### 19.7 扫描循环不 try/catch 导致 App 崩

**症状**：扫描跑到一半，stack trace 漏到窗口外。

**根因**：单个坏文件抛未捕获异常。

**修复**：扫描循环必须 try/catch，**单文件失败不能让 App 崩**：

```csharp
foreach (var file in files)
{
    if (ct.IsCancellationRequested) break;
    try
    {
        await ProcessOneAsync(file, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        _logger.Warn(ex, "Failed: {File}", file);
        Failed++;
    }
}
```

### 19.8 日期比较时区错位

**症状**：TimelineRail 看到日期但 GetByDateAsync 返回 0 行。

**根因**：DB 存 UTC 秒，查询未考虑本地时区。

**修复**：
- 存 `shot_at` 用 UTC 秒
- 查用 `date(shot_at, 'unixepoch', 'localtime')`
- C# 端 `DateTimeKind.Local`

### 19.9 工具栏「媒体」标签是设计冗余

**症状**：原图里工具栏左侧有「媒体」标签 + 右侧「刷新」。

**根因**：原型师画蛇添足，与 NavRail 的「媒体」重复。

**修复**：**只**保留「刷新」，删除「媒体」标签。

---

## 20. 跨模块规则（铁律级）

### 20.1 颜色与样式

- ❌ 禁止硬编码颜色
- ✅ 必须用 token
- ❌ 禁止在 XAML 中写业务逻辑
- ✅ 业务逻辑全部进 ViewModel

### 20.2 状态管理

- ✅ **单一启用控制**：只 XAML `IsEnabled` 绑定
- ❌ **禁止双控**：`IsEnabled` + `[RelayCommand(CanExecute = ...)]` 同存
- ✅ `IsDirty` 由 `RecomputeDirty()` 计算，setter 加变化判断
- ❌ 禁止 partial 方法重复签名（合并成一段）
- ✅ 主题切换**不预览**，保存后才 `ApplyTheme`
- ✅ 加载完成才拍快照，避免「立即脏」

### 20.3 数据持久化

- ❌ 禁止 JSON/INI/XML 存配置
- ❌ 禁止注册表/plist/Keychain 直存
- ❌ 禁止 EF Core / Dapper / 任何 ORM
- ✅ 设置走 `IConfigService`（SQLite 单表 JSON）
- ✅ 业务数据走独立表

### 20.4 Flyout 对齐

- ❌ 禁止 `MinWidth` 绑定 `$parent[Button].Bounds.Width`（不可靠）
- ✅ Flyout 宽度 = FlyoutOpened 事件 + 代码后置同步

### 20.5 RadioButton 绑定

- ❌ 禁止 `EnumToBoolConverter` 双向绑定（不稳定）
- ✅ 用 `bool?` 属性绕开 Converter

### 20.6 图标

- ❌ 禁止用 `PathGeometry`（Bounds 计算延迟，缩放出问题）
- ✅ 用 `StreamGeometry`
- ✅ 用 `PathIcon` 内置控件优先

### 20.7 Image.Source

- ❌ 禁止直接绑 `string` 路径（Avalonia 11 不支持）
- ✅ 用 `StringToBitmapConverter` 桥接

### 20.8 故障隔离

- ✅ 扫描循环必须 try/catch
- ❌ 单文件失败不能让 App 崩
- ✅ ffmpeg Process 必须 `WaitForExit(timeout)` + `Kill` 兜底
- ✅ 所有 IO 调用都要捕获异常

### 20.9 布局职责（三区域模式）

**区域划分**：
- ✅ **标题栏** = 仅窗口标题（星助）
- ✅ **菜单栏**（NavRail）= 仅页面导航
- ✅ **页面工具栏** = **每页独立**，放当前页的动作按钮
- ✅ **页面内容** = 当前页的具体内容

**禁止**：
- ❌ 标题栏放按钮
- ❌ 全局工具栏（应**每页**自带）
- ❌ 菜单栏放动作按钮
- ❌ 工具栏放「媒体」标签（设计冗余，§19.9） |
- ❌ 工具栏跨页复用（不同页动作不同）

---

## 21. 涉及文件清单

### 21.1 新增（v2.1 范围内）

| 文件 | 角色 |
|------|------|
| `App/Controls/NavRail.axaml` + `.cs` | C15 |
| `App/Controls/RefreshButton.axaml` + `.cs` | C12 |
| `App/Controls/ScanProgressBar.axaml` + `.cs` | C14 |
| `App/Converters/StringToBitmapConverter.cs` | C16 |
| `App/Converters/MediaTypeConverters.cs` | Video/Image 转换 |
| `App/Converters/MediaFileConverters.cs` | 状态徽章三态 |
| `App/ViewModels/MainWindowViewModel.cs` | 页面导航 |
| `App/Services/IMediaScanner.cs` + `MediaScanner.cs` | 扫描服务 |
| `App/Services/IThumbnailGenerator.cs` + `ThumbnailGenerator.cs` | 缩略图服务 |
| `App/Services/IFfmpegLocator.cs` + `FfmpegLocator.cs` | ffmpeg 路径 |
| `Core/Data/MediaFile.cs` + `MediaType.cs` | 业务模型 |
| `Core/Data/DateCount.cs` | 时间轴聚合 |
| `Core/Data/IMediaRepository.cs` + `MediaRepository.cs` | 业务数据访问 |

### 21.2 修改

| 文件 | 改动 |
|------|------|
| `App/Views/MainWindow.axaml` | **大改**：三层布局（80,* × 38,48,*），加 NavRail + 工具栏，删标题栏按钮 |
| `App/Views/GalleryView.axaml` | 接 MediaFile + 空态 |
| `App/Views/SettingsView.axaml` | 项目目录 + 主题模式 |
| `App/ViewModels/GalleryViewModel.cs` | 完全重写，接 Repository + 状态机 |
| `App/ViewModels/SettingsViewModel.cs` | 单一启用控制 + Save-First 主题 |
| `Core/Config/SqliteConfigService.cs` | 已有 |
| `App.axaml.cs` | 启动时 ThemeManager.SetTheme |
| `Program.cs` | 创建 MainWindow |
| `App.axaml` | 注册 Styles + Colors + Icons 资源 |

### 21.3 删除

| 文件 | 原因 |
|------|------|
| `Core/Models/Photo.cs` | 替换为 MediaFile |
| `Core/Models/SyncStatus.cs` | 仅作 UI 内部枚举，不入模型层 |
| `App/Converters/StatusToIconConverter.cs` | 重建为 MediaFile 版本 |
| `App/Converters/StatusToColorConverter.cs` | 同上 |
| `App/Converters/StatusToTooltipConverter.cs` | 同上 |
| `App/Converters/CountConverters.cs` | 不再需要 |
| 标题栏里所有按钮 XAML | 移到工具栏 + NavRail |
| 右侧图例区 XAML | 原图这版没有 |

---

## 22. 跨文档引用

- **配置模块详情**：`requirements/configuration-module.md`
- **项目目录设置 brief**：`briefs/project-directory-setting.md`
- **主窗口布局 brief**：`briefs/main-window-layout-v1.md`
- **程序美术 Skill**：`.skills/program-artist/SKILL.md`
- **需求总目录**：`REQUIREMENTS.md`

---

**End of Spec v2.2**

> 本规格是 v2.2，基于实际实现更新。
> v2.2 关键修正：反映单项目结构、ScanProgressBar 位置、NavRail 纯文字实现等实际细节。
> 实现 AI 只需严格按照本文档执行，无需追问设计意图。
> **所有视觉决策、所有行为约束、所有架构铁律已闭环。**

---

## 附录 A：v2.2 变更汇总

### 设计决策

| 项目 | 原规格 | v2.2 实际 |
|------|--------|-----------|
| 项目结构 | src/ 子项目 | 单项目 |
| .NET 版本 | .NET 8 | .NET 9 |
| ScanProgressBar | GalleryView 内 | MainWindow 标题栏 |
| 工具栏高度 | 48px | **40px** |
| 时间轴宽度 | 220px | **180px** |
| NavRail | 图标+文字 | **只有文字** |
| TimelineEntry.Count | Count | **PhotoCount** |
| 数据库表名 | config | **Config** |
| 数据库路径 | ~/Library/Application Support/ | **Environment.SpecialFolder.ApplicationData** |
| Theme 存储 | ThemeMode 枚举 | **int (0/1)** |

### 未实现项（待后续迭代）

| 项目 | 说明 |
|------|------|
| C12 RefreshButton 5 状态 | 目前 Hover/Pressed 样式未完全实现 |
| LRU 缩略图缓存 | FilePathToBitmapConverter 未实现缓存 |
| 主题切换预览 | 尚未实现（Save-First 生效） |
| OSS 配置 Tab | SettingsView 只有「项目」Tab，OSS Tab 未实现 |
| 单元测试 | 未编写 |
| 批量上传 / 删除 | 当前仅 UI 反馈（Toast），不执行真实 IO（v2.3 决定） |
| 右键菜单 | 当前未实现（v2.3 规划中） |
| 真实媒体扫描 | 扫描逻辑待实现 |
| ffmpeg 视频缩略图 | 视频处理待实现 |

---

## 23. v2.3 实现约束（基于原型图）

### 23.1 全局工具栏（修正 v2.2）

**位置**：MainWindow Row 0 标题栏**下方**，Row 1 主内容**上方**。

```
┌──────────────────────────────────────────────────┐
│  [● ● ●]                  星助                    │ 系统标题栏 38px
│  ═══════════════════════════════════════════════ │ ScanProgressBar (2px)
├──────────────────────────────────────────────────┤
│  [多选] [批量上传] [删除]            [刷新]       │ 全局工具栏 40px
├──────┬───────────────────────────────────────────┤
│      │  [当前内容]                                │
│ Nav  │                                            │
│ Rail │                                            │
└──────┴────────────────────────────────────────────┘
```

**外层 Grid**（v2.3）：
```xml
<Grid ColumnDefinitions="80,*"
      RowDefinitions="38,40,*">
    <!-- Row 0: 标题栏 (跨两列) -->
    <!-- Row 1: 工具栏 (跨两列) -->
    <!-- Row 2: 主内容 (Col 1) + NavRail (Col 0) -->
</Grid>
```

### 23.2 工具栏按钮 + 显隐规则

工具栏含 4 个按钮 + 1 个右侧刷新按钮。

| 按钮 | 默认显隐 | 多选模式显隐 | 说明 |
|------|----------|--------------|------|
| **多选** | ✅ 显示 | ❌ 隐藏 | 点击进入多选模式 |
| **取消多选** | ❌ 隐藏 | ✅ 显示 | 点击退出多选模式 |
| **批量上传** | ❌ 隐藏 | ✅ 显示 | 选中≥1项时可点 |
| **删除** | ❌ 隐藏 | ✅ 显示 | 选中≥1项时可点 |
| **刷新** | ✅ 显示 | ✅ 显示 | 始终在右侧 |

**铁律**：
> 非多选模式不显示「批量上传」「删除」按钮（v2.3 用户决定）

### 23.3 多选模式

**状态机**：

```
            点击「多选」              点击「取消多选」
Normal ──────────────────▶ Multi-Select ────────────▶ Normal
   │                            │
   │                            │ 点击「批量上传」或「删除」
   │                            ▼
   │                       执行操作 + 弹 Toast
   │                            │
   │                            ▼
   │                       自动退出多选
   │                            │
   │                            ▼
   └───────────────────────── Normal
```

**ViewModel 状态**：

```csharp
[ObservableProperty] private bool _isMultiSelectMode;
[ObservableProperty] private ObservableCollection<MediaFile> _selectedFiles;

// 计算属性
public bool IsBatchActionEnabled => IsMultiSelectMode && SelectedFiles.Count > 0;
public int SelectedCount => SelectedFiles.Count;
```

**照片 UI 状态机**（单张缩略图）：

| 状态 | 视觉 | 触发 |
|------|------|------|
| **未选择** | 白色圆圈 | 默认 / 不在多选模式 |
| **已选择** | 黑色对勾 | 多选模式下点击 |
| **进行中** | 进度环（百分比） | 扫描 / 上传时 |

### 23.4 批量操作（仅 UI 反馈）

**铁律**：
> 先不实现真实的删除和上传逻辑（v2.3 用户决定）

**实现**：

```csharp
[RelayCommand]
private void BatchUpload()
{
    if (!IsBatchActionEnabled) return;
    
    var count = SelectedFiles.Count;
    StatusMessage = $"已请求上传 {count} 个文件（待实现）";
    ExitMultiSelectMode();
}

[RelayCommand]
private void BatchDelete()
{
    if (!IsBatchActionEnabled) return;
    
    var count = SelectedFiles.Count;
    StatusMessage = $"已请求删除 {count} 个文件（待实现）";
    ExitMultiSelectMode();
}
```

**UI 反馈**：在工具栏下方弹 Toast / StatusMessage，2-3 秒后自动消失。

### 23.5 右键菜单

**菜单项**：

| 菜单 | 操作 |
|------|------|
| 上传 | 调 BatchUploadCommand（仅 UI 反馈） |
| 删除（释放本地空间） | 调 BatchDeleteCommand（仅 UI 反馈） |
| 彻底删除 | 调 BatchDeleteCommand（仅 UI 反馈，**红色文字**） |
| 文件夹中打开 | 调 OpenInFolderCommand（**真实系统调用**） |
| 查看 | 调 ViewCommand（**仅 UI 反馈**） |

**右键触发**：
- 右键单张缩略图时显示
- 菜单显示位置：光标位置

**XAML 实现**：
```xml
<Button.ContextMenu>
    <ContextMenu>
        <MenuItem Header="上传" Command="{Binding $parent[UserControl].DataContext.UploadCommand}"/>
        <MenuItem Header="删除（释放本地空间）" Command="{Binding ...DeleteCommand}"/>
        <MenuItem Header="彻底删除" Foreground="{DynamicResource State.Danger}" Command="{Binding ...DeleteCommand}"/>
        <Separator/>
        <MenuItem Header="文件夹中打开" Command="{Binding ...OpenInFolderCommand}"/>
        <MenuItem Header="查看" Command="{Binding ...ViewCommand}"/>
    </ContextMenu>
</Button.ContextMenu>
```

### 23.6 在系统中打开（跨平台）

**铁律**：
> 「在文件夹中打开」调用系统 API（v2.3 用户决定）

**实现**（`Services/SystemShellService.cs`）：

```csharp
public interface ISystemShellService
{
    /// 打开文件夹并选中文件
    void RevealInFolder(string filePath);
}

public class SystemShellService : ISystemShellService
{
    public void RevealInFolder(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(filePath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: open -R <path> 在 Finder 中显示
            Process.Start("open", $"-R \"{filePath}\"");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: explorer /select,<path>
            Process.Start("explorer", $"/select,\"{filePath}\"");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux: 打开文件所在目录
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Process.Start("xdg-open", $"\"{dir}\"");
            }
        }
    }
}
```

**使用**：
```csharp
[RelayCommand]
private void OpenInFolder(MediaFile? file)
{
    if (file == null) return;
    
    try
    {
        var absolutePath = Path.Combine(file.ProjectPath, file.RelativePath);
        _systemShell.RevealInFolder(absolutePath);
    }
    catch (Exception ex)
    {
        StatusMessage = $"打开失败：{ex.Message}";
    }
}
```

### 23.7 涉及文件清单（v2.3 新增 / 修改）

| 文件 | 角色 | 操作 |
|------|------|------|
| `App/Services/ISystemShellService.cs` | 系统 Shell 调用 | **新增** |
| `App/Services/SystemShellService.cs` | 系统 Shell 实现 | **新增** |
| `App/Views/MainWindow.axaml` | 加 Row 1 工具栏 | **修改** |
| `App/ViewModels/MainWindowViewModel.cs` | 工具栏命令分发 | **修改** |
| `App/Views/GalleryView.axaml` | 单张图加 ContextMenu + 选中状态 | **修改** |
| `App/ViewModels/GalleryViewModel.cs` | 多选模式 + 选中状态 + 批量操作 | **修改** |
| `App/Converters/IsNotNullConverter.cs` | 空态占位（可选） | 可选 |

---

## 24. v2.3 验收标准

- [ ] 工具栏位置在标题栏下方（不是页面内）
- [ ] 非多选模式只显示「多选 / 刷新」
- [ ] 点击「多选」后切换按钮为「取消多选 / 批量上传 / 删除」
- [ ] 多选模式下点击缩略图显示黑色对勾
- [ ] 「批量上传」/「删除」点击后弹 Toast「待实现」并自动退出多选
- [ ] 右键缩略图弹出 5 项菜单
- [ ] 「文件夹中打开」在 macOS Finder / Windows Explorer / Linux 文件管理器中显示文件
- [ ] 「彻底删除」显示红色文字
- [ ] 跨平台 Shell 调用失败有友好错误提示

---

**End of Spec v2.3**

> 本规格是 v2.3，整合原型图反馈 + 多选模式 + 批量操作 + 右键菜单 + 系统 Shell 跨平台调用。
> 实现 AI 只需严格按照本文档执行，无需追问设计意图。
> **所有视觉决策、所有行为约束、所有架构铁律已闭环。**
