# 13 — AI 标签与质量评价字段拆分

> 关联文档：`11-ai-tagging.md`（AI 打标主 spec）、`12-ai-toolbar-buttons.md`（工具栏按钮）、`02-data-layer.md`（数据层）、`05-gallery-view.md`（Gallery 视图）。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v1.0** |
| 目标用户 | 天文摄影爱好者 |
| 实施版本 | StartTooler v0.7 |
| 问题描述 | v0.6 中 AI 返回的 `tags` 混合了天体主体标签（如「星云」「土星」）和质量评价标签（如「欠曝」「噪点」），语义混淆。用户需要：a) AI 标签仅返回照片中的天文主体；b) 质量评价作为独立字段展示。 |
| 文档状态 | **plan** |

### 变更摘要

| 改动 | 内容 |
|---|---|
| **词表拆分** | `ChineseTagVocabulary`（56个）→ `SubjectTagVocabulary`（46个主体）+ `QualityTagVocabulary`（10个质量） |
| **数据层** | `media_files` 加 1 列：`quality_tags TEXT NOT NULL DEFAULT '[]'` |
| **服务层** | `TagResult` 加 `QualityTags` 字段；prompt 改为要求返回 `tags` + `quality_tags` 两个独立数组；`ParseAndValidate` 分别校验 |
| **UI** | photo tile 新增质量标签徽章条（与 subject tag 条并行，视觉区分）；左栏标签分类仅显示 subject tags |

---

## 1. 需求

### 1.1 用户故事

| ID | 故事 | 验收点 |
|---|---|---|
| US-1 | 我希望 AI 标签只描述照片里的天体（月球、土星…），质量评价单独看 | 打标后 card 显示「星云 · 猎户座大星云」标签 + 单独一条「欠曝 · 噪点」质量徽章 |
| US-2 | 我希望左栏「标签」tab 只列天体/题材标签，不被质量标签污染 | 左栏不出现「欠曝」「噪点」等质量词 |
| US-3 | 我希望按质量标签也能筛选（"找出所有欠曝的片子"） | 后续 phase 通过 Search/Filter 实现（本期仅存储 + 展示） |

### 1.2 验收点

- ☐ AI prompt 要求 `tags`（仅 46 个主体词）和 `quality_tags`（仅 10 个质量词）分开返回
- ☐ `TagResult` record 加 `QualityTags: IReadOnlyList<string>`
- ☐ DB 加 `quality_tags TEXT NOT NULL DEFAULT '[]'` 列，幂等迁移
- ☐ `MediaFile` 模型加 `QualityTags` 属性 + `HasQualityTags` 派生
- ☐ `UpdateTagAsync` 方法签名加 `qualityTags` 参数
- ☐ photo tile 底部加质量标签条（如「欠曝 · 噪点」），为橙红色调以区分主体的蓝色调
- ☐ 左栏 `GetTagGroupsAsync` 不聚合质量标签（只统计 subject tags）
- ☐ 已打标的旧数据不受影响：`tags` 列中的历史质量词仍在原位、不会回填到新列（不做迁移）

---

## 2. 词表拆分

### 2.1 主体词表 `SubjectTagVocabulary`（46 个）

用于 AI `tags` 字段 + 左栏标签分类聚合 + `GetByTagAsync` 筛选。

```csharp
public static readonly IReadOnlyList<string> SubjectTagVocabulary = new[]
{
    // 深空天体 (5)
    "星云", "星系", "星团", "超新星遗迹", "暗星云",

    // 太阳系 (8)
    "行星", "月亮", "太阳", "彗星", "小行星", "流星", "卫星", "国际空间站",

    // 命名天体 (15)
    "猎户座大星云", "M42", "仙女座星系", "M31", "昴星团", "M45",
    "银河", "银心", "土星", "木星", "火星", "金星",
    "娥眉月", "满月", "月面特写",

    // 现象 (6)
    "极光", "日食", "月食", "凌日", "月掩星", "合相",

    // 构图 / 拍摄手法 (10)
    "广角", "窄带", "行星摄影", "深空摄影", "星座", "长焦",
    "赤道仪跟踪", "行星叠加", "月面拼接", "全景",

    // 特殊 (2)
    "非天文", "未分类",
};
```

> **设计决策**：「广角」「窄带」「行星摄影」等构图/手法词归入主体（这些描述的是照片"拍什么/怎么拍"，而非"拍得怎么样"）；质量评价是独立维度。

### 2.2 质量词表 `QualityTagVocabulary`（10 个）

用于 AI `quality_tags` 字段 + 后续 Search/Filter 功能。不参与左栏标签聚合。

```csharp
public static readonly IReadOnlyList<string> QualityTagVocabulary = new[]
{
    "拖线", "失焦", "噪点", "过曝", "欠曝", "色差",
    "大气抖动", "视宁度差", "镜头眩光", "杂光",
};
```

### 2.3 兼容旧常量

`ChineseTagVocabulary` 保留但标记 `[Obsolete]`，其值为 `SubjectTagVocabulary.Concat(QualityTagVocabulary).ToList()`，供外部引用（如有）平滑过渡。内部逻辑全部走新拆分常量。

---

## 3. 数据模型

### 3.1 `media_files` 表 schema 变更

```sql
-- v0.7 新增
ALTER TABLE media_files ADD COLUMN quality_tags TEXT NOT NULL DEFAULT '[]';
-- JSON List<string>，与 tags 列同结构（UnsafeRelaxedJsonEscaping 序列化）
```

迁移幂等（`SqliteMigrations.AddColumnIfMissing`），与 v0.6 的 `tags` 列一致。

完整 schema 新增列：

```sql
tags         TEXT NOT NULL DEFAULT '[]',       -- v0.6: JSON 主体标签
score        INTEGER,                           -- v0.6: 0-100
tagged_at    INTEGER,                           -- v0.6: unix ms
tag_error    TEXT,                              -- v0.6: 打标失败原因
quality_tags TEXT NOT NULL DEFAULT '[]',        -- v0.7: JSON 质量标签
```

### 3.2 `MediaFile` 模型（`Data/MediaFile.cs`）

新增字段：

```csharp
/// <summary>AI 质量评价标签列表（如 "欠曝" "噪点"）。DB 列 quality_tags，JSON 序列化。</summary>
[ObservableProperty]
private List<string> _qualityTags = new();

/// <summary>派生：是否有质量标签（UI 质量徽章条 IsVisible 绑定用）。</summary>
public bool HasQualityTags => QualityTags is { Count: > 0 };
```

### 3.3 `TagResult` record 变更（`Services/AITagger.cs`）

```csharp
public sealed record TagResult(
    IReadOnlyList<string> Tags,          // 主体标签（SubjectTagVocabulary 内）
    IReadOnlyList<string> QualityTags,   // 质量标签（QualityTagVocabulary 内），可为空数组
    int Score,
    int LatencyMs,
    string Model);
```

### 3.4 Repository 接口变更（`Data/IMediaRepository.cs`）

```csharp
Task UpdateTagAsync(long fileId,
                    IEnumerable<string> tags,
                    IEnumerable<string> qualityTags,   // v0.7 新增参数
                    int score,
                    long taggedAt,
                    string? tagError,
                    CancellationToken ct = default);
```

---

## 4. AI Prompt 变更

### 4.1 新版 prompt（`BuildImagePrompt`）

两个独立白名单 + 两个独立输出数组 + 评分不变：

```
你是一位天文摄影专家，正在分析一张天文照片。

【主体标签白名单】（只允许使用下列词汇描述照片内容）：
{SubjectTagVocabulary 拼接}、非天文、未分类

【质量评价白名单】（只允许使用下列词汇描述画质问题，无问题则返回空数组）：
{QualityTagVocabulary 拼接}

【输出规则】
- 选 3-7 个最相关的主体标签（按相关性排序，最相关的在前）
- 选 0-5 个存在的质量问题（无问题时返回空数组）
- 评分 0-100 整数：
  · 90-100 作品级（可投稿 / 印刷出版）
  · 75-89  优秀（轻微瑕疵）
  · 60-74  良好（有可见问题但仍可用）
  · 40-59  一般（明显缺陷）
  · 0-39   较差（建议丢弃）
- 非天文内容（普通风景、测试图、色卡）→ tags:["非天文"]，quality_tags:[]，score:0

【示例】：
图：猎户座大星云 HOO 合成作品，暗部有轻微噪点
输出：{"tags":["星云","猎户座大星云","广角"],"quality_tags":["噪点"],"score":82}

图：对焦精准、曝光完美的满月作品
输出：{"tags":["月亮","满月","月面特写"],"quality_tags":[],"score":95}

---

现在分析这张图，按规则输出。
只输出 JSON，禁止 markdown / 解释文字：
{"tags":["标签1","标签2"],"quality_tags":["质量1"],"score":分数}
```

### 4.2 `ParseAndValidate` 变更

```csharp
private static (TagResult? Result, string? Error) ParseAndValidate(string raw)
{
    // 1. 剥 markdown 包裹（不变）
    // 2. JSON parse（不变）
    // 3. 提取 tags → SubjectTagVocabulary 过滤 → Distinct → Take(7)
    // 4. 提取 quality_tags → QualityTagVocabulary 过滤 → Distinct → Take(5)
    // 5. 提取 score + clamp 0-100
    // 6. tags 全不在白名单 → fallback ["未分类"]
    // 7. quality_tags 全不在白名单 → fallback []（空数组，不是错误）
}
```

**异常处理：**

| 场景 | `tags` | `quality_tags` | score |
|---|---|---|---|
| JSON 解析失败 | 重试 | 重试 | 重试 |
| `quality_tags` 字段缺失 | 正常 | `[]`（兜底） | 正常 |
| `quality_tags` 不在白名单 | 正常 | `[]`（丢弃 + warn） | 正常 |
| `tags` 全不在白名单 | `["未分类"]` | 忽略 | 0 |

---

## 5. Repository 实现变更

### 5.1 迁移（`MediaRepository.EnsureDatabase`）

```csharp
// v0.7 质量标签列
SqliteMigrations.AddColumnIfMissing(
    connection, "media_files", "quality_tags",
    "TEXT NOT NULL DEFAULT '[]'");
```

### 5.2 `UpdateTagAsync` 实现

```csharp
public async Task UpdateTagAsync(long fileId, IEnumerable<string> tags,
    IEnumerable<string> qualityTags, int score, long taggedAt,
    string? tagError, CancellationToken ct = default)
{
    var tagsList = tags.ToList();
    var tagsJson = JsonSerializer.Serialize(tagsList, s_writeTagsOptions);
    var qualityTagsList = qualityTags.ToList();
    var qualityTagsJson = JsonSerializer.Serialize(qualityTagsList, s_writeTagsOptions);

    // UPDATE ... SET tags=@tags, quality_tags=@qualityTags, score=@score, ...
}
```

### 5.3 `ReadMediaFileRow` 变更

第 21 列 → `QualityTags = ParseTags(reader.IsDBNull(20) ? null : reader.GetString(20))`

### 5.4 `GetTagGroupsAsync` — 排除质量标签

聚合时用 `SubjectTagVocabulary` 过滤：

```csharp
// 只统计主体标签，忽略质量标签
var subjectSet = new HashSet<string>(AITagger.SubjectTagVocabulary, StringComparer.Ordinal);
foreach (var tag in tags)
{
    if (string.IsNullOrWhiteSpace(tag) || !subjectSet.Contains(tag))
        continue;
    tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
}
```

注：历史数据 `tags` 列里可能有质量词残留（v0.6 打标的旧数据），聚合时通过白名单过滤即可，不做 DB 迁移回填。

### 5.5 `GetByTagAsync` — 不受影响

仍用现有 `LIKE '%"标签"%'` 匹配 `tags` 列。质量标签筛选由后续 Search/Filter phase 单独实现。

---

## 6. ViewModel 变更（`GalleryViewModel.cs`）

### 6.1 `BatchTagCoreAsync` — 成功路径

```csharp
if (result != null)
{
    file.Tags = result.Tags.ToList();
    file.QualityTags = result.QualityTags.ToList();   // v0.7 新增
    file.Score = result.Score;
    file.TagError = null;
    await _mediaRepo.UpdateTagAsync(
        file.Id,
        result.Tags,
        result.QualityTags,   // v0.7 新增
        result.Score,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        null, ct);
    ok++;
}
```

### 6.2 不涉及的 VM 逻辑

- `LoadTagGroupsAsync` / `SelectTagAsync` — 不变（`GetTagGroupsAsync` / `GetByTagAsync` 层已过滤质量标签）
- `OnSortModeChanged` — 不变
- `TagSingle` — 不变（复用 `BatchTagCoreAsync`）

---

## 7. UI 变更

### 7.1 photo tile 质量标签条（`GalleryView.axaml`）

在现有标签条下方新增一条：

```xml
<!-- v0.7: 质量评价标签条 (底部居中, subject 标签条下方) -->
<Border IsVisible="{Binding HasQualityTags}"
        HorizontalAlignment="Center"
        VerticalAlignment="Bottom"
        Margin="0,0,0,24"    <!-- 叠加在 subject tag 条上方（tag 条 bottom margin=6） -->
        Padding="6,2"
        MaxWidth="140"
        Background="#CC3A1F1A"   <!-- 暖红底 BG，区分主体的冷蓝底 #CC0A0E1A -->
        CornerRadius="8"
        ToolTip.Tip="{Binding QualityTags, Converter={StaticResource QualityTagsToTooltip}}">
    <TextBlock Text="{Binding QualityTags, Converter={StaticResource QualityTagsToShortText}}"
               FontSize="10"
               Foreground="#FFCC8888"     <!-- 暖橙文字 -->
               TextTrimming="CharacterEllipsis"
               MaxLines="1"/>
</Border>
```

**视觉区分：**

| 元素 | Subject 标签条 | Quality 标签条 |
|---|---|---|
| 背景色 | `#CC0A0E1A`（冷蓝黑） | `#CC3A1F1A`（暖红黑） |
| 文字色 | `#E6FFFFFF`（白） | `#FFCC8888`（暖橙） |
| 位置 | bottom margin 6 | bottom margin 24（上方） |
| 边框 | 无 | 无 |

### 7.2 Converter 新增（`Converters/AITagConverters.cs`）

```csharp
/// <summary>List&lt;string&gt; QualityTags → string（质量标签条显示）</summary>
public class QualityTagsToShortTextConverter : IValueConverter
{
    // 逻辑复用 TagsToShortTextConverter（截断 + Full tooltip）
}

/// <summary>List&lt;string&gt; QualityTags → string（tooltip 完整列表）</summary>
public class QualityTagsToTooltipConverter : IValueConverter
{
    // ConverterParameter="Tooltip" 触发完整列表 join
}
```

> **实施建议**：可复用 `TagsToShortTextConverter`，加 `ConverterParameter` 区分 subject / quality（仅 tooltip 行为不同）。但为清晰性，也可单独新建两个 converter。

### 7.3 Converter 注册（`GalleryView.axaml`）

```xml
<converters:QualityTagsToShortTextConverter x:Key="QualityTagsToShortText"/>
<converters:QualityTagsToTooltipConverter x:Key="QualityTagsToTooltip"/>
```

### 7.4 左栏「标签」tab — 不出现质量标签

由 `GetTagGroupsAsync` 过滤（§5.4），不需要 XAML 改动。

---

## 8. 文件改动清单

| 文件 | 改动 | 估行 |
|---|---|---|
| `Services/AITagger.cs` | 拆分 `SubjectTagVocabulary` + `QualityTagVocabulary`（保留旧常量 Obsolete）；`TagResult` 加 `QualityTags`；`BuildImagePrompt` 重写；`ParseAndValidate` 加 `quality_tags` 提取 | ~120 |
| `Data/MediaFile.cs` | 加 `QualityTags` + `HasQualityTags` | ~10 |
| `Data/IMediaRepository.cs` | `UpdateTagAsync` 签名加 `qualityTags` 参数 | ~5 |
| `Data/MediaRepository.cs` | 迁移加 `quality_tags` 列；`UpdateTagAsync` 加序列化+写入；`ReadMediaFileRow` 读第21列；`GetTagGroupsAsync` 过滤质量标签 | ~40 |
| `ViewModels/GalleryViewModel.cs` | `BatchTagCoreAsync` 传 `QualityTags` | ~5 |
| `Converters/AITagConverters.cs` | 加 `QualityTagsToShortTextConverter` + `QualityTagsToTooltipConverter` | ~50 |
| `Views/GalleryView.axaml` | photo tile 加 quality 标签条 + converter 注册 | ~25 |
| `doc/11-ai-tagging.md` | 附录 A 更新词表 + 数据模型 + prompt | ~30 |

总计 ~285 行（~230 行新增，~55 行修改）。

---

## 9. 迁移与兼容

### 9.1 DB 迁移

- `quality_tags` 列幂等 `AddColumnIfMissing`，老行默认 `'[]'`
- 不回填历史数据（v0.6 的 `tags` 列中已混入的质量词保留在原位）

### 9.2 旧数据行为

| 场景 | 行为 |
|---|---|
| v0.6 打标的旧文件 | `tags` 列可能含质量词 → 左栏聚合时 `GetTagGroupsAsync` 白名单过滤掉；card 上 subject 标签条仍会显示历史混入的质量词 |
| v0.7 重新打标 | 旧 `tags` 被覆盖为纯主体词；`quality_tags` 写入新列 |
| v0.6 标过但 v0.7 不打标 | `tags` 列可能残留质量词（已知可接受，用户可右键重新打标覆盖） |

### 9.3 AI 向后兼容

如果 AI 返回的 JSON 不含 `quality_tags` 字段 → `ParseAndValidate` 兜底 `[]`，不报错。

---

## 10. 验证清单

- ☐ 编译通过（`dotnet build` 无错）
- ☐ DB 迁移幂等（新/老库均加列成功）
- ☐ 新打标 → `tags` 不含质量词，`quality_tags` 有值或空数组
- ☐ photo tile 显示两条标签条：subject（蓝底白字）+ quality（红底橙字，仅有时显示）
- ☐ 左栏「标签」tab 不出现「欠曝」「噪点」等质量词
- ☐ 旧数据文件在 card 上仍能正常显示（含历史混入质量词，可接受）
- ☐ AI 返回不含 `quality_tags` 字段时 → 成功打标，quality 为空
- ☐ Prompt 打印日志含两个独立白名单
- ☐ `GetTagGroupsAsync` / `GetByTagAsync` 行为不变（旧功能不被破坏）

---

## 附录 A：词表对比（v0.6 → v0.7）

| 分类 | v0.6（ChineseTagVocabulary） | v0.7 归入 |
|---|---|---|
| 深空天体 (5) | 星云、星系、星团、超新星遗迹、暗星云 | `SubjectTagVocabulary` |
| 太阳系 (8) | 行星、月亮、太阳、彗星、小行星、流星、卫星、国际空间站 | `SubjectTagVocabulary` |
| 命名天体 (15) | 猎户座大星云、M42、仙女座星系、M31、昴星团、M45、银河、银心、土星、木星、火星、金星、娥眉月、满月、月面特写 | `SubjectTagVocabulary` |
| 现象 (6) | 极光、日食、月食、凌日、月掩星、合相 | `SubjectTagVocabulary` |
| 构图/手法 (10) | 广角、窄带、行星摄影、深空摄影、星座、长焦、赤道仪跟踪、行星叠加、月面拼接、全景 | `SubjectTagVocabulary` |
| 质量问题 (10) | 拖线、失焦、噪点、过曝、欠曝、色差、大气抖动、视宁度差、镜头眩光、杂光 | **`QualityTagVocabulary`** ← 拆分 |
| 特殊 (2) | 非天文、未分类 | `SubjectTagVocabulary` |

---

> **本文档状态**：plan（v1.0）。待审阅后实施。
