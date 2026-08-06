# S03 附录 — 代码结构设计

> 关联：`spec/03-shooting-diary.md`（主规格）、`demand/03-shooting-diary.md`（需求）、`0.11/demand/08-shooting-session.md`（会话数据层复用）

---

## 1. 整体架构

```
MainWindow
├── NavRail (日记入口)
└── ContentControl
    └── DiaryView
        ├── 翻页区
        │   ├── ← 按钮 (上一页)
        │   ├── DiaryPage (当前页)
        │   └── → 按钮 (下一页)
        └── TimelineBar (底部时间轴)
```

### 依赖关系图

```
DiaryViewModel
├── ISessionRepository ────── SessionRepository ─── SQLite
├── IMediaRepository ──────── MediaRepository ────── SQLite
├── IConfigService ────────── ConfigService ──────── config.db
├── SessionClusteringService ─┐
│   ├── IMediaRepository      │
│   └── ISessionRepository    │
├── EnvironmentService ───────┤
│   ├── HttpClient            │
│   └── IConfigService ───────┘
└── Navigation Callbacks
    ├── NavigateToLightbox(IReadOnlyList<MediaFile>, int index)
    ├── NavigateToGalleryDate(DateTime date)
    └── NavigateToGalleryTag(string tag)
```

---

## 2. 数据层

### 2.1 Models/Session.cs

```csharp
namespace StartTooler.Models;

public sealed class Session
{
    public string Id { get; init; } = "";           // GUID
    public string ProjectPath { get; init; } = "";
    public string Title { get; set; } = "";          // 自动生成，可手动改
    public string Description { get; set; } = "";    // 笔记
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Location { get; set; } = "";       // 地点
    public string WindDir { get; set; } = "";        // 风向
    public int WindLevel { get; set; }               // 风级 0-12
    public string CloudCover { get; set; } = "";     // 云量
    public int? Bortle { get; set; }                 // 光害 1-9
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // 计算属性
    public TimeSpan Duration => EndTime - StartTime;
    public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours}h{(int)Duration.TotalMinutes % 60}m"
        : $"{(int)Duration.TotalMinutes}m";

    // 天气图标 key（从 wind_dir + cloud_cover 推导）
    public string? WeatherIconKey => CloudCover switch
    {
        "晴" => "Icon.Weather.Sunny",
        "少云" => "Icon.Weather.PartlyCloudy",
        "多云" => "Icon.Weather.Cloudy",
        "阴" => "Icon.Weather.Overcast",
        _ => null
    };

    public string? WeatherText => string.IsNullOrEmpty(CloudCover) ? null
        : string.IsNullOrEmpty(WindDir) ? CloudCover
        : $"{WindDir}风 {WindLevel}级 · {CloudCover}";
}
```

### 2.2 Data/ISessionRepository.cs

```csharp
namespace StartTooler.Data;

public interface ISessionRepository
{
    // 查询
    Task<IReadOnlyList<Session>> GetByProjectAsync(string projectPath, CancellationToken ct = default);
    Task<Session?> GetByIdAsync(string sessionId, CancellationToken ct = default);

    // 写入
    Task UpsertAsync(Session session, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);

    // 批量操作
    Task UpsertBatchAsync(IReadOnlyList<Session> sessions, CancellationToken ct = default);
    Task DeleteBatchAsync(IReadOnlyList<string> sessionIds, CancellationToken ct = default);
}
```

### 2.3 Data/SessionRepository.cs

职责：
- 管理 `sessions` 表的 CRUD
- 构造函数接收 `_connectionString`（同 MediaRepository 模式）
- Upsert：`INSERT ... ON CONFLICT(id) DO UPDATE`
- 按 `start_time DESC` 排序

### 2.4 Data/IMediaRepository.cs 新增方法

```csharp
// 按会话获取照片
Task<IReadOnlyList<MediaFile>> GetBySessionAsync(
    string sessionId, SortMode sortMode = SortMode.TimeDesc,
    int offset = 0, int limit = 2000, CancellationToken ct = default);

// 精选照片管理
Task SetDiaryFeaturedAsync(long fileId, bool isFeatured, CancellationToken ct = default);
Task<IReadOnlyList<MediaFile>> GetDiaryFeaturedAsync(
    string sessionId, int limit = 5, CancellationToken ct = default);

// 会话统计
Task<SessionStats> GetSessionStatsAsync(string sessionId, CancellationToken ct = default);
```

### 2.5 Models/SessionStats.cs (新增)

```csharp
namespace StartTooler.Models;

public sealed class SessionStats
{
    public int TotalPhotos { get; init; }
    public int TargetCount { get; init; }           // 不同标签数
    public double TotalExposureHours { get; init; }
    public IReadOnlyList<string> TopTags { get; init; } = Array.Empty<string>();  // 前 3 个
}
```

### 2.6 Data/MediaRepository.cs 新增实现

- `GetBySessionAsync`: `WHERE session_id = @sessionId AND deleted_at IS NULL`
- `SetDiaryFeaturedAsync`: `UPDATE media_files SET is_diary_featured = @value WHERE id = @id`
- `GetDiaryFeaturedAsync`: `WHERE session_id = @sessionId AND is_diary_featured = 1 ORDER BY score DESC LIMIT @limit`
- `GetSessionStatsAsync`: 聚合查询（COUNT, COUNT DISTINCT tags, SUM exposure_time, TOP 3 tags）

### 2.7 DB 迁移

```sql
-- sessions 表
CREATE TABLE IF NOT EXISTS sessions (
    id          TEXT PRIMARY KEY,
    project_path TEXT NOT NULL,
    title       TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    start_time  TEXT NOT NULL,
    end_time    TEXT NOT NULL,
    location    TEXT NOT NULL DEFAULT '',
    wind_dir    TEXT NOT NULL DEFAULT '',
    wind_level  INTEGER NOT NULL DEFAULT 0,
    cloud_cover TEXT NOT NULL DEFAULT '',
    bortle      INTEGER,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sessions_project ON sessions(project_path);

-- media_files 扩展
ALTER TABLE media_files ADD COLUMN session_id TEXT;
ALTER TABLE media_files ADD COLUMN is_diary_featured INTEGER DEFAULT 0;
CREATE INDEX IF NOT EXISTS idx_media_files_session ON media_files(session_id);
```

---

## 3. 服务层

### 3.1 Services/SessionClusteringService.cs

```csharp
namespace StartTooler.Services;

public class SessionClusteringService
{
    private readonly IMediaRepository _mediaRepo;
    private readonly ISessionRepository _sessionRepo;

    public SessionClusteringService(IMediaRepository mediaRepo, ISessionRepository sessionRepo);

    /// <summary>
    /// 对项目下所有照片按时间间隔聚类，生成/更新会话。
    /// 已有手动分配的 session_id 不会被覆盖。
    /// </summary>
    /// <param name="projectPath">项目路径</param>
    /// <param name="intervalHours">切分间隔（小时），默认 4</param>
    /// <returns>生成的会话列表</returns>
    public async Task<IReadOnlyList<Session>> ClusterAsync(
        string projectPath, int intervalHours = 4, CancellationToken ct = default);
}
```

**算法**：
1. 查询所有 `shot_at IS NOT NULL AND session_id IS NULL` 的照片，按 shot_at 排序
2. 一趟扫描：`gap = (next.shot_at - current.shot_at).TotalHours`，gap > intervalHours → 切分
3. 每组生成一个 Session（Guid.NewGuid().ToString()，标题 `{最早日期} 出摊`）
4. 批量 Upsert sessions + 批量 UPDATE media_files.session_id

### 3.2 Services/EnvironmentService.cs

```csharp
namespace StartTooler.Services;

public class EnvironmentService
{
    private readonly HttpClient _http;
    private readonly IConfigService _configService;

    public EnvironmentService(HttpClient http, IConfigService configService);

    /// <summary>
    /// 逆地理编码：经纬度 → 地名。
    /// 优先高德 API（需 Key），否则走 Nominatim（免费，限速 1 req/s）。
    /// </summary>
    public async Task<string?> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct = default);

    /// <summary>
    /// 获取历史天气：经纬度 + 日期 → 风向/风级/云量。
    /// 使用 Open-Meteo Archive API（免费，无需 Key）。
    /// </summary>
    public async Task<WeatherData?> GetHistoricalWeatherAsync(
        double lat, double lon, DateTime date, CancellationToken ct = default);

    /// <summary>
    /// 从 EXIF GPS 坐标获取环境数据（地点 + 天气），一站式调用。
    /// 无 GPS → 返回 null。
    /// </summary>
    public async Task<EnvironmentData?> FetchAsync(
        string? imagePath, DateTime date, CancellationToken ct = default);
}

public sealed class WeatherData
{
    public string? WindDir { get; init; }
    public int WindLevel { get; init; }
    public string? CloudCover { get; init; }
}

public sealed class EnvironmentData
{
    public string? Location { get; init; }
    public WeatherData? Weather { get; init; }
}
```

### 3.3 Converters/ExifInfoConverter.cs 扩展

在 `ExifReader` 中新增 GPS 解析：

```
// IFD0 中找 tag 0x8825 (GPSInfo IFD Pointer) → 跳转到 GPS IFD
// GPS IFD 中解析:
//   tag 0x0001 GPSLatitudeRef  → "N" / "S"
//   tag 0x0002 GPSLatitude     → 3 rationals (度, 分, 秒)
//   tag 0x0003 GPSLongitudeRef → "E" / "W"
//   tag 0x0004 GPSLongitude    → 3 rationals
```

新增方法：
```csharp
public static (double Latitude, double Longitude)? ReadGps(string? path);
```

---

## 4. ViewModel 层

### 4.1 ViewModels/DiaryViewModel.cs

```csharp
namespace StartTooler.ViewModels;

public partial class DiaryViewModel : ObservableObject
{
    // === 依赖 ===
    private readonly IMediaRepository _mediaRepo;
    private readonly ISessionRepository _sessionRepo;
    private readonly IConfigService _configService;
    private readonly SessionClusteringService _clusteringService;
    private readonly EnvironmentService _envService;

    // === 注入回调 ===
    public Action<IReadOnlyList<MediaFile>, int>? NavigateToLightbox { get; set; }
    public Action<DateTime>? NavigateToGalleryDate { get; set; }
    public Action<string>? NavigateToGalleryTag { get; set; }

    // === 数据 ===
    [ObservableProperty] private IReadOnlyList<DiaryPageData> _allPages = Array.Empty<DiaryPageData>();
    [ObservableProperty] private int _currentPageIndex;
    [ObservableProperty] private DiaryPageData? _currentPage;

    // === 导航状态 ===
    [ObservableProperty] private bool _hasPrev;
    [ObservableProperty] private bool _hasNext;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyMessage = "导入照片后，拍摄日记将自动生成";

    // === 构造 ===
    public DiaryViewModel(
        IMediaRepository mediaRepo,
        ISessionRepository sessionRepo,
        IConfigService configService,
        SessionClusteringService clusteringService,
        EnvironmentService envService);

    // === 生命周期 ===
    public async Task LoadAsync();

    // === 导航命令 ===
    [RelayCommand] private void NavigatePrev();
    [RelayCommand] private void NavigateNext();
    [RelayCommand] private void NavigateToPage(int index);

    // === 会话管理 ===
    [RelayCommand] private async Task MergeSessions();
    [RelayCommand] private async Task SplitSession();
    [RelayCommand] private async Task DeleteCurrentSession();
    [RelayCommand] private async Task CreateSession();

    // === 笔记 ===
    [RelayCommand] private async Task SaveNotes(string notes);

    // === 精选照片 ===
    [RelayCommand] private async Task AddFeaturedPhoto(MediaFile photo);
    [RelayCommand] private async Task RemoveFeaturedPhoto(MediaFile photo);

    // === 点击跳转 ===
    [RelayCommand] private void OpenPhoto(MediaFile photo);
    [RelayCommand] private void NavigateToTag(string tag);
    [RelayCommand] private void NavigateToDate(DateTime date);

    // === 内部 ===
    private async Task RefreshCurrentPageAsync();
    private Task EnsureClusteredAsync(string projectPath);
}
```

**关键逻辑**：

1. `LoadAsync()`:
   - 获取 projectPath → 检查是否为空
   - 调用 `EnsureClusteredAsync()` 确保已有聚类
   - 加载所有 sessions → 构建 `DiaryPageData` 列表
   - 设置 `CurrentPageIndex = allPages.Count - 1`（最新）
   - 调用 `RefreshCurrentPageAsync()` 加载当前页详情

2. `EnsureClusteredAsync()`:
   - 检查 sessions 表是否有数据
   - 无数据 → 调用 `clusteringService.ClusterAsync()`
   - 有数据 → 检查是否有未归类的照片 → 增量聚类

3. `RefreshCurrentPageAsync()`:
   - 从 session 加载精选照片、统计
   - 异步获取环境数据（如有 GPS）

4. 翻页：修改 `CurrentPageIndex` → 更新 `HasPrev`/`HasNext` → 调用 `RefreshCurrentPageAsync()`

### 4.2 Models/DiaryPageData.cs (新增)

```csharp
namespace StartTooler.Models;

public sealed class DiaryPageData
{
    public string SessionId { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime Date { get; init; }
    public string? Location { get; set; }
    public string? WeatherIconKey { get; set; }
    public string? WeatherText { get; set; }
    public string Notes { get; set; } = "";
    public IReadOnlyList<MediaFile> FeaturedPhotos { get; set; } = Array.Empty<MediaFile>();
    public int TotalPhotoCount { get; set; }
    public int TargetCount { get; set; }
    public string TotalExposureText { get; set; } = "";
    public IReadOnlyList<string> TopTags { get; set; } = Array.Empty<string>();
}
```

### 4.3 ViewModels/MainWindowViewModel.cs 修改

```diff
- ViewPage.Dashboard
+ ViewPage.Diary

- DashboardViewModel dashboardViewModel
+ DiaryViewModel diaryViewModel

- IsStatsActive
+ IsDiaryActive

- NavStatsTooltip
+ NavDiaryTooltip

- NavigateToStats()
+ NavigateToDiary()

- DashboardViewModel = new DashboardViewModel(_mediaRepository, _configService);
+ diaryViewModel = new DiaryViewModel(
+     _mediaRepository, new SessionRepository(), _configService,
+     new SessionClusteringService(_mediaRepository, new SessionRepository()),
+     new EnvironmentService(new HttpClient(), _configService));
+ diaryViewModel.NavigateToGalleryDate = ...;
+ diaryViewModel.NavigateToGalleryTag = ...;
+ diaryViewModel.NavigateToLightbox = ...;
```

---

## 5. View 层

### 5.1 Views/DiaryView.axaml

```
UserControl
├── Grid (空态)
│   └── 空态提示
└── Grid (内容)
    ├── Grid.Row=0: 翻页区
    │   ├── Button ← (上一页, IsVisible=HasPrev)
    │   ├── ScrollViewer
    │   │   └── controls:DiaryPage
    │   └── Button → (下一页, IsVisible=HasNext)
    └── Grid.Row=1: controls:TimelineBar
```

### 5.2 Views/DiaryView.axaml.cs

```csharp
// 职责：
// - 键盘左右方向键翻页
// - 路由 DiaryPage 的事件到 DiaryViewModel
public partial class DiaryView : UserControl
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Left && DataContext is DiaryViewModel vm && vm.HasPrev)
            vm.NavigatePrevCommand.Execute(null);
        else if (e.Key == Key.Right && DataContext is DiaryViewModel vm && vm.HasNext)
            vm.NavigateNextCommand.Execute(null);
    }
}
```

### 5.3 Controls/DiaryPage.axaml

```
UserControl (x:DataType="models:DiaryPageData")
├── StackPanel
│   ├── 头部行
│   │   ├── TextBlock 日期 (FontSize=18, SemiBold)
│   │   ├── PathIcon 天气 (IsVisible=WeatherIconKey!=null)
│   │   ├── TextBlock 天气文本
│   │   └── TextBlock 地点 (可点击编辑)
│   ├── 精选照片网格
│   │   └── ItemsControl (WrapPanel)
│   │       └── Image + 移除按钮 (hover 显示)
│   ├── 笔记区
│   │   └── TextBox (多行, AcceptsReturn, 失焦保存)
│   └── 统计条
│       ├── TextBlock "拍摄 N 张"
│       ├── TextBlock "N 个目标"
│       └── TextBlock "累计曝光 Xh"
```

### 5.4 Controls/TimelineBar.axaml

```
UserControl
├── ItemsControl (水平排列)
│   └── 每个会话 = 一个圆点
│       ├── 有数据 → 实心圆
│       ├── 当前页 → 高亮色
│       └── ToolTip = 日期 + 标题
```

### 5.5 Controls/WeatherIconPicker.axaml

```
Popup/Flyout
├── Grid (4列)
│   ├── Button ☀️ 晴
│   ├── Button ⛅ 少云
│   ├── Button ☁️ 多云
│   ├── Button 🌥️ 阴
│   ├── Button 🌧️ 雨
│   ├── Button 🌨️ 雪
│   ├── Button 🌫️ 雾
│   └── Button ❌ 清除
```

### 5.6 Views/MainWindow.axaml 修改

```diff
- <DataTemplate DataType="{x:Type vm:DashboardViewModel}">
-     <views:DashboardView DataContext="{Binding}"/>
- </DataTemplate>
+ <DataTemplate DataType="{x:Type vm:DiaryViewModel}">
+     <views:DiaryView DataContext="{Binding}"/>
+ </DataTemplate>
```

### 5.7 Controls/NavRail.axaml 修改

```diff
- 统计按钮 (Grid.Row=2)
+ 日记按钮 (Grid.Row=2)
- Classes.active="{Binding IsStatsActive}"
+ Classes.active="{Binding IsDiaryActive}"
- Command="{Binding NavigateToStatsCommand}"
+ Command="{Binding NavigateToDiaryCommand}"
- ToolTip.Tip="{Binding NavStatsTooltip}"
+ ToolTip.Tip="{Binding NavDiaryTooltip}"
- PathIcon Data="{DynamicResource Icon.Stats}"
+ PathIcon Data="{DynamicResource Icon.Diary}"
- TextBlock Text="统计"
+ TextBlock Text="日记"
```

### 5.8 Themes/Icons.axaml 新增

```xml
<!-- 日记本图标 -->
<StreamGeometry x:Key="Icon.Diary">M14 2H6C4.89 2 4 2.89 4 4V20C4 ...</StreamGeometry>

<!-- 天气图标组 -->
<StreamGeometry x:Key="Icon.Weather.Sunny">M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 ...</StreamGeometry>
<StreamGeometry x:Key="Icon.Weather.PartlyCloudy">...</StreamGeometry>
<StreamGeometry x:Key="Icon.Weather.Cloudy">...</StreamGeometry>
<StreamGeometry x:Key="Icon.Weather.Overcast">...</StreamGeometry>
<StreamGeometry x:Key="Icon.Weather.Rain">...</StreamGeometry>
<StreamGeometry x:Key="Icon.Weather.Snow">...</StreamGeometry>
<StreamGeometry x:Key="Icon.Weather.Fog">...</StreamGeometry>
```

---

## 6. 设置页扩展

### 6.1 Services/AppConfig.cs

```diff
+ public int SessionIntervalHours { get; set; } = 4;
+ public string? AmapApiKey { get; set; }
```

### 6.2 Views/SettingsView.axaml + SettingsViewModel.cs

在「通用」Tab 新增：
- 会话间隔：NumericUpDown 或 Slider（1-24 小时，默认 4）
- 高德 API Key：TextBox（可选，带说明文字）

---

## 7. 删除清单（Phase 1 详细）

### 7.1 删除文件（14 个）

```
Views/DashboardView.axaml
Views/DashboardView.axaml.cs
ViewModels/DashboardViewModel.cs
Models/DashboardModels.cs
Controls/StatCard.axaml
Controls/StatCard.axaml.cs
Controls/CalendarHeatmap.axaml
Controls/CalendarHeatmap.axaml.cs
Controls/SkiaHeatmap.axaml
Controls/SkiaHeatmap.axaml.cs
Controls/SkiaBarChart.axaml
Controls/SkiaBarChart.axaml.cs
Controls/BarChart.axaml
Controls/BarChart.axaml.cs
```

### 7.2 修改文件（6 个）

| 文件 | 修改内容 |
|---|---|
| `Data/IMediaRepository.cs` | 删除 L52-65（Dashboard 方法），保留 `GetLatestPhotoYearAsync`/`GetLatestPhotoDateAsync` |
| `Data/MediaRepository.cs` | 删除 Dashboard 方法实现（约 800 行），保留 `GetLatestPhotoYearAsync`/`GetLatestPhotoDateAsync` |
| `Data/DateCount.cs` | 删除 `DateBucket` 和 `DateBucketSet` 类 |
| `ViewModels/MainWindowViewModel.cs` | 替换 Dashboard 为 Diary，见 §4.3 |
| `Views/MainWindow.axaml` | 替换 DataTemplate，更新快捷键，见 §5.6 |
| `Controls/NavRail.axaml` | 替换导航按钮，见 §5.7 |

---

## 8. 实施顺序

```
Phase 1 (删除) → 编译验证 → 提交
Phase 2 (数据层) → 编译验证
Phase 3 (服务层) → 编译验证
Phase 4 (设置页) → 编译验证
Phase 5 (UI 层) → 编译验证 → 提交
Phase 6 (集成) → 编译验证 → 提交
```