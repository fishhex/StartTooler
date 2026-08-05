# 0.12 — 标签输入自动补全（下拉选择已有标签）

> 关联文档：`0.10/15-manual-tag-edit.md`（手动编辑标签 — 已落地）、`0.11/spec/05-ui-interaction-review.md`（UI 交互评审）、`0.10/13-tag-quality-split.md`（SubjectTagVocabulary 预留）。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.1（设计稿）** |
| 目标用户 | 天文摄影爱好者 |
| 文档状态 | **设计 — 待用户评审** |
| 实施版本 | 实施时按 CHANGELOG 钉，本 spec 不预先挂版本号 |
| 问题描述 | v0.10 落地手动编辑标签后（灯箱/单张弹窗/批量弹窗），用户**添加 tag 只能盲打**——AI 历史打过、用户手敲过的 tag 散落各处，常见场景"我想把上次打过的『M31 仙女座』加到这文件"必须完整拼写或翻 AI 结果区查，体感差。本期让输入框具备**下拉选择已有标签**的能力。 |

### 范围与不变

| 改动 | 内容 |
|---|---|
| **目标控件** | `Controls/TagChipEditor.axaml`（三个宿主页共用：灯箱 / 单张弹窗 / 批量弹窗） |
| **新增能力** | 输入框获得焦点 / 有内容时，下拉显示项目已有 tag 列表（可过滤、可键盘选中） |
| **既有行为** | 纯手敲回车添加、chip 点击删除、批量弹窗的「项目标签建议云」**保留**作为替代入口（不删） |
| **数据层** | **零 schema 变更**。复用 `IMediaRepository.GetTagsAsync(projectPath)` |
| **范围** | 只动 `tags`（主体标签），与 0.10 spec §2.3 一致 |

---

## 1. 现状与痛点

### 1.1 三个标签编辑器对照

| 入口 | VM | 控件 | 输入方式 | 建议入口 |
|---|---|---|---|---|
| 灯箱行内编辑 | `LightboxViewModel` | `TagChipEditor` | 纯输入框 + 回车 | **无** |
| 单张模态弹窗 | `EditTagsDialogViewModel` | `TagChipEditor` | 纯输入框 + 回车 | **无** |
| 批量模态弹窗 | `EditTagsBatchDialogViewModel` | `TagChipEditor` | 纯输入框 + 回车 | 已有「项目标签建议云」（`SuggestedTags`） |

**关键发现**：批量弹窗已经有"项目标签云"（`doc/0.10/15-manual-tag-edit.md` §5 提到、`Views/EditTagsBatchDialog.axaml:130-156` 实现），但灯箱和单张弹窗没有；用户在这两处碰到常用 tag 时只能完整重打或开批量弹窗绕一圈。

### 1.2 用户故事

| ID | 故事 | 验收点 |
|---|---|---|
| US-1 | 我在灯箱里改 tag，输入"月亮"时希望看见之前打过的"月亮"、"月亮细节"、"超级月亮"，点一下就加进 chip 列表 | 输入框聚焦 / 有内容时下拉显示项目已有 tag；中文/英文/数字混合支持；点选即添加 |
| US-2 | 我想不起来某个 tag 的**完整名字**（如"M31 仙女座星云"），希望输入"M31"后能筛出来 | 实时过滤：starts-with 优先 + contains 兜底；忽略大小写 |
| US-3 | 我想保留"输入即新建"的能力（"猎户座大星云"AI 没打过、想新加） | 候选列表为空 / 没匹配项时，回车仍按现有规则新建 tag |
| US-4 | 我已经在 chip 列表里的 tag，不要再建议一次（避免重复） | 候选列表自动排除 `Tags` 里已有的 tag |
| US-5 | 我希望用键盘搞定：↓↑ 选候选、Enter 确认、Esc 关闭、Tab 也能跳到下一字段 | 完整键盘导航，不依赖鼠标 |
| US-6 | 我希望候选里能看见这个 tag 用了多少次（"月亮 (12)"），知道常用不常用 | 候选行展示该 tag 在项目中的文件数 |
| US-7 | 数据很多时（项目 200+ tag），下拉不要一坨 200 行撑爆弹窗 | 下拉最多 N 条（默认 8）+ 滚动条；头部提示"还有 X 个匹配项" |
| US-8 | 三个宿主页（灯箱/单张/批量）的体验一致 | `TagChipEditor` 一处实现，三处共享 |
| US-9 | 已有「项目标签建议云」（批量弹窗）保留，作为下拉的可见替代 | 批量弹窗不删建议云；其他两处加下拉 |
| US-10 | 输入 tag 时，光标应总在输入框；下拉打开不抢焦点 | 下拉是 Popup，焦点保留在 TextBox |

---

## 2. 交互设计

### 2.1 触发模型

下拉有 **三种触发条件**，任一满足即显示：

| 触发 | 条件 | 说明 |
|---|---|---|
| **聚焦触发** | TextBox 获得焦点 | 用户主动点入输入框 → 立即展示候选（无须输入） |
| **输入触发** | TextBox 文本非空 | 用户开始打字 → 重新过滤 |
| **回车触发** | 输入文本无匹配候选 | 显示「按 Enter 新建 "xxx"」行，按 Enter 创建新 tag（沿用现有 `AddTagCommand`） |

> **关闭条件**：Esc、点击输入框外、选中候选后、TextBox 失焦且文本为空时延迟 200ms 关闭（避免误触）。

### 2.2 候选列表行为

```
┌─────────────────────────────────────────────────┐
│ 🔍 输入框：[月|                              ]   │  ← 焦点保留在 TextBox
├─────────────────────────────────────────────────┤
│ ★ 月亮 (12)                              ← 高亮  │  ← 1st 项默认高亮
│   月亮细节 (3)                                  │
│   超级月亮 (5)                                  │
│ ─────────────────────────────────────────────  │
│   + 新建 "月"（按 Enter 创建）                   │  ← 仅当文本无精确匹配时显示
└─────────────────────────────────────────────────┘
        候选 3 / 共 8 个匹配项 ↑↓ 选择, Enter 确认
```

#### 2.2.1 过滤算法

```
1. query = NewTagInput.Trim()（忽略大小写）
2. allTags = 项目已有 tag 列表（去重、按使用频次降序）
3. filtered = allTags
     .Where(t => !Tags.Contains(t))         // 已在 chip 列表 → 排除
     .Where(t => t.IContains(query)        // 不区分大小写 contains
              || t.StartsWith(query))      // starts-with 排前
4. ordered = filtered
     .OrderByDescending(t => t.StartsWith(query))   // starts-with 优先
     .ThenByDescending(t => t.UsageCount)            // 频次高优先
     .ThenBy(t => t.Name)                            // 同频次按名字稳定排序
5. 显示前 maxItems(8) 条
```

#### 2.2.2 高亮匹配段

候选行内 `query` 子串用 `Accent.Stellar` 色 + Bold 高亮（如 "月亮" 的"月"字高亮）。

### 2.3 键盘交互

| 按键 | 行为 |
|---|---|
| `↓` | 高亮下一条；到底后回到第一行（环形） |
| `↑` | 高亮上一条；到顶后跳到最后一行（环形） |
| `Enter` | 选中当前高亮项：若高亮候选 → 添加到 chip 并清空输入框；若高亮"新建"行 → 创建新 tag |
| `Esc` | 关闭下拉，焦点保留在 TextBox；TextBox 文本不变 |
| `Tab` | 关闭下拉，焦点移到下一焦点元素（沿用默认 Tab 行为） |
| `Backspace` | 输入框空时光标闪一下作为反馈，下拉不变 |

> **重要**：回车的语义 = "添加当前高亮项"。如果用户没动键盘、只是输入文本然后回车，行为是：
> - 有匹配候选 → 选中第一个候选（stars-with 优先那个）
> - 无匹配候选 → 新建
>
> 这与现行"回车就新建"略有差异，需要小心处理——见 §4.2 兼容方案。

### 2.4 鼠标交互

| 操作 | 行为 |
|---|---|
| 候选行 hover | 高亮 + 显示指针 |
| 候选行 click | 选中（同键盘 Enter） |
| 点击下拉外部 | 关闭下拉（延迟 150ms 让 click 完成） |
| 点击输入框 | 重新打开下拉并显示完整列表 |

### 2.5 选中后的回执

候选被选中后：
1. 文本被加进 `Tags` 集合（触发 `IsDirty` 刷新）
2. `NewTagInput` 清空
3. 焦点回到 TextBox（用户可继续输入）
4. 下拉保持打开（除非输入框已空且未聚焦，见 §2.1 关闭条件）

---

## 3. UI 设计

### 3.1 控件结构（控件树）

```
TagChipEditor (UserControl, DataContext = ITagEditorHost)
└── StackPanel
    ├── ItemsControl                ← 已有：chip 列表
    │   └── WrapPanel
    │       └── Button.chip-removable  ← 每个 chip
    └── Grid (TextBox + 触发按钮)    ← 改造：替换原 TextBox
        ├── Column 0: TextBox (内嵌 Popup)
        │   └── Popup (IsOpen 绑 HasSuggestions)
        │       └── Border (suggestion-dropdown)
        │           ├── ItemsControl (ListBox 行为)
        │           │   └── StackPanel
        │           │       ├── 高亮项 1
        │           │       ├── ...
        │           │       └── "+ 新建 xxx" 行（条件显示）
        │           └── TextBlock (footer: "↑↓ 选择, Enter 确认")
        └── Column 1: Button (chevron-down)  ← 强制展开按钮（可选）
```

### 3.2 文字稿（三个入口改造前后）

#### 3.2.1 灯箱行内编辑（改造后）

```
┌─ 灯箱右侧栏 ─────────────┐
│ AI 标签                   │
│                            │
│ [月亮 ×] [猎户座 ×] [M42 ×]│  ← 已有 chip
│                            │
│ ┌──────────────────────┐  │
│ │ 月|                 🔍│  │  ← 输入框：左侧 12px 缩进，右侧 chevron 按钮
│ └──────────────────────┘  │
│   ┌──────────────────┐    │
│   │ ★ 月亮 (12)      │ ← 1│
│   │   月光 (4)        │  2│
│   │   月光云 (1)      │  3│
│   ├──────────────────┤   │
│   │ + 新建 "月"      │   │  ← Enter 创建
│   └──────────────────┘   │
│  ↑↓ 选择, Enter 确认, Esc 关闭│
│                            │
│ 提示：AI 重新打标将覆盖手动修改│
│               [取消] [保存]│
└────────────────────────────┘
```

#### 3.2.2 单张模态弹窗 `EditTagsDialog`

布局与原状一致，仅输入框一行替换为下拉式（视觉同 3.2.1，宽度跟随弹窗）。

#### 3.2.3 批量模态弹窗 `EditTagsBatchDialog`

「添加标签」section 内部输入框改造为下拉式。

**既有的「项目标签建议云」**：保留不变（作为下拉的可见替代 / 鼠标浏览用）。下拉与建议云共存，互补。

### 3.3 下拉视觉规范

| 元素 | Token | 说明 |
|---|---|---|
| 容器背景 | `Bg.Outer` (不透明) | 比 Surface 更深的"浮层感" |
| 容器边框 | `Border.Strong` 1px | 强边框，区分于 chip |
| 容器圆角 | 6 | 与输入框一致 |
| 容器阴影 | `Overlay.Scrim` 10% | 浮层微阴影 |
| 行高 | 28px | 紧凑列表 |
| 行 hover / 选中 | `Bg.Hover` | 统一 hover 态 |
| 文字（常规） | `Text.Primary` | 候选名 |
| 文字（高亮段） | `Accent.Stellar` Bold | 匹配子串 |
| 文字（频次） | `Text.Tertiary` 11px | 候选名后的 (N) |
| "+ 新建"行 | `Text.Secondary` + Border.Subtle 顶部分隔 | 视觉降级，提示"非已有" |
| Footer 提示 | `Text.Tertiary` 10px | 操作指引 |
| Popup 位置 | 锚定到 TextBox 下沿、宽度 = TextBox 宽度 + chevron 按钮 | 视觉对齐 |
| Popup 最大高度 | 280px (≈ 8 行) | 超出滚动 |

### 3.4 chevron 按钮（可选）

- 位置：TextBox 右侧内嵌（Column 1），24×24
- 图标：`Icon.ChevronDown`（沿用 0.10 §3.2 风格，参考 `Icon.Pencil` / `Icon.X`）
- 默认行为：点击 → 强制展开下拉（即使用户没聚焦、文本为空）
- 状态：下拉打开时旋转 180°，表示"展开中"

> **可裁剪**：如果用户认为"focus 即展开"已足够，chevron 按钮可省。本期**默认包含**，但样式上低调（仅图标、无文字）。

---

## 4. 行为细节

### 4.1 候选数据加载

#### 4.1.1 数据源

`IMediaRepository.GetTagsAsync(projectPath)` 当前返回 `IReadOnlyList<Tag>`（仅 `Id` / `Name`）。本 spec 需要 `UsageCount`，需要：

**方案 A（轻量）**：扩展 `GetTagsAsync` 返回 `TagWithCount`（含 `UsageCount`），`MediaRepository` 内部走一个 SQL：
```sql
SELECT t.id, t.name, COUNT(mt.media_file_id) AS usage_count
FROM tags t
LEFT JOIN media_file_tags mt ON mt.tag_id = t.id
LEFT JOIN media_files mf ON mf.id = mt.media_file_id
    AND mf.deleted_at IS NULL
WHERE t.project_path = @projectPath
GROUP BY t.id, t.name
ORDER BY usage_count DESC, t.name ASC
```

**方案 B（零 SQL 变更）**：用现有 `GetTagsAsync` 拿 name 列表，加一个 `GetTagUsageCountsAsync` 单独拿 { name → count } 字典（性能可能差，多次查）。

**选 A**：更干净，1 个 SQL 解决问题。

#### 4.1.2 加载时机

| 入口 | 加载时机 |
|---|---|
| 灯箱行内编辑 | 进入编辑态时（`EnterEditTagsCommand`）触发一次；后续切文件若不退出编辑态，则缓存复用 |
| 单张弹窗 | 弹窗 `Loaded` 事件触发一次 |
| 批量弹窗 | 复用现有的 `LoadSuggestedTagsAsync` 时机（构造时 fire-and-forget） |

**缓存策略**：每个 VM 持有一份 `ObservableCollection<TagWithCount> AllProjectTags`，加载完成后不重复请求。
灯箱里若用户在不同文件间切（且一直处于编辑态），**不重载**（tag 集合是项目级别，与单文件无关）。

### 4.2 与现行"回车即新建"的兼容

> ⚠ **行为变化点**：现行回车 = 直接调 `AddTagCommand.Execute(null)`，永远走新建。改造后回车 = 选中当前高亮候选。

**过渡设计**：

```
OnEnterPressed():
  1. 若下拉打开且有高亮候选 → 选中该候选（添加到 chip 列表）
  2. 若下拉打开但无高亮（仅"新建"行） → 走原 AddTagCommand
  3. 若下拉关闭（用户没聚焦候选） → 走原 AddTagCommand
  4. 高亮永远默认为第一项（filtered list 头部）
```

**回退路径**：用户在没动键盘、只输入文本然后回车时，**默认行为仍是新建**（因无候选时直接走 step 2）。但若用户输入 "M31" 看到候选"M31 仙女座星云"再回车，则会**选中候选**而非新建"M31"。

> 这个行为变化需要在 chip 列表变化时给出轻提示（可选）：如新增 chip 是来自候选，chip 颜色闪一下 `Accent.Stellar` 200ms 渐隐（暂不做，先按"无差别添加"实现）。

### 4.3 重复 / 超长校验

沿用 `EditTagsDialogViewModel.AddTagFromInputRaw` 现有规则（trim / 去空 / 长度 ≤ 20 / 大小写不敏感去重）。选中候选等价于直接调用 `Tags.Add(t.Name)`——因为候选必然满足这些规则（来自项目已有 tag）。

### 4.4 AI 锁定 / 软删除文件

本期不引入新锁。沿用 `CanEditTags` / `CanEditTagsSingle` / `IsBatchActionEnabled` 既有判定（`0.10/15-manual-tag-edit.md` §7）。宿主在禁用状态下，TagChipEditor 的输入框已不可用，下拉自然不会出现。

---

## 5. 架构

### 5.1 新增/修改文件清单

| 文件 | 用途 | 类型 |
|---|---|---|
| `Data/TagWithCount.cs` | 新增 DTO（`Id` / `Name` / `UsageCount`） | 新增 |
| `Data/IMediaRepository.cs` | `GetTagsAsync` 返回类型改为 `IReadOnlyList<TagWithCount>` | 修改 |
| `Data/MediaRepository.cs` | 改 SQL 实现：tag + LEFT JOIN count | 修改 |
| `Controls/TagChipEditor.axaml` | TextBox 内嵌 Popup + 候选列表 + chevron 按钮 | 修改 |
| `Controls/TagChipEditor.axaml.cs` | `OnTextChanged` / `OnKeyDown` / `OnSelectionClick` / Popup 开关 | 修改 |
| `Controls/ITagEditorHost.cs` | 新增 `AllProjectTags : ObservableCollection<TagWithCount>` / `MaxSuggestions = 8` | 修改 |
| `ViewModels/LightboxViewModel.cs` | 构造时 `LoadAllProjectTagsAsync()` 触发一次 | 修改 |
| `ViewModels/EditTagsDialogViewModel.cs` | 弹窗 `Loaded` 时触发 | 修改 |
| `ViewModels/EditTagsBatchDialogViewModel.cs` | 替换 `LoadSuggestedTagsAsync` 返回类型，复用 `AllProjectTags` | 修改 |
| `Views/EditTagsDialog.axaml.cs` | 订阅 `Loaded` 事件触发 `LoadAllProjectTagsAsync` | 修改 |
| `Themes/Styles.axaml` | 新增 `Style Selector="Border.suggestion-dropdown"` 等下拉样式 | 修改 |

### 5.2 控件契约扩展（`ITagEditorHost`）

```csharp
public interface ITagEditorHost
{
    // 既有
    ObservableCollection<string> Tags { get; }
    string NewTagInput { get; set; }
    int MaxTagLength { get; }
    string Watermark { get; }
    bool ShowInputBox { get; }
    ICommand AddTagCommand { get; }
    ICommand RemoveTagCommand { get; }

    // 新增
    /// <summary>项目所有 tag 字典（含使用频次），由 VM 在加载完成后填充。</summary>
    ObservableCollection<TagWithCount> AllProjectTags { get; }

    /// <summary>下拉最多展示候选数（默认 8）。</summary>
    int MaxSuggestions => 8;

    /// <summary>从候选选中一个 tag（默认等价于 AddTagCommand.Execute(name)）。</summary>
    ICommand AddTagFromSuggestionCommand { get; }
}
```

### 5.3 控件 code-behind 关键方法

```csharp
// 文本变化 → 重算 filtered → 更新 Popup.IsOpen / 选中默认第一项
private void OnNewTagInputChanged(string value)
{
    var query = value?.Trim() ?? "";
    var all = (DataContext as ITagEditorHost)?.AllProjectTags;
    if (all == null) { _filtered.Clear(); Popup.IsOpen = false; return; }

    var currentTags = (DataContext as ITagEditorHost)?.Tags;
    _filtered = all
        .Where(t => currentTags == null
                 || !currentTags.Any(c => string.Equals(c, t.Name, StringComparison.OrdinalIgnoreCase)))
        .Where(t => string.IsNullOrEmpty(query)
                 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(t => t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(t => t.UsageCount)
        .ThenBy(t => t.Name)
        .Take((DataContext as ITagEditorHost)?.MaxSuggestions ?? 8)
        .ToList();

    Suggestions = new ObservableCollection<TagWithCount>(_filtered);
    HasExactMatch = _filtered.Any(t => string.Equals(t.Name, query, StringComparison.OrdinalIgnoreCase));
    Popup.IsOpen = Suggestions.Count > 0 || (query.Length > 0 && !HasExactMatch);
    HighlightedIndex = 0;
}

// 键盘 ↓↑ Enter Esc
private void OnInputKeyDown(KeyEventArgs e)
{
    if (!Popup.IsOpen) return;

    switch (e.Key)
    {
        case Key.Down:
            HighlightedIndex = (HighlightedIndex + 1) % (Suggestions.Count + (HasExactMatch ? 0 : 1));
            e.Handled = true; break;
        case Key.Up:
            HighlightedIndex = (HighlightedIndex - 1 + Suggestions.Count + (HasExactMatch ? 0 : 1))
                              % (Suggestions.Count + (HasExactMatch ? 0 : 1));
            e.Handled = true; break;
        case Key.Enter:
            if (HighlightedIndex < Suggestions.Count)
                AddTagFromSuggestionCommand.Execute(Suggestions[HighlightedIndex].Name);
            else
                AddTagCommand.Execute(null);  // 走新建
            e.Handled = true; break;
        case Key.Escape:
            Popup.IsOpen = false;
            e.Handled = true; break;
    }
}
```

> **编译 binding 模式注意**：与 `TagChipEditor` 现行做法一致——不写 `$parent[UserControl].DataContext.XxxCommand` 链式 cast，code-behind 强转 `ITagEditorHost` 拿命令（参见 `0.10/15-manual-tag-edit.md` §4 警告）。

### 5.4 VM 加载范式

```csharp
// LightboxViewModel / EditTagsDialogViewModel / EditTagsBatchDialogViewModel
private async Task LoadAllProjectTagsAsync()
{
    try
    {
        var tags = await _mediaRepo.GetTagsAsync(_projectPath);
        AllProjectTags.Clear();
        foreach (var t in tags) AllProjectTags.Add(t);
    }
    catch (Exception ex)
    {
        Trace.WriteLine($"[TagChipEditor] LoadAllProjectTags failed: {ex.Message}");
        // 静默失败：下拉一直为空，等同"用户盲打"旧体验，不阻塞主流程
    }
}
```

**灯箱场景**：`LightboxViewModel` 需要注入 `IMediaRepository` + `projectPath`（已部分存在，构造时扩展）。

**复用性**：`EditTagsBatchDialogViewModel` 现有的 `LoadSuggestedTagsAsync` 直接替换为 `LoadAllProjectTagsAsync`，**建议云**改为绑定 `AllProjectTags`（取前 20 个）渲染——代码量更少、视觉更一致。

---

## 6. 边界与异常

| 场景 | 行为 |
|---|---|
| 加载项目 tag 失败（DB 异常） | 静默忽略，下拉永远为空，回退纯手敲 |
| 项目无任何 tag（新建项目） | 下拉永远为空，"+ 新建" 行在有输入时显示 |
| 输入超 20 字符 | 沿用 `MaxLength="20"` 硬限，超出字符物理上无法输入 |
| 输入特殊字符（emoji、空白） | trim 后非空就接受；与现行规则一致 |
| 输入已存在 tag 的精确名 | "新建" 行隐藏，回车选中第一项（=精确匹配）→ 加进 chip（实际是 no-op 去重，但视觉友好） |
| 候选被选中后再次输入相同字符 | 重新打开下拉，因 chip 已有此 tag 候选被排除 |
| 弹窗 Owner 关闭（外部 click） | Popup 自动随之关闭 |
| 灯箱切文件时编辑态保持 | 不重新加载（项目级 tag 不变），用户编辑态不打断 |

---

## 7. 决策记录（用户已确认）

| ID | 决策点 | 选定方案 |
|---|---|---|
| D1 | 触发模型 | ✅ **聚焦 + 输入 + chevron**（三态触发） |
| D2 | 候选数据范围 | ✅ **仅当前项目**（复用 `GetTagsAsync(projectPath)`） |
| D3 | 数据加载时机 | ✅ **进入编辑态时一次**（灯箱编辑态期间切文件复用缓存） |
| D4 | 回车行为 | ✅ **选中第一候选**（无匹配候选时回退为新建） |
| D5 | 批量弹窗建议云 | ✅ **保留**（与下拉互补） |
| D6 | 频次展示 | ✅ **显示 (N)**（默认） |
| D7 | chevron 按钮 | ✅ **做**（24×24、旋转 180° 提示展开中） |
| D8 | 匹配段高亮 | ✅ **Accent.Stellar + Bold** |
| D9 | `GetTagsAsync` 返回类型 | ✅ **扩成 `TagWithCount`**（含 UsageCount） |

---

## 8. 不在本期 scope

- 标签管理（左栏重命名 / 合并 / 删除 / 拖拽）——属标签管理功能，单独做
- AI 推荐 tag 排序（"用得越多越靠前"是历史频次，与"AI 推荐 top 5"不是一回事）
- 输入历史 / 最近使用 / 收藏
- 跨项目 tag 搜索
- tag 变更历史快照
- 批量弹窗的「操作 3：删除标签」section 内 `RemovableTags` 加下拉（当前已是 chip × 形式，体验足够）

---

## 9. 实施步骤（草案，待评审后细化）

按 spec 走，按顺序实施（每个独立 commit）：

1. **数据层**：`TagWithCount` DTO + `GetTagsAsync` 扩返回类型 + SQL 改造
2. **契约扩展**：`ITagEditorHost` 加 `AllProjectTags` / `MaxSuggestions` / `AddTagFromSuggestionCommand`
3. **VM 加载**：三个 VM 调 `LoadAllProjectTagsAsync`，批量弹窗建议云切到 `AllProjectTags`
4. **控件改造**：`TagChipEditor.axaml` 内嵌 Popup + 候选 ItemsControl + chevron 按钮；code-behind 写过滤/键盘/选中
5. **样式**：`Styles.axaml` 加 `suggestion-dropdown` / `suggestion-row` / `suggestion-row-highlighted` 等样式
6. **灯箱切文件复用**：保证 `EditingTags` 切文件不重载
7. **测试 & Trace**：每个 VM 加 `[TagChipEditor] ...` 日志；DB 失败留痕
8. **回归**：批量弹窗「项目标签云」视觉不变

> ⚠ **不自动 commit**。每步做完改完留在工作区，等用户拍板「commit」再 git commit。
