# 02 — 数据层（SQLite 持久化）

> 对应代码：`Services/ConfigService.cs`、`Data/MediaRepository.cs`、`Data/UploadJobRepository.cs`、所有 `*Config.cs`、`Data/MediaFile.cs`、`Data/DateCount.cs`、`Models.cs`、`Data/UploadJob.cs`。

---

## 1. 总体设计

项目用 **两个独立的 SQLite 文件**：

| 文件 | 路径 | 内容 | 读取入口 |
|---|---|---|---|
| `config.db` | `~/Library/Application Support/StartTooler/config.db`（mac）等 | 设置 / 配置 KV JSON（单表 `config`） | `ConfigService` |
| `media.db` | `~/Library/Application Support/StartTooler/media.db` | `media_files` + `upload_jobs` 两表 | `MediaRepository` + `UploadJobRepository` |

> **遗留文件**：`starttooler.db`（v0 早期 AI / 多云存储实验产物，5 张 PascalCase 表，代码 0 引用）— 已废弃，新代码不读不写；详见 `10-trap-book.md` §36。

**为什么拆两个**：
- 设置高频小写 → KV JSON 表够用
- 业务数据（文件索引 + 任务状态）量大、需索引 → 专用表
- 一个文件损坏不污染另一个（用户反馈「设置丢了但文件还在」）

`Windows` 上 `%LocalAppData%\StartTooler\`，`Linux` 上 `~/.local/share/StartTooler/`。统一通过 `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` 解析。

---

## 2. ConfigService — 设置 KV

### 2.1 表结构（`Services/ConfigService.cs:36-42`）

```sql
CREATE TABLE IF NOT EXISTS config (
    key TEXT PRIMARY KEY,        -- ConfigKeys.* 中的常量
    value TEXT NOT NULL,         -- JSON 序列化后的整个对象
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
)
```

表名固定小写 `config`，与 `media_files` / `upload_jobs` 对齐；老版本大写 `Config` 在 `InitializeDatabase` 启动时自动 `ALTER TABLE RENAME` 兼容（`ConfigService.cs:48-60`），详见 `10-trap-book.md` §5。

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
| `ai` | `AIConfig`（Provider 枚举字符串 + ApiKey + BaseUrl + Model） | `Services/AIConfig.cs` |
| `anthropic` | **已废弃**（旧单厂商 Anthropic 配置）。新代码不再读取，保留以便未来回滚 | — |

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
    last_modified INTEGER NOT NULL DEFAULT 0,  -- unix ms（业务时间字段，见 §11）
    shot_at INTEGER,                          -- unix ms（业务时间字段；先用 last_modified 兜底）
    is_uploaded INTEGER NOT NULL DEFAULT 0,    -- 0/1
    local_exists INTEGER NOT NULL DEFAULT 1,   -- 0/1
    thumbnail_path TEXT,
    remote_url TEXT,
    uploaded_at INTEGER,                      -- unix ms（业务时间字段）
    scanned_at INTEGER NOT NULL DEFAULT 0,     -- unix ms（业务时间字段）
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
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
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
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

---

## 10. `sync_for_vps_task` 表（公网接收任务持久化）

> 对应代码：`Data/SyncForVpsTaskRepository.cs`、`Models/SyncForVpsTask.cs`。
> 详见 `08-public-relay.md` §5.4-5.6（batch scp worker）。

### 10.1 表结构

```sql
CREATE TABLE IF NOT EXISTS sync_for_vps_task (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id TEXT NOT NULL,              -- Go relay 32 字符 hex id（v0.4+ VPS tmp/ 直接用 file_name 落盘，id 仅用于 pending map + ack）
    file_name TEXT NOT NULL,            -- 原始文件名
    size_bytes INTEGER NOT NULL,
    remote_path TEXT,                   -- v0.3+ 拉取前的 VPS 路径；nullable
    local_path TEXT,                    -- 拉到本地后的最终路径；失败时为 null
    status INTEGER NOT NULL DEFAULT 0,  -- 0=Pending, 1=Received, 2=Failed
    attempt_count INTEGER NOT NULL DEFAULT 0,
    last_error TEXT,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UNIQUE(file_id)
);
CREATE INDEX IF NOT EXISTS idx_sync_for_vps_task_status ON sync_for_vps_task(status);
CREATE INDEX IF NOT EXISTS idx_sync_for_vps_task_created ON sync_for_vps_task(created_at);
```

**存在 db**：`media.db`（与 `media_files` / `upload_jobs` 同库，业务数据统一）。

### 10.2 状态机

```
[DrainBatchAsync 准备 scp] ─── status=Pending (enqueue 时)
   ├─ scp exit 0                       → status=Received, local_path=<本地路径>
   ├─ scp exit != 0 (解析 stderr)      → status=Failed, last_error=<stderr 片段>
   └─ v0.3 启动时扫 VPS tmp/ 差集      → 重新入队 status=Pending
```

### 10.3 接口（`Data/ISyncForVpsTaskRepository.cs`）

```csharp
public interface ISyncForVpsTaskRepository {
    Task UpsertAsync(SyncForVpsTask task, CancellationToken ct = default);
    Task<SyncForVpsTask?> GetByFileIdAsync(string fileId, CancellationToken ct = default);
    Task<IReadOnlyList<SyncForVpsTask>> GetByStatusAsync(SyncForVpsTaskStatus status, CancellationToken ct = default);
}
```

### 10.4 写入时机（v0.2）

```
DrainBatchAsync(batch):
    Process.Start("scp", ...).WaitForExit
    foreach (f in batch):
        if success:
            repo.UpsertAsync(file_id=f.Id, status=Received, local_path=..., last_error=null)
            NotificationService.Show("公网接收", "已收到 ...")
        else:
            repo.UpsertAsync(file_id=f.Id, status=Failed, local_path=null, last_error=...)
            NotificationService.Show("公网接收失败", "...")
    SSH.NET RunCommand("rm -f tmp/{成功name1} tmp/{成功name2} ...")  // v0.4+ 用原始文件名
    foreach (成功 id): Task.Run(AckFileAsync(...))     // HTTP POST /ack/{id}
```

### 10.5 v0.3 计划：启动扫描兜底（暂未实现）

**目的**：进程崩溃（kill -9 / 断电）时，channel 残留文件丢失；启动时主动扫描 VPS `~/starttooler/tmp/*`，对比表里 status=Received 的 `file_id`，差集 = Pending → 入队重拉。

**v0.4+ 变更**：磁盘不再有 `<id>.bin` / `<id>.meta`，文件名就是原文件名。崩溃恢复需要换思路：
- 方案 A：保留 `<id>.meta` 仅用于崩溃恢复（即使 `.bin` 已改成原文件名），C# 扫 `tmp/*.meta` 拿回 `(id, name)` 映射 → 简单但磁盘上多一份"幽灵 .meta"
- 方案 B：扫描 `tmp/*` 后用文件名查 VPS 进程外存（如 sqlite/leveldb）反查 id → 复杂
- 方案 C：放弃崩溃恢复，relay 重启期间上传的文件由用户重新上传 → 最简单

> 选定：暂用方案 C（用户上传频率低，re-upload 成本可接受）。后续如果重启频率上去再补方案 A。

```
# v0.4 计划（已废弃的写法，留作对照）：
# var pendingOnVps = ssh.RunCommand("ls ~/starttooler/tmp/*.bin").Split('\n')
#     .Select(Path.GetFileNameWithoutExtension).ToList()
# ...
#     // 从 VPS 读 .meta 拿 name → 入队重拉
#     var meta = ssh.RunCommand($"cat ~/starttooler/tmp/{id}.meta")
```

### 10.6 已知陷阱

- **v0.2 进程崩溃会丢 channel 残留** —— sync_for_vps_task 表只有 enqueue+scp 完成后才有记录，崩溃前 enqueue 但 scp 没跑的没记录；v0.3 加启动扫描兜底
- **scp stderr 解析跨 OpenBSD scp / 新版 scp 不一致** —— 解析 `scp: .../tmp/{filename}: <reason>` 行拆失败 ID（v0.4+ 按原文件名匹配）；写入 `last_error` 时截断到 1KB 防撑爆
- **批量通知堆叠** —— 100 张图成功 = 100 张通知卡片堆右下角；v0.3 加「批量接收完成」汇总通知（"已收到 100 个文件，失败 3 个"）

---

## 11. 表结构规范（v0.5+ 强制）

所有新表 / 现有表迁移必须遵守：

### 11.1 字段命名：snake_case

- 全部小写 + 下划线分词（`project_path` / `remote_url` / `created_at`）
- 不允许 PascalCase（`ProjectPath` / `RemoteUrl`）或 camelCase
- 关键词全大写：`CREATE / INDEX / UNIQUE` 仍用 SQL 约定

### 11.2 审计字段：每张业务表必须有 `created_at` / `updated_at`

- 类型：**TEXT**，ISO 8601 "O" 格式 UTC（例 `2026-07-02T03:09:47.0000000Z`）
- 写入约定：C# 端 `SqliteDateTime.ToDb(DateTime.UtcNow)` 或 SQL DEFAULT `(strftime('%Y-%m-%dT%H:%M:%fZ','now'))`
- 读取约定：C# 端 `SqliteDateTime.FromDb(string)`，带 `DateTimeStyles.RoundtripKind`
- **业务时间字段**（`shot_at` / `last_modified` / `uploaded_at` / `scanned_at`）保持 `INTEGER` (unix ms) 不动 —— ffmpeg/FS mtime 天然 unix ms，且 GalleryViewModel 用了 `date(shot_at/1000,'unixepoch','localtime')` 表达式，没必要改

### 11.3 `created_at` 行为：首次创建时间，**永不更新**

- 新行：SQL DEFAULT 或应用层显式写入当前 UTC
- ON CONFLICT DO UPDATE：SET 列表里**不写** `created_at`，保留原值
  - 例：`ON CONFLICT(...) DO UPDATE SET ... created_at = created_at, updated_at = @updatedAt`

### 11.4 `updated_at` 行为：每次写入都刷新

- INSERT：新行由 SQL DEFAULT 兜底
- UPDATE：应用层显式传当前 UTC（`SqliteDateTime.ToDb(DateTime.UtcNow)`）
- 不允许在 SQL 里省略 `updated_at` 然后靠 trigger 维护（避免跨表逻辑分叉）

### 11.5 现有 4 张表合规性

| 表 | 字段命名 | created_at/updated_at | 类型 | 状态 |
|---|---|---|---|---|
| `config` | `key` / `value` / `created_at` / `updated_at` | ✅ | TEXT | v0.5 迁移完成 |
| `media_files` | 全 snake_case | ✅ | TEXT | v0.5 迁移完成 |
| `upload_jobs` | 全 snake_case | ✅ | TEXT | v0.5 迁移完成（v0.4 之前是 INTEGER） |
| `sync_for_vps_task` | 全 snake_case | ✅ | TEXT | v0.5 迁移完成（v0.4 之前是 INTEGER） |

### 11.6 迁移策略

- **缺列**：`ALTER TABLE ADD COLUMN ... DEFAULT ...` 兜底回填（`SqliteMigrations.AddColumnIfMissing`）
- **列名 PascalCase → snake_case**：`ALTER TABLE RENAME COLUMN`（SQLite 3.25+ 自带）
- **类型不对**（INTEGER → TEXT）：SQLite ALTER 不支持改类型 → 整表重建
  1. `RENAME TO ..._legacy`
  2. 重新建表
  3. `INSERT INTO ... SELECT ..., strftime('%Y-%m-%dT%H:%M:%fZ', created_at/1000, 'unixepoch') FROM ..._legacy`
  4. 重建索引
  5. `DROP ..._legacy`

### 11.7 工具方法

`Data/SqliteHelpers.cs`：
- `SqliteDateTime.ToDb(DateTime)` / `FromDb(string)` —— ISO 8601 round-trip
- `SqliteMigrations.ColumnExists` / `GetColumnType` —— PRAGMA 检测
- `SqliteMigrations.RenameColumnIfExists` / `AddColumnIfMissing` —— 幂等迁移

### 11.8 已知陷阱

- **SQLite 没有原生 datetime 类型** —— "datetime" 在 SQLite 里是 type affinity（NUMERIC），不是强类型。我们用 TEXT + ISO 8601 是最稳的方案。
- **ISO 8601 字符串比较 ≡ 时间序** —— `created_at > '2026-07-01'` 等价于「时间晚于 2026-07-01」，ORDER BY / INDEX 都正确。
- **跨时区** —— 统一存 UTC，UI 层再做时区转换。SQLite 内置 `strftime` 也以 UTC 为基准。
- **ON CONFLICT DO UPDATE 的 created_at 陷阱** —— SQLite 在 DO UPDATE 分支会先按 INSERT 计算所有 DEFAULT（含 `strftime('now')`），然后只覆盖 SET 列表里的列。如果 SET 里**不写** `created_at`，保留的是「刚才算出来的当前时间」而不是「原行的 created_at」。**必须显式写 `created_at = created_at`** 强制保留。

