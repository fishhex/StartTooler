# S03 — 拍摄日记（替换数据统计）

> 关联文档：`demand/08-shooting-session.md`（会话数据模型与聚类）、`spec/19-statistics-dashboard.md`（将被移除的旧仪表盘）

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v1.0** |
| 目标用户 | 天文摄影爱好者 |
| 文档状态 | **需求已确认，待实施** |
| 前置依赖 | 复用 `demand/08-shooting-session.md` 的数据层（sessions 表、聚类算法、环境 API） |

---

## 1. 需求概述

将现有「数据统计」仪表盘**完全替换**为「拍摄日记」。以"一次完整的外出拍摄会话"为粒度，用翻页日记本的形式回顾拍摄历程。

### 1.1 与旧 Dashboard 的关系

- **Dashboard 全部移除**：KPI 卡片、日历热力图、柱状图、排行榜、焦距/ISO/曝光分布等全部删除
- 侧边栏导航入口从「统计」改为「日记」，图标替换
- 快捷键 Ctrl+5 保持，跳转到日记页

### 1.2 核心概念

| 维度 | 旧（Dashboard） | 新（拍摄日记） |
|---|---|---|
| 粒度 | 年/季度/月 聚合统计 | 一次拍摄会话 |
| 展示形式 | 数据图表 | 翻页日记本 |
| 内容 | 数字、百分比 | 照片精选、天气、地点、笔记 |
| 用户参与 | 只读 | 可编辑笔记、精选照片、管理会话 |

---

## 2. 功能需求

### 2.1 会话检测

复用 `demand/08-shooting-session.md §2.1` 的聚类算法，但时间阈值改为**可配置**：

- 算法：按 `shot_at` 排序 → 一趟扫描，相邻间隔 > N 小时则切分
- 默认 N = 4 小时
- N 值在设置页可配置
- 跨午夜自动合并（同一天内按间隔判断）

### 2.2 日记页布局

每页日记 = 一个拍摄会话，内容布局：

```
┌──────────────────────────────────────────────┐
│  2026年3月15日  ☀️ 晴  浙江省杭州市临安区天荒坪  │  ← 头部：日期 + 天气图标 + 地点
│                                              │
│  ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐        │
│  │ 📷 │ │ 📷 │ │ 📷 │ │ 📷 │ │ 📷 │  ...   │  ← 精选照片网格（可点击放大）
│  └────┘ └────┘ └────┘ └────┘ └────┘        │
│                                              │
│  今晚终于拍到了猎户座大星云，导星很稳...        │  ← 用户笔记（多行文本，可编辑）
│                                              │
│  拍摄 47 张 | 2 个目标 | 累计曝光 1.5h        │  ← 底部统计条
└──────────────────────────────────────────────┘
```

### 2.3 精选照片

- **自动精选**：按评分最高的前 N 张（默认 5 张），无评分则均匀采样
- **手动管理**：每张照片有"加入日记精选"/"移除精选"快捷操作
- 精选照片存储在 `session_photos` 关联表（`is_featured = 1`）
- 点击照片 → 跳转到 Lightbox 预览

### 2.4 位置信息

- **自动获取**：解析 EXIF GPS 坐标 → 反地理编码（高德 API / Nominatim）
- **手动输入**：无 GPS 或用户想覆盖时，点击地点文字直接编辑
- 复用 `demand/08-shooting-session.md §2.3.2` 的 API 设计

### 2.5 天气信息

- **自动获取**：调用 Open-Meteo Archive API（按经纬度 + 日期查历史天气）
- **手动修改**：点击天气区域可切换天气图标
- API 无需 Key，但需在设置页留扩展位（后续可能换 Provider）
- 复用 `demand/08-shooting-session.md §2.3.2` 的 API 设计

### 2.6 笔记

- 纯文本多行编辑，自动保存（失焦保存，或 debounce 500ms）
- 复用 `sessions.description` 字段

### 2.7 会话管理

| 操作 | 行为 |
|---|---|
| 合并 | 多选两个会话 → 合并为一个，取最早开始和最晚结束 |
| 拆分 | 在会话中选一张照片作为分割点 → 拆为两个会话 |
| 删除 | 仅删除会话元数据，照片回到"未归类" |
| 手动创建 | 新建空白会话，手动指定起止时间，选择照片归入 |

### 2.8 翻页与导航

- **翻页**：左右箭头按钮翻页（上一页/下一页）
- **键盘**：左右方向键翻页
- **时间轴**：底部时间轴快速跳转到任意会话
- **默认页面**：打开日记本显示最新一次拍摄

### 2.9 从日记页跳转

- 点击精选照片 → 打开 Lightbox
- 点击底部统计中的目标标签 → 跳转 Gallery 按标签筛选
- 点击日期 → 跳转 Gallery 按日期筛选

---

## 3. 数据模型

### 3.1 复用现有模型

直接复用 `demand/08-shooting-session.md §3` 的数据设计：

- `sessions` 表（id, project_id, title, description, start_time, end_time, location, wind_dir, wind_level, cloud_cover, bortle, rig_id, created_at, updated_at）
- `media_files.session_id` 列
- `ISessionRepository` 接口

### 3.2 新增：精选照片关联

```sql
-- 或复用 media_files 表的现有字段，在 session 上下文中通过 is_featured 标记
-- 方案：在 media_files 表加 is_diary_featured 列
ALTER TABLE media_files ADD COLUMN is_diary_featured INTEGER DEFAULT 0;
```

### 3.3 新增：日记页 ViewModel 模型

```csharp
public sealed class DiaryPageData
{
    public string SessionId { get; init; }
    public string Title { get; init; }           // 日期 + 标题
    public DateTime Date { get; init; }
    public string? Location { get; init; }
    public string? WeatherIcon { get; init; }    // 天气图标 key
    public string? WeatherText { get; init; }    // 天气描述文本
    public string? Notes { get; init; }          // 用户笔记
    public IReadOnlyList<MediaFile> FeaturedPhotos { get; init; }  // 精选照片
    public int TotalPhotoCount { get; init; }
    public int TargetCount { get; init; }
    public string TotalExposureText { get; init; } // "1.5h"
    public IReadOnlyList<string> TopTags { get; init; } // 前 3 个目标
}
```

### 3.4 新增：设置模型

```csharp
// 在 AppConfig 中新增
public int? SessionIntervalHours { get; set; }  // 会话切分间隔，默认 4
public string? AmapApiKey { get; set; }          // 高德 API Key（可选）
```

---

## 4. 删除清单

### 4.1 文件级删除

| 文件 | 说明 |
|---|---|
| `Views/DashboardView.axaml` | 旧仪表盘视图 |
| `Views/DashboardView.axaml.cs` | 旧仪表盘 code-behind |
| `ViewModels/DashboardViewModel.cs` | 旧仪表盘 ViewModel |
| `Models/DashboardModels.cs` | 旧仪表盘数据模型（全部删除） |
| `Controls/StatCard.axaml` | 统计卡片控件 |
| `Controls/StatCard.axaml.cs` | 统计卡片控件 code-behind |
| `Controls/CalendarHeatmap.axaml` | 日历热力图控件 |
| `Controls/CalendarHeatmap.axaml.cs` | 日历热力图 code-behind |
| `Controls/SkiaHeatmap.axaml` | Skia 热力图控件 |
| `Controls/SkiaHeatmap.axaml.cs` | Skia 热力图 code-behind |
| `Controls/SkiaBarChart.axaml` | Skia 柱状图控件 |
| `Controls/SkiaBarChart.axaml.cs` | Skia 柱状图 code-behind |
| `Controls/BarChart.axaml` | 通用柱状图控件 |
| `Controls/BarChart.axaml.cs` | 通用柱状图 code-behind |

### 4.2 文件级修改

| 文件 | 修改内容 |
|---|---|
| `Data/IMediaRepository.cs` | 删除所有 Dashboard 方法（L52-65），保留 `GetLatestPhotoYearAsync` 和 `GetLatestPhotoDateAsync` |
| `Data/MediaRepository.cs` | 删除所有 Dashboard 方法实现 |
| `Data/DateCount.cs` | 删除 `DateBucket` 和 `DateBucketSet`（Dashboard 专用），保留 `DateCount`（Gallery 还用） |
| `ViewModels/MainWindowViewModel.cs` | 删除 `DashboardViewModel` 字段/属性/导航方法；`ViewPage.Dashboard` 改为 `ViewPage.Diary`；`IsStatsActive` 改为 `IsDiaryActive`；`NavStatsTooltip` 改为 `NavDiaryTooltip`；`NavigateToStats` 改为 `NavigateToDiary` |
| `Views/MainWindow.axaml` | 删除 Dashboard DataTemplate（L354-356）；快捷键 Ctrl+5 改为 `NavigateToDiaryCommand` |
| `Controls/NavRail.axaml` | "统计"按钮改为"日记"，图标从 `Icon.Stats` 改为 `Icon.Diary`（或新增图标资源） |

### 4.3 资源文件修改

| 文件 | 修改内容 |
|---|---|
| `Themes/Icons.axaml` | 新增 `Icon.Diary` 路径图标资源（日记本图标） |

---

## 5. 新增清单

### 5.1 新文件

| 文件 | 说明 |
|---|---|
| `ViewModels/DiaryViewModel.cs` | 日记本 ViewModel（翻页、加载、会话管理） |
| `Views/DiaryView.axaml` | 日记本视图 |
| `Views/DiaryView.axaml.cs` | 日记本 code-behind |
| `Controls/DiaryPage.axaml` | 单页日记控件 |
| `Controls/DiaryPage.axaml.cs` | 单页日记 code-behind |
| `Controls/TimelineBar.axaml` | 底部时间轴控件 |
| `Controls/TimelineBar.axaml.cs` | 底部时间轴 code-behind |
| `Controls/WeatherIconPicker.axaml` | 天气图标选择器（弹出式） |
| `Controls/WeatherIconPicker.axaml.cs` | 天气图标选择器 code-behind |
| `Services/EnvironmentService.cs` | 环境数据服务（GPS 解析、逆地理编码、天气 API） |
| `Services/SessionClusteringService.cs` | 会话聚类服务（按时间间隔分组） |
| `Data/SessionRepository.cs` | 会话仓储实现 |
| `Data/ISessionRepository.cs` | 会话仓储接口 |
| `Models/Session.cs` | 会话数据模型 |

### 5.2 通用资源新增

| 资源 | 说明 |
|---|---|
| `Icon.Diary` | 日记本 PathIcon |
| 天气图标组 | 晴/多云/阴/雨/雪/雾等 SVG PathIcon（`Icon.Weather.Sunny` 等） |

---

## 6. 实施步骤

### Phase 1: 删除旧代码（优先）

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 1.1 | 删除 Dashboard View/ViewModel/Models/Controls 文件 | 14 个文件（见 §4.1） |
| 1.2 | 清理 `IMediaRepository` 和 `MediaRepository` 的 Dashboard 方法 | 2 个文件 |
| 1.3 | 清理 `DateCount.cs` 中 Dashboard 专用类型 | 1 个文件 |
| 1.4 | 修改 `MainWindowViewModel` 导航逻辑 | 1 个文件 |
| 1.5 | 修改 `MainWindow.axaml` 和 `NavRail.axaml` | 2 个文件 |
| 1.6 | 编译验证，确保删除后项目可构建 | — |

### Phase 2: 数据层

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 2.1 | 创建 `Session` 模型 | `Models/Session.cs` |
| 2.2 | 创建 `ISessionRepository` + `SessionRepository`（sessions 表 CRUD） | 2 个文件 |
| 2.3 | 数据库迁移：`sessions` 表 + `media_files.session_id` + `media_files.is_diary_featured` | SQL migration |
| 2.4 | 扩展 `IMediaRepository`：`GetBySessionAsync`、`SetDiaryFeaturedAsync`、`GetDiaryFeaturedAsync` | 2 个文件 |

### Phase 3: 服务层

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 3.1 | `SessionClusteringService`：按时间间隔聚类 | 1 个文件 |
| 3.2 | `EnvironmentService`：EXIF GPS 解析 + 逆地理编码 + 天气 API | 1 个文件 |
| 3.3 | 扩展 `ExifReader`：解析 GPS 标签（GPSLatitude/GPSLongitude/GPSLatitudeRef/GPSLongitudeRef） | `Converters/ExifInfoConverter.cs` |

### Phase 4: 设置页

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 4.1 | `AppConfig` 新增 `SessionIntervalHours` 和 `AmapApiKey` | `Services/AppConfig.cs` |
| 4.2 | 设置页 UI：会话间隔滑块/输入框 + 高德 API Key 输入框 | `SettingsView.axaml` + `SettingsViewModel.cs` |

### Phase 5: UI 层

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 5.1 | `DiaryViewModel`：翻页逻辑、数据加载、会话管理命令 | 1 个文件 |
| 5.2 | `DiaryPage` 控件：单页日记布局 | 2 个文件 |
| 5.3 | `TimelineBar` 控件：底部时间轴 | 2 个文件 |
| 5.4 | `WeatherIconPicker` 控件：天气图标选择 | 2 个文件 |
| 5.5 | `DiaryView`：日记本主视图（翻页 + 时间轴） | 2 个文件 |
| 5.6 | `NavRail` 集成：图标、文字、导航 | 1 个文件 |
| 5.7 | `MainWindow` 集成：DataTemplate + 快捷键 | 2 个文件 |
| 5.8 | 新增图标资源：`Icon.Diary` + 天气图标组 | `Themes/Icons.axaml` |

### Phase 6: 集成与测试

| 步骤 | 内容 |
|---|---|
| 6.1 | 扫描完成后触发会话聚类 |
| 6.2 | 进入日记页时自动加载环境数据（如有 GPS） |
| 6.3 | 精选照片与 Lightbox 联动 |
| 6.4 | 从日记跳转 Gallery 的日期/标签筛选 |
| 6.5 | 编译 + 运行验证 |

---

## 7. 边界与空态

| 状态 | 表现 |
|---|---|
| 无任何照片 | 日记页显示空态：「导入照片后，拍摄日记将自动生成」 |
| 有照片但未聚类 | 显示「正在生成拍摄日记...」+ 加载动画，或手动触发聚类按钮 |
| 只有一次拍摄 | 不显示左右翻页箭头（或置灰）；时间轴只有一个点 |
| 照片无 GPS | 地点字段留空，用户手动输入 |
| 天气 API 无数据 | 天气字段留空，用户手动选择 |
| 照片无评分 | 自动精选改为均匀采样（取首尾中间各 N 张） |
| 会话删除后 | 翻到前一页或后一页 |

---

## 8. 不做清单

| 内容 | 理由 |
|---|---|
| 封面页/目录页 | 用户确认直接进入最近日记页 |
| 旧 Dashboard 的任何功能 | 用户确认全部移除 |
| 导出日记报告 | 独立需求，后续迭代 |
| 光害等级（Bortle） | 现有 D08 有此字段，但日记页暂不展示，后续可加 |
| 器材关联 | 依赖 D13 设备管理模块 |
| 日记页翻页动画 | 先做即时切换，翻页动画后续迭代 |
| 多本日记本 | 当前一个项目 = 一本日记 |

---

## 9. 待决策

| # | 事项 | 决策 |
|---|---|---|
| 1 | 自动精选取几张？ | 默认 5 张，后续可配置 |
| 2 | 笔记自动保存策略 | 失焦保存，debounce 500ms |
| 3 | 天气 API 无数据时是否静默？ | 是，天气字段留空，不弹错误提示 |
| 4 | 日记页是否显示光害等级？ | 暂不显示，后续再加 |