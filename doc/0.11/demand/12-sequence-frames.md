# D12 — 序列帧：手动编组管理

> 关联文档：`02-data-layer.md`（数据层）、`05-gallery-view.md`（Gallery 视图）、`08-shooting-session.md`（拍摄会话）

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.1（需求稿）** |
| 目标用户 | 天文摄影爱好者 |
| 文档状态 | **需求 — 待评审** |
| 关联模块 | Gallery (05)、Data Layer (02)、拍摄会话 (D08 0.11) |

---

## 1. 需求总览

### 1.1 背景

当前星助按「拍摄日期」组织照片，缺少对「同一组素材」的手动分组能力。在天文摄影工作流中，用户经常对同一目标连续拍摄多张曝光（如 30×300s 的 M31），这些照片共享相同的目标、器材和参数，形成一个天然的工作单元。用户需要主动将它们编组，以便后续选片、对比和叠加。

### 1.2 核心价值

| 维度 | 现在 | 将来 |
|---|---|---|
| 素材组织 | 按日期平铺，无法区分不同拍摄意图 | 用户手动创建序列组，按意图组织 |
| 选片对比 | 翻图墙逐张找同组照片 | 序列组内一键对比，快速选最佳帧 |
| 后续处理 | 手动记住哪些照片是一组 | 序列组直接作为叠加/拼接的输入 |

### 1.3 一句话概括

**用户手动多选照片创建序列帧组，在图墙中以视觉分组展示，支持组内排序、对比和批量操作。**

---

## 2. 用户场景

### 场景一：拍完 M31，创建序列组

> 用户昨晚拍摄了 M31 仙女座星系：30 张 300s 亮场 + 10 张暗场。今晚打开星助。
>
> 在图墙中多选 30 张亮场，右键 →「创建序列帧组」，弹出命名框，输入「M31 亮场 300s」，确认。
>
> 图墙中这 30 张照片立刻被一个分组框包裹，组头显示「M31 亮场 300s · 30 帧」。
>
> 再选 10 张暗场，同样创建「M31 暗场 300s」序列组。

### 场景二：选片 — 同组对比找最佳帧

> 用户打开「M31 亮场 300s」序列组，点击组头「组内对比」按钮。
>
> 灯箱打开，左右键翻页，只在该序列组 30 帧内循环。用户快速对比，挑出 3 张拖线/星点不圆的，决定不用于叠加。

### 场景三：调整组的顺序和内容

> 用户发现有一张照片序列位置不对，拖拽到正确位置。又发现两张照片不应该在这个组，多选后右键 →「从序列组移除」。

---

## 3. 功能需求

### 3.1 创建序列组

| 需求 | 说明 |
|---|---|
| 入口 | 图墙中多选 ≥1 张照片 → 右键 →「创建序列帧组」 |
| 命名 | 弹出命名对话框，输入框预填「未命名序列 N」（N 为当日已创建序列组的递增序号，如「未命名序列 1」），用户可修改或留空 |
| 默认名 | 留空等同于「未命名序列 N」，不允许纯空白的组名 |
| 多选入组 | 已有 `sequence_id` 的照片被选入新组时，自动从原组移除（一帧一组） |
| 确认后 | 创建 `sequences` 记录，照片按选中顺序写入 `sequence_order`（0, 1, 2...） |
| 取消 | 对话框关闭，不做任何操作 |

### 3.2 添加到已有序列组

| 需求 | 说明 |
|---|---|
| 入口 | 选中照片 → 右键 →「添加到序列组」→ 弹出子菜单列出所有已有序列组 |
| 行为 | 选中照片追加到目标序列组末尾（`sequence_order = max + 1`） |
| 跨组 | 照片原本属于其他组则先移除再添加 |
| 子菜单 | 如果项目无序列组，子菜单项显示「（无序列组）」并禁用 |

### 3.3 从序列组移除

| 需求 | 说明 |
|---|---|
| 入口 | 选中组内照片 → 右键 →「从序列组移除」 |
| 行为 | `sequence_id` 和 `sequence_order` 清空；组内其余照片的 `sequence_order` 自动重排，不留空位 |

### 3.4 调整顺序

| 需求 | 说明 |
|---|---|
| 入口 | 组内拖拽照片到新位置 |
| 行为 | 更新受影响照片的 `sequence_order` |
| 反馈 | 拖拽时显示插入位置指示线 |

### 3.5 重命名序列组

| 需求 | 说明 |
|---|---|
| 入口 | 双击组头名称 / 右键组头 →「重命名」 |
| 行为 | 弹出编辑框，预填当前名称，确认后更新 |
| 空名 | 不允许空名，回退到「未命名序列 N」 |

### 3.6 删除序列组

| 需求 | 说明 |
|---|---|
| 入口 | 右键组头 →「删除序列组」 |
| 确认 | 弹确认：「删除序列组『XXX』，其中 N 张照片将取消编组，照片本身不受影响」 |
| 确认后 | 删除 `sequences` 记录，清空组内所有照片的 `sequence_id` 和 `sequence_order` |
| 取消 | 不做任何操作 |

### 3.7 解散序列组

与删除序列组行为相同（删除组，照片保留，清空编组字段）。提供独立入口是为了语义清晰，但实现可复用同一逻辑。

### 3.8 右键菜单规则

| 选中情况 | 右键菜单项 |
|---|---|
| 选中 ≥1 张照片 | 创建序列帧组、添加到序列组（子菜单） |
| 选中 ≥1 张组内照片 | 从序列组移除 |
| 选中组头 | 重命名、删除序列组、解散序列组、全选组 |

---

## 4. UI 交互

### 4.1 图墙中的序列组展示

```
┌── 序列组 · M31 亮场 300s · 12 帧 ──────────────────────────┐
│ [帧1] [帧2] [帧3] [帧4] [帧5] [帧6]                        │
│ [帧7] [帧8] [帧9] [帧10] [帧11] [帧12]                      │
│                          [全选组] [解散组]         [⋮ 更多]  │
└────────────────────────────────────────────────────────────┘
┌── 序列组 · 银河连拍 · 5 帧 ────────────────────────────────┐
│ [帧1] [帧2] [帧3] [帧4] [帧5]                               │
└────────────────────────────────────────────────────────────┘
```

| 元素 | 说明 |
|---|---|
| 组头 | 浅灰底色条（`Bg.Subtle`），左侧显示「序列组名 · N 帧」，右侧显示操作按钮 |
| 组内帧 | 紧密排列，组间有明显间距 |
| 组头折叠 | 默认展开；点击组头可折叠/展开组内照片；折叠状态存在内存中，切换视图后重置 |
| 拖拽 | 拖拽帧放到两个组之间 = 从原组移除（不自动加入新组，避免误操作）；拖拽帧放到目标组区域内 = 加入该组 |

### 4.2 与现有功能的兼容

| 现有功能 | 在序列组视图下的行为 |
|---|---|
| 多选 | 正常多选，可跨组选择 |
| 媒体类型过滤 | 正常过滤，序列组内混合类型也正常显示 |
| 排序 | 组内帧按 `sequence_order` 固定顺序，不受外部排序模式影响；无组照片正常排序 |
| 搜索 | 搜索结果中属于序列组的照片，显示组名标签 |
| 灯箱预览 | 灯箱内翻页，若当前照片属于序列组，则仅在组内按 `sequence_order` 循环翻页 |
| 标签筛选 | 筛选结果中序列组可能不完整（只有部分帧符合标签），组头仍显示但实际可见帧数可能少于总帧数 |

### 4.3 左侧导航

序列组不在左栏有独立 Tab 入口。序列组是图墙的展示模式，在图墙内以视觉分组呈现。

---

## 5. 数据模型

### 5.1 新表 `sequences`（`media.db`）

```sql
CREATE TABLE IF NOT EXISTS sequences (
    id          TEXT PRIMARY KEY,          -- GUID
    project_path TEXT NOT NULL,            -- 规范化后的绝对路径，复用 media_files.project_path 格式
    name        TEXT NOT NULL DEFAULT '',  -- 用户命名
    frame_count INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS idx_sequences_project ON sequences(project_path);
```

### 5.2 `media_files` 扩展

```sql
ALTER TABLE media_files ADD COLUMN sequence_id TEXT;
ALTER TABLE media_files ADD COLUMN sequence_order INTEGER;
CREATE INDEX IF NOT EXISTS idx_media_files_sequence ON media_files(sequence_id);
```

- `sequence_id = NULL`：未编入任何序列组
- `sequence_order`：组内序号，0-based，按用户指定顺序

### 5.3 C# 模型

```csharp
// Models/Models.cs 新增
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

`MediaFile` 新增属性：

```csharp
public string? SequenceId { get; set; }
public int? SequenceOrder { get; set; }
```

### 5.4 新增 Repository 接口

```csharp
// Data/ISequenceRepository.cs（新增文件）
public interface ISequenceRepository
{
    Task<IReadOnlyList<Sequence>> GetByProjectAsync(string projectPath, CancellationToken ct = default);
    Task<Sequence?> GetByIdAsync(string sequenceId, CancellationToken ct = default);
    Task<Sequence> CreateAsync(Sequence sequence, CancellationToken ct = default);
    Task UpdateAsync(Sequence sequence, CancellationToken ct = default);
    Task DeleteAsync(string sequenceId, CancellationToken ct = default);
}
```

`IMediaRepository` 追加方法：

```csharp
// 获取指定序列组的全部照片（按 sequence_order 排序）
Task<IReadOnlyList<MediaFile>> GetBySequenceAsync(string sequenceId, CancellationToken ct = default);

// 批量设置照片的序列组归属
Task BatchSetSequenceAsync(IReadOnlyList<long> mediaIds, string sequenceId, int startOrder, CancellationToken ct = default);

// 批量清空照片的序列组归属
Task ClearSequenceAsync(IReadOnlyList<long> mediaIds, CancellationToken ct = default);

// 重排序列组内照片顺序
Task ReorderSequenceAsync(string sequenceId, IReadOnlyList<long> orderedMediaIds, CancellationToken ct = default);
```

---

## 6. ViewModel 变更

在 `GalleryViewModel` 中新增：

```csharp
// 序列组数据
ObservableCollection<Sequence> Sequences { get; }           // 当前视图下的序列组列表
ObservableCollection<object> FlattenedMediaItems { get; }   // 混合列表：组头占位 + 媒体文件

// 命令
ICommand CreateSequenceCommand { get; }       // 多选 → 创建序列组
ICommand AddToSequenceCommand { get; }        // 选中 → 添加到已有序列组
ICommand RemoveFromSequenceCommand { get; }   // 选中 → 从序列组移除
ICommand DeleteSequenceCommand { get; }       // 删除序列组
ICommand RenameSequenceCommand { get; }       // 重命名序列组
ICommand ReorderFramesCommand { get; }        // 拖拽调整顺序
ICommand SelectAllInSequenceCommand { get; }  // 全选组内帧
```

---

## 7. 边界与规则

| 规则 | 说明 |
|---|---|
| 一帧一组 | 一张照片只能属于一个序列组 |
| 单帧组 | 允许只有 1 帧的序列组（用户可能后续添加） |
| 空组不保留 | 组内最后一帧被移除时，自动删除序列组记录 |
| 跨会话/跨日期 | 序列组可以包含不同会话、不同日期的照片（用户自由组织） |
| 无自动编组 | 系统永远不自动创建/修改序列组 |
| 跨项目不可见 | 序列组严格按 `project_path` 隔离 |
| `shot_at` 为 NULL | 可正常编入序列组，按用户指定的 `sequence_order` 排序 |

### 空态

| 状态 | 表现 |
|---|---|
| 项目无序列组 | 图墙正常平铺，无组头，右键菜单「添加到序列组」子菜单显示「（无序列组）」 |
| 无照片 | 与现有 Gallery 空态一致 |
| 序列组内无照片（边界情况） | 组自动删除，不展示空组头 |

---

## 8. 技术约束

| 项 | 约束 |
|---|---|
| 数据库 | `sequences` 表写入 `media.db`（与 `media_files` 同库），不新建独立数据库 |
| 迁移 | 新增列使用 `ALTER TABLE ADD COLUMN`，与现有 `SqliteMigrations.AddColumnIfMissing` 模式一致 |
| 组头渲染 | 在现有 `ItemsControl` 中通过 `DataTemplateSelector` 区分组头占位和媒体缩略图 |
| 性能 | 序列组查询按 `project_path` 索引，不引入全表扫描 |
| 扫描 | 扫描流程不修改 `sequence_id` / `sequence_order`，序列组数据完全由用户手动管理 |

---

## 9. 不做清单

以下内容**不在本次需求范围内**：

| 内容 | 理由 |
|---|---|
| 自动聚类（按时间间隔/参数自动编组） | 本次只做手动管理，自动编组后续评估 |
| 序列组在左栏的独立 Tab 入口 | 已确认不做，序列组是图墙内展示模式 |
| 校准帧关联（亮场→暗场/平场） | 属于 D08 拍摄会话或后续叠加需求 |
| 序列组作为叠加输入 | 依赖 D01 一键叠加需求先行落地 |
| 序列组嵌套（子组） | 增加复杂度，当前无明确场景 |
| 序列组导出/导入 | 后续评估 |
| 跨项目序列组 | 已确认不做，`project_path` 隔离 |

---

## 10. 待决策事项

| # | 事项 | 决策 | 状态 |
|---|---|---|---|
| 1 | 序列组是否在左栏有列表入口 | 否 | ✓ 已定 |
| 2 | 序列组是否跨项目 | 否（`project_path` 隔离） | ✓ 已定 |
| 3 | 组内帧的展示顺序 | A: 严格按 `sequence_order` | ✓ 已定 |
| 4 | 创建序列组时命名方式 | A+B: 弹出命名框 + 预填「未命名序列 N」 | ✓ 已定 |
| 5 | 单帧是否独立成组 | 允许，用户可后续添加 | ✓ 已定 |
| 6 | 灯箱翻页在序列组内的行为 | 仅在组内按 `sequence_order` 循环翻页 | ✓ 已定 |