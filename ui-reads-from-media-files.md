# 界面从 media_files 读取数据 Spec v1.0

> 完整的 UI 数据源迁移规格。`media_files` 表已存在（由 refresh-and-media-index 落地），
> 本 spec 专注于把界面**真**接上 DB 数据。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 功能名 | 界面数据源迁移：mock → media_files |
| 文档版本 | 1.0 |
| 所属模块 | 主相册页（GalleryView） |
| 涉及文件 | 3 个修改 / 2 个删除 / 1 个新增 |
| 涉及数据库 | 读取 `media_files` 表（不写） |
| 前置依赖 | `media_files` 表已创建（refresh spec §5） |
| 字数限制 | **本次忽略**（用户授权） |

---

## 1. 功能概述

### 1.1 目标

主相册页（GalleryView）数据源**完全**从硬编码 mock 切换到 `media_files` 表：
- TimelineRail 左侧时间轴 → 来自 `GetDateGroupsAsync`
- PhotoGrid 右侧网格 → 来自 `GetByDateAsync`
- 单个 PhotoTile 绑定 `MediaFile`（不是 `Photo`）
- 扫描完成后自动刷新
- 关闭重开 App 数据持久化

### 1.2 设计原则

1. **一刀切**：删除旧 `Photo`/`SyncStatus` 模型，**不**保留双轨
2. **数据流单向**：UI ← ViewModel ← Repository ← SQLite，**不**反向
3. **响应式刷新**：扫描完成事件触发 ViewModel 自动重载
4. **状态显式**：loading / empty / error 三态明确区分
5. **token 严格**：所有视觉走 token，不写 hex

### 1.3 范围

**做**：
- 旧模型删除
- PhotoTile 适配 MediaFile
- GalleryViewModel 数据源接 Repository
- TimelineRail 接 GetDateGroupsAsync
- PhotoGrid 接 GetByDateAsync
- 扫描完成自动刷新
- 关闭重开数据恢复

**不做**：
- 修改 `IMediaRepository` 接口
- 重构 PhotoTile 内部结构（保持模板）
- 修改扫描逻辑
- 新增视觉状态（在 DoD 范围内的状态）
- 上传 / 删除 / 重命名

---

## 2. UI 改动详解

### 2.1 总体变化

**Before（mock）**：
```
GalleryView
├── TimelineRail (硬编码 3 个日期)
└── PhotoGrid (硬编码 12 个 PhotoTile)
    └── PhotoTile (绑 Photo)
```

**After（DB）**：
```
GalleryView
├── TimelineRail (绑 DateGroups ObservableCollection)
└── PhotoGrid (绑 CurrentMediaFiles ObservableCollection)
    └── MediaFileTile / PhotoTile 重构 (绑 MediaFile)
```

### 2.2 TimelineRail 改造

#### 数据源

```csharp
public ObservableCollection<DateCount> DateGroups { get; }
```

`DateCount` 来自 `IMediaRepository.GetDateGroupsAsync(projectPath)`。

#### 显示规则

| 字段 | 渲染 |
|------|------|
| `Date` | 文字 `yyyy-MM-dd`，`Text.Primary` 12px mono，间距 12px |
| `Count` | 数字徽章 `Text.Secondary` 11px，圆角 Pill，padding 6×2 |

#### 状态

| 状态 | 表现 |
|------|------|
| 默认 | 圆点 `Bg.Divider` 8×8 + 文字 `Text.Secondary` |
| Hover | 圆点 `Text.Disabled` + 文字 `Text.Primary` |
| 选中 | 圆点 `Accent.Stellar` + `Glow.Stellar` + 文字 `Accent.Stellar` SemiBold |
| 空态 | 文字「未发现媒体文件，请点刷新扫描」`Text.Tertiary` 居中 |
| 加载中 | 3 行骨架屏（占位圆点 + 占位文字条） |

#### 选中行为

点击某日期 → `SelectedDate = dateCount` → 触发 `LoadDateAsync(dateCount)` → 网格重新加载。

### 2.3 PhotoGrid 改造

#### 数据源

```csharp
public ObservableCollection<MediaFile> CurrentMediaFiles { get; }
```

`MediaFile` 来自 `IMediaRepository.GetByDateAsync(projectPath, date)`。

#### 布局

- 容器 `WrapPanel`，4 列等宽，间距 16px
- 高度自适应（每行 120px）
- 容器内边距 24px
- 缩略图尺寸 160×120px

#### 状态

| 状态 | 表现 |
|------|------|
| 有数据 | 渲染 `MediaFileTile` 列表 |
| 加载中 | 12 个占位 `MediaFileTile`（星形 + 灰色背景） |
| 空数据 | 居中「该日期无媒体」`Text.Tertiary` |
| 项目目录为空 | 居中「请先在设置中选择项目目录」`Text.Tertiary` |

### 2.4 MediaFileTile 改造（原 PhotoTile）

#### 字段映射

| 原 PhotoTile 字段 | 新 MediaFileTile 字段 |
|------------------|----------------------|
| `Photo.ThumbnailPath` | `MediaFile.ThumbnailPath` |
| `Photo.Count` | **删除**（不再需要"几张"徽章） |
| `Photo.Status` → StatusBadge | `MediaFile.IsUploaded`+`LocalExists` → StatusBadge（规则同 §2.5） |
| 无 | `MediaFile.MediaType == Video` → VideoBadge（▶） |
| 无 | `MediaFile.FileName` → hover tooltip |

#### 视觉变化

- **删除**：右下角「{N}张」徽章
- **新增**：左上角「▶」徽章（仅视频）
- **保留**：右上角状态徽章（云/警告/禁云）

### 2.5 StatusBadge 三态映射

| `IsUploaded` | `LocalExists` | 图标 | 颜色 | 含义 |
|--------------|---------------|------|------|------|
| `true` | `true` | `Icon.Cloud` | `State.Success` | 已上传且本地有 |
| `true` | `false` | `Icon.AlertTriangle` | `State.Warning` | 云端有本地缺 |
| `false` | `*` | `Icon.CloudOff` | `State.Quiet` | 未上传 |

> 复用 `specs/refresh-and-media-index.md §7` 中定义的 `StatusToIconConverter` / `StatusToColorConverter`，
> 只是把入参从 `SyncStatus` 改为 `MediaFile`，或新增 `MediaFileToStatusConverter` 桥接。

### 2.6 VideoBadge（新增组件 C13）

**位置**：缩略图左上角内边距 6px

**外观**：
- 圆形 22×22
- 背景：`#CC0A0E1A`（半透明深色）
- 内容：`Icon.Play` 12×12，`State.Warning` 色
- 阴影：`0 2 6 0 #80000000`

**可见性**：`IsVisible="{Binding MediaType, Converter={StaticResource MediaTypeToVideo}}"`

### 2.7 加载占位（Skeleton）

**TimelineRail skeleton**：
- 3 行占位
- 圆点 8×8 `Bg.SurfaceElevated`
- 文字条 80×12 `Bg.SurfaceElevated` 圆角 4

**PhotoGrid skeleton**：
- 12 个 `MediaFileTile`
- 内部 `Icon.StarOutline` 48×48 `Bg.SurfaceElevated`
- 文字条代替徽章

**实现**：用 `IsVisible="{Binding IsLoading}"` 切换 skeleton / 实际内容。

---

## 3. 数据模型

### 3.1 删除

```csharp
// Core/Models/Photo.cs - 整个文件删除
// Core/Models/SyncStatus.cs - 整个文件删除
// Converters/StatusToIconConverter.cs - 删除（重建为 MediaFile 版本）
// Converters/StatusToColorConverter.cs - 删除（重建）
// Converters/StatusToTooltipConverter.cs - 删除
// Converters/CountConverters.cs - 删除（不再需要）
```

### 3.2 复用（来自 refresh spec）

```csharp
// Core/Data/MediaFile.cs
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

// Core/Data/DateCount.cs
public sealed class DateCount
{
    public DateTime Date { get; init; }
    public int Count { get; init; }
}
```

### 3.3 新增 ViewModel 字段

```csharp
public partial class GalleryViewModel : ObservableObject
{
    // === 数据源 ===
    public ObservableCollection<DateCount> DateGroups { get; } = new();
    public ObservableCollection<MediaFile> CurrentMediaFiles { get; } = new();

    // === 选中态 ===
    [ObservableProperty] private DateCount? _selectedDate;

    // === 加载状态 ===
    [ObservableProperty] private bool _isLoadingDateGroups;
    [ObservableProperty] private bool _isLoadingMedia;
    [ObservableProperty] private string? _loadErrorMessage;

    // === 派生 ===
    public bool IsEmpty => !IsLoadingDateGroups && DateGroups.Count == 0;
    public bool HasNoProject => string.IsNullOrEmpty(_projectPath);

    // === 依赖 ===
    private readonly IMediaRepository _mediaRepo;
    private readonly IConfigService _config;
    private readonly ISubscriber<RefreshStateChanged> _refreshSub;  // 可选
    private string? _projectPath;
}
```

### 3.4 新增 Converter

```csharp
// Converters/MediaTypeConverters.cs
public class MediaTypeToVideoConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
        => value is MediaType mt && mt == MediaType.Video;
    public object? ConvertBack(...) => throw new NotSupportedException();
}

public class MediaTypeToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
        => value is MediaType mt && mt == MediaType.Image;
    public object? ConvertBack(...) => throw new NotSupportedException();
}

// Converters/MediaFileConverters.cs
public class MediaFileToStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not MediaFile f) return SyncStatus.NotUploaded;
        if (f.IsUploaded && f.LocalExists) return SyncStatus.UploadedAndLocal;
        if (f.IsUploaded && !f.LocalExists) return SyncStatus.UploadedButMissingLocal;
        return SyncStatus.NotUploaded;
    }
    public object? ConvertBack(...) => throw new NotSupportedException();
}
```

> **SyncStatus 保留**作为 UI 内部状态枚举（不暴露为业务模型），仅用于 converter 桥接。
> 也可直接用 `(MediaFile, LocalExists) → string` 三态判定，省去 SyncStatus。

---

## 4. 业务流程

### 4.1 启动加载流程

```
App 启动
  → MainWindow 加载
  → GalleryView 实例化
  → DataContext = GalleryViewModel
  → GalleryViewModel 构造函数注入 IMediaRepository, IConfigService
  → 触发 InitializeAsync()
      1. 读 ProjectConfig.CurrentDirectory → _projectPath
      2. 若 _projectPath 为空 → DateGroups.Clear()，显示「请先选择项目」空态
      3. 若 _projectPath 非空 → 调 GetDateGroupsAsync → 填 DateGroups
      4. 若 DateGroups.Count == 0 → 显示「未发现媒体」空态
      5. 若 DateGroups.Count > 0 → 自动选中第一个日期
      6. 选中后 → 调 GetByDateAsync → 填 CurrentMediaFiles
```

### 4.2 切换日期流程

```
用户点击 TimelineRail 中某日期
  → SelectedDate = dateCount
  → OnSelectedDateChanged 触发
  → LoadDateAsync(SelectedDate)
      1. IsLoadingMedia = true
      2. 清空 CurrentMediaFiles
      3. 调 GetByDateAsync(_projectPath, SelectedDate.Date)
      4. 填 CurrentMediaFiles
      5. IsLoadingMedia = false
```

### 4.3 扫描完成自动刷新流程

```
扫描完成（RefreshState.Completed）
  → GalleryViewModel 订阅该事件
  → 触发 ReloadAsync()
      1. 保存当前 SelectedDate
      2. 重新调 GetDateGroupsAsync → 更新 DateGroups
      3. 若旧 SelectedDate 仍存在 → 保持选中，调 GetByDateAsync 刷新
      4. 若旧 SelectedDate 已不存在 → 选最新日期（DateGroups[0]）
      5. 若 DateGroups 为空 → 清空 CurrentMediaFiles，显示空态
```

### 4.4 错误处理流程

```
任意 DB 调用抛异常
  → catch
  → LoadErrorMessage = $"加载失败：{ex.Message}"
  → IsLoadingDateGroups = false / IsLoadingMedia = false
  → UI 显示：Toast「加载失败」+ 重试按钮
  → 重试按钮 → 重新调 LoadAsync()
```

### 4.5 项目目录变更流程

```
用户在设置页改了 ProjectConfig.CurrentDirectory
  → 保存到 SQLite
  → GalleryViewModel 监听 ProjectConfig 变化（或重新打开 GalleryView）
  → 重新 LoadAsync()
```

> v1.0 简化：项目目录变更需要**重启 App**或**切回 Gallery 页面**才生效，不做实时跨页面同步。
> 实时同步 v1.1 引入「全局事件总线」。

---

## 5. 接口契约

### 5.1 调用的 Repository 方法

```csharp
public interface IMediaRepository
{
    Task<IReadOnlyList<DateCount>> GetDateGroupsAsync(string projectPath, CancellationToken ct = default);
    Task<IReadOnlyList<MediaFile>> GetByDateAsync(string projectPath, DateTime date, CancellationToken ct = default);
}
```

**不**调用：Upsert / BulkUpsert / MarkMissing（写操作属于扫描模块）。

### 5.2 调用的 ConfigService 方法

```csharp
public interface IConfigService
{
    Task<ProjectConfig?> GetAsync<ProjectConfig>(string key, CancellationToken ct = default);
}
```

只读 `ConfigKeys.Project`。

### 5.3 GalleryViewModel 暴露的接口

```csharp
public partial class GalleryViewModel : ObservableObject
{
    // 集合
    ObservableCollection<DateCount> DateGroups { get; }
    ObservableCollection<MediaFile> CurrentMediaFiles { get; }

    // 状态
    bool IsLoadingDateGroups / IsLoadingMedia / IsEmpty
    string? LoadErrorMessage
    string? ProjectPath  // 用于空态判断

    // 选中
    DateCount? SelectedDate

    // 派生
    bool IsEmpty { get; }
    bool HasNoProject { get; }

    // 命令
    IRelayCommand ReloadCommand           // 手动重载（重试用）
    IRelayCommand<DateCount> SelectDateCommand

    // 生命周期
    Task InitializeAsync()  // 启动时调一次
}
```

### 5.4 MediaFileTile 暴露的接口

保持现有 PhotoTile 模板不变，仅改 DataType 和绑定：

```csharp
public partial class MediaFileTile : UserControl
{
    public static readonly StyledProperty<MediaFile?> FileProperty = ...;
    public MediaFile? File { get; set; }  // 直接绑 DataContext 即可
}
```

---

## 6. 状态机

### 6.1 GalleryView 页面级状态

```
                            ┌─────────────────┐
                            │  NoProject      │ ← _projectPath 空
                            └────────┬────────┘
                                     │ 选了项目
                                     ▼
                            ┌─────────────────┐
              ┌──────────── │  Loading        │
              │             └────────┬────────┘
              │                      │ 完成
              │                      ▼
              │             ┌─────────────────┐
              │             │  Empty          │ ← DateGroups.Count == 0
              │             └────────┬────────┘
              │                      │ 扫描到媒体
              │                      ▼
              │             ┌─────────────────┐
              ├──────────── │  Loaded         │
              │             └────────┬────────┘
              │                      │ 扫描完成
              │                      │ → ReloadAsync
              │                      ▼
              │             (回到 Loaded 或 Empty)
              │
              │ 任意时刻出错
              ▼
    ┌─────────────────┐
    │  Error          │ ← LoadErrorMessage 非空
    └─────────────────┘
              │ 重试
              ▼
        (回到 Loading)
```

### 6.2 PhotoGrid 内部状态

```
Loading（skeleton）→ Loaded（有数据）/ Empty（无数据但日期有效）
                 → Error（重试）
```

### 6.3 媒体项状态映射

```
[MediaFile.IsUploaded, MediaFile.LocalExists]
  [true,  true]  → 已上传且本地有（云图标，绿色）
  [true,  false] → 已上传但本地缺（警告图标，黄色）
  [false, *]     → 未上传（禁云图标，灰色）
```

---

## 7. 性能要求

| 指标 | 目标 |
|------|------|
| TimelineRail 加载 | < 50ms（10 日期）/ < 200ms（100 日期） |
| PhotoGrid 加载 | < 100ms（100 张）/ < 500ms（1000 张） |
| 切换日期响应 | < 100ms |
| 扫描完成自动刷新 | < 300ms（含 IO） |
| 10000 行扫描后渲染 | < 500ms |
| 内存占用 | DateGroups 全量 + CurrentMediaFiles 单日（最大 1000 张） |

**优化策略**：
- DateGroups 不分页（最多几百行）
- CurrentMediaFiles 限制单日 1000 张（超出显示「+N 张更多」提示）
- 缩略图异步加载 + LRU 缓存（不在本 spec 范围）
- 切换日期时不清空 UI，先展示 skeleton 再替换

---

## 8. 错误处理

| 场景 | 行为 |
|------|------|
| `_projectPath` 为空 | `HasNoProject=true`，显示「请先选择项目目录」空态，**不**调 DB |
| `GetDateGroupsAsync` 抛异常 | `LoadErrorMessage` 设值，显示「加载失败」+ 重试按钮 |
| `GetByDateAsync` 抛异常 | 同上 |
| DB 文件被外部删除/损坏 | 同上，提示用户「数据库异常，请检查 `%LocalAppData%/StarHelper/config.db`」 |
| 用户取消（未来） | 保留已加载数据，状态显示「已停止」 |
| 网络错误（未来） | 不适用，本模块纯本地读 |

**重试机制**：
- 「重试」按钮 → `ReloadCommand` → 重新跑 `InitializeAsync`
- 成功后 `LoadErrorMessage` 清空

---

## 9. 验收标准（DoD）

### 9.1 数据迁移

- [ ] `Photo.cs`、`SyncStatus.cs` 文件**物理删除**
- [ ] `StatusToIconConverter.cs`、`StatusToColorConverter.cs`、`StatusToTooltipConverter.cs`、`CountConverters.cs` 删除或重写
- [ ] `MediaFile.cs` 存在（来自 refresh spec）
- [ ] `DateCount` 类存在
- [ ] 无编译警告「Photo/SyncStatus 未使用」

### 9.2 UI 改造

- [ ] TimelineRail 节点从 `DateGroups` 渲染，**无**硬编码日期
- [ ] PhotoGrid tile 从 `CurrentMediaFiles` 渲染，**无**硬编码占位
- [ ] 视频 tile 带 ▶ 徽章
- [ ] 无缩略图显示 StarOutline 占位
- [ ] 三种状态徽章（云/警告/禁云）颜色正确

### 9.3 状态完整

- [ ] 无项目目录：显示「请先选择项目目录」空态
- [ ] 无媒体：显示「未发现媒体文件」空态
- [ ] 加载中：skeleton 骨架屏
- [ ] 选中日期无文件：显示「该日期无媒体」空态
- [ ] DB 错误：Toast + 重试按钮

### 9.4 响应式

- [ ] 扫描完成后 TimelineRail 自动刷新
- [ ] 扫描完成后当前日期网格自动刷新
- [ ] 当前日期被删除时自动选最新日期
- [ ] 切换日期不卡顿

### 9.5 持久化

- [ ] 关闭重开 App，TimelineRail 从 DB 恢复
- [ ] 关闭重开 App，PhotoGrid 显示上次选中日期
- [ ] 不需要重新扫描就能看到上次扫描结果

### 9.6 性能

- [ ] 10000 行查询 < 50ms
- [ ] 扫描完成后刷新 UI < 300ms
- [ ] 切换日期 < 100ms

### 9.7 反模式（必避）

- ❌ 保留旧 `Photo`/`SyncStatus` 混用
- ❌ 在 UI 层直接 `new SqliteConnection`
- ❌ TimelineRail 走硬编码
- ❌ PhotoTile 里写业务逻辑
- ❌ 扫描过程中频繁刷新
- ❌ 缩略图路径字符串拼接（必须用 `Path.Combine`）
- ❌ 硬编码颜色（必须 token）
- ❌ 在 ViewModel 构造函数里同步调 DB（必须 InitializeAsync 异步）

---

## 10. 边界

**做**：
- 旧模型删除
- 数据源切换
- 状态机补全
- 扫描后自动刷新
- 持久化恢复

**不做**：
- ❌ 修改 `IMediaRepository` 接口
- ❌ 重构 PhotoTile 内部模板
- ❌ 修改扫描逻辑
- ❌ 全局事件总线（v1.1）
- ❌ 实时跨页面同步（v1.1）
- ❌ 缩略图懒加载（v1.1）
- ❌ 缩略图缓存清理（v1.1）
- ❌ 性能监控埋点

---

## 11. 涉及文件清单

### 删除（5 个）

| 文件 | 原因 |
|------|------|
| `Core/Models/Photo.cs` | 替换为 MediaFile |
| `Core/Models/SyncStatus.cs` | 仅作 UI 内部枚举使用，不入模型层 |
| `Converters/StatusToIconConverter.cs` | 重建为 MediaFile 版本 |
| `Converters/StatusToColorConverter.cs` | 同上 |
| `Converters/StatusToTooltipConverter.cs` | 同上 |
| `Converters/CountConverters.cs` | 不再需要数量徽章 |

### 新增（3 个）

| 文件 | 角色 |
|------|------|
| `Core/Data/DateCount.cs` | 时间轴聚合数据 |
| `Converters/MediaTypeConverters.cs` | `MediaTypeToVideo` / `MediaTypeToImage` |
| `Converters/MediaFileConverters.cs` | `MediaFileToStatus` / `MediaFileToTooltip` |

### 修改（5 个）

| 文件 | 改动 |
|------|------|
| `Controls/PhotoTile.axaml` + `.cs` | 改名 `MediaFileTile`（或保留名） + 改绑 `MediaFile` + 加 `VideoBadge` |
| `ViewModels/GalleryViewModel.cs` | 完全重写：删 mock，接 Repository，加状态机 |
| `Views/GalleryView.axaml` + `.cs` | 删 skeleton / 实际内容二选一，加空态文案 |
| `Program.cs` | DI 加 `IMediaRepository` 注册 |
| `App.axaml.cs` | 启动时实例化 `MediaRepository`（来自 refresh spec 已有） |

### 不变（来自 refresh spec）

- `Core/Data/MediaFile.cs` — 直接复用
- `Core/Data/MediaRepository.cs` — 直接复用
- `Services/IMediaScanner.cs` — 数据写源，本 spec 只读

---

## 12. 实施顺序

```
Phase 1 — 数据层验证
  1.1 确认 MediaFile.cs 存在
  1.2 确认 MediaRepository 存在
  1.3 确认 DateCount 类存在
  1.4 单元测试 GetDateGroupsAsync / GetByDateAsync

Phase 2 — 模型清理
  2.1 删除 Photo.cs / SyncStatus.cs
  2.2 删除旧 StatusTo* Converter
  2.3 编译：找所有引用 → 全删

Phase 3 — 新 Converter
  3.1 MediaTypeToVideoConverter
  3.2 MediaTypeToImageConverter
  3.3 MediaFileToStatusConverter
  3.4 MediaFileToTooltipConverter

Phase 4 — MediaFileTile
  4.1 改 x:DataType="models:MediaFile"
  4.2 改 ThumbnailPath 绑定
  4.3 删 {N}张 徽章
  4.4 改 StatusBadge 绑定（用 MediaFileToStatus）
  4.5 加 VideoBadge + Icon.Play

Phase 5 — GalleryViewModel
  5.1 完全重写
  5.2 字段：DateGroups / CurrentMediaFiles / SelectedDate / IsLoadingXxx
  5.3 构造函数：注入 IMediaRepository, IConfigService
  5.4 InitializeAsync：流程 §4.1
  5.5 LoadDateAsync：流程 §4.2
  5.6 扫描完成订阅 + ReloadAsync：流程 §4.3
  5.7 OnSelectedDateChanged 触发 LoadDateAsync

Phase 6 — GalleryView XAML
  6.1 TimelineRail 绑 DateGroups
  6.2 PhotoGrid 绑 CurrentMediaFiles
  6.3 加空态/错误态文案
  6.4 IsLoading 切换 skeleton

Phase 7 — 集成
  7.1 DI 注册 IMediaRepository
  7.2 Program.cs / App.axaml.cs 接线

Phase 8 — 验证
  8.1 启动 App，TimelineRail 显示 DB 中的日期
  8.2 选中某日期，PhotoGrid 显示该日媒体
  8.3 关闭重开，数据恢复
  8.4 扫描后自动刷新
  8.5 DoD 全过
```

---

## 13. 跨模块规则引用

实施时**必须**遵守 `REQUIREMENTS.md §8` 中的所有铁律：

- 颜色 / 样式 token 化
- 单一启用控制（无双控）
- Flyout 对齐
- RadioButton 绑定
- 图标用 StreamGeometry

---

## 14. 与已有 Spec 的关系

```
star-helper-spec.md              ← 视觉 + 组件 + 持久化（基础）
  └─ requirements/configuration-module.md  ← 配置模块
specs/refresh-and-media-index.md ← 媒体索引 + 扫描（数据来源）
  └─ specs/ui-reads-from-media-files.md    ← 本 spec（UI 消费数据） ★ 你在这里
```

本 spec 是「数据层（refresh）」到「UI 层」的最后一步。

---

**End of Spec v1.0**
