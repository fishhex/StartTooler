# 19 — 数据统计仪表盘（v0.11）

> 对应需求：`doc/0.11/demand/09-statistics-dashboard.md`。
> 核心改动：新增「统计」Tab，全屏仪表盘展示拍摄画像 + 天文复盘维度。纯描述型，不做评价/建议/预测。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | v0.1（实施规范） |
| 文档状态 | **规范 — 待实现** |
| 适用版本 | 0.11 |
| 关联模块 | Gallery（D02）、AI Tagging（D11）、Shooting Session（D08）、Data Layer（D02） |

---

## 1. 模块边界

```
MainWindow NavRail
  └─ 「统计」Tab（新增，与媒体/上传/垃圾筒/设置同级）
       └─ DashboardView（全屏独立布局，不区分左栏/图墙）
            ├─ §2 基础统计区
            │   ├─ KPI 概览卡片（5 个数字卡片）
            │   ├─ 拍摄热力图 + 月度柱状图
            │   ├─ 目标排行榜 TOP 15
            │   └─ 曝光参数分布（焦距/ISO/曝光时长）
            ├─ §3 天文复盘区（依赖 D08 sessions 表）
            │   ├─ 月相偏好画像
            │   ├─ 历史事件 × 出摊情况
            │   ├─ Top 5 出摊（按质量降序）
            │   └─ 全部出摊时间轴
            └─ §4 年度总结（Phase 2）
```

**依赖**：
- `MediaRepository`（已有）—— 基础统计查询
- `sessions` 表 + `SessionRepository`（依赖 D08，需先建表）—— 天文复盘数据源
- `tags` 字典表（已有）—— 目标排行榜
- `astronomy-events.toml`（新增）—— 历史事件匹配
- `MeeusSharp` NuGet 包（新增）—— 月相回溯
- SkiaSharp（已有）—— 图表自绘

---

## 2. 新增/修改文件清单

### 2.1 Phase 1 — 基础统计（可独立交付）

| 文件 | 用途 | 类型 |
|------|------|------|
| `Views/DashboardView.axaml` | 仪表盘全屏视图 | 新增 |
| `Views/DashboardView.axaml.cs` | code-behind（年份切换等） | 新增 |
| `ViewModels/DashboardViewModel.cs` | 仪表盘 VM（数据加载 + 图表绑定） | 新增 |
| `Controls/StatCard.axaml` | KPI 数字卡片控件 | 新增 |
| `Controls/StatCard.axaml.cs` | 卡片控件 code-behind | 新增 |
| `Controls/CalendarHeatmap.axaml` | 热力图控件（SkiaSharp 自绘） | 新增 |
| `Controls/CalendarHeatmap.axaml.cs` | 热力图绘制逻辑 | 新增 |
| `Controls/BarChart.axaml` | 条形图/柱状图控件（SkiaSharp 自绘） | 新增 |
| `Controls/BarChart.axaml.cs` | 图表绘制逻辑 | 新增 |
| `Controls/NavRail.axaml` | 新增「统计」按钮 | 修改 |
| `ViewModels/MainWindowViewModel.cs` | 新增 `ViewPage.Dashboard`、导航命令 | 修改 |
| `Views/MainWindow.axaml` | 新增 DashboardView 路由 | 修改 |
| `Models/Models.cs` | 新增 `ViewPage.Dashboard` 枚举值 | 修改 |
| `Data/MediaRepository.cs` | 新增统计聚合查询方法 | 修改 |
| `Data/IMediaRepository.cs` | 新增统计查询接口 | 修改 |
| `Themes/Colors.axaml` | 新增 `Chart.*` 系列 token（热力图色阶） | 修改 |
| `Themes/Icons.axaml` | 新增 `Icon.Stats` 图标 | 修改 |

### 2.2 Phase 2 — 天文复盘（依赖 D08）

| 文件 | 用途 | 类型 |
|------|------|------|
| `Resources/astronomy-events.toml` | 历史天文事件模板（< 30 条） | 新增 |
| `Data/EventRepository.cs` | 事件库加载 + 查询 | 新增 |
| `Services/MoonRetrospectionService.cs` | 启动时补全历史会话月相 | 新增 |
| `Services/EventHistoryMatcher.cs` | 事件 × 出摊 JOIN | 新增 |
| `Services/MoonPreferenceAnalyzer.cs` | 月相偏好 SQL 聚合 | 新增 |
| `Data/SessionRepository.cs` | 会话查询（D08 提供，D09 复用） | 新增（D08） |
| `Models/Session.cs` | 会话模型（D08 提供） | 新增（D08） |
| `ViewModels/DashboardViewModel.cs` | 追加天文复盘数据加载 | 修改 |
| `Views/DashboardView.axaml` | 追加天文复盘区域 UI | 修改 |

### 2.3 Phase 3 — 年度总结

| 文件 | 用途 | 类型 |
|------|------|------|
| `Views/YearInReviewView.axaml` | 年度总结页 | 新增 |
| `Views/YearInReviewView.axaml.cs` | code-behind | 新增 |
| `ViewModels/YearInReviewViewModel.cs` | 年度总结 VM | 新增 |

---

## 3. 仪表盘整体布局

```
┌──────────────────────────────────────────────────────────┐
│  📊 数据统计                         年份: [2025 ▼]     │
├──────────────────────────────────────────────────────────┤
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ │
│  │14,256  │ │386h42m │ │124 天  │ │15 目标 │ │1.2 TB  │ │  ← KPI 卡片行
│  │总照片数│ │累计曝光│ │拍摄天数│ │天体种类│ │数据量  │ │
│  └────────┘ └────────┘ └────────┘ └────────┘ └────────┘ │
│                                                          │
│  ┌──────────────────┐ ┌────────────────────────────────┐ │
│  │   拍摄热力图      │ │  月度柱状图                    │ │  ← 时间维度行
│  │  (365天日历格)   │ │                                │ │
│  └──────────────────┘ └────────────────────────────────┘ │
│                                                          │
│  ┌────────────────────┐ ┌──────────────────────────────┐ │
│  │  目标排行榜 TOP15   │ │  曝光参数分布                │ │  ← 目标+技术行
│  │  银河    ████ 1890 │ │  焦距 / ISO / 曝光时长       │ │
│  └────────────────────┘ └──────────────────────────────┘ │
│                                                          │
│  ──────────── 🌙 天文复盘 ────────────                    │
│                                                          │
│  ┌──────────────────────┐ ┌──────────────────────────┐   │
│  │ 🌙 月相偏好          │ │ 📋 流星雨极大日 × 出摊   │   │
│  └──────────────────────┘ └──────────────────────────┘   │
│                                                          │
│  ┌─────────────────────────────────────────────────────┐ │
│  │ 📋 全部出摊时间轴（按时间倒序，滚动加载）           │ │
│  └─────────────────────────────────────────────────────┘ │
│                                                          │
│  [📸 生成年度总结]                                       │
└──────────────────────────────────────────────────────────┘
```

- 整体为 `ScrollViewer`，垂直滚动。
- 内容区 `Margin="40,24"`，`Spacing="24"`。
- 年份切换 `ComboBox` 位于右上角，与标题同行。

---

## 4. NavRail 集成

### 4.1 新增「统计」按钮

在 [NavRail.axaml](file:///Users/hex/code/StartTooler/StartTooler/Controls/NavRail.axaml) 中，在「上传」和「垃圾筒」之间插入新按钮：

```xml
<!-- 统计按钮（D09） -->
<Button Grid.Row="1"
        x:Name="StatsButton"
        Height="56"
        Classes="nav-item"
        Classes.active="{Binding IsStatsActive}"
        Command="{Binding NavigateToStatsCommand}"
        ToolTip.Tip="{Binding NavStatsTooltip}"
        VerticalAlignment="Center"
        HorizontalContentAlignment="Center"
        VerticalContentAlignment="Center"
        Padding="0">
    <StackPanel Spacing="4" HorizontalAlignment="Center" VerticalAlignment="Center">
        <PathIcon Data="{DynamicResource Icon.Stats}"
                  Width="18" Height="18" />
        <TextBlock Text="统计" FontSize="11" TextAlignment="Center" />
    </StackPanel>
</Button>
```

> 注意：插入后原 Grid.Row 需要整体后移——上传 → Row="2"，垃圾筒 → Row="3"，设置 → Row="4"，Grid 加 `RowDefinitions="Auto,Auto,Auto,Auto,*"`。

### 4.2 MainWindowViewModel 追加

```csharp
// ViewPage 枚举追加
public enum ViewPage
{
    Gallery,
    Settings,
    UploadServer,
    Trash,
    Dashboard,  // 新增
}

// 新增属性
public bool IsStatsActive => CurrentPage == ViewPage.Dashboard;
public string NavStatsTooltip => OperatingSystem.IsMacOS() ? "统计 (⌘5)" : "统计 (Ctrl+5)";

// 新增 VM
[ObservableProperty] private DashboardViewModel dashboardViewModel;

// 构造函数中初始化
DashboardViewModel = new DashboardViewModel(_mediaRepository, _configService);

// 导航命令
[RelayCommand]
private void NavigateToStats()
{
    CurrentView = DashboardViewModel;
    IsSettingsPage = false;
    CurrentPage = ViewPage.Dashboard;
    _ = DashboardViewModel.LoadAsync();
}
```

### 4.3 MainWindow.axaml 路由

在内容区添加 DashboardView 的条件显示：

```xml
<controls:DashboardView IsVisible="{Binding IsStatsActive}" 
                         DataContext="{Binding DashboardViewModel}" />
```

---

## 5. 控件设计

### 5.1 StatCard（KPI 数字卡片）

**属性**：
```csharp
public partial class StatCard : UserControl
{
    public static readonly StyledProperty<string> LabelProperty = ...;
    public static readonly StyledProperty<string> ValueProperty = ...;
    public static readonly StyledProperty<string> IconDataProperty = ...;
}
```

**视觉**：
- 背景 `Bg.Surface`，圆角 `Radius.Medium`（8），内边距 `20`。
- 顶部图标（16×16，`Text.Secondary`）+ 标签文字（`Text.Secondary` 12px）。
- 中间大数字（`Text.Primary` 24px SemiBold）。
- 固定宽度 180，均分一行 5 个。

### 5.2 CalendarHeatmap（热力图）

**SkiaSharp 自绘控件**：
- 绑定属性：`IReadOnlyList<HeatmapDay> Days`（365 天数据）。
- 绘制逻辑：
  - 7 行（周一→周日）× 53 列（约一年）。
  - 每格 12×12，间距 2px。
  - 颜色：0 张 = `Chart.Heatmap.Zero`（灰色），max = `Chart.Heatmap.Max`（深蓝），中间线性插值。
- Hover：`ToolTip` 显示日期 + 张数 + 当日主要目标。
- 点击：触发 `DayClicked` 事件 → VM 跳转到 Gallery 该日期。

**Colors.axaml 新增 token**：
```xml
<Color x:Key="Chart.Heatmap.Zero">#1E2233</Color>
<Color x:Key="Chart.Heatmap.Max">#4FC3F7</Color>
<Color x:Key="Chart.Bar.Accent">#4FC3F7</Color>
<Color x:Key="Chart.Bar.Secondary">#8892B0</Color>
```

### 5.3 BarChart（条形图/柱状图）

**SkiaSharp 自绘控件**：
- 绑定属性：
  - `IReadOnlyList<BarItem> Items`（标签 + 值 + 百分比）。
  - `BarOrientation Orientation`（Horizontal=条形图，Vertical=柱状图）。
  - `bool ShowValue`。
- 条形图（目标排行榜）：
  - 左侧标签文字，右侧条形 + 数值。
  - 点击触发 `ItemClicked` → 跳转到 Gallery 该标签过滤。
- 柱状图（月度统计）：
  - X 轴月份标签，Y 轴数量。
  - 柱子上方标注数值。

---

## 6. 数据层

### 6.1 MediaRepository 新增统计查询

```csharp
// IMediaRepository / MediaRepository 追加

/// <summary>KPI 概览：总张数、累计曝光时长、拍摄天数、目标数、总容量</summary>
Task<DashboardKpi> GetKpiAsync(string projectPath, CancellationToken ct);

/// <summary>热力图：365 天每天拍摄张数 + 主要目标</summary>
Task<IReadOnlyList<HeatmapDay>> GetHeatmapAsync(string projectPath, int year, CancellationToken ct);

/// <summary>月度统计：每月拍摄张数</summary>
Task<IReadOnlyList<MonthStat>> GetMonthlyStatsAsync(string projectPath, int year, CancellationToken ct);

/// <summary>目标排行：按标签分组计数 TOP 15</summary>
Task<IReadOnlyList<TagRank>> GetTagRankingAsync(string projectPath, CancellationToken ct);

/// <summary>焦距分布：按焦段分组计数</summary>
Task<IReadOnlyList<FocalRangeStat>> GetFocalDistributionAsync(string projectPath, CancellationToken ct);

/// <summary>ISO 分布</summary>
Task<IReadOnlyList<IsoStat>> GetIsoDistributionAsync(string projectPath, CancellationToken ct);

/// <summary>曝光时长分布</summary>
Task<IReadOnlyList<ExposureStat>> GetExposureDistributionAsync(string projectPath, CancellationToken ct);
```

**模型**：
```csharp
public sealed class DashboardKpi
{
    public int TotalPhotos { get; init; }
    public double TotalExposureHours { get; init; }  // 小时
    public int ShootingDays { get; init; }
    public int TargetCount { get; init; }
    public long TotalBytes { get; init; }
}

public sealed class HeatmapDay
{
    public DateTime Date { get; init; }
    public int Count { get; init; }
    public string? TopTarget { get; init; }
}

public sealed class MonthStat
{
    public int Month { get; init; }   // 1-12
    public int Count { get; init; }
}

public sealed class TagRank
{
    public string TagName { get; init; } = "";
    public int Count { get; init; }
    public double Percentage { get; init; }
}

public sealed class FocalRangeStat
{
    public string RangeLabel { get; init; } = "";  // "超广角 (<24mm)"
    public int Count { get; init; }
    public double Percentage { get; init; }
}

public sealed class IsoStat
{
    public string IsoLabel { get; init; } = "";  // "ISO 800"
    public int Count { get; init; }
    public double Percentage { get; init; }
}

public sealed class ExposureStat
{
    public string RangeLabel { get; init; } = "";  // "10-30s"
    public int Count { get; init; }
    public double Percentage { get; init; }
}
```

### 6.2 EXIF 列迁移（需求 §3.1）

在 `EnsureDatabase()` 中追加：

```csharp
SqliteMigrations.AddColumnIfMissing(connection, "media_files", "focal_length_35mm", "REAL");
SqliteMigrations.AddColumnIfMissing(connection, "media_files", "iso", "INTEGER");
SqliteMigrations.AddColumnIfMissing(connection, "media_files", "exposure_time", "REAL");
```

导入时从 EXIF 提取填入，缺失则留 NULL。存量照片在统计查询中用 `WHERE xxx IS NOT NULL` 排除。

### 6.3 天文复盘数据（Phase 2，依赖 D08）

#### sessions 表月相列

D09 要求在 D08 的 `sessions` 表追加月相字段（需求 §3.2.1）：

```sql
ALTER TABLE sessions ADD COLUMN moon_age REAL;           -- 月龄 0-29.53
ALTER TABLE sessions ADD COLUMN phase_icon TEXT;         -- 🌑🌒🌓🌔🌕🌖🌗🌘
ALTER TABLE sessions ADD COLUMN moon_impact TEXT;        -- None/Low/Medium/High
ALTER TABLE sessions ADD COLUMN moonrise_time TEXT;      -- HH:mm
ALTER TABLE sessions ADD COLUMN moonset_time TEXT;       -- HH:mm
```

#### astronomy-events.toml

```toml
[[meteor_showers]]
id = "quadrantids"
name = "象限仪座流星雨"
active_start = "12-28"
active_end = "01-12"
peak = "01-04"
zhr = 120

[[meteor_showers]]
id = "perseids"
name = "英仙座流星雨"
active_start = "07-17"
active_end = "08-24"
peak = "08-12"
zhr = 100

# ... 6 大流星雨 + 重要日月食，总计 < 30 条
```

#### 新增服务

| 服务 | 职责 |
|------|------|
| `EventRepository` | 启动时加载 TOML → 内存，提供 `GetEventsForDate(DateTime)` 查询 |
| `MoonRetrospectionService` | 启动时扫描 `sessions WHERE moon_age IS NULL`，用 MeeusSharp 计算 → 写回 |
| `EventHistoryMatcher` | 遍历 sessions JOIN 事件库，返回「事件 × 出摊」列表 |
| `MoonPreferenceAnalyzer` | 执行需求 §2.7.2 的 SQL 聚合，返回月相偏好数据 |

---

## 7. DashboardViewModel

### 7.1 Phase 1 属性

```csharp
public partial class DashboardViewModel : ObservableObject
{
    private readonly MediaRepository _mediaRepo;
    private readonly ConfigService _configService;

    // === 年份切换 ===
    [ObservableProperty] private int _selectedYear = DateTime.Now.Year;
    [ObservableProperty] private List<int> _availableYears = new();

    // === KPI ===
    [ObservableProperty] private DashboardKpi? _kpi;

    // === 热力图 ===
    [ObservableProperty] private IReadOnlyList<HeatmapDay> _heatmapDays = Array.Empty<HeatmapDay>();

    // === 月度统计 ===
    [ObservableProperty] private IReadOnlyList<MonthStat> _monthlyStats = Array.Empty<MonthStat>();

    // === 目标排行 ===
    [ObservableProperty] private IReadOnlyList<TagRank> _tagRanks = Array.Empty<TagRank>();

    // === 曝光参数 ===
    [ObservableProperty] private IReadOnlyList<FocalRangeStat> _focalStats = Array.Empty<FocalRangeStat>();
    [ObservableProperty] private IReadOnlyList<IsoStat> _isoStats = Array.Empty<IsoStat>();
    [ObservableProperty] private IReadOnlyList<ExposureStat> _exposureStats = Array.Empty<ExposureStat>();

    // === 空态 ===
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isLoading;

    public async Task LoadAsync()
    {
        var projectPath = _configService.GetProjectPath();
        if (string.IsNullOrEmpty(projectPath)) { IsEmpty = true; return; }

        IsLoading = true;
        try
        {
            // 并行加载所有统计
            var kpiTask = _mediaRepo.GetKpiAsync(projectPath, CancellationToken.None);
            var heatmapTask = _mediaRepo.GetHeatmapAsync(projectPath, SelectedYear, CancellationToken.None);
            var monthlyTask = _mediaRepo.GetMonthlyStatsAsync(projectPath, SelectedYear, CancellationToken.None);
            var tagRankTask = _mediaRepo.GetTagRankingAsync(projectPath, CancellationToken.None);
            var focalTask = _mediaRepo.GetFocalDistributionAsync(projectPath, CancellationToken.None);
            var isoTask = _mediaRepo.GetIsoDistributionAsync(projectPath, CancellationToken.None);
            var exposureTask = _mediaRepo.GetExposureDistributionAsync(projectPath, CancellationToken.None);

            await Task.WhenAll(kpiTask, heatmapTask, monthlyTask, tagRankTask,
                               focalTask, isoTask, exposureTask);

            Kpi = kpiTask.Result;
            HeatmapDays = heatmapTask.Result;
            MonthlyStats = monthlyTask.Result;
            TagRanks = tagRankTask.Result;
            FocalStats = focalTask.Result;
            IsoStats = isoTask.Result;
            ExposureStats = exposureTask.Result;

            IsEmpty = Kpi.TotalPhotos == 0;
        }
        finally { IsLoading = false; }
    }

    partial void OnSelectedYearChanged(int value) => _ = LoadAsync();
}
```

### 7.2 Phase 2 追加属性（依赖 D08）

```csharp
// === 天文复盘（Phase 2） ===
[ObservableProperty] private IReadOnlyList<MoonPhaseStat> _moonPhaseStats = ...;
[ObservableProperty] private IReadOnlyList<EventSessionMatch> _eventMatches = ...;
[ObservableProperty] private IReadOnlyList<SessionSummary> _topSessions = ...;
[ObservableProperty] private IReadOnlyList<SessionSummary> _allSessions = ...;
```

### 7.3 跳转回调

```csharp
// 热力图点击 → 跳转到 Gallery 该日期
[RelayCommand]
private void NavigateToDate(DateTime date)
{
    NavigateToGalleryDate?.Invoke(date);
}

// 目标排行点击 → 跳转到 Gallery 该标签
[RelayCommand]
private void NavigateToTag(string tagName)
{
    NavigateToGalleryTag?.Invoke(tagName);
}

// 出摊时间轴点击 → 跳转到 Gallery 该会话
[RelayCommand]
private void NavigateToSession(string sessionId)
{
    NavigateToGallerySession?.Invoke(sessionId);
}

// 回调委托（由 MainWindowViewModel 注入）
public Action<DateTime>? NavigateToGalleryDate { get; set; }
public Action<string>? NavigateToGalleryTag { get; set; }
public Action<string>? NavigateToGallerySession { get; set; }
```

---

## 8. 视觉规范（全部走 tokens）

| 属性 | Token | 说明 |
|------|-------|------|
| 页面背景 | `Bg.Page` | 仪表盘整体背景 |
| 卡片背景 | `Bg.Surface` | KPI 卡片、图表容器 |
| 分隔标题 | `Text.Primary` `16 SemiBold` | "🌙 天文复盘" |
| 卡片标题 | `Text.Primary` `14 SemiBold` | 图表标题 |
| 正文 | `Text.Primary` | 图表标签、数值 |
| 次要文字 | `Text.Secondary` | 单位、说明 |
| 三级文字 | `Text.Tertiary` | 空态提示 |
| 图表主色 | `Chart.Bar.Accent` | 条形图/柱状图主色 |
| 图表辅色 | `Chart.Bar.Secondary` | 次要数据系列 |
| 热力图零值 | `Chart.Heatmap.Zero` | 无拍摄日 |
| 热力图高值 | `Chart.Heatmap.Max` | 拍摄最多日 |
| 卡片圆角 | `Radius.Medium` | `8` |
| 卡片内边距 | — | `20` |

**禁止**：硬编码颜色、SkiaSharp 中直接 `new SKColor(0xFF, ...)`。

---

## 9. 边界与空态

| 状态 | 表现 |
|------|------|
| 无项目 | 仪表盘全空态：「请先在设置中选择项目目录」 |
| 有项目但无照片 | 仪表盘全空态：「导入照片后将生成统计数据」 |
| 照片无 EXIF（焦距/ISO/曝光缺失） | 相关图表标注「N 张照片参数未知」 |
| 照片无 AI 标签 | 目标排行榜空态：「AI 打标后将展示目标排行」 |
| 照片量极少（< 10 张） | 图表正常渲染但标注「数据量较少」 |
| 切换年份后无数据 | 图表空态：「该年份无拍摄记录」 |
| 会话无月相字段 | 天文区域标注「月相数据待补全」+ [立即计算] 按钮 |
| 无任何天文事件匹配 | 事件回顾模块空态：「暂无天文事件匹配记录」 |
| 出摊 < 5 次 | 月相偏好图标注「数据量较少」 |

---

## 10. 性能策略

| 场景 | 方案 |
|------|------|
| 万张照片时统计查询慢 | 统计结果缓存到 `DashboardViewModel`，切换 Tab 不重新查询 |
| 热力图 365 格 + 3 个图表同时渲染 | 各图表独立 `SKBitmap` 缓存，仅在数据变化时重绘 |
| 年份切换 | 重新加载该年份数据，not 全量 |
| 月相回溯（启动时） | 仅计算 `moon_age IS NULL` 的会话，写回 DB |
| 事件库加载 | < 10KB TOML，启动时一次性加载到内存 |

---

## 11. 实施检查清单

### Phase 1 — 基础统计

- [ ] `ViewPage.Dashboard` 枚举值 + `MainWindowViewModel` 导航属性/命令
- [ ] NavRail 新增「统计」按钮（Grid.Row 重新编号）
- [ ] `Icon.Stats` 图标（条形图风格，18×18）
- [ ] `Chart.*` 颜色 token 加入 `Colors.axaml`
- [ ] `media_files` 表加 `focal_length_35mm`、`iso`、`exposure_time` 列
- [ ] `MediaRepository` 新增 7 个统计查询方法
- [ ] `DashboardViewModel` 实现（Phase 1 属性 + LoadAsync）
- [ ] `DashboardView.axaml` 全屏布局（ScrollViewer + KPI 行 + 图表行）
- [ ] `StatCard` 控件（5 个 KPI 卡片）
- [ ] `CalendarHeatmap` 控件（SkiaSharp 自绘，含 hover + 点击）
- [ ] `BarChart` 控件（目标排行 + 月度柱状图 + 曝光分布）
- [ ] 年份切换 `ComboBox` + 空态处理
- [ ] 热力图点击 → 跳转 Gallery 日期
- [ ] 目标排行点击 → 跳转 Gallery 标签过滤
- [ ] 主题切换（DeepSpace ↔ RedNightVision）下图表颜色即时更新
- [ ] `dotnet build` 0 warnings 0 errors

### Phase 2 — 天文复盘（依赖 D08）

- [ ] `sessions` 表建表（D08）+ 月相列追加（D09）
- [ ] `astronomy-events.toml` + `EventRepository`
- [ ] `MoonRetrospectionService`（MeeusSharp 集成）
- [ ] `EventHistoryMatcher` + `MoonPreferenceAnalyzer`
- [ ] `DashboardViewModel` Phase 2 属性 + 数据加载
- [ ] `DashboardView.axaml` 天文复盘区域 UI（月相偏好 + 事件×出摊 + Top 5 + 全部时间轴）
- [ ] 出摊时间轴滚动加载
- [ ] 出摊时间轴点击 → 跳转 Gallery 会话

### Phase 3 — 年度总结

- [ ] `YearInReviewView` + `YearInReviewViewModel`
- [ ] 异步生成 PNG 长图（SkiaSharp 渲染 + 进度条）
- [ ] 分享功能（保存到文件）

---

## 12. 不做清单

| 内容 | 理由 |
|------|------|
| 与云端数据对比（本地 vs OSS） | 统计聚焦本地数据 |
| 与其他用户对比/排行 | 无在线账户系统 |
| 按器材分组统计 | 依赖 D13，Phase 2 |
| 导出为 Excel/CSV | 可视化为主 |
| 实时统计刷新 | 数据变更频率低 |
| 月相预测 / 未来事件列表 | 纯描述型定位 |
| 拍摄建议 / "最适合拍什么" | 纯描述型，不做主观建议 |
| 任何外部 API | 全部本地 |
| 系统通知 / 提醒 | 纯描述型 |