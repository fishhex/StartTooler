# 02 — 数据层（SQLite 持久化）

> 对应代码：`Services/ConfigService.cs`、`Data/MediaRepository.cs`、`Data/UploadJobRepository.cs`、所有 `*Config.cs`、`Data/MediaFile.cs`、`Data/DateCount.cs`、`Models.cs`、`Data/UploadJob.cs`。

---

## 1. 总体设计

项目用 **两个独立的 SQLite 文件**：

| 文件 | 路径 | 内容 | 读取入口 |
|---|---|---|---|
| `Config.db` | `~/Library/Application Support/StartTooler/config.db`（mac）等 | 设置 / 配置 KV JSON | `ConfigService` |
| `media.db` | `~/Library/Application Support/StartTooler/media.db` | `media_files` + `upload_jobs` 两表 | `MediaRepository` + `UploadJobRepository` |

**为什么拆两个**：
- 设置高频小写 → KV JSON 表够用
- 业务数据（文件索引 + 任务状态）量大、需索引 → 专用表
- 一个文件损坏不污染另一个（用户反馈「设置丢了但文件还在」）

`Windows` 上 `%LocalAppData%\StartTooler\`，`Linux` 上 `~/.local/share/StartTooler/`。统一通过 `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` 解析。

---

## 2. ConfigService — 设置 KV

### 2.1 表结构（`Services/ConfigService.cs:36-42`）

```sql
CREATE TABLE IF NOT EXISTS Config (
    Key TEXT PRIMARY KEY,    -- ConfigKeys.* 中的常量
    Value TEXT NOT NULL,     -- JSON 序列化后的整个对象
    UpdatedAt TEXT NOT NULL  -- ISO 8601 UTC（"O" 格式）
)
```

注意：**表名 `Config`（大写 C）**——v0.x 早期用 `config` 小写有过混乱，详见 `10-trap-book.md`。

### 2.2 接口（`Services/IConfigService.cs:5-10`）

```csharp
public interface IConfigService {
    Task<T?> GetAsync<T>(string key) where T : class;
    Task SetAsync<T>(string key, T value) where T : class;
    Task<T> GetOrCreateAsync<T>(string key) where T : class, new();
}
```

- `GetAsync` 返回 `null` 而不是抛——「没存过」是正常状态，不是错。
- `GetOrCreateAsync` 读不到就 `new T()` 并写入。**第一次启动某 key** 用这个最方便（`ProjectConfig`、`OssConfig`、`PublicRelayConfig` 都用它）。

### 2.3 标准 keys（`Services/ConfigKeys.cs:3-9`）

```csharp
public static class ConfigKeys {
    public const string Project      = "project";       // → ProjectConfig
    public const string App          = "app";           // → AppConfig
    public const string Oss          = "oss";           // → OssConfig
    public const string PublicRelay  = "publicRelay";   // → PublicRelayConfig
}
```

新增设置 key 必须：
1. 加常量到 `ConfigKeys`
2. 在 `Models` 或 `Services` 加配置 `record`/`class`
3. 在 `SettingsViewModel` 加字段 + dirty 跟踪 + 保存逻辑

### 2.4 持久化对象模型

| Key | 类型 | 文件 |
|---|---|---|
| `project` | `ProjectConfig`（当前项目目录 + 最近目录） | `Services/ProjectConfig.cs` |
| `app` | `AppConfig`（主题 + FFmpeg / FFprobe 路径） | `Services/AppConfig.cs` |
| `oss` | `OssConfig`（凭据 + region + bucket + pathPrefix） | `Services/OssConfig.cs` |
| `publicRelay` | `PublicRelayConfig`（SSH 配置 + 端口 + 远程路径 + 架构） | `Services/PublicRelayConfig.cs` |

### 2.5 读写模式

```csharp
// 写（SettingsViewModel.Save 多 key 串行）
await _configService.SetAsync(ConfigKeys.Oss, ossConfig);

// 读（构造器期）
var cfg = await _configService.GetOrCreateAsync<OssConfig>(ConfigKeys.Oss);

// 读（fire-and-forget 期）
var cfg = await _configService.GetAsync<AppConfig>(ConfigKeys.App);
```

注意：所有读写都是 `await`，但 `MainWindowViewModel` 构造期间**只 fire-and-forget 不 await**（避免阻塞 ctor）；事实上所有读都先经过 `InitializeAsync`，已经在 `async` 通道里。

### 2.6 JSON 序列化约定

`ConfigService` 用 `System.Text.Json` 默认选项：
- camelCase（默认）写入；如果字段是 `PascalCase` 写进数据库，**所有 Config 字段命名要稳定**——已用 PascalCase，跨版本兼容。
- **不能加字典/集合根**：`SetAsync<OssConfig>` 会序列化为整个对象 JSON，如果以后想加 JSON 子树就不行（v0.1 不做）。

---

## 3. MediaRepository — 媒体索引

### 3.1 表结构（`Data/MediaRepository.cs:36-56`）

```sql
CREATE TABLE IF NOT EXISTS media_files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_path TEXT NOT NULL,           -- 规范化后的绝对路径（无尾部 /）
    relative_path TEXT NOT NULL,          -- 相对 project_path
    file_name TEXT NOT NULL,
    media_type INTEGER NOT NULL DEFAULT 0, -- 0=Image, 1=Video
    file_size INTEGER NOT NULL DEFAULT 0,
    last_modified INTEGER NOT NULL DEFAULT 0,  -- unix ms
    shot_at INTEGER,                          -- unix ms（先用 last_modified 兜底）
    is_uploaded INTEGER NOT NULL DEFAULT 0,    -- 0/1
    local_exists INTEGER NOT NULL DEFAULT 1,   -- 0/1
    thumbnail_path TEXT,
    remote_url TEXT,
    uploaded_at INTEGER,                      -- unix ms
    scanned_at INTEGER NOT NULL DEFAULT 0,
    UNIQUE(project_path, relative_path)        -- 重扫描幂等 key
);
CREATE INDEX IF NOT EXISTS idx_media_files_date ON media_files(shot_at);
CREATE INDEX IF NOT EXISTS idx_media_files_project ON media_files(project_path);
```

**唯一约束**：`UNIQUE(project_path, relative_path)` —— 重扫描不会插入重复，**走 `ON CONFLICT(...) DO UPDATE`** 路径更新 `file_size / last_modified / shot_at / thumbnail_path / scanned_at`。

### 3.2 接口（`Data/IMediaRepository.cs:10-21`）

```csharp
public interface IMediaRepository {
    Task<IReadOnlyList<DateCount>> GetDateGroupsAsync(string projectPath, CancellationToken ct = default);
    Task<IReadOnlyList<MediaFile>> GetByDateAsync(string projectPath, DateTime date, CancellationToken ct = default);
    Task<ScanResult> ScanDirectoryAsync(string projectPath, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);
    Task GenerateThumbnailsAsync(string projectPath, IThumbnailService thumbnailService, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);
    Task UpdateUploadStateAsync(long fileId, bool isUploaded, long? uploadedAt, string? remoteUrl, CancellationToken ct = default);
}

public class ScanResult {
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Failed { get; set; }
    public int NewFiles { get; set; }
    public int UpdatedFiles { get; set; }
}
```

### 3.3 关键约定

#### 路径规范化契约

**所有读写 `project_path` 前必须 `Path.GetFullPath(...).TrimEnd(Path.DirectorySeparatorChar)`**——写入读出一致化：
- 写：`ScanDirectoryAsync` `MediaRepository.cs:233`、`GenerateThumbnailsAsync` `MediaRepository.cs:297`
- 读：`GetDateGroupsAsync` `MediaRepository.cs:70`、`GetByDateAsync` `MediaRepository.cs:111`

**踩坑**：早期不规范化导致 `~/shots` vs `~/shots/` 重复条目，已被 list & trim 修复，但读侧也得做才一致。

#### 路径比较策略

- **DB 里**：项目路径是字符串（规范化后）。
- **业务比较**：用 `StringComparer.OrdinalIgnoreCase`（macOS HFS+/APFS 默认大小写不敏感）。

#### 扫描的两遍结构（`MediaRepository.cs:173-290`）

```
ScanDirectoryAsync(projectPath, progress, ct)
├─ 第一遍 Task.Run: Directory.EnumerateFiles(... RecurseSubdirectories=true ...)
│   └─ 过滤白名单后缀（图片 / 视频）→ 全量列文件
│   └─ 报 progress Total=X / Processed=0
├─ Total=0 直接 return（空项目目录兜底）
└─ 第二遍 connection.BeginTransaction: foreach (file)
    ├─ 计算 relative_path / file_size / last_modified / media_type
    ├─ INSERT ... ON CONFLICT DO UPDATE  // 增量扫描免重复计算
    ├─ 累加 NewFiles / Processed / Failed
    ├─ 单文件异常 catch 累加 Failed 不抛出
    └─ 报 progress
```

**为什么分两遍**：
1. 第一遍只 `Task.Run` 不写库，速度极快（只 IO），能给用户「总进度」
2. 第二遍单连接循环 + 单条 INSERT，比 prepared batch 更易断点续扫

#### 缩略图生成（`MediaRepository.cs:292-348`）

`GenerateThumbnailsAsync` 拿到所有 `thumbnail_path IS NULL OR = ''` 的文件，**串行**循环（不要并行——避免 ffmpeg 把 CPU 吃满）：
```csharp
foreach (var (id, relativePath) in files) {
    ct.ThrowIfCancellationRequested();
    var fullPath = Path.Combine(normalizedPath, relativePath);
    if (!File.Exists(fullPath)) continue;
    var thumbnailPath = await thumbnailService.GenerateThumbnailAsync(fullPath, normalizedPath, ct);
    if (thumbnailPath != null) {
        UPDATE media_files SET thumbnail_path = @thumbnailPath WHERE id = @id
    }
}
```

`ScanProgress{ Total, Processed, CurrentFile }` 上报给 `GalleryViewModel`。

---

## 4. UploadJobRepository — Multipart 续传任务

### 4.1 表结构（`Data/UploadJobRepository.cs:35-50`）

```sql
CREATE TABLE IF NOT EXISTS upload_jobs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_path TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    object_key TEXT NOT NULL,
    upload_id TEXT NOT NULL,
    file_size INTEGER NOT NULL,
    part_size INTEGER NOT NULL,
    parts_uploaded TEXT NOT NULL DEFAULT '[]',  -- JSON List<UploadedPart>
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    UNIQUE(project_path, relative_path)         -- 一个文件同时只能一个 job
);
CREATE INDEX IF NOT EXISTS idx_upload_jobs_project ON upload_jobs(project_path);
```

### 4.2 接口（`Data/UploadJobRepository.cs:191-198`）

```csharp
public interface IUploadJobRepository {
    Task<IReadOnlyList<UploadJob>> GetInProgressAsync(string projectPath, CancellationToken ct = default);
    Task<UploadJob?> GetByFileAsync(string projectPath, string relativePath, CancellationToken ct = default);
    Task UpsertAsync(UploadJob job, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task DeleteByFileAsync(string projectPath, string relativePath, CancellationToken ct = default);
}
```

### 4.3 状态机

```
[启动上传]
   ↓ GetByFileAsync(project, relPath)
   ├─ null           → 全新建：InitiateMultipartAsync → UpsertAsync
   └─ UploadJob      → 续传：ListPartsAsync（OSS 权威） + 补传缺失 → Complete

[每片成功]
   UpsertAsync(job.PartsUploaded += 新 PartETag)

[Complete 成功]
   DeleteAsync(job.Id)

[用户取消 / 异常退出]
   保留 job record，下次启动弹窗续传
```

**关键**：取消不调 Abort（OSS 保留分片）。这与「重扫数据不能丢上传进度」强相关。

### 4.4 为什么 `parts_uploaded` 是 JSON 字符串而不是关联表

`UploadJobRepository.cs:114-138`：SQLite 没有原生数组类型，**JSON 列** 是简单且够用的方案：
- 写：`JsonSerializer.Serialize(job.PartsUploaded)`
- 读：`JsonSerializer.Deserialize<List<UploadedPart>>(partsJson) ?? new()`
- 列表有序（PartNumber 自然升序）+ 量小（一个文件最多几十片）→ JSON 没有性能问题

未来如果需要按 PartNumber 查重传，得迁到关联表。

---

## 5. MediaFile 模型（`Data/MediaFile.cs`）

### 5.1 字段分类

| 字段 | 类型 | 来源 | 是否持久化 | 何时更新 |
|---|---|---|---|---|
| `Id / ProjectPath / RelativePath / FileName` | string/int64 | DB 读出 | ✅ | 永不（DB 写入） |
| `MediaType` | enum (Image/Video) | DB 读出 | ✅ | 永不 |
| `FileSize / LastModified / ScannedAt` | long | DB 读出 | ✅ | 扫描时 |
| `ShotAt / ShotAtDateTime` | long? / DateTime? | DB 读出 | ✅ | 扫描时 |
| **`IsUploaded / UploadedAt / RemoteUrl`** | bool/long?/string? | DB 读出 | ✅ | **每次上传成功** → `UpdateUploadStateAsync` |
| **`LocalExists`** | bool（[ObservableProperty]）| UI 瞬时 | ❌ | 下载完成后置 true |
| **`ThumbnailPath`** | string?（[ObservableProperty]）| DB 读出 | ✅ | 缩略图生成时写 DB |
| **`IsSelected`** | bool（[ObservableProperty]）| UI 瞬时 | ❌ | SelectedFiles.CollectionChanged 同步 |
| **`UploadStatus`** | enum（[ObservableProperty]）| UI 瞬时 | ❌ | 进 Gallery 时反推 |
| **`UploadError`** | string?（[ObservableProperty]）| UI 瞬时 | ❌ | 上传失败置 |

### 5.2 UploadStatus 反推逻辑（`GalleryViewModel.cs:213-235`）

```csharp
var pausedSet = new HashSet<string>(jobs.Select(j => j.RelativePath), StringComparer.OrdinalIgnoreCase);
foreach (var file in files) {
    file.UploadStatus = file.IsUploaded ? UploadStatus.Uploaded
                    : pausedSet.Contains(file.RelativePath) ? UploadStatus.Paused
                    : UploadStatus.NotUploaded;
}
```

**`upload_jobs` 是续传任务的唯一持久化来源**——任何未完成 multipart 都映射到 `Paused`，与 DB 中 `is_uploaded=0` 共存。

---

## 6. 事务与并发

### 6.1 单连接原则

- Repository 方法都是 `using var connection = new SqliteConnection(...)`（`MediaRepository.cs:219`、`UploadJobRepository.cs:111`）—— **每个方法一个连接**，用完即弃。
- **不要** 在 Repository 内部跨方法共享 connection（容易事务泄漏）。
- 微批 INSERT（每文件一条）的开销在「几千张照片」量级 < 1s，可接受。

### 6.2 取消处理

所有接口都接受 `CancellationToken`：
- 扫描：单文件 try/catch，循环顶部 `ct.IsCancellationRequested` 判断
- 上传：每个分片上传前/后 `ct.ThrowIfCancellationRequested()`
- 查询：传 `ct` 到 `SqliteCommand.ExecuteReaderAsync(ct)`

### 6.3 错误隔离

| 错误源 | 处理 |
|---|---|
| 单文件扫描失败 | catch + `result.Failed++` |
| 单个缩略图失败 | catch + 跳过（不抛）`ThumbnailService.cs:65-70` |
| DB 查询失败 | catch in `GalleryViewModel.LoadDateAsync` → LoadErrorMessage |
| 上传 I/O 失败 | `UploadError` 字段 + errors 列表 + 失败汇总对话框 |

---

## 7. 缩略图路径（与数据层耦合）

- 缩略图磁盘根目录：`{LocalAppData}/StartTooler/thumbnails/`（`ThumbnailService.cs:23-28`）
- 文件名：`<hash>.jpg`，hash = `unchecked(17; foreach c: hash = hash*31 + c)` 字符串哈希（`ThumbnailService.cs:206-218`）
- 路径写入 `media_files.thumbnail_path`（绝对路径，跨会话有效）
- **缓存清空场景**：用户清缓存后 ThumbnailPath 是死路径——依赖 `FilePathToBitmapConverter` 自动隐藏（见 `09-ui-commons.md`）

---

## 8. 修改这个模块前的检查清单

| 改动 | 涉及 | 验证 |
|---|---|---|
| 加设置项 | `ConfigKeys` + 新 `XxxConfig` + `SettingsViewModel` | 设置页可见 + 保存/重启保留 |
| 改 `media_files` schema | `MediaRepository.EnsureDatabase` + 所有 SELECT 字段映射 | 迁移现有 DB → 备份后 `ALTER TABLE` 或新建列 nullable |
| 加 repository 表 | `Data/XxxRepository.cs` 同约定（path via LocalAppData + EnsureTable） | 全新 DB 不出错 |
| 改 `UploadJob` 字段 | `UploadJob` + `UploadJobRepository` SQL + ORM 映射 + `GalleryViewModel.UploadOneAsync` | 重启后能续传 |
| 改 path 规范化规则 | 所有 repository 的 read + write 路径 | DB 现有数据 vs 新数据一致（写幂等测试） |

---

## 9. 已知陷阱（详见 `10-trap-book.md`）

- **路径规范化**：项目路径必须 `GetFullPath + TrimEnd` 双向做
- **unique 约束**：`UNIQUE(project_path, relative_path)` 写死，不要试图给每个文件单独 UUID——文件可重命名/移动，相对路径能稳定匹配
- **JSON 列**：SQLite 没有数组类型，用 TEXT + JSON 序列化
- **多任务续传**：`MaxNumberOfRetries` 没设置，目前不重试，下个分片失败直接退出（已节流）
- **DB 写失败**：上传成功但 DB 写失败——UI 已是 Uploaded，正确；下次重试会重新写（已测试）
