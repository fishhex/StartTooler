# S03 Phase 2 — 数据层实现计划

> 关联：`spec/03-shooting-diary.md`（主规格）、`spec/03-shooting-diary-code-structure.md`（代码结构）

---

## 目标

新建 Session 数据模型 + 仓储 + DB 迁移，扩展 MediaRepository 支持会话关联查询。

---

## Step 1: Models/Session.cs

**文件：** `StartTooler/Models/Session.cs`（新建）

### 1.1 字段

| 字段 | 类型 | DB 列 | 说明 |
|------|------|-------|------|
| Id | `string` | `id TEXT PK` | `Guid.NewGuid().ToString("N")` |
| ProjectPath | `string` | `project_path TEXT` | 项目路径 |
| Title | `string` | `title TEXT` | 自动生成，用户可改 |
| Description | `string` | `description TEXT` | 笔记 |
| StartTime | `DateTime` | `start_time TEXT` | 会话起始时间 |
| EndTime | `DateTime` | `end_time TEXT` | 会话结束时间 |
| Location | `string` | `location TEXT` | 地点 |
| WindDir | `string` | `wind_dir TEXT` | 风向 |
| WindLevel | `int` | `wind_level INTEGER` | 风级 0-12 |
| CloudCover | `string` | `cloud_cover TEXT` | 云量 |
| Bortle | `int?` | `bortle INTEGER` | 光害等级 1-9 |
| CreatedAt | `DateTime` | `created_at TEXT` | 创建时间 |
| UpdatedAt | `DateTime` | `updated_at TEXT` | 更新时间 |

### 1.2 计算属性

```csharp
public TimeSpan Duration => EndTime - StartTime;

public string DurationText => Duration.TotalHours >= 1
    ? $"{(int)Duration.TotalHours}h{(int)Duration.TotalMinutes % 60}m"
    : $"{(int)Duration.TotalMinutes}m";

public string? WeatherIconKey => CloudCover switch
{
    "晴" => "Icon.Weather.Sunny",
    "少云" => "Icon.Weather.PartlyCloudy",
    "多云" => "Icon.Weather.Cloudy",
    "阴" => "Icon.Weather.Overcast",
    "雨" => "Icon.Weather.Rain",
    "雪" => "Icon.Weather.Snow",
    "雾" => "Icon.Weather.Fog",
    _ => null
};

public string? WeatherText => string.IsNullOrEmpty(CloudCover) ? null
    : string.IsNullOrEmpty(WindDir) ? CloudCover
    : $"{WindDir}风 {WindLevel}级 · {CloudCover}";
```

### 1.3 实现要点

- 使用 `sealed class`（同 MediaFile）
- 属性全部 `{ get; set; }`（简单 POCO，不继承 ObservableObject）
- 放在 `StartTooler.Models` 命名空间

---

## Step 2: Models/SessionStats.cs

**文件：** `StartTooler/Models/SessionStats.cs`（新建）

### 2.1 字段

```csharp
public sealed class SessionStats
{
    public int TotalPhotos { get; init; }
    public int TargetCount { get; init; }
    public double TotalExposureHours { get; init; }
    public IReadOnlyList<string> TopTags { get; init; } = Array.Empty<string>();
}
```

### 2.2 实现要点

- 纯数据对象，`{ get; init; }` 不可变
- 放在 `StartTooler.Models` 命名空间

---

## Step 3: Data/ISessionRepository.cs

**文件：** `StartTooler/Data/ISessionRepository.cs`（新建）

### 3.1 接口

```csharp
namespace StartTooler.Data;

public interface ISessionRepository
{
    Task<IReadOnlyList<Session>> GetByProjectAsync(string projectPath, CancellationToken ct = default);
    Task<Session?> GetByIdAsync(string sessionId, CancellationToken ct = default);
    Task UpsertAsync(Session session, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
    Task UpsertBatchAsync(IReadOnlyList<Session> sessions, CancellationToken ct = default);
    Task DeleteBatchAsync(IReadOnlyList<string> sessionIds, CancellationToken ct = default);
}
```

### 3.2 实现要点

- 接口放在 `StartTooler.Data` 命名空间
- 每个方法接受 `CancellationToken ct = default`

---

## Step 4: Data/SessionRepository.cs

**文件：** `StartTooler/Data/SessionRepository.cs`（新建）

### 4.1 构造函数 + 迁移

```csharp
public class SessionRepository : ISessionRepository
{
    private readonly string _connectionString;

    public SessionRepository()
    {
        var dbPath = AppPaths.MediaDbPath;
        _connectionString = $"Data Source={dbPath}";
        EnsureDatabase();
    }
}
```

**EnsureDatabase 迁移逻辑：**

```sql
CREATE TABLE IF NOT EXISTS sessions (
    id          TEXT PRIMARY KEY,
    project_path TEXT NOT NULL,
    title       TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    start_time  TEXT NOT NULL,
    end_time    TEXT NOT NULL,
    location    TEXT NOT NULL DEFAULT '',
    wind_dir    TEXT NOT NULL DEFAULT '',
    wind_level  INTEGER NOT NULL DEFAULT 0,
    cloud_cover TEXT NOT NULL DEFAULT '',
    bortle      INTEGER,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sessions_project ON sessions(project_path);
```

### 4.2 方法实现

**GetByProjectAsync：**
```sql
SELECT * FROM sessions
WHERE project_path = @projectPath
ORDER BY start_time DESC
```

**GetByIdAsync：**
```sql
SELECT * FROM sessions WHERE id = @id
```

**UpsertAsync：**
```sql
INSERT INTO sessions (id, project_path, title, description, start_time, end_time,
    location, wind_dir, wind_level, cloud_cover, bortle, created_at, updated_at)
VALUES (@id, @projectPath, @title, @description, @startTime, @endTime,
    @location, @windDir, @windLevel, @cloudCover, @bortle, @createdAt, @updatedAt)
ON CONFLICT(id) DO UPDATE SET
    title = excluded.title,
    description = excluded.description,
    start_time = excluded.start_time,
    end_time = excluded.end_time,
    location = excluded.location,
    wind_dir = excluded.wind_dir,
    wind_level = excluded.wind_level,
    cloud_cover = excluded.cloud_cover,
    bortle = excluded.bortle,
    updated_at = excluded.updated_at
```

**DeleteAsync：**
```sql
DELETE FROM sessions WHERE id = @id
```

**UpsertBatchAsync：** 事务中循环 Upsert

**DeleteBatchAsync：**
```sql
DELETE FROM sessions WHERE id IN (...)
```

### 4.3 行读取方法

```csharp
private static Session ReadSession(SqliteDataReader reader) => new()
{
    Id = reader.GetString(0),
    ProjectPath = reader.GetString(1),
    Title = reader.GetString(2),
    Description = reader.GetString(3),
    StartTime = SqliteDateTime.FromDb(reader.GetString(4)),
    EndTime = SqliteDateTime.FromDb(reader.GetString(5)),
    Location = reader.GetString(6),
    WindDir = reader.GetString(7),
    WindLevel = reader.GetInt32(8),
    CloudCover = reader.GetString(9),
    Bortle = reader.IsDBNull(10) ? null : reader.GetInt32(10),
    CreatedAt = SqliteDateTime.FromDb(reader.GetString(11)),
    UpdatedAt = SqliteDateTime.FromDb(reader.GetString(12)),
};
```

### 4.4 实现要点

- 构造函数模式同 MediaRepository：`AppPaths.MediaDbPath` → `_connectionString` → `EnsureDatabase()`
- 时间字段使用 `SqliteDateTime.ToDb()` / `SqliteDateTime.FromDb()` 一致约定
- `ON CONFLICT(id) DO UPDATE` 实现幂等 Upsert
- `using` 资源管理（`SqliteConnection` / `SqliteCommand` / `SqliteDataReader`）

---

## Step 5: Data/IMediaRepository.cs 扩展

**文件：** `StartTooler/Data/IMediaRepository.cs`（修改）

### 5.1 新增方法签名

在 `GetByTagAsync` 之后、`GetTagsAsync` 之前插入：

```csharp
// === v0.12: 拍摄日记查询 ===

Task<IReadOnlyList<MediaFile>> GetBySessionAsync(
    string sessionId, SortMode sortMode = SortMode.TimeDesc,
    int offset = 0, int limit = 2000, CancellationToken ct = default);

Task SetDiaryFeaturedAsync(long fileId, bool isFeatured, CancellationToken ct = default);

Task<IReadOnlyList<MediaFile>> GetDiaryFeaturedAsync(
    string sessionId, int limit = 5, CancellationToken ct = default);

Task<SessionStats> GetSessionStatsAsync(string sessionId, CancellationToken ct = default);
```

### 5.2 实现要点

- 需要 `using StartTooler.Models;`（SessionStats）
- 接口方法加 `CancellationToken ct = default`

---

## Step 6: Data/MediaRepository.cs 扩展

**文件：** `StartTooler/Data/MediaRepository.cs`（修改）

### 6.1 DB 迁移：新增列

在 `EnsureDatabase()` 方法末尾（现有 EXIF 列迁移之后）添加：

```csharp
// === v0.12: 拍摄日记会话关联 ===
SqliteMigrations.AddColumnIfMissing(
    connection, "media_files", "session_id",
    "TEXT");
SqliteMigrations.AddColumnIfMissing(
    connection, "media_files", "is_diary_featured",
    "INTEGER DEFAULT 0");

// 索引用 try/catch 包裹：不阻塞新列创建成功后索引创建失败（如并发冲突）
try
{
    using var idxCmd = new SqliteCommand(
        "CREATE INDEX IF NOT EXISTS idx_media_files_session ON media_files(session_id)",
        connection);
    idxCmd.ExecuteNonQuery();
}
catch { /* 索引已存在 */ }
```

### 6.2 GetBySessionAsync

```sql
SELECT * FROM media_files
WHERE session_id = @sessionId
  AND deleted_at IS NULL
ORDER BY {sortModeToSql(sortMode)}
LIMIT @limit OFFSET @offset
```

### 6.3 SetDiaryFeaturedAsync

```sql
UPDATE media_files SET is_diary_featured = @value WHERE id = @id
```

### 6.4 GetDiaryFeaturedAsync

```sql
SELECT * FROM media_files
WHERE session_id = @sessionId
  AND is_diary_featured = 1
  AND deleted_at IS NULL
ORDER BY score DESC
LIMIT @limit
```

### 6.5 GetSessionStatsAsync

单次 SQL 聚合查询返回 SessionStats：

```sql
SELECT
    COUNT(*) AS total_photos,
    COUNT(DISTINCT t.id) AS target_count,
    COALESCE(SUM(exposure_time), 0) / 3600.0 AS total_exposure_hours,
    (SELECT GROUP_CONCAT(name, ',') FROM (
        SELECT t2.name
        FROM tags t2
        WHERE t2.project_path = m.project_path
          AND EXISTS (
            SELECT 1 FROM json_each(m.tags)
            WHERE json_each.value = t2.id
          )
        GROUP BY t2.name
        ORDER BY COUNT(*) DESC
        LIMIT 3
    )) AS top_tags
FROM media_files m
WHERE m.session_id = @sessionId
  AND m.deleted_at IS NULL
```

### 6.6 实现要点

- 复用现有 `ReadMediaFileRow()` 方法读取行
- 复用现有 `SortMode` 排序逻辑
- JSON 数组中 `json_each` 用于统计标签
- 时间字段使用 `SqliteDateTime` 格式

---

## Step 7: Data/MediaFile.cs 扩展

**文件：** `StartTooler/Data/MediaFile.cs`（修改）

### 7.1 新增属性

在已有的 `QualityTags` 等属性之后添加：

```csharp
[ObservableProperty] private string? _sessionId;
[ObservableProperty] private bool _isDiaryFeatured;
```

### 7.2 实现要点

- 使用 `[ObservableProperty]` source generator（同现有属性）
- `sessionId` 可为 null（未关联会话的照片）
- `isDiaryFeatured` 默认 false

---

## Step 8: 编译验证

### 8.1 验证清单

- [ ] `dotnet build` 0 错误 0 警告
- [ ] 所有新文件在项目中正确引用（`.csproj` 默认包含所有 `.cs` 文件）
- [ ] `IMediaRepository` 接口与 `MediaRepository` 实现一致
- [ ] `ISessionRepository` 接口与 `SessionRepository` 实现一致

### 8.2 编译命令

```bash
dotnet build StartTooler/StartTooler.csproj
```

---

## 文件变更清单

| 操作 | 文件 | 行数(估) |
|------|------|----------|
| 新建 | `Models/Session.cs` | ~60 |
| 新建 | `Models/SessionStats.cs` | ~15 |
| 新建 | `Data/ISessionRepository.cs` | ~20 |
| 新建 | `Data/SessionRepository.cs` | ~180 |
| 修改 | `Data/IMediaRepository.cs` | +12 |
| 修改 | `Data/MediaRepository.cs` | +100 |
| 修改 | `Data/MediaFile.cs` | +3 |

---

## 依赖关系

```
SessionRepository ──→ Session
MediaRepository  ──→ SessionStats, MediaFile (扩展)
```

无外部服务依赖，Phase 2 后可独立编译验证。