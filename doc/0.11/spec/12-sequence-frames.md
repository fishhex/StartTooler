# 12 — 序列帧手动编组（v0.11）

> 对应需求文档 `doc/0.11/demand/12-sequence-frames.md`。
> 核心改动：新增 `sequences` 表 + `media_files` 扩展列 + 图墙分组展示 + 右键菜单操作。

---

## 1. 模块边界

```
┌──────────────────────────────────────────────────────┐
│                    序列帧编组流程                       │
│                                                      │
│  GalleryViewModel                                     │
│    ├─ ISequenceRepository（CRUD 序列组）                │
│    ├─ IMediaRepository（新增 GetBySequence /           │
│    │   BatchSetSequence / ClearSequence / Reorder）    │
│    ├─ FlattenedMediaItems（混合组头+媒体项）             │
│    └─ Commands（Create / AddTo / Remove / Delete /     │
│        Rename / Reorder / SelectAllInSequence）        │
│                                                      │
│  GalleryView.axaml                                    │
│    ├─ DataTemplateSelector（序列组头 vs 媒体缩略图）     │
│    ├─ ContextMenu（创建/添加/移除/重命名/删除）          │
│    └─ Drag & Drop（组内排序 + 跨组移入）                │
│                                                      │
│  数据层                                               │
│    ├─ sequences 表（media.db 新增）                    │
│    └─ media_files 扩展（sequence_id, sequence_order）  │
└──────────────────────────────────────────────────────┘

依赖链：
  GalleryViewModel
    ├─ ISequenceRepository（新增）
    ├─ IMediaRepository（追加 4 个方法）
    └─ IConfigService（读取 project_path）
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Data/SequenceRepository.cs` | `ISequenceRepository` 实现 | **新增** |
| `Data/ISequenceRepository.cs` | 接口声明 | **新增** |
| `Models/Models.cs` | 新增 `Sequence` 类 | 修改 |
| `Data/MediaFile.cs` | 新增 `SequenceId`、`SequenceOrder` 属性 | 修改 |
| `Data/MediaRepository.cs` | ① 新增 `sequences` 表创建 ② `media_files` 新列迁移 ③ 实现 4 个新方法 | 修改 |
| `Data/IMediaRepository.cs` | 新增 4 个接口方法声明 | 修改 |
| `ViewModels/GalleryViewModel.cs` | ① 序列组数据源 ② 混合列表 ③ 7 个 Command | 修改 |
| `Views/GalleryView.axaml` | ① DataTemplateSelector ② 组头模板 ③ 右键菜单 ④ 拖拽 | 修改 |
| `Views/GalleryView.axaml.cs` | 拖拽事件处理 | 修改 |
| `Converters/SequenceConverters.cs` | 序列组相关值转换器 | **新增** |
| `Themes/Colors.axaml` | 新增序列组主题 token（若需要） | 可能修改 |

> **不引入新 NuGet 包。**

---

## 3. 数据层改动

### 3.1 新表 `sequences`

[MediaRepository.cs](file:///Users/hex/code/StartTooler/StartTooler/Data/MediaRepository.cs) `EnsureDatabase()` 新增建表：

```sql
CREATE TABLE IF NOT EXISTS sequences (
    id          TEXT PRIMARY KEY,
    project_path TEXT NOT NULL,
    name        TEXT NOT NULL DEFAULT '',
    frame_count INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS idx_sequences_project ON sequences(project_path);
```

### 3.2 `media_files` 表扩展

```sql
-- 迁移（SqliteMigrations.AddColumnIfMissing）
ALTER TABLE media_files ADD COLUMN sequence_id TEXT;
ALTER TABLE media_files ADD COLUMN sequence_order INTEGER;
CREATE INDEX IF NOT EXISTS idx_media_files_sequence ON media_files(sequence_id);
```

### 3.3 C# 模型

[Models/Models.cs](file:///Users/hex/code/StartTooler/StartTooler/Models/Models.cs) 新增：

```csharp
public class Sequence
{
    public string Id { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string Name { get; set; } = "";
    public int FrameCount { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}
```

[MediaFile.cs](file:///Users/hex/code/StartTooler/StartTooler/Data/MediaFile.cs) 新增属性：

```csharp
public string? SequenceId { get; set; }
public int? SequenceOrder { get; set; }
```

`ReadMediaFileRow` 中新增读取这两列（SELECT 列序 +2，索引分别为当前最大 +1、+2）。

### 3.4 ISequenceRepository 接口

[ISequenceRepository.cs](file:///Users/hex/code/StartTooler/StartTooler/Data/ISequenceRepository.cs)（新增）：

```csharp
public interface ISequenceRepository
{
    Task<IReadOnlyList<Sequence>> GetByProjectAsync(string projectPath, CancellationToken ct = default);
    Task<Sequence?> GetByIdAsync(string sequenceId, CancellationToken ct = default);
    Task<Sequence> CreateAsync(Sequence sequence, CancellationToken ct = default);
    Task UpdateAsync(Sequence sequence, CancellationToken ct = default);
    Task DeleteAsync(string sequenceId, CancellationToken ct = default);
}
```

### 3.5 SequenceRepository 实现

[SequenceRepository.cs](file:///Users/hex/code/StartTooler/StartTooler/Data/SequenceRepository.cs)（新增）：

- 构造函数注入 `connectionString`（与 `MediaRepository` 共享同一 `media.db` 路径）
- `CreateAsync`：插入新行，返回带 `CreatedAt`/`UpdatedAt` 的完整 `Sequence`
- `UpdateAsync`：更新 `name`、`frame_count`、`updated_at`
- `DeleteAsync`：删除行 + 同时清空关联 `media_files` 的 `sequence_id`/`sequence_order`（事务保证一致性）
- `GetByProjectAsync`：`WHERE project_path = @p ORDER BY created_at DESC`

### 3.6 IMediaRepository 追加方法

```csharp
// 按 sequence_order 排序获取序列组内全部照片
Task<IReadOnlyList<MediaFile>> GetBySequenceAsync(string sequenceId, CancellationToken ct = default);

// 批量设置照片归属（创建序列组时使用）
// startOrder: 起始序号，通常为 0 或 max+1
Task BatchSetSequenceAsync(IReadOnlyList<long> mediaIds, string sequenceId, int startOrder, CancellationToken ct = default);

// 批量清空照片归属（从组移除 / 删除组时使用）
Task ClearSequenceAsync(IReadOnlyList<long> mediaIds, CancellationToken ct = default);

// 重排序（拖拽后使用，orderedMediaIds 按新顺序排列）
Task ReorderSequenceAsync(string sequenceId, IReadOnlyList<long> orderedMediaIds, CancellationToken ct = default);
```

**实现要点**：
- `BatchSetSequenceAsync`：单事务内 `foreach` UPDATE，同时更新 `frame_count`
- `ClearSequenceAsync`：`UPDATE media_files SET sequence_id = NULL, sequence_order = NULL WHERE id IN (...)`
- `ReorderSequenceAsync`：单事务内逐条 UPDATE `sequence_order`
- 扫描流程（`ScanDirectoryAsync`）不修改 `sequence_id` / `sequence_order` 列

---

## 4. ViewModel 改动

### 4.1 GalleryViewModel 新增字段

[GalleryViewModel.cs](file:///Users/hex/code/StartTooler/StartTooler/ViewModels/GalleryViewModel.cs)：

```csharp
// 当前项目所有序列组
private IReadOnlyList<Sequence> _sequences = Array.Empty<Sequence>();

// 混合列表：SequenceHeader 占位符 + MediaFile
// SequenceHeader 是内部类，用于在 ItemsControl 中插入组头
public ObservableCollection<object> FlattenedMediaItems { get; } = new();

// 序列组是否可见（图墙内开关）
public bool IsSequenceGroupingEnabled { get; set; } = true;
```

### 4.2 混合列表构建

提供 `BuildFlattenedList()` 方法，在每次数据变更后调用：

```csharp
private void BuildFlattenedList()
{
    FlattenedMediaItems.Clear();
    if (!IsSequenceGroupingEnabled)
    {
        // 降级为纯平铺
        foreach (var m in MediaFiles) FlattenedMediaItems.Add(m);
        return;
    }

    // 先收集有序列组的照片，按 sequence_id 分组
    var grouped = new Dictionary<string, List<MediaFile>>();
    var ungrouped = new List<MediaFile>();

    foreach (var m in MediaFiles)
    {
        if (m.SequenceId != null)
        {
            if (!grouped.ContainsKey(m.SequenceId))
                grouped[m.SequenceId] = new List<MediaFile>();
            grouped[m.SequenceId].Add(m);
        }
        else
        {
            ungrouped.Add(m);
        }
    }

    // 按序列组创建时间显现
    foreach (var seq in _sequences)
    {
        if (grouped.TryGetValue(seq.Id, out var frames))
        {
            frames.Sort((a, b) => (a.SequenceOrder ?? 0).CompareTo(b.SequenceOrder ?? 0));
            FlattenedMediaItems.Add(new SequenceHeader(seq));
            foreach (var f in frames) FlattenedMediaItems.Add(f);
        }
    }

    // 无组照片放在最后
    foreach (var m in ungrouped) FlattenedMediaItems.Add(m);
}

// 内部占位类
public sealed class SequenceHeader
{
    public Sequence Sequence { get; }
    public SequenceHeader(Sequence seq) => Sequence = seq;
}
```

### 4.3 命令

```csharp
[RelayCommand]
private async Task CreateSequence(IReadOnlyList<MediaFile>? selectedFiles)
{
    // 1. 弹出命名对话框（预填「未命名序列 N」，N=当日已创建数+1）
    // 2. 生成 GUID，创建 Sequence 记录
    // 3. 调用 BatchSetSequenceAsync 写入选中文件
    // 4. 刷新 FlattenedMediaItems
}

[RelayCommand]
private async Task AddToSequence(string sequenceId, IReadOnlyList<MediaFile>? selectedFiles)
{
    // 1. 计算目标组当前最大 sequence_order = max + 1
    // 2. 调用 BatchSetSequenceAsync
    // 3. 刷新
}

[RelayCommand]
private async Task RemoveFromSequence(IReadOnlyList<MediaFile>? selectedFiles)
{
    // 1. 调用 ClearSequenceAsync
    // 2. 如果组内帧数变为 0，删除序列组
    // 3. 否则重排剩余帧的 sequence_order + 更新 frame_count
    // 4. 刷新
}

[RelayCommand]
private async Task DeleteSequence(string sequenceId)
{
    // 1. 弹确认对话框
    // 2. 调用 SequenceRepository.DeleteAsync（内部清空 media_files 关联）
    // 3. 刷新
}

[RelayCommand]
private async Task RenameSequence(string sequenceId)
{
    // 1. 弹出编辑框，预填当前名称
    // 2. 调用 SequenceRepository.UpdateAsync
    // 3. 刷新组头显示
}

[RelayCommand]
private async Task ReorderFrames(string sequenceId, IReadOnlyList<long> orderedMediaIds)
{
    // 1. 调用 ReorderSequenceAsync
    // 2. 刷新 FlattenedMediaItems
}

[RelayCommand]
private void SelectAllInSequence(string sequenceId)
{
    // 遍历 MediaFiles，选中所有 sequence_id == sequenceId 的照片
}
```

### 4.4 数据加载流程

`InitializeAsync` / `LoadMediaPageAsync` / 筛选/排序变更后：

```
1. 加载媒体文件列表（现有逻辑不变）
2. 加载序列组列表（_sequenceRepo.GetByProjectAsync）
3. 调用 BuildFlattenedList()
4. 通知 UI 刷新
```

`FilterStateSnapshot` 中新增 `IsSequenceGroupingEnabled` 快照（与媒体类型过滤同模式）。

---

## 5. View 改动

### 5.1 DataTemplateSelector

[GalleryView.axaml](file:///Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml)：

```xml
<!-- 替换现有 ItemsControl.ItemTemplate 为 DataTemplateSelector -->
<ItemsControl ItemsSource="{Binding FlattenedMediaItems}">
    <ItemsControl.ItemTemplateSelector>
        <local:SequenceItemTemplateSelector>
            <local:SequenceItemTemplateSelector.SequenceHeaderTemplate>
                <DataTemplate DataType="{x:Type vm:GalleryViewModel+SequenceHeader}">
                    <!-- 组头模板：灰色底色条 + 组名 + 帧数 + 操作按钮 -->
                </DataTemplate>
            </local:SequenceItemTemplateSelector.SequenceHeaderTemplate>
            <local:SequenceItemTemplateSelector.MediaItemTemplate>
                <DataTemplate DataType="{x:Type data:MediaFile}">
                    <!-- 现有缩略图模板，加上组内序号角标 -->
                </DataTemplate>
            </local:SequenceItemTemplateSelector.MediaItemTemplate>
        </local:SequenceItemTemplateSelector>
    </ItemsControl.ItemTemplateSelector>
</ItemsControl>
```

### 5.2 组头模板

```xml
<Border Background="{DynamicResource Bg.Subtle}" CornerRadius="6" Padding="12,8" Margin="4,8,4,0">
    <Grid ColumnDefinitions="*,Auto,Auto,Auto">
        <TextBlock Grid.Column="0" 
                   Text="{Binding Sequence.Name}" 
                   FontWeight="SemiBold" VerticalAlignment="Center"/>
        <TextBlock Grid.Column="1" 
                   Text="{Binding Sequence.FrameCount, StringFormat='{}{0} 帧'}" 
                   Foreground="{DynamicResource Text.Secondary}" 
                   Margin="12,0,0,0" VerticalAlignment="Center"/>
        <Button Grid.Column="2" Content="全选组" Margin="8,0,0,0" 
                Command="{Binding DataContext.SelectAllInSequenceCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                CommandParameter="{Binding Sequence.Id}"/>
        <Button Grid.Column="3" Content="⋮" Margin="8,0,0,0">
            <Button.ContextMenu>
                <ContextMenu>
                    <MenuItem Header="全选组" .../>
                    <MenuItem Header="重命名" .../>
                    <Separator/>
                    <MenuItem Header="解散序列组" .../>
                    <MenuItem Header="删除序列组" .../>
                </ContextMenu>
            </Button.ContextMenu>
        </Button>
    </Grid>
</Border>
```

### 5.3 右键菜单

在媒体缩略图的 `ContextMenu` 中动态添加/移除菜单项：

```csharp
// GalleryView.axaml.cs 中处理 ContextMenuOpening 事件
private void OnMediaItemContextMenuOpening(object sender, ContextMenuEventArgs e)
{
    var menu = (sender as Control)?.ContextMenu;
    if (menu == null) return;

    // 清空动态项
    // 根据选中状态添加：
    //   - 始终显示「创建序列帧组」
    //   - 始终显示「添加到序列组」（子菜单）
    //   - 若选中照片全部有 sequence_id → 显示「从序列组移除」
}
```

### 5.4 拖拽排序

```csharp
// GalleryView.axaml.cs
// 在 ItemsControl 上启用 AllowDrop
// 处理 DragOver / Drop 事件：
//   1. 判断拖拽目标：落在组内 → 加入该组 / 落在组间 → 从原组移除
//   2. 调用 ReorderFramesCommand
```

### 5.5 组头折叠

```csharp
// 在 SequenceHeader 类中增加 IsExpanded 属性
// 组头点击：切换 IsExpanded
// 模板中：IsExpanded=false 时隐藏组内帧（通过 IValueConverter 控制 Visibility）
```

---

## 6. 转换器

[SequenceConverters.cs](file:///Users/hex/code/StartTooler/StartTooler/Converters/SequenceConverters.cs)（新增）：

| 转换器 | 用途 |
|---|---|
| `BoolToVisibilityConverter` | 组头折叠/展开控制组内帧可见性 |
| `SequenceOrderConverter` | 显示组内序号角标（如 `#3/12`） |

---

## 7. DI 注册

[App.axaml.cs](file:///Users/hex/code/StartTooler/StartTooler/App.axaml.cs) 或 DI 配置中：

```csharp
services.AddSingleton<ISequenceRepository>(sp =>
{
    var dbPath = Path.Combine(localAppData, "StartTooler", "media.db");
    return new SequenceRepository($"Data Source={dbPath}");
});
```

---

## 8. 边界情况

| 场景 | 处理 |
|---|---|
| 创建序列组时未选中任何照片 | 命令按钮禁用（`CanExecute` 返回 false） |
| 添加到序列组时项目无序列组 | 子菜单显示「（无序列组）」并禁用 |
| 组内最后一帧被移除 | 自动删除序列组记录 |
| 删除序列组时组内无照片 | 直接删除，不弹确认（或弹确认但写「0 张照片」） |
| 扫描后新照片 | 无 `sequence_id`，显示在无组区域 |
| 切换媒体类型过滤 | 序列组内照片被过滤后组头仍显示，但帧数显示实际可见数 |
| 切换排序模式 | 组内帧顺序不变；无组照片按所选排序模式排列 |
| `shot_at` 为 NULL 的照片 | 可正常编入序列组，`sequence_order` 决定位置 |
| 拖拽到两个组之间 | 从原组移除，不自动加入新组 |
| 同时选中多组照片进行操作 | 跨组操作正常（如多选后创建新组，自动从各原组移除） |
| 数据库迁移失败 | `AddColumnIfMissing` 幂等，重复执行不报错 |
| 序列组表为空 | `BuildFlattenedList` 只输出无组照片，与现有平铺行为一致 |

---

## 9. 与现有系统的关系

### 9.1 不影响现有查询

`media_files` 新增列为 NULLABLE，所有现有 `SELECT` 查询不受影响。`ReadMediaFileRow` 新增列读取在末尾，不影响现有列索引。

### 9.2 不影响扫描流程

`ScanDirectoryAsync` 的 INSERT / ON CONFLICT DO UPDATE 不涉及 `sequence_id` / `sequence_order`，新照片默认无编组。

### 9.3 不影响灯箱基础逻辑

灯箱翻页增加「组内循环」模式的条件判断：`if (currentFile.SequenceId != null) { /* 组内翻页 */ }`，否则走现有全量翻页逻辑。

### 9.4 不影响上传/下载/垃圾筒/设置

完全不涉及。

### 9.5 组头不在筛选快照中

序列组分组展示开关（`IsSequenceGroupingEnabled`）在 `FilterStateSnapshot` 中快照，切换视图后重置为默认值（与媒体类型过滤同模式）。

---

## 10. 实施步骤

| 步骤 | 内容 | 影响范围 |
|------|------|---------|
| 1 | `Models.cs` 新增 `Sequence` 类 | `Models.cs` |
| 2 | `MediaFile.cs` 新增 `SequenceId`、`SequenceOrder` 属性 | `MediaFile.cs` |
| 3 | `MediaRepository.cs` 新增 `sequences` 建表 + `media_files` 列迁移 | `MediaRepository.cs` |
| 4 | 新建 `ISequenceRepository.cs` + `SequenceRepository.cs` | 2 个新文件 |
| 5 | `IMediaRepository.cs` + `MediaRepository.cs` 追加 4 个方法 | `IMediaRepository.cs`、`MediaRepository.cs` |
| 6 | `App.axaml.cs` DI 注册 `ISequenceRepository` | `App.axaml.cs` |
| 7 | `GalleryViewModel.cs` 新增序列组数据源、混合列表、7 个 Command | `GalleryViewModel.cs` |
| 8 | `SequenceConverters.cs` 新增转换器 | 新文件 |
| 9 | `GalleryView.axaml` 新增 DataTemplateSelector、组头模板、右键菜单 | `GalleryView.axaml` |
| 10 | `GalleryView.axaml.cs` 新增拖拽事件处理 | `GalleryView.axaml.cs` |
| 11 | `LightboxViewModel.cs` 组内翻页适配 | `LightboxViewModel.cs` |
| 12 | 构建验证：`dotnet build` 0 警告 0 错误 | — |