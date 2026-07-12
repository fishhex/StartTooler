# 0.12 — 跨设备云端同步

> 对应需求文档 `doc/0.12/demand/02-cross-device-sync.md`。
> 核心改动：引入 ProjectName 跨设备标识、新增 OSS ListObjects、从云端重建索引流程。

---

## 1. 模块边界

```
┌──────────────────────────────────────────────────────┐
│                    跨设备同步流程                       │
│                                                      │
│  旧设备                                               │
│  Settings → ExportConfig → starttooler-config.json    │
│    ├─ oss（凭据）                                      │
│    ├─ project（CurrentDirectory + ProjectName）        │
│    └─ app / ai / ...                                 │
│                                                      │
│  新设备                                               │
│  Settings → ImportConfig                              │
│    → ProjectName 恢复                                  │
│    → OSS 凭据恢复                                      │
│    → 用户设置新的本地目录                                │
│    → Gallery：「从云端恢复」                             │
│        → ListObjects(PathPrefix)                        │
│        → 逐条写入 media_files（local_exists=0）          │
│        → Gallery 显示"云端有、本地无"                     │
│        → 批量下载到本地                                  │
└──────────────────────────────────────────────────────┘

依赖链：
  GalleryViewModel
    ├─ IMediaRepository（新增 RebuildFromCloudAsync）
    ├─ IOssStorage（新增 ListObjectsAsync）
    ├─ IConfigService（读 ProjectName / OssConfig）
    └─ 现有 DownloadToLocalCoreAsync（复用）
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Services/ProjectConfig.cs` | 新增 `ProjectName` 字段 | 修改 |
| `Data/MediaFile.cs` | 新增 `ProjectName` 属性 | 修改 |
| `Data/MediaRepository.cs` | 新增 `project_name` 列迁移 + 新增 `RebuildFromCloudAsync` + `GetAllUploadedAsync` 方法 | 修改 |
| `Data/IMediaRepository.cs` | 新增接口方法声明 | 修改 |
| `Services/IOssStorage.cs` | 新增 `ListObjectsAsync` + `OssObjectInfo` 模型 | 修改 |
| `Services/AliyunOssStorage.cs` | 实现 `ListObjectsAsync`（阿里云 SDK `ListObjects`） | 修改 |
| `ViewModels/GalleryViewModel.cs` | ① 新增 `SyncFromCloudCommand` ② 新增 `DownloadAllCloudCommand` ③ 初始化时回填 `project_name` | 修改 |
| `ViewModels/SettingsViewModel.cs` | 新增 `ProjectName` 绑定 + 编辑 | 修改 |
| `Views/SettingsView.axaml` | 新增 ProjectName 输入框 | 修改 |
| `ViewModels/MainWindowViewModel.cs` | OSS 状态评估中接入 ProjectName | 修改 |

> **不引入新 NuGet 包。不新增文件。**

---

## 3. 数据层改动

### 3.1 ProjectConfig 新增 ProjectName

[ProjectConfig.cs](file:///Users/hex/code/StartTooler/StartTooler/Services/ProjectConfig.cs)：

```csharp
public class ProjectConfig
{
    public string? CurrentDirectory { get; set; }
    public string? ProjectName { get; set; }        // 新增
    public List<string> RecentDirectories { get; set; } = new();
}
```

不新增 ConfigKey——`ProjectName` 随 `project` key 一起持久化，JSON 结构中多一个字段是向前兼容的（旧配置反序列化时 ProjectName 为 null）。

### 3.2 media_files 表新增 project_name 列

[MediaRepository.cs](file:///Users/hex/code/StartTooler/StartTooler/StartTooler/Data/MediaRepository.cs) `EnsureDatabase()` 新增迁移：

```csharp
// v0.12: 跨设备项目标识（spec 01-cross-device-sync.md §3.2）
SqliteMigrations.AddColumnIfMissing(
    connection, "media_files", "project_name", "TEXT");
```

`MediaFile.cs` 新增属性：

```csharp
/// <summary>
/// v0.12: 跨设备项目标识。用于跨设备索引重建时的分组匹配。
/// NULL = 旧数据尚未回填（扫描时自动补）。
/// </summary>
public string? ProjectName { get; set; }
```

`ReadMediaFileRow` 中新增读取此列（SELECT 列序 +1，索引 22）。

### 3.3 旧数据回填

`ScanDirectoryAsync` 中，INSERT 和 ON CONFLICT DO UPDATE 时新增 `project_name` 参数。值为 `ProjectConfig.ProjectName`，传参方式同 `project_path`。

初始化回填 SQL（可选独立方法，在 `EnsureDatabase` 末尾调用）：

```csharp
// 旧行 project_name 为空时，用同 project_path 的最新行的 project_name 回填
// 或从 ProjectConfig 读取后批量 UPDATE
UPDATE media_files 
SET project_name = @projectName 
WHERE project_name IS NULL AND project_path = @projectPath
```

---

## 4. OSS 接口改动

### 4.1 新增 OssObjectInfo 模型

[IOssStorage.cs](file:///Users/hex/code/StartTooler/StartTooler/StartTooler/Services/IOssStorage.cs)：

```csharp
/// <summary>OSS 对象摘要，用于 ListObjectsAsync 返回。</summary>
public sealed class OssObjectInfo
{
    /// <summary>对象 key（相对 bucket 根，已包含前缀）。</summary>
    public string Key { get; init; } = "";

    /// <summary>对象大小（字节）。</summary>
    public long Size { get; init; }

    /// <summary>最后修改时间（UTC）。</summary>
    public DateTime LastModified { get; init; }
}
```

### 4.2 IOssStorage 新增方法

```csharp
/// <summary>
/// 列出指定前缀下的所有对象。
/// 用于跨设备同步时重建本地 media_files 索引。
/// </summary>
/// <param name="prefix">对象 key 前缀（如 "deepsky-2025/"）</param>
/// <param name="ct">取消令牌</param>
/// <returns>对象列表（按 key 排序）</returns>
Task<IReadOnlyList<OssObjectInfo>> ListObjectsAsync(string prefix, CancellationToken ct = default);
```

### 4.3 AliyunOssStorage 实现

[AliyunOssStorage.cs](file:///Users/hex/code/StartTooler/StartTooler/StartTooler/Services/AliyunOssStorage.cs) 新增：

```csharp
public async Task<IReadOnlyList<OssObjectInfo>> ListObjectsAsync(string prefix, CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();

    var results = new List<OssObjectInfo>();

    await Task.Run(() =>
    {
        string? marker = null;
        do
        {
            var req = new ListObjectsRequest(_config.Bucket)
            {
                Prefix = prefix,
                Marker = marker,
                MaxKeys = 100,
            };
            var resp = _client.ListObjects(req);

            foreach (var obj in resp.ObjectSummaries)
            {
                results.Add(new OssObjectInfo
                {
                    Key = obj.Key,
                    Size = obj.Size,
                    LastModified = obj.LastModified,
                });
            }

            marker = resp.NextMarker;
        }
        while (!string.IsNullOrEmpty(marker));
    }, ct);

    Debug.WriteLine($"[OSS] ListObjectsAsync: prefix='{prefix}', count={results.Count}");
    return results;
}
```

---

## 5. 从云端重建索引

### 5.1 MediaRepository 新增方法

```csharp
// IMediaRepository

/// <summary>
/// v0.12: 从 OSS 对象列表重建本地索引。
/// 用 (project_name, relative_path) 做去重——已有记录跳过，新记录写入（local_exists=0）。
/// </summary>
/// <returns>新增的文件数量。</returns>
Task<int> RebuildFromCloudAsync(
    string projectName,
    string projectPath,
    string pathPrefix,
    IReadOnlyList<OssObjectInfo> objects,
    CancellationToken ct = default);
```

实现：

```csharp
public async Task<int> RebuildFromCloudAsync(
    string projectName, string projectPath, string pathPrefix,
    IReadOnlyList<OssObjectInfo> objects, CancellationToken ct)
{
    var added = 0;

    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    await using var tx = await connection.BeginTransactionAsync(ct);

    // 先查现有 (project_name + relative_path) 对，用于去重
    var existingSql = "SELECT relative_path FROM media_files WHERE project_name = @pn";
    var existingPaths = new HashSet<string>();
    await using (var cmd = new SqliteCommand(existingSql, connection, tx))
    {
        cmd.Parameters.AddWithValue("@pn", projectName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            existingPaths.Add(reader.GetString(0));
        }
    }

    var insertSql = @"
        INSERT INTO media_files (project_path, project_name, relative_path, file_name,
            media_type, file_size, last_modified, shot_at, is_uploaded, local_exists,
            remote_url, uploaded_at, scanned_at, created_at, updated_at)
        VALUES (@projectPath, @projectName, @relativePath, @fileName,
            @mediaType, @fileSize, @lastModified, @shotAt, 1, 0,
            @remoteUrl, @uploadedAt, @scannedAt, @createdAt, @updatedAt)";

    var now = DateTime.UtcNow;
    var nowIso = SqliteDateTime.ToDb(now);
    var nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();

    // 用于从 objectKey 剥离前缀得到 relative_path
    var prefix = (pathPrefix ?? "").TrimEnd('/') + "/";

    foreach (var obj in objects)
    {
        // 从 objectKey 解析 relative_path：去掉 PathPrefix 前缀
        var relativePath = obj.Key.StartsWith(prefix)
            ? obj.Key.Substring(prefix.Length)
            : obj.Key;

        if (existingPaths.Contains(relativePath)) continue;

        var fileName = Path.GetFileName(relativePath);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var mediaType = IsImageExtension(ext) ? 0 : 1;

        await using var cmd = new SqliteCommand(insertSql, connection, tx);
        cmd.Parameters.AddWithValue("@projectPath", projectPath);
        cmd.Parameters.AddWithValue("@projectName", projectName);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);
        cmd.Parameters.AddWithValue("@fileName", fileName);
        cmd.Parameters.AddWithValue("@mediaType", mediaType);
        cmd.Parameters.AddWithValue("@fileSize", obj.Size);
        cmd.Parameters.AddWithValue("@lastModified", new DateTimeOffset(obj.LastModified).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@shotAt", new DateTimeOffset(obj.LastModified).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@remoteUrl", $"oss://{obj.Key}");
        cmd.Parameters.AddWithValue("@uploadedAt", nowMs);
        cmd.Parameters.AddWithValue("@scannedAt", nowMs);
        cmd.Parameters.AddWithValue("@createdAt", nowIso);
        cmd.Parameters.AddWithValue("@updatedAt", nowIso);

        await cmd.ExecuteNonQueryAsync(ct);
        added++;
    }

    await tx.CommitAsync(ct);
    return added;
}
```

### 5.2 GalleryViewModel 主流程

```csharp
// === 从云端恢复（新设备首次使用 / 双设备同步） ===

[RelayCommand]
private async Task SyncFromCloud()
{
    // 1. 读配置
    var projectCfg = await _configService.GetAsync<ProjectConfig>(ConfigKeys.Project);
    var ossCfg = await _configService.GetAsync<OssConfig>(ConfigKeys.Oss);

    var projectName = projectCfg?.ProjectName;
    var pathPrefix = ossCfg?.PathPrefix;

    if (string.IsNullOrWhiteSpace(pathPrefix))
    {
        ShowToast("请先在设置页配置 OSS 路径前缀（PathPrefix）");
        return;
    }

    if (string.IsNullOrEmpty(ProjectPath))
    {
        ShowToast("请先选择本地项目目录");
        return;
    }

    // 2. OSS 配置检查
    var storage = _ossFactory.TryCreate();
    if (storage == null)
    {
        await PromptOssNotConfiguredAsync();
        return;
    }

    var displayName = projectName ?? pathPrefix;
    ShowToast($"正在从云端同步 {displayName}…");

    try
    {
        // 3. ListObjects（用 PathPrefix 作为前缀）
        var objects = await storage.ListObjectsAsync(
            pathPrefix.TrimEnd('/') + "/",
            _cts?.Token ?? default);

        if (objects.Count == 0)
        {
            ShowToast($"云端路径 '{pathPrefix}' 下没有找到文件");
            return;
        }

        // 4. 重建索引
        var displayProjectName = projectName ?? pathPrefix;
        var added = await _mediaRepo.RebuildFromCloudAsync(
            displayProjectName, ProjectPath, pathPrefix, objects);

        // 5. 刷新 Gallery
        await InitializeAsync();

        ShowToast($"从云端同步完成：新增 {added} 个文件索引（共 {objects.Count} 个云端文件）");
    }
    catch (OperationCanceledException)
    {
        ShowToast("已取消同步");
    }
    catch (Exception ex)
    {
        ShowToast($"同步失败：{ex.Message}");
    }
}
```

---

## 6.「下载全部云端文件」

在现有 `BatchDownload` 基础上新增全量下载（不限于当前选中的文件）：

```csharp
[RelayCommand]
private async Task DownloadAllCloud()
{
    if (string.IsNullOrEmpty(ProjectPath)) return;

    // 列出所有 is_uploaded=true && local_exists=false 的文件
    var allFiles = await _mediaRepo.GetAllUploadedMissingLocalAsync(ProjectPath);
    if (allFiles.Count == 0)
    {
        ShowToast("所有云端文件已在本地");
        return;
    }

    var window = DialogHelper.GetMainWindow();
    if (window == null) return;

    var confirmed = await DialogHelper.ShowConfirmAsync(
        window,
        title: "下载全部云端文件",
        message: $"将从云端下载 {allFiles.Count} 个文件，继续？",
        primaryButtonText: "下载",
        secondaryButtonText: "取消");

    if (!confirmed) return;

    // 复用现有逐个下载逻辑
    _downloadCts?.Dispose();
    _downloadCts = new CancellationTokenSource();
    var ct = _downloadCts.Token;

    var completed = 0;
    var failed = 0;
    try
    {
        foreach (var file in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ShowToast($"下载中 {completed + failed + 1}/{allFiles.Count}：{file.FileName}");
                var success = await DownloadToLocalCoreAsync(file, ct);
                if (success) completed++; else failed++;
            }
            catch (OperationCanceledException) { break; }
            catch { failed++; }
        }
    }
    finally
    {
        _downloadCts?.Dispose();
        _downloadCts = null;
    }

    ShowToast($"下载完成：{completed}/{allFiles.Count} 成功" + (failed > 0 ? $"，{failed} 失败" : ""));
}
```

`IMediaRepository` 新增：

```csharp
Task<IReadOnlyList<MediaFile>> GetAllUploadedMissingLocalAsync(string projectPath, CancellationToken ct = default);
```

---

## 7. SettingsViewModel 改动

### 7.1 新增字段

```csharp
// General Tab 新增
[ObservableProperty]
private string? projectName;

private string? _lastSavedProjectName;
```

### 7.2 脏状态跟踪

`IsDirty` 计算加入 `projectName != _lastSavedProjectName`。

### 7.3 加载和保存

`InitializeAsync` / `ReloadFromConfigAsync` 中读 `ProjectConfig.ProjectName`。`SaveAsync` 中写回。

### 7.4 默认值生成

首次进入设置页且 `ProjectName` 为空时，根据 `SelectedProjectDirectory` 的目录名自动填充默认值：

```csharp
if (string.IsNullOrWhiteSpace(ProjectName) && !string.IsNullOrWhiteSpace(SelectedProjectDirectory))
{
    ProjectName = Path.GetFileName(SelectedProjectDirectory.TrimEnd('/').TrimEnd('\\'));
}
```

---

## 8. 边界情况

| 场景 | 处理 |
|------|------|
| PathPrefix 未配置时点击「从云端恢复」 | Toast "请先在设置页配置 OSS 路径前缀（PathPrefix）" |
| 云端 ListObjects 返回空 | Toast "云端路径下没有找到文件" |
| RebuildFromCloud 时 DB 写失败 | 事务回滚，Toast 错误信息 |
| 未配置 OSS 时点击「从云端恢复」 | 弹 OSS 未配置对话框（复用现有 `PromptOssNotConfiguredAsync`） |
| 未设置 ProjectName 时点击「从云端恢复」 | 以 PathPrefix 作为 displayProjectName 兜底 |
| 未选择本地目录时点击「从云端恢复」 | Toast "请先选择本地项目目录" |
| 从云端恢复后 `shot_at` 为空 | 用 OSS 对象 `LastModified` 作为 `shot_at` |
| 云端文件 `relative_path` 含特殊字符 | `BuildObjectKey` 已做 `Replace('\\', '/').TrimStart('/')` |
| 并发操作：恢复中用户点击刷新 | `_cts` 取消旧的 ListObjects + Rebuild |
| media_files 表 `project_name` 列迁移失败 | `AddColumnIfMissing` 幂等，重复执行不报错 |

---

## 9. 与现有系统的关系

### 9.1 不新增数据库迁移

`AddColumnIfMissing` 机制已存在，新增 `project_name` 列为 NULLABLE TEXT，旧行不报错。

### 9.2 不影响现有上传/下载流程

OSS objectKey 构建规则不变，仍然使用 `PathPrefix`。上传、下载、云端删除等所有 OSS 操作不受影响。

### 9.3 不影响配置导出/导入

`ProjectConfig.ProjectName` 是 JSON 多一个字段，旧版本导入时会忽略（反序列化安全），新版本导出时包含。

### 9.4 不影响 Gallery 现有查询

`project_path` 保留为 Gallery 查询的主键，`project_name` 仅用于跨设备身份标识和索引重建去重。现有所有 `WHERE project_path = @projectPath` 查询不动。

### 9.5 不影响 Trash / UploadServer / PublicRelay

OSS key 构建不变，这些模块无需任何修改。

### 9.6 不动 OssConfig

`OssConfig` 所有字段完全不动。`PathPrefix` 仍然是 OSS 对象 key 的唯一前缀来源。

---

## 10. 实施步骤

| 步骤 | 内容 | 影响范围 |
|------|------|---------|
| 1 | `ProjectConfig` 新增 `ProjectName` | `ProjectConfig.cs` |
| 2 | `media_files` 表新增 `project_name` 列 + `MediaFile` 新增属性 | `MediaRepository.cs`、`MediaFile.cs` |
| 3 | `IOssStorage` 新增 `ListObjectsAsync` + `OssObjectInfo` | `IOssStorage.cs` |
| 4 | `AliyunOssStorage` 实现 `ListObjectsAsync` | `AliyunOssStorage.cs` |
| 5 | `IMediaRepository` + `MediaRepository` 新增 `RebuildFromCloudAsync` / `GetAllUploadedMissingLocalAsync` | `IMediaRepository.cs`、`MediaRepository.cs` |
| 6 | `GalleryViewModel` 新增 `SyncFromCloudCommand` + `DownloadAllCloudCommand` | `GalleryViewModel.cs` |
| 7 | `SettingsViewModel` 新增 `ProjectName` 绑定 | `SettingsViewModel.cs`、`SettingsView.axaml` |
| 8 | Gallery 初始化时回填旧数据的 `project_name` | `GalleryViewModel.InitializeAsync` |
| 9 | 端到端测试：导出配置 → 新设备导入 → 从云端恢复 → 下载 | — |
