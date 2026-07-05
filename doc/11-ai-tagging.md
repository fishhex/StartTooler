# 11 — AI 打标与标签 / 评分

> 对应代码（待实施）：`Services/AITagger.cs`、`Services/AIVideoFrameExtractor.cs`、`Data/MediaRepository.cs`、`Data/MediaFile.cs`、`ViewModels/GalleryViewModel.cs`、`Views/MainWindow.axaml`、`Views/GalleryView.axaml`、`Converters/ScoreToColorConverter.cs`。
>
> 关联文档：`02-data-layer.md`（数据层）、`05-gallery-view.md`（Gallery 视图）、`06-settings.md`（AI 配置）、`10-trap-book.md`（陷阱沉淀）。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.6**（从 v0.5 加 AI 打标 + 标签分类 + 评分排序） |
| 目标用户 | 天文摄影爱好者（astrophotographer） |
| 实施版本 | StartTooler v0.6.0 |
| 关联模块 | Gallery (05)、Data (02)、AI Settings (06)、UI Commons (09) |
| 文档状态 | **spec（待实施）** — 用户已拍板方案，待开发完成后回填实际文件路径与行号 |

### v0.6 相对 v0.5 的变更摘要

| 新增 | 内容 |
|---|---|
| **数据层** | `media_files` 加 4 列：`tags` (TEXT JSON) / `score` (INTEGER 0-100) / `tagged_at` (INTEGER unix ms) / `tag_error` (TEXT) |
| **服务层** | `AITagger`（图片 + 视频 vision 调用，OpenAI/Anthropic 双协议）、`AIVideoFrameExtractor`（ffmpeg 抽帧） |
| **AI 配置** | `AIConfig` 加 `Protocol` 字段（运行时唯一权威）；TOML 仅渲染设置页；v0.6 移除 Gemini 原生协议 |
| **VM 层** | `GroupMode` (Date/Tag)、`SortMode` (TimeDesc/ScoreDesc)、`TagGroups`、`IsTagging` / `TagCompletedCount` / `TagTotalCount` / `TagError` |
| **UI** | 左栏顶部 TabControl [时间 \| 标签]、工具栏「批量打标 / 取消打标 / 排序」ComboBox、photo tile 评分角标、底部标签小角标条、右键 "AI 打标" |

---

## 1. 需求拆解

### 1.1 用户故事

| ID | 故事 | 验收点 |
|---|---|---|
| **US-1** | 作为用户，我希望能多选一批照片后一键 AI 打标，节省每张手动标注时间 | 选中 10 张图 → 点「批量打标」→ 进度条跑完 → 卡片显示评分和标签 |
| **US-2** | 作为用户，我希望 AI 也能给我的视频打标（行星 / 月面 / 导星） | 选中 5 段视频 → 点「批量打标」→ ffmpeg 抽 8 帧 → 综合打分 |
| **US-3** | 作为用户，我希望按标签筛选照片（"找出所有星云照片"） | 工具栏切到「标签」分类 → 左栏显示所有 tag + 计数 → 点 "星云" → 右栏列出所有打过 "星云" 标签的文件 |
| **US-4** | 作为用户，我希望按评分排序，找出最好的片子 | 工具栏排序切到「评分↓」→ 文件按 score 降序，null 排最后 |
| **US-5** | 作为用户，我希望能看到每个文件的 AI 评分和标签，一目了然 | photo tile 右下角显示评分（如 "8.7"），底部小角标条显示 2-3 个标签 |
| **US-6** | 作为用户，我希望对单文件能重新打标（覆盖之前的） | 右键 → "AI 打标" → 覆盖 tags/score/tagged_at |
| **US-7** | 作为用户，我希望打标失败的文件有标记，方便后续补打 | 失败的卡片显示红色叹号徽章 + hover 看错误原因 |

### 1.2 验收点总览

- ✅ 多选 N 个文件 → 「批量打标」按钮启用（N ≥ 1）
- ✅ 「批量打标」走串行 vision 调用 + 200ms 间隔 + 1 次 JSON retry
- ✅ 视频走 ffmpeg thumbnail 抽 8 帧 → 一次 vision 调用综合打分
- ✅ 标签中文 56 个白名单，AI 只能返回白名单内（不在的丢 + warn log）
- ✅ DB 加 `tags` (TEXT JSON) / `score` (INTEGER 0-100) / `tagged_at` / `tag_error`，迁移幂等
- ✅ 左栏顶部 [时间 \| 标签] tab 切换；切到「标签」显示 `TagGroups`
- ✅ 工具栏排序 ComboBox：`时间↓` / `评分↓`；评分 null 排最后
- ✅ photo tile 显示评分 + 标签；评分颜色梯度 ≥80 绿 / 60–79 黄 / <60 灰（原始 0–100 值）；Display 显示 `score/10` 一位小数（如 82 → "8.2"）
- ✅ 失败文件保留 `tag_error`，UI 红色徽章 + hover tooltip
- ✅ AI 未配置或 Key 缺失：toast 提示，不影响其他功能

---

## 2. 数据模型

### 2.1 `media_files` 表 schema 变更

`Data/MediaRepository.cs:36-58` 当前 schema 扩展：

```sql
CREATE TABLE IF NOT EXISTS media_files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_path TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    file_name TEXT NOT NULL,
    media_type INTEGER NOT NULL DEFAULT 0,
    file_size INTEGER NOT NULL DEFAULT 0,
    last_modified INTEGER NOT NULL DEFAULT 0,
    shot_at INTEGER,
    is_uploaded INTEGER NOT NULL DEFAULT 0,
    local_exists INTEGER NOT NULL DEFAULT 1,
    thumbnail_path TEXT,
    remote_url TEXT,
    uploaded_at INTEGER,
    scanned_at INTEGER NOT NULL DEFAULT 0,
    -- v0.6 新增 ↓
    tags TEXT NOT NULL DEFAULT '[]',       -- JSON List<string>，中文标签白名单
    score INTEGER,                          -- 0-100，nullable（未打标）
    tagged_at INTEGER,                      -- unix ms，nullable
    tag_error TEXT,                         -- 打标失败时的错误摘要，nullable
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UNIQUE(project_path, relative_path)
);
CREATE INDEX IF NOT EXISTS idx_media_files_date ON media_files(shot_at);
CREATE INDEX IF NOT EXISTS idx_media_files_project ON media_files(project_path);
-- v0.6 新增 ↓
CREATE INDEX IF NOT EXISTS idx_media_files_score ON media_files(score);  -- 评分排序用
CREATE INDEX IF NOT EXISTS idx_media_files_tagged_at ON media_files(tagged_at);  -- "最近打标" 类查询
```

**注意**：`tags` 字段 JSON 字符串内不能用 `LIKE` 高效索引 — 单文件标签数 < 7、跨文件 LIKE 拼接即可，v0.6 不上 FTS5。

### 2.2 迁移（幂等）

`MediaRepository.EnsureDatabase` 加 4 行（沿用 `SqliteMigrations.AddColumnIfMissing`）：

```csharp
SqliteMigrations.AddColumnIfMissing(connection, "media_files", "tags",
    "TEXT NOT NULL DEFAULT '[]'");
SqliteMigrations.AddColumnIfMissing(connection, "media_files", "score",
    "INTEGER");
SqliteMigrations.AddColumnIfMissing(connection, "media_files", "tagged_at",
    "INTEGER");
SqliteMigrations.AddColumnIfMissing(connection, "media_files", "tag_error",
    "TEXT");
// 索引加 CREATE INDEX IF NOT EXISTS，幂等
```

详见 `02-data-layer.md` §11.6 迁移策略。

### 2.3 `MediaFile` 模型字段（`Data/MediaFile.cs`）

新增 4 个持久化字段 + 2 个 UI 派生字段：

```csharp
/// <summary>AI 打标标签列表（中文白名单）。DB 列 tags，JSON 序列化。</summary>
[ObservableProperty]
private List<string> _tags = new();

/// <summary>AI 评分 0-100。DB 列 score，nullable（未打标）。</summary>
[ObservableProperty]
private int? _score;

/// <summary>最近一次成功打标时间。DB 列 tagged_at，unix ms。</summary>
public long? TaggedAt { get; set; }

/// <summary>打标失败的错误摘要。DB 列 tag_error。成功打标时清空。</summary>
[ObservableProperty]
private string? _tagError;

/// <summary>派生：评分是否有效（用于排序时空值处理）。</summary>
public bool HasScore => Score.HasValue;

/// <summary>派生：是否有打标结果（含仅标签无评分的情况，用于 UI 可见性绑定）。</summary>
public bool HasTags => Tags is { Count: > 0 };
```

**字段分类：**

| 字段 | 类型 | 来源 | 持久化 | 何时更新 |
|---|---|---|---|---|
| `Tags` | `List<string>`（[ObservableProperty]） | DB 读出 | ✅ | 打标成功 → `UpdateTagAsync` |
| `Score` | `int?`（[ObservableProperty]） | DB 读出 | ✅ | 同上 |
| `TaggedAt` | `long?` | DB 读出 | ✅ | 同上 |
| `TagError` | `string?`（[ObservableProperty]） | DB 读出 | ✅ | 打标失败写 / 成功清 |
| `HasScore` | `bool` | 派生 | ❌ | Score 变化联动 |
| `HasTags` | `bool` | 派生 | ❌ | Tags 变化联动 |

### 2.4 Repository 接口扩展（`Data/IMediaRepository.cs`）

```csharp
public interface IMediaRepository
{
    // ... 既有方法 ...

    /// <summary>更新文件的 AI 打标结果。成功传 score，失败传 tagError。</summary>
    Task UpdateTagAsync(long fileId,
                        IReadOnlyList<string> tags,
                        int? score,
                        long? taggedAt,
                        string? tagError,
                        CancellationToken ct = default);

    /// <summary>获取所有出现过的标签及其在该项目下的文件数。</summary>
    /// <returns>按 count desc 排序，含空标签组（tags='[]'）的"未分类"桶。</returns>
    Task<IReadOnlyList<TagGroupCount>> GetTagGroupsAsync(string projectPath, CancellationToken ct = default);

    /// <summary>按标签筛选 + 排序拉文件。tag 为空串表示"未分类"组。</summary>
    Task<IReadOnlyList<MediaFile>> GetByTagAsync(string projectPath,
                                                  string tag,
                                                  SortMode sortMode,
                                                  CancellationToken ct = default);
}

public sealed class TagGroupCount
{
    public string Tag { get; set; } = "";       // "星云" / "" 表示未分类
    public int Count { get; set; }
}
```

### 2.5 标签 JSON 序列化约定

`Tags` 序列化为紧凑 JSON（`System.Text.Json` 默认 options）：

```json
["星云", "猎户座大星云", "广角"]
```

**约束：**
- 元素已小写化 / 中文形态直接保留（Chinese 无大小写）
- 长度：3-7 个（白名单校验在 AITagger 里，DB 写入前过滤）
- 不去重（AI 偶尔返回重复，DB 写入时 `Distinct` 兜底）
- 空值序列化为 `[]`，反序列化失败 → 空 List

**读取**：`MediaRepository.GetByDateAsync` / `GetByTagAsync` 反序列化用 `JsonSerializer.Deserialize<List<string>>(tagsJson) ?? new()`。

---

## 3. AI 打标服务

### 3.1 服务接口（`Services/AITagger.cs`）

```csharp
public sealed record TagResult(
    IReadOnlyList<string> Tags,        // 3-7 个白名单内的中文标签
    int Score,                          // 0-100
    int LatencyMs,
    string Model);

public sealed record TagFailure(
    string Reason,                      // 错误摘要，写入 tag_error
    bool IsFatal);                      // true = 整批终止（Key 失效），false = 单图跳过

public interface IAITagger
{
    /// <summary>对单文件路由：图片走图片 prompt，视频走抽帧 + 视频 prompt。</summary>
    Task<(TagResult? Result, TagFailure? Failure)> TagFileAsync(
        MediaFile file,
        AIConfig config,
        AIProviderMeta meta,
        CancellationToken ct);

    /// <summary>视频抽帧 + 单次 vision 调用。</summary>
    Task<(TagResult? Result, TagFailure? Failure)> TagVideoAsync(
        string localPath,
        AIConfig config,
        AIProviderMeta meta,
        CancellationToken ct);
}
```

### 3.2 图片打标流程

```
TagFileAsync (MediaType == Image)
  ├─ 1. 读本地文件（绝对路径）
  ├─ 2. 用 SkiaSharp 长边 resize 到 512px（保持宽高比）
  │     └─ JPEG q=75 编码（单图 30-80KB）
  ├─ 3. base64 编码
  ├─ 4. 构造 messages：
  │     └─ content: [text prompt, image attachment]
  ├─ 5. 发 HTTP 请求（按 AIConfig.Protocol 字符串路由，见 §3.4）
  ├─ 6. 解析响应 JSON（见 §3.7）
  ├─ 7. 返回 TagResult
  └─ 失败 → 返回 TagFailure（IsFatal 由 HTTP 状态码决定）
```

**resize 实现（用项目现 SkiaSharp）：**
```csharp
using var input = File.OpenRead(localPath);
using var bitmap = SKBitmap.Decode(input);
var (newW, newH) = ComputeFitSize(bitmap.Width, bitmap.Height, 512);
using var resized = bitmap.Resize(new SKImageInfo(newW, newH), SKFilterQuality.Medium);
using var image = SKImage.FromBitmap(resized);
using var data = image.Encode(SKEncodedImageFormat.Jpeg, 75);
return data.ToArray();  // → base64
```

### 3.3 视频打标流程（AIVideoFrameExtractor + AITagger）

```
TagVideoAsync (MediaType == Video)
  ├─ 1. AIVideoFrameExtractor.ExtractFramesAsync(localPath, frameCount=8)
  │     └─ 输出临时 jpg 文件列表（路径见 §3.8）
  ├─ 2. 每帧 resize 到 512px（JPEG q=75）
  ├─ 3. 全部 base64 编码
  ├─ 4. 构造 messages：
  │     └─ content: [text prompt, image × 8]   ← 多 image 一次调用
  ├─ 5. 发 HTTP 请求
  ├─ 6. 解析响应 JSON（视频 prompt，结构相同）
  ├─ 7. 清理临时帧目录
  └─ 失败 → 清理 + 返回 TagFailure
```

**AIVideoFrameExtractor 接口（`Services/AIVideoFrameExtractor.cs`）：**

```csharp
public interface IAIVideoFrameExtractor
{
    /// <summary>
    /// ffmpeg thumbnail filter 抽 N 帧到临时目录。
    /// 返回 jpg 文件绝对路径列表（顺序与抽出顺序一致）。
    /// </summary>
    Task<IReadOnlyList<string>> ExtractFramesAsync(
        string localVideoPath,
        int frameCount,
        CancellationToken ct);
}
```

**ffmpeg 命令（沿用项目现有 thumbnail filter 风格，详见 `10-trap-book.md` thumbnail 条目）：**

```bash
ffmpeg -y -i input.avi \
  -vf "thumbnail=10000,scale=512:-1" \
  -frames:v 8 \
  -vsync vfr \
  thumb_%03d.jpg
```

- `-frames:v 8`：限制输出 8 帧（thumbnail filter 默认会去重）
- `-vsync vfr`：可变帧率，避免重复帧
- `thumbnail=10000`：选色彩变化最大的，N=10000 是经验值（跟项目现 thumbnail 一致）
- **实际帧数 < 8 的处理**：短视频或画面变化少时，thumbnail 去重后可能只产出 2-7 帧。此时直接发送实际帧数（≥1），Prompt 开头动态改为「正在分析一段天文视频的 N 帧采样」，不用硬编码"8 帧"

**调用方式：** 通过 `FfmpegSnapshotRunner`（已存在，`Services/FfmpegSnapshotRunner.cs`）的扩展，或新建 `FfmpegFrameExtractorRunner`：

```csharp
// 复用 FfmpegSnapshotRunner 风格：Process.Start("ffmpeg", args) + 异步等退出 + 解析 stderr
// 不引第三方 ffmpeg wrapper，跟现有代码风格一致
```

### 3.4 协议层

> **v0.6 实施边界声明**（用户拍板）：
> - **`AIConfig` 是运行时唯一权威**（Provider / ApiKey / BaseUrl / Model / **Protocol** 五个字段）
> - **TOML（`ai-providers.default.toml`）仅用于渲染设置页厂商下拉框**（DisplayName / DefaultBaseUrl / RecommendedModels / DefaultModel），不参与运行时
> - **`AITagger` 不依赖 `AIProviderMeta` 透传**；只读 `AIConfig.Protocol` 字符串路由（"OpenAI" / "Anthropic"）
> - **v0.6 不实现 Gemini 原生协议**（用户决策）；Gemini 厂商走 OpenAI 兼容分支（TOML `Protocol = "OpenAI"` + 用户在 UI 选 OpenAI 协议）

按 `AIConfig.Protocol` 字符串路由。AITagger 编译期 switch `"OpenAI"` / `"Anthropic"`，未识别值抛 `NotSupportedException`。

#### 3.4.1 OpenAI 兼容

适配：`OpenAI` / `DeepSeek` / `Zhipu` / `Moonshot` / `DashScope` / `Gemini (protocol=openai)` / `Custom (protocol=openai)`

```http
POST {baseUrl}/chat/completions
Authorization: Bearer {apiKey}
Content-Type: application/json

{
  "model": "{model}",
  "max_tokens": 300,
  "messages": [{
    "role": "user",
    "content": [
      { "type": "text", "text": "<PROMPT>" },
      { "type": "image_url",
        "image_url": { "url": "data:image/jpeg;base64,<B64>" } },
      ... 更多图片（视频场景）...
    ]
  }]
}
```

**响应解析：**
```json
{
  "choices": [{
    "message": {
      "content": "{\"tags\": [\"星云\"], \"score\": 82}"
    }
  }]
}
```

#### 3.4.2 Anthropic 原生

```http
POST {baseUrl}/v1/messages
x-api-key: {apiKey}
anthropic-version: 2023-06-01
Content-Type: application/json

{
  "model": "{model}",
  "max_tokens": 300,
  "messages": [{
    "role": "user",
    "content": [
      { "type": "text", "text": "<PROMPT>" },
      { "type": "image",
        "source": { "type": "base64", "media_type": "image/jpeg", "data": "<B64>" } },
      ... 更多图片（视频场景）...
    ]
  }]
}
```

**响应解析：**
```json
{
  "content": [{
    "type": "text",
    "text": "{\"tags\": [\"星云\"], \"score\": 82}"
  }]
}
```

### 3.5 中文 Prompt（图片版）

```
你是一位天文摄影专家，正在分析一张天文照片。

【标签白名单】（只允许使用下列词汇，建议中文）：
深空天体：星云、星系、星团、超新星遗迹、暗星云
太阳系：行星、月亮、太阳、彗星、小行星、流星、卫星、国际空间站
命名天体：猎户座大星云、M42、仙女座星系、M31、昴星团、M45、银河、
         银心、土星、木星、火星、金星、娥眉月、满月、月面特写
现象：极光、日食、月食、凌日、月掩星、合相
构图：广角、窄带、行星摄影、深空摄影、星座、长焦、赤道仪跟踪、
     行星叠加、月面拼接、全景
质量问题：拖线、失焦、噪点、过曝、欠曝、色差、大气抖动、
         视宁度差、镜头眩光、杂光
特殊：非天文、未分类

【输出规则】
- 选 3-7 个最相关的标签（按相关性排序，最相关的在前）
- 评分 0-100 整数：
  · 90-100 作品级（可投稿 / 印刷出版）
  · 75-89  优秀（轻微瑕疵）
  · 60-74  良好（有可见问题但仍可用）
  · 40-59  一般（明显缺陷）
  · 0-39   较差（建议丢弃）
- 非天文内容（普通风景、测试图、色卡）→ tags:["非天文"]，score:0

【示例】（仅供格式参考，不要照抄内容）：
图：猎户座大星云 HOO 合成作品
输出：{"tags": ["星云", "猎户座大星云", "广角"], "score": 82}

---

现在分析这张图，按规则输出。
只输出 JSON，禁止 markdown / 解释文字：
{"tags": ["标签1", "标签2"], "score": 分数}
```

**注意**：prompt 里的白名单列表是从代码常量 `ChineseTagVocabulary`（附录 A）拼接的，避免硬编码漂移。

### 3.6 中文 Prompt（视频版）

```
你是一位天文摄影专家，正在分析一段天文视频的 8 帧采样。
帧是从视频中按代表性原则抽取的，覆盖整段时间。

请把 8 帧作为一个【序列】综合分析，不要单帧判断：
- 关注帧间变化（行星自转、月面 traverse、卫星过境）
- 关注最佳帧的稳定性（忽略最差帧）

【标签白名单】同图片 prompt（56 个词，从代码常量拼）。

【输出规则】
- 选 3-7 个最相关的标签
- 评分 0-100 整数：
  · 基于技术质量（对焦、跟踪、曝光一致性）× 帧间稳定性 × 主题价值
  · 视频比单图更看重【最佳帧】的清晰度
- 非天文内容 → tags:["非天文"]，score:0

只输出 JSON，禁止 markdown：
{"tags": ["标签1", "标签2"], "score": 分数}
```

### 3.7 JSON 解析 + 重试

```csharp
private static (TagResult? Result, TagFailure? Failure) ParseAndValidate(
    string rawResponse, string model, int latencyMs, AIProviderMeta meta)
{
    // 1. 剥 markdown 包裹（AI 偶尔返回 ```json ... ```）
    rawResponse = rawResponse.Trim();
    if (rawResponse.StartsWith("```"))
    {
        rawResponse = Regex.Replace(rawResponse, @"^```(?:json)?\s*|\s*```$", "", RegexOptions.Multiline);
    }

    // 2. JSON parse
    JsonElement root;
    try
    {
        using var doc = JsonDocument.Parse(rawResponse);
        root = doc.RootElement.Clone();  // Clone 避免 doc dispose 后访问
    }
    catch (JsonException ex)
    {
        return (null, new TagFailure($"JSON 解析失败：{ex.Message}", IsFatal: false));
    }

    // 3. 提取 tags
    var rawTags = new List<string>();
    if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
    {
        foreach (var tagEl in tagsEl.EnumerateArray())
        {
            var tag = tagEl.GetString()?.Trim();
            if (!string.IsNullOrEmpty(tag)) rawTags.Add(tag);
        }
    }

    // 4. 白名单过滤（不在白名单的丢 + warn log）
    var validTags = rawTags
        .Where(t => AllowedTagSet.Contains(t))
        .Distinct()
        .Take(7)
        .ToList();
    if (validTags.Count == 0)
    {
        validTags.Add("未分类");  // 兜底
        Trace.WriteLine($"[AITagger] all tags out of whitelist, fallback to '未分类'. raw={string.Join(",", rawTags)}");
    }
    else if (validTags.Count < rawTags.Count)
    {
        Trace.WriteLine($"[AITagger] filtered {rawTags.Count - validTags.Count} out-of-whitelist tags. dropped=[{string.Join(",", rawTags.Except(validTags))}]");
    }

    // 5. 提取 score + clamp
    int score = 0;
    if (root.TryGetProperty("score", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number)
    {
        score = scoreEl.TryGetInt32(out var s) ? Math.Clamp(s, 0, 100) : 0;
    }

    return (new TagResult(validTags, score, latencyMs, model), null);
}
```

**重试策略：**

| 失败类型 | 行为 |
|---|---|
| JSON 解析失败 | retry 1 次（prompt 不变；偶发 markdown 包裹） |
| HTTP 429 (rate limit) | backoff 3s / 6s / 12s，最多 3 次 |
| HTTP 401 (Key 失效) | 立刻终止，IsFatal=true，整批不再继续 |
| HTTP 403 (权限) | 立刻终止，IsFatal=true |
| HTTP 5xx | backoff 1s / 3s，最多 2 次 |
| HTTP 4xx (其他) | 不重试，TagFailure |
| 超时 30s | retry 1 次 |
| `score < 0 \|\| > 100` | clamp，不抛 |
| `tags` 全不在白名单 | tag `["未分类"]` + warn log |

**Fatal 信号传递：** `BatchTag` 命令收到 `IsFatal=true` 的失败，立即终止循环 + toast "AI 配置错误，请检查设置"。

### 3.8 临时文件管理

**路径：** `~/Library/Application Support/StartTooler/ai-frames/<sha256(video-path)[:16]>_<unix-ms>/`

**命名：** `frame_001.jpg` ... `frame_008.jpg`

**生命周期：**
1. `AIVideoFrameExtractor.ExtractFramesAsync` 调用开始 → 清空该 hash 下旧目录
2. ffmpeg 输出到该目录
3. 返回文件列表给 `AITagger`
4. `AITagger` 读完所有帧 base64 后 → `finally { Directory.Delete(dir, recursive: true); }`

**孤儿清理（防进程崩溃）：**
`App.OnStartup` 里加一行：
```csharp
Task.Run(async () =>
{
    var baseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StartTooler", "ai-frames");
    if (!Directory.Exists(baseDir)) return;
    foreach (var dir in Directory.GetDirectories(baseDir))
    {
        var ageDays = (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir)).TotalDays;
        if (ageDays > 7) Directory.Delete(dir, recursive: true);
    }
});
```

### 3.9 错误处理表

| 场景 | 处理 |
|---|---|
| AI 未配置（ConfigKeys.AI 不存在） | toast "请先在设置页配置 AI" + 不进入 BatchTag |
| ApiKey 为空 | 同上 |
| BaseUrl 为空 / 不可达 | 单图失败 + tag_error |
| HTTP 401 | 整批终止 + toast "API Key 无效，请检查设置" |
| HTTP 429 重试 3 次仍失败 | 单图失败 + tag_error "rate limit" |
| ffmpeg 进程退出码 != 0 | 单视频失败 + tag_error "ffmpeg 抽帧失败：{stderr 摘要}" |
| ffmpeg 抽帧 < 1 张 | 单视频失败 + tag_error "抽帧为空" |
| 网络中断 | 单图失败 + tag_error "网络错误：{msg}" |
| 文件不存在（LocalExists=false） | 单图跳过（不进入 BatchTag 选择集 — 跟上传过滤 IsUploaded 一致） |

---

## 4. 批量打标状态机

### 4.1 VM 字段（`ViewModels/GalleryViewModel.cs` 新增）

```csharp
// === v0.6 AI 打标状态 ===
[ObservableProperty] private bool _isTagging;
[ObservableProperty] private int _tagCompletedCount;
[ObservableProperty] private int _tagTotalCount;
public string TagProgressText => IsTagging && TagTotalCount > 0
    ? $"打标中 {TagCompletedCount}/{TagTotalCount}"
    : string.Empty;

partial void OnIsTaggingChanged(bool value) => OnPropertyChanged(nameof(TagProgressText));
partial void OnTagCompletedCountChanged(int value) => OnPropertyChanged(nameof(TagProgressText));
partial void OnTagTotalCountChanged(int value) => OnPropertyChanged(nameof(TagProgressText));

private CancellationTokenSource? _tagCts;
```

### 4.2 `BatchTag` 命令

```csharp
[RelayCommand]
private async Task BatchTag()
{
    if (IsTagging || !IsBatchActionEnabled) return;

    // AI 配置检查
    var aiCfg = await _configService.GetAsync<AIConfig>(ConfigKeys.AI);
    if (aiCfg == null || string.IsNullOrWhiteSpace(aiCfg.ApiKey))
    {
        ShowToast("AI 未配置，请在设置页填写 API Key");
        return;
    }
    var meta = AIProviderCatalog.Get(Enum.Parse<AIProvider>(aiCfg.Provider));

    var files = SelectedFiles
        .Where(f => f.LocalExists)            // 本地不存在的跳过
        .ToList();
    ExitMultiSelect();

    if (files.Count == 0)
    {
        ShowToast("所选文件本地不存在，无法打标");
        return;
    }

    IsTagging = true;
    TagTotalCount = files.Count;
    TagCompletedCount = 0;
    _tagCts = new CancellationTokenSource();
    var ct = _tagCts.Token;
    var errors = new List<(string Name, string Reason)>();

    ShowToast($"开始打标 {files.Count} 个文件…");

    foreach (var file in files)
    {
        if (ct.IsCancellationRequested) break;

        try
        {
            var (result, failure) = await _aiTagger.TagFileAsync(file, aiCfg, meta, ct);
            if (failure != null)
            {
                if (failure.IsFatal)
                {
                    // Key 失效：终止整批
                    ShowToast($"AI 配置错误：{failure.Reason}");
                    break;
                }
                file.TagError = TruncateError(failure.Reason);
                errors.Add((file.FileName, failure.Reason));
            }
            else if (result != null)
            {
                file.Tags = result.Tags.ToList();
                file.Score = result.Score;
                file.TagError = null;
                await _mediaRepo.UpdateTagAsync(
                    file.Id, result.Tags, result.Score,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    tagError: null, ct);
            }
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            file.TagError = TruncateError(ex.Message);
            errors.Add((file.FileName, ex.Message));
        }

        TagCompletedCount++;
        await Task.Delay(200, ct);  // 节流防 burst
    }

    IsTagging = false;

    // 汇总 toast
    var ok = TagCompletedCount - errors.Count;
    ShowToast(errors.Count == 0
        ? $"打标完成：{ok} 个文件"
        : $"打标完成：成功 {ok}，失败 {errors.Count}");

    // 失败列表：弹窗（参考 BatchUpload 错误处理，§5.3 05-gallery-view.md）
    if (errors.Count > 0)
    {
        var window = DialogHelper.GetMainWindow();
        var sb = new StringBuilder();
        foreach (var (name, reason) in errors.Take(20))
            sb.AppendLine($"• {name}: {reason}");
        if (errors.Count > 20) sb.AppendLine($"…及其他 {errors.Count - 20} 项");
        await DialogHelper.ShowAlertAsync(window, $"打标失败（{errors.Count}）", sb.ToString());
    }

    // 触发 Gallery 重新加载（如果当前在标签分类视图下，需要刷新 TagGroups）
    if (GroupMode == GroupMode.Tag) await LoadTagGroupsAsync(ct);
}

private static string TruncateError(string msg) =>
    msg.Length > 200 ? msg.Substring(0, 200) + "…" : msg;
```

### 4.3 `CancelTag` 命令

```csharp
[RelayCommand(CanExecute = nameof(CanCancelTag))]
private void CancelTag()
{
    _tagCts?.Cancel();
}

private bool CanCancelTag() => IsTagging;
```

### 4.4 单文件打标（右键菜单用）

`TagSingle` 用独立的 `_tagCts`（不是共享的 `_cts`），避免用户切日期 / 刷新时取消打标。

```csharp
[RelayCommand]
private async Task TagSingle(MediaFile? file)
{
    if (file == null || !file.LocalExists || IsTagging) return;

    var aiCfg = await _configService.GetAsync<AIConfig>(ConfigKeys.AI);
    if (aiCfg == null || string.IsNullOrWhiteSpace(aiCfg.ApiKey))
    {
        ShowToast("AI 未配置，请在设置页填写 API Key");
        return;
    }
    var meta = AIProviderCatalog.Get(Enum.Parse<AIProvider>(aiCfg.Provider));

    _tagCts = new CancellationTokenSource();
    IsTagging = true;
    TagTotalCount = 1;
    TagCompletedCount = 0;
    try
    {
        var (result, failure) = await _aiTagger.TagFileAsync(file, aiCfg, meta, _tagCts.Token);
        if (failure != null)
        {
            file.TagError = TruncateError(failure.Reason);
            ShowToast($"打标失败：{failure.Reason}");
        }
        else if (result != null)
        {
            file.Tags = result.Tags.ToList();
            file.Score = result.Score;
            file.TagError = null;
            await _mediaRepo.UpdateTagAsync(
                file.Id, result.Tags, result.Score,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                tagError: null, default);
            ShowToast($"打标完成：{file.FileName}");
        }
    }
    finally
    {
        _tagCts?.Cancel();
        _tagCts?.Dispose();
        _tagCts = null;
        IsTagging = false;
    }
}
```

### 4.5 状态机约束

```
[Idle] ──BatchTag──► [Tagging]
   ▲                    │
   │                    ├─正常完成 ──► [Idle] (toast 汇总)
   │                    ├─取消 ──────► [Idle] (toast "已取消 X 个")
   │                    ├─Fatal失败 ─► [Idle] (toast "AI 配置错误")
   │                    └─单图失败 ──► [Idle] (toast 成功 X 失败 Y)
```

**按钮灰态（沿用 v0.5 IsBatchActionEnabled 模式）：**

| 组件 | IsTagging=false | IsTagging=true |
|---|---|---|
| 「批量打标」 | ✅ IsBatchActionEnabled | ❌ |
| 「取消打标」 | ❌ | ✅ |
| 「批量上传」 | ✅ | ❌ |
| 多选 / 反选 / 全选 | ✅ | ❌ |

---

## 5. Gallery 分类扩展

### 5.1 `GroupMode` enum（新增）

```csharp
public enum GroupMode
{
    Date,   // 时间轴（v0.5 行为）
    Tag,    // 标签分组（v0.6 新增）
}
```

VM 字段：

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(IsDateGroupMode))]
[NotifyPropertyChangedFor(nameof(IsTagGroupMode))]
private GroupMode _groupMode = GroupMode.Date;

public bool IsDateGroupMode => GroupMode == GroupMode.Date;
public bool IsTagGroupMode => GroupMode == GroupMode.Tag;
```

### 5.2 `TagGroups` collection

```csharp
public ObservableCollection<TagGroupCount> TagGroups { get; } = new();
```

### 5.3 `LoadTagGroupsAsync` 方法

```csharp
[RelayCommand]
private async Task LoadTagGroupsAsync(CancellationToken ct)
{
    if (string.IsNullOrEmpty(ProjectPath)) return;
    var groups = await _mediaRepo.GetTagGroupsAsync(ProjectPath, ct);
    TagGroups.Clear();
    foreach (var g in groups) TagGroups.Add(g);
}
```

**`GetTagGroupsAsync` SQL（`Data/MediaRepository.cs`）：**

```sql
-- 拆 JSON 数组 → 行，统计每个标签的文件数
SELECT tag, COUNT(*) as count
FROM (
    SELECT json_each.value AS tag
    FROM media_files, json_each(media_files.tags)
    WHERE project_path = @projectPath
)
GROUP BY tag
ORDER BY count DESC, tag ASC

UNION ALL

-- "未分类" 桶（tags='[]' 或 NULL 的文件）
SELECT '' as tag, COUNT(*) as count
FROM media_files
WHERE project_path = @projectPath
  AND (tags = '[]' OR tags IS NULL)
```

**SQLite `json_each` 内置**（SQLite 3.38+，macOS 系统 SQLite 通常满足；详见 §9 风险）。如果用户环境 SQLite < 3.38，fallback 到内存里 split + count。

### 5.4 `SelectTagAsync` 命令

```csharp
[RelayCommand]
private async Task SelectTagAsync(TagGroupCount? group)
{
    if (group == null) return;

    _cts?.Cancel();
    _cts = new CancellationTokenSource();
    var ct = _cts.Token;

    ExitMultiSelect();
    SelectedTag = group.Tag;

    IsLoadingMedia = true;
    var files = await _mediaRepo.GetByTagAsync(ProjectPath!, group.Tag, SortMode, ct);
    CurrentMediaFiles.Clear();
    foreach (var f in files) CurrentMediaFiles.Add(f);
    IsLoadingMedia = false;
}
```

### 5.5 左栏顶部 TabControl

`Views/GalleryView.axaml:26` 把外层 Grid 从 `ColumnDefinitions="180,*"` 改为：

```xml
<Grid ColumnDefinitions="180,*">
    <!-- 左栏 -->
    <Border Grid.Column="0" Background="{DynamicResource Bg.Outer}"
            BorderBrush="{DynamicResource Bg.Divider}" BorderThickness="0,0,1,0">
        <DockPanel>
            <!-- 顶部 Tab 切换 -->
            <Border DockPanel.Dock="Top" Padding="16,16,16,8">
                <StackPanel Orientation="Horizontal" Spacing="4">
                    <RadioButton Content="时间" GroupName="GroupMode"
                                 IsChecked="{Binding IsDateGroupMode}"
                                 Classes="group-tab"/>
                    <RadioButton Content="标签" GroupName="GroupMode"
                                 IsChecked="{Binding IsTagGroupMode}"
                                 Classes="group-tab"/>
                </StackPanel>
            </Border>

            <!-- 时间轴列表（IsDateGroupMode 时显示） -->
            <ScrollViewer IsVisible="{Binding IsDateGroupMode}">
                <ItemsControl ItemsSource="{Binding DateGroups}" Margin="16,0,16,24">
                    <!-- 沿用现有 TimelineEntry template -->
                </ItemsControl>
            </ScrollViewer>

            <!-- 标签列表（IsTagGroupMode 时显示） -->
            <ScrollViewer IsVisible="{Binding IsTagGroupMode}">
                <ItemsControl ItemsSource="{Binding TagGroups}" Margin="16,0,16,24">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate x:DataType="data:TagGroupCount">
                            <Button Classes="tag-node"
                                    Command="{Binding $parent[UserControl].DataContext.SelectTagCommand}"
                                    CommandParameter="{Binding}">
                                <Grid>
                                    <TextBlock Text="{Binding Tag, Converter={StaticResource EmptyTagToUntitled}}"
                                               HorizontalAlignment="Left" FontSize="13"/>
                                    <TextBlock Text="{Binding Count, StringFormat='{}{0} 张'}"
                                               HorizontalAlignment="Right" FontSize="12"
                                               Foreground="{DynamicResource Text.Secondary}"/>
                                </Grid>
                            </Button>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>
        </DockPanel>
    </Border>
    <!-- 右栏不变 -->
</Grid>
```

**新增 Converter（`Converters/EmptyTagToUntitledConverter.cs`）：** 空字符串 → "未分类"。

### 5.6 切 GroupMode 联动

```csharp
partial void OnGroupModeChanged(GroupMode value)
{
    switch (value)
    {
        case GroupMode.Date:
            // 重新加载时间轴
            _ = InitializeAsync();
            break;
        case GroupMode.Tag:
            // 加载 TagGroups
            _ = LoadTagGroupsAsync(_cts?.Token ?? default);
            SelectedTag = null;  // 清空旧选中
            CurrentMediaFiles.Clear();
            break;
    }
}
```

---

## 6. 排序

### 6.1 `SortMode` enum（新增）

```csharp
public enum SortMode
{
    TimeDesc,   // 时间↓（v0.5 默认）
    ScoreDesc,  // 评分↓（v0.6 新增）
}
```

VM 字段：

```csharp
[ObservableProperty] private SortMode _sortMode = SortMode.TimeDesc;
```

### 6.2 `CurrentMediaFiles` 排序

`LoadDateAsync` / `LoadTagAsync` / `OnSortModeChanged` 都触发重建：

```csharp
partial void OnSortModeChanged(SortMode value)
{
    // 重新加载当前视图
    _ = GroupMode switch
    {
        GroupMode.Date when SelectedDate != null => LoadDateAsync(SelectedDate, _cts?.Token ?? default),
        GroupMode.Tag when SelectedTag != null   => SelectTagAsync(TagGroups.FirstOrDefault(g => g.Tag == SelectedTag)),
        _ => Task.CompletedTask,
    };
}
```

**SQL 层排序：**

| SortMode | SQL `ORDER BY` |
|---|---|
| `TimeDesc` | `ORDER BY shot_at DESC NULLS LAST` |
| `ScoreDesc` | `ORDER BY score DESC NULLS LAST, shot_at DESC` |

**NULL 排最后（SQLite）：** SQLite 不支持 `NULLS LAST`，要用：

```sql
ORDER BY score IS NULL, score DESC, shot_at DESC
-- 解释: score IS NULL 返回 0 或 1, 0 排前 → 加 DESC 反转 → NULL 排最后
```

### 6.3 UI 控件

`Views/MainWindow.axaml` 工具栏新增 ComboBox：

```xml
<ComboBox SelectedIndex="{Binding SortModeIndex}"
          IsVisible="{Binding IsGalleryPage}"
          ToolTip.Tip="排序方式"
          Classes="sort-combo">
    <ComboBoxItem Content="时间↓"/>
    <ComboBoxItem Content="评分↓"/>
</ComboBox>
```

`SortModeIndex` 是 `[ObservableProperty] int` 包装：

```csharp
[ObservableProperty] private int _sortModeIndex;  // 0=TimeDesc, 1=ScoreDesc

partial void OnSortModeIndexChanged(int value)
{
    SortMode = value == 0 ? SortMode.TimeDesc : SortMode.ScoreDesc;
}
partial void OnSortModeChanged(SortMode value)
{
    SortModeIndex = value == SortMode.TimeDesc ? 0 : 1;
}
```

---

## 7. UI 改动清单

### 7.1 MainWindow 工具栏（`Views/MainWindow.axaml:106-166` 区域）

新增 3 个控件：

```xml
<!-- v0.6 新增 ↓ -->

<!-- 排序 ComboBox -->
<ComboBox SelectedIndex="{Binding GalleryViewModel.SortModeIndex}"
          IsVisible="{Binding IsGalleryPage}"
          ToolTip.Tip="排序方式"
          Classes="sort-combo" Width="100">
    <ComboBoxItem Content="时间↓"/>
    <ComboBoxItem Content="评分↓"/>
</ComboBox>

<!-- 批量打标按钮 -->
<Button Classes="toolbar-button"
        Content="批量打标"
        Command="{Binding GalleryViewModel.BatchTagCommand}"
        IsEnabled="{Binding GalleryViewModel.IsBatchActionEnabled}"
        IsVisible="{Binding GalleryViewModel.IsMultiSelectMode}"/>

<!-- 取消打标按钮 -->
<Button Classes="toolbar-button-danger"
        Content="取消打标"
        Command="{Binding GalleryViewModel.CancelTagCommand}"
        IsVisible="{Binding GalleryViewModel.IsTagging}"/>

<!-- 打标进度文本 -->
<TextBlock Text="{Binding GalleryViewModel.TagProgressText}"
           FontSize="12"
           Foreground="{DynamicResource Accent.Stellar}"
           VerticalAlignment="Center"
           Margin="8,0,0,0"
           IsVisible="{Binding GalleryViewModel.IsTagging}"/>
```

### 7.2 GalleryView photo tile（`Views/GalleryView.axaml`）

**评分角标（左下角，跟视频徽章对称）：**

```xml
<!-- 评分徽章 (左下角) -->
<Border IsVisible="{Binding HasScore}"
        HorizontalAlignment="Left"
        VerticalAlignment="Bottom"
        Margin="6,6,10,6"
        Padding="6,2"
        Background="{Binding Score, Converter={StaticResource ScoreToBrush}}"
        CornerRadius="8">
    <TextBlock Text="{Binding Score, Converter={StaticResource ScoreToDisplay}}"
               FontSize="11"
               FontWeight="SemiBold"
               Foreground="#FFFFFF"
               VerticalAlignment="Center"/>
</Border>
```

**新增 Converter：**
- `Converters/ScoreToBrushConverter.cs`：score → Brush
  - `score >= 80` → `#4CAF50` (绿)
  - `60 <= score < 80` → `#FFA726` (黄)
  - `40 <= score < 60` → `#90A4AE` (灰蓝)
  - `score < 40` → `#78909C` (深灰)
- `Converters/ScoreToDisplayConverter.cs`：int? score → "8.7" / ""
  - `null` → `""`
  - 其它 → `{(score / 10.0):0.0}` (一位小数)

**标签小角标条（底部居中）：**

```xml
<!-- 标签小角标条 -->
<Border IsVisible="{Binding HasTags}"
        HorizontalAlignment="Center"
        VerticalAlignment="Bottom"
        Margin="0,0,0,6"
        Padding="6,2"
        MaxWidth="140"
        Background="#CC0A0E1A"
        CornerRadius="8">
    <ToolTip.Tip>
        <ToolTip>
            <ItemsControl ItemsSource="{Binding Tags}"/>
        </ToolTip>
    </ToolTip.Tip>
    <TextBlock Text="{Binding Tags, Converter={StaticResource TagsToShortText}}"
               FontSize="10"
               Foreground="#E6FFFFFF"
               TextTrimming="CharacterEllipsis"
               MaxLines="1"/>
</Border>
```

**新增 Converter：**
- `Converters/TagsToShortTextConverter.cs`：List<string> → "星云 · 广角 · ..." (前 2-3 个 · 分隔)
  - 空 → ""
  - ≤ 3 个全部显示
  - > 3 个显示前 2 个 + " +N"
- **Tooltip 显示全部**：hover 时 ItemsControl 列所有 tags

**打标失败徽章（替代上传 Failed 徽章的对称设计）：**

```xml
<!-- TagError: 左下角红色叹号 -->
<Border IsVisible="{Binding TagError, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
        HorizontalAlignment="Right"
        VerticalAlignment="Bottom"
        Margin="6,6,10,6"
        Width="20" Height="20"
        Background="#CCFFFFFF"
        CornerRadius="10"
        ToolTip.Tip="{Binding TagError}">
    <TextBlock Text="!" FontSize="13" FontWeight="Bold"
               Foreground="{DynamicResource State.Danger}"
               HorizontalAlignment="Center" VerticalAlignment="Center"/>
</Border>
```

### 7.3 右键菜单（`Views/GalleryView.axaml` photo tile 区域）

```xml
<ContextFlyout>
    <MenuItem Header="查看" Command="{Binding $parent[UserControl].((vm:GalleryViewModel)DataContext).OpenFileCommand}"
              CommandParameter="{Binding}"/>
    <MenuItem Header="在文件夹中打开" Command="{Binding $parent[UserControl].((vm:GalleryViewModel)DataContext).OpenInFolderCommand}"
              CommandParameter="{Binding}"/>
    <MenuItem Header="上传" Command="{Binding $parent[UserControl].((vm:GalleryViewModel)DataContext).UploadSingleCommand}"
              CommandParameter="{Binding}"/>
    <Separator/>
    <MenuItem Header="AI 打标" Command="{Binding $parent[UserControl].((vm:GalleryViewModel)DataContext).TagSingleCommand}"
              CommandParameter="{Binding}"/>
    <Separator/>
    <MenuItem Header="删除" Command="{Binding $parent[UserControl].((vm:GalleryViewModel)DataContext).DeleteSingleCommand}"
              CommandParameter="{Binding}"/>
</ContextFlyout>
```

### 7.4 设置页（`Views/SettingsView.axaml`）

AI 配置页签加一个 "批量打标测试" 按钮（沿用现有 AITester）：

```xml
<Button Content="测试连接"
        Command="{Binding TestAiCommand}"
        Classes="primary-button"/>
```

`TestAiCommand` 已在 v0.5 实现（调 AITester）。

> **v0.6 实施变更**：`AITester` 改用 `AIConfig.Protocol` 字符串参数（不再依赖 `AIProviderMeta.ProtocolKind`），Gemini 走 OpenAI 兼容分支（用户在 UI 选 OpenAI 协议）。

---

## 8. 数据流 Sequence

### 8.1 多选 → 批量打标

```
用户 [多选模式]
  ├─ 点 5 张照片卡 → SelectedFiles += 5 项
  └─ 点工具栏「批量打标」
        ↓
GalleryViewModel.BatchTag
  ├─ AIConfig 检查（缺 Key → toast + return）
  ├─ files = SelectedFiles.Where(LocalExists).ToList()
  ├─ ExitMultiSelect()                  ← 清空选择，触发 IsSelected 同步
  ├─ IsTagging = true / TagTotalCount = 5 / TagCompletedCount = 0
  ├─ foreach file in files:
  │   ├─ AITagger.TagFileAsync(file, aiCfg, meta, ct)
  │   │   ├─ 图片：SkiaSharp resize 512px + base64
  │   │   ├─ 视频：ffmpeg thumbnail 抽 8 帧 → resize → base64
  │   │   ├─ 构造 messages（按 AIConfig.Protocol 路由）
  │   │   ├─ HTTP POST → 解析 JSON → 白名单校验
  │   │   └─ 返回 TagResult / TagFailure
  │   ├─ 成功 → file.Tags / file.Score / file.TagError = null
  │   │       + MediaRepository.UpdateTagAsync(file.Id, ...)
  │   ├─ 失败 → file.TagError = reason（不更新 DB？— 待定 v0.6.1：失败也写 tag_error）
  │   ├─ TagCompletedCount++
  │   └─ Task.Delay(200)
  ├─ IsTagging = false
  ├─ ShowToast(汇总)
  └─ if GroupMode == Tag → LoadTagGroupsAsync 刷新
```

### 8.2 切换到「标签」分类

```
用户 [点工具栏/左栏顶部「标签」RadioButton]
  ↓
GalleryViewModel.GroupMode = Tag
  ↓
OnGroupModeChanged(Tag)
  ├─ LoadTagGroupsAsync(ct)
  │   └─ MediaRepository.GetTagGroupsAsync
  │       └─ SQL: json_each(tags) + count
  ├─ TagGroups 更新 → 左栏 ItemsControl 重渲
  └─ CurrentMediaFiles.Clear()（等用户点具体 tag）
        ↓
用户 [点某个 tag 行（如"星云 · 12 张"）]
  ↓
SelectTagAsync(group)
  ├─ SelectedTag = "星云"
  ├─ LoadTagGroupsAsync? — 不需要，已经在显示
  └─ MediaRepository.GetByTagAsync(projectPath, "星云", SortMode)
        └─ SQL: WHERE tags LIKE '%"星云"%' OR json_each contains "星云"
                                                    + ORDER BY 当前 SortMode
        ↓
CurrentMediaFiles.Clear() + Add(files)
        ↓
用户 [切排序 ComboBox「评分↓」]
  ↓
GalleryViewModel.SortMode = ScoreDesc
  ↓
OnSortModeChanged → 重新调 SelectTagAsync（保留当前 SelectedTag）
```

### 8.3 切排序

```
用户 [ComboBox: 时间↓ → 评分↓]
  ↓
SortModeIndex = 1 → SortMode = ScoreDesc
  ↓
OnSortModeChanged(ScoreDesc)
  ↓
switch (GroupMode):
  ├─ Date: LoadDateAsync(SelectedDate, ct)  ← SQL 加 ORDER BY score DESC NULLS LAST
  └─ Tag:  SelectTagAsync(currentGroup)     ← 同上
        ↓
CurrentMediaFiles 重建（按新排序）
```

---

## 9. 风险 & 兜底

| 风险 | 触发条件 | 兜底 |
|---|---|---|
| **SQLite 版本 < 3.38**（不支持 json_each） | macOS 系统 SQLite 通常 ≥ 3.39，但旧 Linux 发行版可能 3.32 | 在 `GetTagGroupsAsync` 里 try/catch，fallback 到内存 split：`reader.GetString("tags")` → `JsonSerializer.Deserialize<List<string>>` → `SelectMany` → `GroupBy` → `Count` |
| **AI 配额耗尽**（HTTP 429 重试 3 次仍失败） | 高频打标 | 单图跳过 + tag_error "rate limit"，不影响其他文件 |
| **API Key 过期**（HTTP 401/403） | 用户改了密码没更新 | 整批终止 + toast "AI 配置错误" + 引导跳设置页（沿用 `_onOssNotConfigured` 模式，加 `_onAiNotConfigured` 回调） |
| **视频 ffmpeg 抽帧失败** | ffmpeg 不在 PATH 或视频损坏 | 单视频失败 + tag_error "ffmpeg 抽帧失败：{stderr 摘要}" |
| **AI 返回 markdown 包裹 JSON** | model 不严格遵守指令 | Parse 前 regex 剥 ``` 包裹 |
| **AI 返回超长（token 超限）** | 高分图 + prompt 长 | resize 再小（256px）+ retry 1 次 |
| **进程崩溃时临时帧残留** | kill -9 / 断电 | App.OnStartup 启动时 sweep 7 天前的 ai-frames/ 目录 |
| **同文件并发打标** | 用户狂点 BatchTag | `_tagCts.Cancel()` 旧 + new；早 return `if (IsTagging)` |
| **标签过多污染左栏** | 一批文件产生 50 个不同 tag | 左栏 ScrollViewer 滚动即可；v0.6.1 考虑加"按出现频率过滤" |
| **DB 写失败** | sqlite lock | catch + toast "保存失败：xxx" + 内存里仍有 UI 显示（不刷新就还在） |
| **模型拒绝生成（content filter）** | OpenAI / Anthropic 偶尔 | 解析失败 → 单图跳过 + tag_error "模型拒绝响应" |
| **Tag 白名单漂移** | 用户提新词（"星轨"等） | v0.6.1 增加白名单（修订常量 `ChineseTagVocabulary`） |

---

## 10. 修改检查清单

### 10.1 数据层

| 改动 | 涉及文件 | 验证 |
|---|---|---|
| `media_files` 加 4 列 + 2 索引 | `Data/MediaRepository.cs` `EnsureDatabase` + `GetByDateAsync` SELECT | 新老 DB 兼容（AddColumnIfMissing） |
| `MediaFile` 加 Tags / Score / TaggedAt / TagError | `Data/MediaFile.cs` | 重启 DB 字段映射无错 |
| `IMediaRepository` 加 3 方法 | `Data/IMediaRepository.cs` + 实现 | 编译通过 |
| `Tags` 字段 JSON 序列化 | `MediaRepository` 读 + `AITagger` 写 | 写后 SELECT 读出 List |

### 10.2 服务层

| 改动 | 涉及文件 | 验证 |
|---|---|---|
| `AITagger` 主服务 | `Services/AITagger.cs` 新建 | 5 张图 mock 测试：返回 tags/score |
| `AIVideoFrameExtractor` | `Services/AIVideoFrameExtractor.cs` 新建 | 1 段视频 mock 测试：抽出 8 张 |
| `FfmpegFrameExtractorRunner`（ffmpeg 封装） | `Services/FfmpegFrameExtractorRunner.cs` 新建 | 进程退出码 + 文件存在 |
| `ChineseTagVocabulary` 常量 | `Services/AITagger.cs` 内 | 56 个词 |
| 中文 prompt 模板 | `Services/AITagger.cs` 内 | 启动拼入 messages |
| DI 注册 `IAITagger` / `IAIVideoFrameExtractor` / `FfmpegFrameExtractorRunner` | `Program.cs` (`Main`) 或 `App.axaml.cs` 的 `ServiceCollection` | 注入 GalleryViewModel 不抛 |

### 10.3 VM 层

| 改动 | 涉及文件 | 验证 |
|---|---|---|
| `GroupMode` enum | `Models/Models.cs` 或 `ViewModels/GalleryViewModel.cs` | 切 tab 联动 |
| `SortMode` enum | 同上 | 切排序联动 |
| `TagGroups` / `IsTagging` / `TagCompletedCount` / `TagTotalCount` | `GalleryViewModel.cs` | 多选打标 toast/进度条 |
| `BatchTag` / `CancelTag` / `TagSingle` / `LoadTagGroupsAsync` / `SelectTagAsync` | 同上 | 编译 + 单元 |
| `OnGroupModeChanged` / `OnSortModeChanged` | 同上 | 切 tab 重新加载 |

### 10.4 UI 层

| 改动 | 涉及文件 | 验证 |
|---|---|---|
| 左栏顶部 TabControl | `Views/GalleryView.axaml` | [时间 \| 标签] 显示 + 切换 |
| 工具栏「批量打标 / 取消打标 / 排序」 | `Views/MainWindow.axaml` | 灰态正确 |
| photo tile 评分角标 + 标签条 + TagError 徽章 | `Views/GalleryView.axaml` | 三者互斥可见 |
| 3 个 Converter | `Converters/` | 单测 |
| 右键菜单 "AI 打标" | `Views/GalleryView.axaml` ContextFlyout | 右键触发 |
| `EmptyTagToUntitledConverter` | `Converters/` | "" → "未分类" |

### 10.5 跨模块规则（沿用项目跨模块铁律 `09-ui-commons.md` §20）

- 颜色走 token (`Accent.Stellar` / `State.Danger` 等)
- `IsEnabled` 与 `CanExecute` 二选一
- 状态可追踪（IsDirty / IsTagging 显式）
- 故障隔离（每文件 try/catch，crash 单图不毁整批）

### 10.6 关联文档同步更新（实施时改）

- `02-data-layer.md` §3.1：media_files 表加 4 列 + 2 索引
- `02-data-layer.md` §3.2：IMediaRepository 加 3 方法
- `05-gallery-view.md` §2.1：加 GroupMode / SortMode / TagGroups
- `05-gallery-view.md` §3：新增 LoadTagGroupsAsync / SelectTagAsync 生命周期
- `05-gallery-view.md` §4：状态机加 IsTagging 一行
- `05-gallery-view.md` §9：修改检查清单加 v0.6 一节
- `06-settings.md` §2：AI 配置测试按钮说明
- `09-ui-commons.md` §20：跨模块铁律加 "AI 调用日志走 Trace.WriteLine，加 [AITagger] 前缀"

---

## 11. 已知陷阱（沉淀复用）

- **`tags='[]'` vs `tags IS NULL`** — DB 写入统一 `'[]'`，查询"未分类"组 `WHERE tags = '[]' OR tags IS NULL`（兼容老行 NULL）
- **JSON `LIKE` 子串匹配小心** — `"星云"` 跟 `"暗星云"` 会误匹配。LIKE 模式用 `%"星云"%` 配合左右双引号："LIKE '%""星云""%'"（SQLite LIKE 不区分 `:` 等，但 JSON 数组元素前后必有 `"`）
- **评分排序 NULLS LAST** — SQLite 不支持 `NULLS LAST` 关键字，要用 `ORDER BY score IS NULL, score DESC`
- **AI 调用日志** — 全部走 `Trace.WriteLine($"[AITagger] ...")`，前缀统一方便 grep
- **临时目录并发安全** — `ai-frames/<hash>_<unix-ms>/` 加 unix-ms 后缀，避免两个相同视频并发抽帧冲突
- **HttpClient 复用** — `AITagger` 持有 static `HttpClient`（`SocketsHttpHandler` 池化），不要每次 new（DNS 缓存 / TIME_WAIT 累积）
- **AI 超时 ≠ 用户取消** — `TaskCanceledException` 分两种：`ct.IsCancellationRequested` 走用户取消路径；否则走超时重试
- **协议独立于厂商** — v0.6 起 `AIConfig.Protocol` 是运行时唯一权威，`AIProviderMeta.ProtocolKind` 仅供设置页渲染；用户可在 UI 强制选 OpenAI 或 Anthropic（覆盖厂商默认）

---

## 附录 A：中文标签白名单（56 个）

代码常量 `ChineseTagVocabulary`：

```csharp
public static readonly IReadOnlyList<string> ChineseTagVocabulary = new[]
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

    // 质量问题 (10)
    "拖线", "失焦", "噪点", "过曝", "欠曝", "色差",
    "大气抖动", "视宁度差", "镜头眩光", "杂光",

    // 特殊 (2)
    "非天文", "未分类",
};
// 总计 56 个
```

**白名单扩展流程（v0.6.1+）：**
1. 用户提新词（如 "星轨" / "火流星"）
2. 改 `ChineseTagVocabulary` 常量
3. **不需要 DB 迁移**（已有标签无破坏）
4. **不需要 AI 重新打标**（用户对历史文件可右键单文件重新打标）

**白名单剪枝流程：**
1. 发现某词 AI 几乎不返回 → 从常量移除
2. **已打标文件保留该词**（DB 历史数据不动）
3. 下次批量打标自动不再写入

---

---

## 附录 C：实施阶段拆分（roadmap）

| Phase | 内容 | 估时 | 依赖 |
|---|---|---|---|
| **D.1 数据 + 服务基础** | DB migration / `MediaFile` 字段 / `IMediaRepository` 扩展 / `AITagger` 图片版（OpenAI + Anthropic）/ `ChineseTagVocabulary` / `BatchTag` 命令 | 2-3 天 | 无 |
| **D.2 视频抽帧**（v0.6 改：去 Gemini） | `AIVideoFrameExtractor` / `FfmpegFrameExtractorRunner` / `AITagger` 视频分支 / 临时目录管理 / `App.OnStartup` sweep 7 天前 ai-frames | 1-2 天 | D.1 |
| **D.3 Gallery 分类 + 排序** | `GroupMode` / `SortMode` / 左栏 tab / 工具栏 ComboBox / 排序联动 | 1 天 | D.1 |
| **D.4 UI 打磨** | photo tile 评分角标 + 标签条 + TagError 徽章 / 右键菜单 / 3 个 Converter | 1 天 | D.1 |
| **D.5 文档同步** | `02-data-layer.md` §3.1/§3.2 / `05-gallery-view.md` §2/§3/§4/§9 / `06-settings.md` §2 / `09-ui-commons.md` §20 | 0.5 天 | 全部 |
| **D.6 调试 + 单测** | SQLite json_each fallback / ffmpeg 抽帧异常 / 临时目录清理 / 100 张图端到端 | 1 天 | D.1-D.5 |

**总计 ~6-8 天**。

---

> **本文档状态**：spec（待实施）。所有"对应代码"为占位，实施时回填实际 `file:line` 引用。
>
> **后续动作**：用户审完本 spec → 确认 → 开 Phase D.1 数据 + 服务基础。