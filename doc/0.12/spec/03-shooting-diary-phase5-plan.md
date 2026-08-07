# S03 Phase 5 — UI 层实施计划

> 关联：`spec/03-shooting-diary.md`（主规格）、`spec/03-shooting-diary-code-structure.md`（§4 VM/§5 View）

---

## 目标

完成日记本的所有 UI：

1. `DiaryPageData` 模型
2. `DiaryViewModel`（含分页、聚类触发、笔记保存）
3. `DiaryPage`（日记页内容）
4. `TimelineBar`（底部时间轴）
5. `DiaryView`（容器 + 翻页控制 + 键盘导航）
6. 接入 `MainWindowViewModel.NavigateToDiary()`
7. `MainWindow.axaml` DataTemplate 替换
8. `NavRail` 文字最终化（已替换）

---

## Step 1: DiaryPageData 模型（已完成）

**文件：** `Models/DiaryPageData.cs`

纯数据载体，XAML 直接绑定。所有计算属性（DurationText、TotalExposureText）由 VM 计算填入。

---

## Step 2: DiaryViewModel 完整实现

**文件：** `ViewModels/DiaryViewModel.cs`（重写）

### 2.1 依赖注入

构造函数接收：
- `IMediaRepository`
- `ISessionRepository`
- `IConfigService`
- `SessionClusteringService`
- `EnvironmentService`
- `HttpClient`（注入给 EnvironmentService，Phase 3 已实现）
- `Func<HttpClient>` 工厂或单独 HttpClient 实例

### 2.2 属性

```csharp
public ObservableCollection<DiaryPageData> AllPages { get; }
public int CurrentPageIndex { get; set; }  // 双向
public DiaryPageData? CurrentPage { get; }
public bool HasPrev => CurrentPageIndex > 0;
public bool HasNext => CurrentPageIndex < AllPages.Count - 1;
public bool IsLoading { get; set; }
public bool IsEmpty => !IsLoading && AllPages.Count == 0;
public string EmptyMessage { get; } = "导入照片后，拍摄日记将自动生成";
```

### 2.3 命令

```csharp
[RelayCommand] NavigatePrev()
[RelayCommand] NavigateNext()
[RelayCommand] NavigateToPage(int index)
[RelayCommand] async Task SaveNotesAsync()
[RelayCommand] async Task DeleteCurrentSessionAsync()
[RelayCommand] async Task RefreshEnvironmentAsync()  // 用户点击"刷新天气/地点"
```

### 2.4 回调（由 MainWindowViewModel 注入）

```csharp
public Action<IReadOnlyList<MediaFile>, int>? NavigateToLightbox { get; set; }
public Action<DateTime>? NavigateToGalleryDate { get; set; }
public Action<string>? NavigateToGalleryTag { get; set; }
```

### 2.5 关键流程

**LoadAsync(projectPath)**：
```
1. IsLoading = true
2. var intervalHours = await GetIntervalHoursFromConfig()
3. await _clusteringService.ClusterAsync(projectPath, intervalHours)
4. var sessions = await _sessionRepo.GetByProjectAsync(projectPath)
5. AllPages.Clear(); foreach (sessions → DiaryPageData)
6. CurrentPageIndex = AllPages.Count - 1  // 最新
7. await RefreshCurrentPageAsync()
8. IsLoading = false
```

**RefreshCurrentPageAsync()**：
```
1. var page = CurrentPage
2. if (page == null) return
3. var featured = await _mediaRepo.GetDiaryFeaturedAsync(page.SessionId, limit: 12)
4. var stats = await _mediaRepo.GetSessionStatsAsync(page.SessionId)
5. page.FeaturedPhotos = featured
6. page.TotalPhotoCount = stats.TotalPhotos
7. page.TargetCount = stats.TargetCount
8. page.TotalExposureText = FormatExposure(stats.TotalExposureHours)
9. page.TopTags = stats.TopTags
10. // 已有 Location/WeatherText/Notes 不覆盖（用户手动输入优先）
11. // 如果任一字段为空且日记页配置的环境 API 可用 → 触发异步获取
```

**EnsureSessionAsync(sessionId)**（每次显示新页时）：
```
1. var session = await _sessionRepo.GetByIdAsync(sessionId)
2. 更新 CurrentPage 的基础字段（Title, Date, DurationText, Location, WeatherText, WeatherIconKey）
3. 如果 Location 或 WeatherText 为空 → 异步 FetchAsync(首张照片路径 + Date)
```

### 2.6 自动精选逻辑

```csharp
private async Task<IReadOnlyList<MediaFile>> AutoSelectFeaturedAsync(string sessionId, int limit)
{
    var all = await _mediaRepo.GetBySessionAsync(sessionId, SortMode.ScoreDesc);
    if (all.Count == 0) return Array.Empty<MediaFile>();

    // 评分高的优先 + 时间均匀采样
    var scored = all.Where(m => m.Score.HasValue).OrderByDescending(m => m.Score).Take(limit).ToList();
    if (scored.Count >= limit) return scored;

    // 剩余从时间维度均匀采样
    var remaining = limit - scored.Count;
    var pool = all.Except(scored).ToList();
    if (pool.Count == 0) return scored;
    var step = pool.Count / (double)remaining;
    var sampled = new List<MediaFile>();
    for (int i = 0; i < remaining && i < pool.Count; i++)
    {
        sampled.Add(pool[(int)(i * step)]);
    }
    return scored.Concat(sampled).ToList();
}
```

---

## Step 3: DiaryPage XAML

**文件：** `Controls/DiaryPage.axaml` + `Controls/DiaryPage.axaml.cs`

### 3.1 结构

```
UserControl (x:DataType="models:DiaryPageData")
└── Border (圆角 + 背景)
    └── StackPanel (Vertical, Spacing=20)
        ├── 头部行 (Grid 3 列)
        │   ├── 左: 日期 + 标题 + 时长
        │   ├── 中: 天气图标 + 天气文本 + 地点
        │   └── 右: 操作按钮（删除当前页、刷新环境）
        ├── 精选照片网格 (ItemsControl + WrapPanel)
        ├── 笔记区 (TextBox, LostFocus → VM.SaveNotes)
        └── 统计条 (Grid 4 列: 张数/目标/曝光/标签)
```

### 3.2 关键绑定

```xml
<!-- 日期 + 标题 -->
<TextBlock Text="{Binding Date, StringFormat='{}{0:yyyy年M月d日}'}" FontSize="20" FontWeight="SemiBold"/>
<TextBlock Text="{Binding Title}" FontSize="14"/>
<TextBlock Text="{Binding DurationText}" FontSize="12"/>

<!-- 天气图标 -->
<Path Grid.Column="0" Data="{DynamicResource Icon.Weather.Sunny}" 
      IsVisible="{Binding WeatherIconKey, Converter=...}"/>

<!-- 地点 -->
<TextBlock Text="{Binding Location}" />

<!-- 精选照片 -->
<ItemsControl ItemsSource="{Binding FeaturedPhotos}">
    <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
    <DataTemplate>
        <Border>
            <Image Source="{Binding ThumbnailPath}" />
        </Border>
    </DataTemplate>
</ItemsControl>

<!-- 笔记 -->
<TextBox Text="{Binding Notes, Mode=TwoWay}" 
         LostFocus="OnNotesLostFocus"
         AcceptsReturn="True" TextWrapping="Wrap"/>
```

---

## Step 4: TimelineBar XAML

**文件：** `Controls/TimelineBar.axaml` + `Controls/TimelineBar.axaml.cs`

### 4.1 结构

```xml
<UserControl>
  <ScrollViewer HorizontalScrollBarVisibility="Auto">
    <ItemsControl ItemsSource="{Binding Sessions}">
      <ItemsPanelTemplate><StackPanel Orientation="Horizontal"/></ItemsPanelTemplate>
      <DataTemplate>
        <Button Command="{Binding DataContext.NavigateToPageCommand, RelativeSource={...}}"
                CommandParameter="{Binding Index}"
                Width="14" Height="14">
          <Ellipse Width="10" Height="10" Fill="..." />
        </Button>
      </DataTemplate>
    </ItemsControl>
  </ScrollViewer>
</UserControl>
```

### 4.2 交互

- 当前页 → 高亮色（圆形填充）
- 其他页 → 灰色空心圆
- Hover → 显示 ToolTip（日期 + 标题）
- 点击 → VM.NavigateToPage(index)

---

## Step 5: DiaryView XAML

**文件：** `Views/DiaryView.axaml` + `Views/DiaryView.axaml.cs`

### 5.1 结构

```
UserControl
└── Grid
    ├── Row 0: 空态（IsVisible=IsEmpty）
    │   └── StackPanel（提示文字 + Button "立即导入" → NavigateToGallery）
    └── Row 0: 加载态/内容态（IsVisible=!IsEmpty）
        ├── Row 0: 翻页区
        │   ├── ← 按钮（IsVisible=HasPrev, Command=NavigatePrev）
        │   ├── ScrollViewer
        │   │   └── controls:DiaryPage DataContext={Binding CurrentPage}
        │   └── → 按钮（IsVisible=HasNext）
        └── Row 1: controls:TimelineBar
```

### 5.2 code-behind

```csharp
protected override void OnKeyDown(KeyEventArgs e)
{
    if (DataContext is not DiaryViewModel vm) return;
    if (e.Key == Key.Left && vm.HasPrev) vm.NavigatePrevCommand.Execute(null);
    else if (e.Key == Key.Right && vm.HasNext) vm.NavigateNextCommand.Execute(null);
}
```

---

## Step 6: 接入 MainWindow

### 6.1 MainWindowViewModel

修改构造函数：

```csharp
private readonly SessionRepository _sessionRepository = new();
private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

DiaryViewModel = new DiaryViewModel(
    _mediaRepository, _sessionRepository, _configService,
    new SessionClusteringService(_mediaRepository, _sessionRepository),
    new EnvironmentService(_httpClient, _configService));

DiaryViewModel.NavigateToGalleryDate = NavigateToGalleryAndDateAsync;
DiaryViewModel.NavigateToGalleryTag = NavigateToGalleryAndTagAsync;
DiaryViewModel.NavigateToLightbox = NavigateToLightbox;

DiaryViewModel.LoadAsync();  // fire-and-forget
```

### 6.2 NavigateToDiary

```csharp
private async Task NavigateToDiary()
{
    CurrentPage = ViewPage.Diary;
    CurrentView = DiaryViewModel;
    await DiaryViewModel.LoadAsync(GetCurrentProjectPath());
}
```

### 6.3 MainWindow.axaml

DataTemplate 已有绑定：

```xml
<DataTemplate DataType="{x:Type vm:DiaryViewModel}">
    <views:DiaryView DataContext="{Binding}"/>
</DataTemplate>
```

只需确保 DiaryView 已注册。

---

## Step 7: NavRail 图标

`Controls/NavRail.axaml` 已替换 "统计" → "日记"（Phase 1 已做）。

`Themes/Icons.axaml` 不新增 Icon.Diary（暂用 Icon.Book 或 Icon.Calendar）。

---

## 文件变更清单

| 操作 | 文件 | 行数(估) |
|------|------|----------|
| 新建 | `Models/DiaryPageData.cs` | ~50 |
| 重写 | `ViewModels/DiaryViewModel.cs` | ~350 |
| 新建 | `Controls/DiaryPage.axaml` + cs | ~150 |
| 新建 | `Controls/TimelineBar.axaml` + cs | ~80 |
| 重写 | `Views/DiaryView.axaml` + cs | ~80 |
| 修改 | `ViewModels/MainWindowViewModel.cs` | +20 |

总计约 +730 行。

---

## 关键设计决策

1. **空态优先**：没有照片时不显示空翻页控件，直接"导入照片后自动生成"提示
2. **手动优先**：用户修改 Location/Weather 后不自动覆盖（VM 仅在字段为空时填充）
3. **键盘快捷键**：左右方向键翻页（XAML 通过 OnKeyDown 路由）
4. **时间轴**：用 WrapPanel 水平排列，不做缩放（数据量 < 1000 个会话时足够）
5. **不实现 CreateSession/ManualSplit/MergeSession**（Phase 6+ 才做用户操作）