# 15 — 手动编辑照片 / 视频标签

> 关联文档：`11-ai-tagging.md`（AI 打标）、`12-ai-toolbar-buttons.md`（工具栏）、`13-tag-quality-split.md`（标签/质量拆分）、`05-gallery-view.md`（Gallery）、`0.11/01-lightbox-preview.md`（灯箱）。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v1.0** |
| 目标用户 | 天文摄影爱好者 |
| 文档状态 | **plan**（待用户审） |
| 实施版本 | 实施时按 CHANGELOG 钉，本 spec 不预先挂版本号 |
| 问题描述 | v0.6 起 AI 自动打标后，用户只能「接受 AI 全部结果」或「整张 AI 重打」。实际使用中常有"AI 漏了关键星体 / 把广角认成深空"的情况，需要**在 AI 基础上人工增删单个 tag**。本期只动主体标签（`tags`），不动 `quality_tags` / `score`。 |

### 变更摘要

| 改动 | 内容 |
|---|---|
| **入口** | 灯箱右侧「AI 标签」section 末尾加 ✏️ 进入行内编辑（主路径）；Gallery photo tile 右键菜单加「编辑标签」调模态弹窗（兜底） |
| **批量** | Gallery 多选模式工具栏加「编辑标签」按钮，弹模态框支持 替换 / 追加 / 删除 三种 scope |
| **范围** | 只动 `tags`（主体标签）。`quality_tags` / `score` 保留，AI 重新打标会**覆盖**手动编辑（trade-off） |
| **数据层** | **零 schema 变更**。复用 `MediaRepository.UpdateTagAsync`，`tagged_at` 刷新、`tag_error` 清空、`score` 保留 |
| **左栏聚合** | `MediaFile.Tags` 即时改动触发 `LoadTagGroupsAsync` 重跑，被改文件可能从当前 tag 视图消失（按预期） |

---

## 1. 需求

### 1.1 用户故事

| ID | 故事 | 验收点 |
|---|---|---|
| US-1 | 我希望灯箱里看着原图就能加 / 删主体 tag，不用打开模态弹窗打断 | 灯箱「AI 标签」section 末尾点 ✏️ → 行内编辑；点 × 删 tag；底部输入框回车加 tag；右上「保存」/「取消」 |
| US-2 | 我希望右键 photo tile 也能改 tag（不进灯箱也能用） | Gallery 右键菜单加「编辑标签」→ 模态弹窗，编辑完保存立即生效 |
| US-3 | 我希望批量改 tag：选 N 张 → 一次性替换 / 追加 / 删除某几个 tag，不用一张张开灯箱 | Gallery 多选 N≥1 时，工具栏「编辑标签」启用；弹模态框选 scope（替换 / 追加 / 删除）→ diff 预览 → 应用 |
| US-4 | 我希望手动改完 tag 后，AI 重新打标前能看见提示"手动修改将被覆盖" | 编辑对话框底部一行小字提示 |
| US-5 | 我希望左栏 Tag 视图里，被改动的文件即时更新（删了 tag 的文件从该视图消失） | `MediaFile.Tags` ObservableProperty 触发 → `LoadTagGroupsAsync` 重跑（带 debounce 500ms 避免连续编辑产生 N 次查询） |
| US-6 | 我希望手动改完 tag 后灯箱里立刻看见新值（翻页时也跟着变） | `LightboxViewModel.CurrentFile` 引用 `GalleryViewModel.CurrentMediaFiles[i]`，内存 List 替换即触发 chip 列表重新渲染 |

### 1.2 验收点

- ☐ 灯箱「AI 标签」section 末尾加 ✏️ 按钮（无 tag 时也显示，点击直接进入编辑态）
- ☐ 编辑态：每个 chip 右上角 ×、底部输入框 + 回车提交、右上「保存」/「取消」
- ☐ 输入校验：去 trim、去空、去重（大小写不敏感）、单 tag ≤ 20 字符
- ☐ Gallery 右键菜单新增「编辑标签」菜单项（在「AI 打标」下方）
- ☐ 工具栏「编辑标签」按钮：多选 N≥1 时启用，AI 打标中禁用
- ☐ 批量编辑弹窗：scope 单选（替换 / 追加 / 删除）+ chip 编辑器 + diff 预览区
- ☐ 复用 `UpdateTagAsync`：`tags` 写新值，`tagged_at` 刷新，`tag_error` 清空，`score` 保留
- ☐ 乐观更新：先改 `MediaFile.Tags = newList`（UI 即时刷），DB 失败回滚 + toast
- ☐ 边界锁：AI 正在打标的文件 / 软删除文件 → 编辑入口灰显，tooltip 说明原因
- ☐ 左栏 `LoadTagGroupsAsync` 在 Tags 变化后 500ms debounce 重跑
- ☐ QualityTags / score 在灯箱和编辑流程中**完全不动**

---

## 2. 数据层

### 2.1 Schema 变更

**无**。完全复用现有 `media_files` 表的 `tags` / `tagged_at` / `tag_error` / `score` 列。

### 2.2 Repository 复用

`MediaRepository.UpdateTagAsync(long fileId, IEnumerable<string> tags, IEnumerable<string> qualityTags, int score, long taggedAt, string? tagError, CancellationToken ct)` 已存在（`Data/MediaRepository.cs:503`），直接复用。

调用方约定：

```csharp
// 手动编辑单张
await _mediaRepo.UpdateTagAsync(
    fileId: file.Id,
    tags: newTags,                    // List<string> 编辑后的主体标签
    qualityTags: file.QualityTags,    // 原值透传，不动
    score: file.Score ?? 0,           // 原值透传，0 是合理 fallback（spec §2.3）
    taggedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    tagError: null);                  // 清空错误

// 批量
foreach (var file in files)
    await _mediaRepo.UpdateTagAsync(file.Id, file.Tags, file.QualityTags, file.Score ?? 0, nowMs, null);
```

**关于 `score` 透传**：手动编辑不动评分。如果当前 `Score = null`（未打标），传 0 写库是合理的——v0.6 schema 把 `score` 设为 INTEGER NULL，但 `UpdateTagAsync` 签名是 `int score`（非可空），实现里 `cmd.Parameters.AddWithValue("@score", score)` 必须有值。0 + `tagged_at` 一起写表示"被用户手动打标过"，UI 通过 `HasScore = Score.HasValue`（`null → false`）判定，所以**写 0 反而会让卡片误显示评分 0 角标**。

**修正方案**：本期给 `UpdateTagAsync` 加一个**可选重载**或**新方法** `UpdateTagsOnlyAsync`，只写 `tags` / `tagged_at` / `tag_error`，不动 `score` / `quality_tags`：

```csharp
/// <summary>
/// 手动编辑主体标签专用：只动 tags / tagged_at / tag_error，
/// 保留 score 和 quality_tags 原值。
/// </summary>
Task UpdateTagsOnlyAsync(long fileId, IEnumerable<string> tags, long taggedAt, CancellationToken ct = default);
```

SQL：
```sql
UPDATE media_files
SET tags = @tags,
    tagged_at = @taggedAt,
    tag_error = NULL,
    updated_at = @updatedAt
WHERE id = @id
```

JSON 编码仍走 `s_writeTagsOptions`（`UnsafeRelaxedJsonEscaping`），中文原样写。

### 2.3 不动 QualityTags / score

`MediaFile.QualityTags` 和 `MediaFile.Score` 在整个编辑流程中**只读**，UI 不暴露编辑入口，VM 不写。这俩继续由 AI 流程管（`BatchTagCoreAsync` 调旧 `UpdateTagAsync`）。

---

## 3. UI — 灯箱行内编辑（主路径）

### 3.1 现状

`Views/LightboxWindow.axaml:198-220` 已有「AI 标签」section，只读 `ItemsControl` + chip 渲染。

### 3.2 默认态（改 1 处：末尾加 ✏️）

```xml
<StackPanel Spacing="4" IsVisible="{Binding CurrentFile.HasTags}">
    <Grid ColumnDefinitions="*,Auto">
        <TextBlock Grid.Column="0" Text="AI 标签" Classes="InfoLabel" />
        <Button Grid.Column="1"
                Classes="InfoIconButton"
                Command="{Binding EnterEditTagsCommand}"
                ToolTip.Tip="编辑标签">
            <PathIcon Data="{StaticResource Icon.Pencil}" Width="12" Height="12" />
        </Button>
    </Grid>
    <ItemsControl ItemsSource="{Binding CurrentFile.Tags}">
        <!-- ... 现有 chip 渲染不变 ... -->
    </ItemsControl>
</StackPanel>
```

**额外**：当 `HasTags == false`（AI 没打标过 / 用户全删了）时也允许编辑。新增一个无条件显示的「添加标签」入口：

```xml
<Button IsVisible="{Binding !CurrentFile.HasTags}"
        Command="{Binding EnterEditTagsCommand}"
        Classes="InfoAddTagButton"
        Content="+ 添加标签" />
```

### 3.3 编辑态

整个「AI 标签」section 在 `IsEditing` 状态下切换为：

```xml
<StackPanel Spacing="8" IsVisible="{Binding IsEditingTags}">
    <Grid ColumnDefinitions="*,Auto,Auto">
        <TextBlock Grid.Column="0" Text="编辑标签" Classes="InfoLabel" />
        <Button Grid.Column="1" Classes="InfoIconButton"
                Command="{Binding CancelEditTagsCommand}"
                ToolTip.Tip="取消">
            <PathIcon Data="{StaticResource Icon.X}" Width="12" Height="12" />
        </Button>
        <Button Grid.Column="2" Classes="InfoIconButton"
                Command="{Binding SaveEditTagsCommand}"
                ToolTip.Tip="保存 (⌘↩)">
            <PathIcon Data="{StaticResource Icon.Check}" Width="12" Height="12" />
        </Button>
    </Grid>

    <!-- 已存在的 tags：每个 chip 右上角 × -->
    <ItemsControl ItemsSource="{Binding EditingTags}">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><WrapPanel Orientation="Horizontal" /></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <Border Background="{DynamicResource Bg.SurfaceElevated}"
                        CornerRadius="4" Padding="6,2" Margin="0,2,4,2">
                    <StackPanel Orientation="Horizontal" Spacing="4">
                        <TextBlock Text="{Binding}" FontSize="11"
                                   Foreground="{DynamicResource Text.Primary}" />
                        <Button Classes="ChipRemoveButton"
                                Command="{Binding $parent[Window].((vm:LightboxViewModel)DataContext).RemoveEditingTagCommand}"
                                CommandParameter="{Binding}">
                            <PathIcon Data="{StaticResource Icon.X}" Width="8" Height="8" />
                        </Button>
                    </StackPanel>
                </Border>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>

    <!-- 输入框 -->
    <TextBox x:Name="TagInputBox"
             Text="{Binding NewTagInput, Mode=TwoWay}"
             KeyDown="OnTagInputKeyDown"
             Watermark="输入标签后按回车添加"
             MaxLength="20" />

    <TextBlock Text="提示：AI 重新打标将覆盖手动修改"
               FontSize="10"
               Foreground="{DynamicResource Text.Tertiary}" />
</StackPanel>
```

**默认态 ↔ 编辑态 切换**用 `IsVisible` 双 StackPanel 互斥：绑 `IsEditingTags` 互斥条件 = `IsEditingTags` / `!IsEditingTags`。

### 3.4 输入校验

`LightboxViewModel.AddEditingTagFromInput(string raw)` 流程：

```
1. text = raw.Trim()
2. if string.IsNullOrEmpty(text) → return（静默丢弃）
3. if text.Length > 20 → 弹 tooltip / inline error "标签最长 20 字符"
4. if EditingTags.Any(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase)) → return（去重）
5. EditingTags.Add(text)
6. NewTagInput = ""
7. TextInput.Focus()  // 焦点回输入框
```

`TextBox.MaxLength="20"` 兜底硬限（XAML 层面就拦）。

### 3.5 保存 / 取消

```
SaveEditTags():
  if !IsDirty → return
  if EditingTags.SequenceEqual(CurrentFile.Tags) → return
  try:
      newList = EditingTags.ToList()
      CurrentFile.Tags = newList  // 乐观更新，UI 即时刷
      await _mediaRepo.UpdateTagsOnlyAsync(CurrentFile.Id, newList, nowMs, ct)
      ShowToast($"已更新 {CurrentFile.FileName} 的标签")
  catch (ex):
      CurrentFile.Tags = _originalTags  // 回滚
      ShowToast($"保存失败: {ex.Message}", isError: true)
  finally:
      IsEditingTags = false

CancelEditTags():
  EditingTags = _originalTags
  IsEditingTags = false
```

`IsDirty` 派生：`EditingTags` 与 `CurrentFile.Tags` 长度或内容不一致。

### 3.6 翻页 / 切换 CurrentFile 时

`CurrentIndex` 变化时若 `IsEditingTags == true`：

- 方案 A（推荐）：自动保存（沿用 SaveEditTags 流程），无 diff 则不写库
- 方案 B：弹确认对话框「当前编辑未保存，是否保存？」 三选一

选 A：翻页心智更顺，灯箱是浏览流不是表单。A 失败则 B 兜底（catch 弹错误 toast，不强行切）。

```
OnCurrentIndexChanged():
  if IsEditingTags:
      await SaveEditTags()  // 内部 try/catch，失败不阻塞翻页
  ... // 继续原来的 LoadCurrentAsync
```

### 3.7 AI 打标进行中 / 软删除文件

`LightboxViewModel` 新增 `CanEditTags` 派生：

```csharp
public bool CanEditTags =>
    CurrentFile != null
    && CurrentFile.DeletedAt == null
    && !_galleryVm.IsTagging  // AI 正在打标 → 锁
```

✏️ 按钮 `IsEnabled="{Binding CanEditTags}"`；false 时 tooltip 切内容（"AI 正在打标" / "文件已删除"）。

---

## 4. UI — 右键菜单（兜底入口）

### 4.1 菜单项

`Views/GalleryView.axaml.cs:64-101` `BuildPhotoContextMenu` 新增一项，**放在「AI 打标」下方**：

```csharp
menu.Items.Add(new MenuItem
{
    Header = "编辑标签",
    Command = vm.EditTagsSingleCommand,
    CommandParameter = file,
});
```

### 4.2 模态弹窗

`EditTagsSingleCommand` 调出 `EditTagsDialog`（新建 `Views/EditTagsDialog.axaml`）：

- 窗口：宽 480 / 高自适应，居中模态
- 内容：
  - 顶部缩略图（file.ThumbnailPath，80×60）+ 文件名（InfoValue 样式）
  - 「AI 标签」section 复用灯箱的 chip 编辑器（建议抽 `Controls/TagChipEditor.axaml` UserControl 复用）
  - 「提示：AI 重新打标将覆盖手动修改」同上
  - 底部「保存」「取消」按钮
- 多选模式不弹（spec §5.3 同款约束，code-behind 入口跳过）
- AI 打标中 / 软删除 → 弹窗按钮灰显

`EditTagsDialog` ViewModel 逻辑和灯箱内嵌的编辑态一致（共用 `TagEditorState` 结构），只是入口不同。

---

## 5. UI — 批量编辑

### 5.1 工具栏入口

`Views/MainWindow.axaml:183` 工具栏「AI 打标」按钮旁加：

```xml
<Button Classes="ToolbarButton"
        Command="{Binding GalleryViewModel.EditTagsBatchCommand}"
        IsEnabled="{Binding GalleryViewModel.IsBatchActionEnabled}"
        ToolTip.Tip="编辑标签（多选）">
    <StackPanel Orientation="Horizontal" Spacing="6">
        <PathIcon Data="{StaticResource Icon.Pencil}" Width="14" Height="14" />
        <TextBlock Text="编辑标签" FontSize="12" />
    </StackPanel>
</Button>
```

`IsBatchActionEnabled` 已有（`GalleryViewModel.cs:148`），会随多选 + 非打标态自动切换。

### 5.2 模态弹窗 `EditTagsBatchDialog`

布局：

```
┌─────────────────────────────────────────────────────────┐
│ 编辑标签 — 选中 5 个文件                                │
├─────────────────────────────────────────────────────────┤
│ 操作:  ○ 替换为  ○ 追加  ○ 删除                        │
│                                                         │
│ 标签:  [猎户座大星云 ×] [月亮 ×] [广角 ×]              │
│        ┌────────────────────────────────────┐          │
│        │ 输入标签后回车                       │          │
│        └────────────────────────────────────┘          │
│                                                         │
│ 预览:                                                  │
│   • IMG_001.jpg  [+ 猎户座大星云]  [- 月亮]           │
│   • IMG_002.jpg  [+ 猎户座大星云]  [- 月亮]           │
│   • ... (折叠超过 5 个)                                 │
│                                                         │
│ ⚠ AI 重新打标将覆盖手动修改                             │
│                                                         │
│              [取消]              [应用到 5 个文件]      │
└─────────────────────────────────────────────────────────┘
```

- 三个 scope 互斥单选（RadioButton）
- 标签 chip 编辑器：复用 §4.2 的 `TagChipEditor`
- 预览区：根据当前 scope + chip 列表算每张文件的 diff（+/- 标签名），实时刷新
- 「应用到 N 个文件」按钮：触发批量写入

### 5.3 Scope 语义

| Scope | 行为 | 例子 |
|---|---|---|
| **替换为** | `new file.Tags = editorTags`（原 tags 全删） | 编辑器有 `[A, B]` → 文件 tags 变为 `[A, B]`，原 `[X, Y, Z]` 全丢 |
| **追加** | `new file.Tags = file.Tags ∪ editorTags`（去重） | 编辑器有 `[A]` → 文件 `[X, Y, Z]` 变为 `[X, Y, Z, A]`，无 A 才加 |
| **删除** | `new file.Tags = file.Tags - editorTags` | 编辑器有 `[A]` → 文件 `[A, B, C]` 变为 `[B, C]` |

三种 scope 的共同前置：**chip 编辑器为空时不能保存**（替换/追加/删除都没意义），按钮 `IsEnabled` 绑 `HasAnyEditingTag`。

### 5.4 diff 预览算法

```csharp
// 替换
diff = (added: editorTags.Except(file.Tags), removed: file.Tags.Except(editorTags))

// 追加
diff = (added: editorTags.Except(file.Tags), removed: [])

// 删除
diff = (added: [], removed: file.Tags.Intersect(editorTags))
```

预览行：`<文件名> [+ 新增1] [+ 新增2] [- 删除1]`，纯展示不写库。

### 5.5 批量保存

```
EditTagsBatchCommand():
  if !IsBatchActionEnabled → return
  files = SelectedFiles.ToList()
  if files.Count == 0 → return
  var dialog = new EditTagsBatchDialog(files)
  var result = await dialog.ShowDialog<EditTagsBatchResult?>(owner)
  if result == null → return  // 取消
  nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  // 乐观更新：先改内存
  foreach (file in files):
      file.Tags = result.ComputeNewTags(file.Tags)
  // 批量写库
  try:
      await Task.WhenAll(files.Select(f =>
          _mediaRepo.UpdateTagsOnlyAsync(f.Id, f.Tags, nowMs, ct)))
      ShowToast($"已更新 {files.Count} 个文件的标签")
  catch (ex):
      // 整批回滚：把内存 file.Tags 恢复原值很难（丢了原值快照）
      // 简化方案：catch 后只 toast 报错，让用户手动 Reload
      ShowToast($"批量保存失败: {ex.Message}", isError: true)
      await ReloadCurrentViewAsync()  // 重载当前 Date/Tag 视图
```

**回滚策略的妥协**：批量时如果回滚要保留每张文件的原 Tags 快照，内存成本不高但代码啰嗦。本期选**简单方案**——写库失败 → toast + Reload 当前视图（重新 SELECT）。理由：批量写库失败概率极低（本地 SQLite），为这个写 rollback 不值得。

### 5.6 批量保存对左栏 Tag 视图的影响

如果当前 `GroupMode == Tag` 且编辑操作让某些文件从该 tag 视图里掉出去（删了 tag / 替换删了 tag / 等），保存后**当前 CurrentMediaFiles 列表里这些文件要消失**。

策略：保存后调 `_ = ReloadCurrentViewAsync()` 重载当前视图（按 SelectedTag 重新查），让 UI 自然更新。500ms debounce 走 `TagChangeDebouncer`（§7）。

---

## 6. ViewModel 扩展

### 6.1 `LightboxViewModel` 新增

```csharp
// 字段
private List<string> _originalTags = new();  // 进入编辑时的快照
private List<string> _editingTags = new();
private string _newTagInput = "";
private bool _isEditingTags;
private readonly GalleryViewModel? _galleryVm;  // 注入，AI 锁定用

// 属性
[ObservableProperty] private string _newTagInput = "";
[ObservableProperty] private bool _isEditingTags;
public ObservableCollection<string> EditingTags { get; } = new();
public bool IsDirty => !EditingTags.SequenceEqual(CurrentFile?.Tags ?? new List<string>());
public bool CanEditTags => CurrentFile != null
    && CurrentFile.DeletedAt == null
    && (_galleryVm?.IsTagging ?? false) == false;

// 命令
[RelayCommand(CanExecute = nameof(CanEditTags))]
private void EnterEditTags() { ... }

[RelayCommand]
private void CancelEditTags() { ... }

[RelayCommand(CanExecute = nameof(IsEditingTags))]
private async Task SaveEditTagsAsync() { ... }

[RelayCommand]
private void RemoveEditingTag(string tag) { ... }

[RelayCommand]
private void AddEditingTagFromInput() { ... }  // 内部 AddEditingTagFromInputRaw
```

`LightboxViewModel` 构造器加 `GalleryViewModel? galleryVm = null` 参数（DI 注入，可选——灯箱可能独立创建）。

### 6.2 `GalleryViewModel` 新增

```csharp
[RelayCommand(CanExecute = nameof(CanEditTagsSingle))]
private async Task EditTagsSingleAsync(MediaFile? file) { ... }
private bool CanEditTagsSingle(MediaFile? file) =>
    file != null && file.DeletedAt == null && !IsTagging;

[RelayCommand(CanExecute = nameof(IsBatchActionEnabled))]
private async Task EditTagsBatchAsync() { ... }

// 切日期 / 切 tag 时，如果 IsEditingTags 在 Lightbox 里，自动 Save（Lightbox 内部处理）
// GalleryVM 这边不直接管，但需要 IsTagging 状态变化时通知 Lightbox 刷新 CanEditTags
```

`OnIsTaggingChanged` 里追加：

```csharp
if (LightboxVm?.CurrentFile != null)
    LightboxVm.OnPropertyChanged(nameof(LightboxVm.CanEditTags));
```

### 6.3 `EditTagsDialogViewModel` / `EditTagsBatchDialogViewModel`

各新建一个文件：

- `ViewModels/EditTagsDialogViewModel.cs` —— 单张编辑（构造接受 `MediaFile` + `MediaRepository`）
- `ViewModels/EditTagsBatchDialogViewModel.cs` —— 批量编辑（构造接受 `IReadOnlyList<MediaFile>`）

两个都共用一个 `Controls/TagChipEditor.axaml` UserControl（chip 列表 + 输入框 + × 按钮），VM 暴露 `EditingTags : ObservableCollection<string>` + `AddTag(string)` / `RemoveTag(string)` / `CanAddTag(string)`。

### 6.4 `TagChangeDebouncer`（新增 `Helpers/TagChangeDebouncer.cs`）

```csharp
/// <summary>
/// 500ms debounce：MediaFile.Tags 变化触发后，等 500ms 没有新变化再跑一次回调。
/// 用途：连续编辑 N 张文件时，左栏 TagGroups 不刷 N 次，只刷 1 次。
/// </summary>
public class TagChangeDebouncer
{
    private CancellationTokenSource? _cts;
    private readonly int _delayMs;

    public TagChangeDebouncer(int delayMs = 500) { _delayMs = delayMs; }

    public void Trigger(Func<CancellationToken, Task> action)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delayMs, ct);
                await action(ct);
            }
            catch (OperationCanceledException) { }
        });
    }
}
```

`GalleryViewModel` 持有 `private readonly TagChangeDebouncer _tagDebouncer = new();`，在 `OnMediaFileTagsChanged(file)` 钩子里调 `_tagDebouncer.Trigger(LoadTagGroupsAsync)`。

订阅方式：在 `CurrentMediaFiles` 添加 / 替换时，给 `MediaFile.PropertyChanged` 挂 handler（`Tags` 变化时触发 debouncer），文件从列表移除时解绑。`LoadTagGroupsAsync` 已经在 `GroupMode.Tag` 下被 `OnGroupModeChanged` 调用，复用即可。

---

## 7. 边界 & 锁

| 场景 | 行为 | 涉及 |
|---|---|---|
| AI 正在打标 | 编辑入口灰显，tooltip "AI 正在打标"；批量按钮在工具栏自动 disable（`IsBatchActionEnabled` 已包含 `!IsTagging`） | 灯箱 ✏️ / 右键菜单 / 工具栏 |
| 软删除文件 | 编辑入口灰显，tooltip "文件已删除" | 灯箱 ✏️ / 右键菜单 |
| 灯箱翻页时正在编辑 | 自动 Save（无 diff 则不写库） | 灯箱 `OnCurrentIndexChanged` |
| 批量保存中点 Gallery 切日期 | 允许切换（批量是后台任务） | Gallery 切日 |
| 批量保存中点灯箱翻页 | 灯箱是另一文件，不冲突 | 灯箱 |
| 灯箱 Save 失败 | 内存回滚 + toast，**不切页不阻塞** | 灯箱 Save |
| 批量 Save 失败 | 整批 toast + Reload 当前视图 | 批量 Save |
| 输入超长 / 空 | inline 静默丢弃 | TagChipEditor |
| 重复 tag | 静默丢弃（大小写不敏感） | TagChipEditor |
| 中文 tag | `UpdateTagsOnlyAsync` 仍走 `UnsafeRelaxedJsonEscaping`（沿用 `s_writeTagsOptions`），LIKE 查询不会踩坑 ✓ | Data |
| 编辑过程中网络/DB 异常 | 见 5.5 / 3.5 各自的 catch | Save |
| 切 GroupMode（Date ↔ Tag） | 灯箱里的编辑态不受影响（LightboxVM 独立） | Gallery / Lightbox |

---

## 8. 持久化

### 8.1 SQL 模板

复用 `s_writeTagsOptions`（`MediaRepository.cs:33`）：

```csharp
private static readonly JsonSerializerOptions s_writeTagsOptions = new()
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
```

新方法 `UpdateTagsOnlyAsync`：

```csharp
public async Task UpdateTagsOnlyAsync(long fileId, IEnumerable<string> tags, long taggedAt, CancellationToken ct = default)
{
    var tagsList = tags.ToList();
    var tagsJson = JsonSerializer.Serialize(tagsList, s_writeTagsOptions);

    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    var sql = @"
        UPDATE media_files
        SET tags = @tags,
            tagged_at = @taggedAt,
            tag_error = NULL,
            updated_at = @updatedAt
        WHERE id = @id";

    await using var cmd = new SqliteCommand(sql, connection);
    cmd.Parameters.AddWithValue("@id", fileId);
    cmd.Parameters.AddWithValue("@tags", tagsJson);
    cmd.Parameters.AddWithValue("@taggedAt", taggedAt);
    cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));

    await cmd.ExecuteNonQueryAsync(ct);
}
```

### 8.2 `IMediaRepository` 新增

```csharp
/// <summary>
/// 手动编辑主体标签：只动 tags / tagged_at / tag_error，
/// 保留 score 和 quality_tags 原值。
/// </summary>
Task UpdateTagsOnlyAsync(long fileId, IEnumerable<string> tags, long taggedAt, CancellationToken ct = default);
```

### 8.3 字段写入矩阵

| 字段 | 手动编辑 | AI 打标（`BatchTagCoreAsync`） |
|---|---|---|
| `tags` | ✅ 新值 | ✅ 新值 |
| `quality_tags` | ❌ 不动 | ✅ 新值 |
| `score` | ❌ 不动 | ✅ 新值 |
| `tagged_at` | ✅ 刷 nowMs | ✅ 刷 nowMs |
| `tag_error` | ✅ 清空 | ✅ 成功时清空，失败时写入 |
| `updated_at` | ✅ SQL DEFAULT / 显式刷 | ✅ 同上 |

---

## 9. 实施步骤

按 spec 走，按顺序实施（每个独立 commit）：

1. **数据层**：`IMediaRepository` + `MediaRepository` 加 `UpdateTagsOnlyAsync`（1 个 commit）
2. **VM 基础**：`LightboxViewModel` 加 IsEditingTags / EditingTags / EnterEdit / Save / Cancel / RemoveEditingTag / AddEditingTagFromInput（1 个 commit）
3. **灯箱 UI**：LightboxWindow.axaml 改造 AI 标签 section，加 ✏️ / 编辑态 / 输入框（1 个 commit）
4. **公共控件**：抽 `Controls/TagChipEditor.axaml` UserControl + 配套 VM state（chip 列表 + 输入校验 + × 按钮）（1 个 commit）
5. **右键菜单 + 单张模态弹窗**：`EditTagsDialog` + `GalleryViewModel.EditTagsSingleCommand` + 右键菜单项（1 个 commit）
6. **批量 VM + 模态弹窗**：`EditTagsBatchDialog` + scope / diff 预览 + 「应用到 N 个文件」（1 个 commit）
7. **工具栏入口**：`MainWindow.axaml` 加「编辑标签」按钮 + `EditTagsBatchCommand`（1 个 commit）
8. **左栏刷新 + debounce**：`TagChangeDebouncer` + `OnMediaFileTagsChanged` 订阅（1 个 commit）
9. **边界 & 锁**：CanEditTags 派生 + AI 打标/软删除灰显 + 翻页自动 Save（1 个 commit）
10. **测试 & 调试日志**：Trace.WriteLine 按 step 1/3 拆，错误必留痕

> ⚠️ **不自动 commit**。每步做完改完留在工作区，等用户拍板「commit」再 git commit。

---

## 10. 不在本期 scope

- Undo / Redo（手动编辑的撤销栈，依赖 tag 变更历史，不做）
- 标签合并 / 重命名（"把所有'月亮'统一改成'超级月亮'"—— 属批量管理，单独做）
- 输入时自动补完已有标签（`SubjectTagVocabulary` 已经在 `13-tag-quality-split.md` 定义了，可以补但不补也行）
- tag 变更历史快照（who/when 改了什么，依赖 schema 加 `tag_history` 表，不做）
- QualityTags 手动编辑（用户拍板：本期**只动 tags**，`QualityTags` 继续走 AI）
- 灯箱里显示 QualityTags（v0.7 拆字段后漏的，顺手 bug，不在本期）
- 标签分组 / 收藏夹（属标签管理功能，单独做）

---

## 11. 待用户确认

- [ ] 入口方案（灯箱 + 右键）OK？
- [ ] 批量 scope（替换 / 追加 / 删除）三种行为符合预期？
- [ ] 翻页自动 Save 方案 A（不弹确认）OK？
- [ ] 批量保存失败 → 简单回滚（toast + Reload）OK？
- [ ] 不挂版本号，按 CHANGELOG 钉
- [ ] 不动 QualityTags / score
- [ ] 不做 Undo / 不做补完 / 不做历史
