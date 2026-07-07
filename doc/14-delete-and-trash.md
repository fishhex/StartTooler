# 14 — 删除、垃圾筒、释放本地空间与下载到本地

> 本规范覆盖四个独立操作：**软删除**（→垃圾筒）、**彻底删除**（从垃圾筒）、**释放本地空间**（Gallery 侧）和**下载到本地**（Gallery / 垃圾筒）。待实装。

---

## 1. 概念分离

| 概念 | 触发位置 | 文件操作 | DB 操作 | 可恢复 |
|------|---------|---------|---------|--------|
| **软删除** | Gallery → 删除按钮 | 不删文件 | `UPDATE deleted_at = now` | ✅ 可恢复 |
| **彻底删除** | 垃圾筒 → 清理 | 删本地 / 云上 | `DELETE FROM media_files` | ❌ 不可恢复 |
| **释放本地空间** | Gallery → 工具栏 / 右键 | 仅删本地文件 | `UPDATE local_exists = 0` | ✅ 可重新下载 |
| **下载到本地** | Gallery / 垃圾筒 → 右键 / 按钮 | 从 OSS 拉取文件到本地 | `UPDATE local_exists = 1` | — （不涉及删除） |

四者互不交叉：释放本地空间不过垃圾筒；软删除不释放磁盘；彻底删除是唯一会动 OSS 的路径；下载到本地是纯拉取操作。

---

## 2. 数据层

### 2.1 Schema 变更

`media_files` 表新增一列：

```sql
deleted_at INTEGER   -- NULL = 正常文件，NOT NULL = unix ms 时间戳标记已删除
```

迁移方式：`SqliteMigrations.AddColumnIfMissing(connection, "media_files", "deleted_at", "INTEGER")`，与现有 `score` / `tagged_at` 同模式。

### 2.2 现有查询过滤

所有 Gallery 查询必须加 `deleted_at IS NULL` 过滤，涉及的 SQL 位置：

| 方法 | 新的 WHERE 子句 |
|------|----------------|
| `GetDateGroupsAsync` | `WHERE project_path = @p AND shot_at IS NOT NULL AND deleted_at IS NULL` |
| `GetByDateAsync` | `WHERE project_path = @p AND shot_at >= @s AND shot_at < @e AND deleted_at IS NULL` |
| `GetByTagAsync` | `WHERE project_path = @p AND tags LIKE @t AND deleted_at IS NULL` |
| `GetTagGroupsAsync` | `WHERE project_path = @p AND tags IS NOT NULL AND tags != '[]' AND deleted_at IS NULL` |

`ScanDirectoryAsync` 只管 INSERT/UPDATE，不涉及 `deleted_at` 过滤。

### 2.3 新增 Repository 方法

```csharp
// IMediaRepository 新增（Data/IMediaRepository.cs）

/// <summary>软删除：标记 deleted_at = now（unix ms），不删文件。</summary>
Task SoftDeleteAsync(long fileId, long deletedAt, CancellationToken ct = default);

/// <summary>恢复：deleted_at = NULL，回到 Gallery。</summary>
Task RestoreAsync(long fileId, CancellationToken ct = default);

/// <summary>彻底删除：DELETE FROM media_files WHERE id = @id。</summary>
Task PermanentDeleteAsync(long fileId, CancellationToken ct = default);

/// <summary>获取某项目下所有已删除文件。</summary>
Task<IReadOnlyList<MediaFile>> GetDeletedAsync(string projectPath, CancellationToken ct = default);

/// <summary>更新 local_exists 标记。</summary>
Task UpdateLocalExistsAsync(long fileId, bool exists, CancellationToken ct = default);
```

`MediaRepository.cs` 实现：

- `SoftDeleteAsync`：`UPDATE media_files SET deleted_at = @t, updated_at = @now WHERE id = @id`
- `RestoreAsync`：`UPDATE media_files SET deleted_at = NULL, updated_at = @now WHERE id = @id`
- `PermanentDeleteAsync`：`DELETE FROM media_files WHERE id = @id AND deleted_at IS NOT NULL`
- `GetDeletedAsync`：完整 SELECT 所有列（复用 `ReadMediaFileRow`），`WHERE project_path = @p AND deleted_at IS NOT NULL ORDER BY deleted_at DESC`
- `UpdateLocalExistsAsync`：`UPDATE media_files SET local_exists = @e, updated_at = @now WHERE id = @id`

---

## 3. OSS 接口扩展

### 3.1 `IOssStorage` 新增

```csharp
/// <summary>从 OSS 删除单个对象。调用方确认桶内对象存在。</summary>
Task DeleteObjectAsync(string objectKey, CancellationToken ct = default);
```

### 3.2 `AliyunOssStorage` 实现

```csharp
public async Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();
    await Task.Run(() => _client.DeleteObject(_config.Bucket, objectKey), ct);
}
```

阿里云 SDK `DeleteObject` 删除不存在的 key 不会抛异常（幂等），无需额外检查。

---

## 4. MediaFile 模型变更

`Data/MediaFile.cs` 新增：

```csharp
/// <summary>软删除时间戳（unix ms），NULL = 未删除。</summary>
public long? DeletedAt { get; set; }
```

`ReadMediaFileRow` 在 SELECT 列序末尾追加 `deleted_at`，**现有列序不变**——SELECT 列序在 `GetByDateAsync` / `GetByTagAsync` 的 SQL 模板中显式列出，所以新增列加在末尾即可，不影响已有列索引。

---

## 5. 软删除流程（Gallery 侧）

### 5.1 BatchDelete（工具栏「删除」按钮）

```
BatchDelete()
  ├─ if !IsBatchActionEnabled → return
  ├─ files = SelectedFiles.ToList(), count = files.Count
  ├─ DialogHelper.ShowConfirm("确定将 N 个文件移入垃圾筒？")
  ├─ if no → return
  ├─ nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  ├─ foreach file in files:
  │     await _mediaRepo.SoftDeleteAsync(file.Id, nowMs)
  ├─ 从 CurrentMediaFiles 移除所有 files
  ├─ ExitMultiSelect()
  └─ ShowToast($"已将 {count} 个文件移入垃圾筒")
```

### 5.2 DeleteSingle（右键「删除」）

```
DeleteSingle(file)
  ├─ if file == null → return
  ├─ DialogHelper.ShowConfirm($"确定将 {file.FileName} 移入垃圾筒？")
  ├─ if no → return
  ├─ await _mediaRepo.SoftDeleteAsync(file.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
  ├─ 从 CurrentMediaFiles 移除
  └─ ShowToast($"已将 {file.FileName} 移入垃圾筒")
```

### 5.3 右键菜单扩展

当前右键菜单仅「AI 打标」（`GalleryView.axaml:222-224`）。新增两项：

```xml
<MenuItem Header="删除"
          Command="{Binding ... DeleteSingleCommand}"
          CommandParameter="{Binding}"/>
<MenuItem Header="释放本地空间"
          Command="{Binding ... FreeUpSpaceCommand}"
          CommandParameter="{Binding}"
          IsVisible="{Binding IsUploaded}"/>  <!-- 仅 IsUploaded 控制：LocalExists 在命令内二次判断 -->
<MenuItem Header="下载到本地"
          Command="{Binding ... DownloadSingleCommand}"
          CommandParameter="{Binding}"
          IsVisible="{Binding IsUploaded}"/>  <!-- 仅 IsUploaded 控制：LocalExists 在命令内二次判断 -->
```

---

## 6. 释放本地空间（Gallery 侧独立操作）

### 6.1 FreeUpSpace 命令

```
FreeUpSpace(file)
  ├─ if !file.IsUploaded → return  // 不适用于未上传文件
  ├─ if !file.LocalExists → return   // 本地已经不在了
  ├─ fullPath = Path.Combine(file.ProjectPath, file.RelativePath)
  ├─ if File.Exists(fullPath):
  │     File.Delete(fullPath)
  ├─ file.LocalExists = false
  ├─ await _mediaRepo.UpdateLocalExistsAsync(file.Id, false)
  └─ ShowToast("已释放本地空间")
```

退化的缩略图：`local_exists=0` → `FilePathToBitmapConverter` 找不到源文件 → 自动回退占位图标（现有行为，无需改动）。

### 6.2 批量释放

多选模式下可批量释放：`SelectedFiles.Where(f => f.IsUploaded && f.LocalExists)` 逐一执行。

```
BatchFreeUpSpace()
  ├─ files = SelectedFiles.Where(f => f.IsUploaded && f.LocalExists).ToList()
  ├─ if files.Count == 0 → ShowToast("所选文件无需释放") → return
  ├─ DialogHelper.ShowConfirm($"将删除 {files.Count} 个文件（云端保留）")
  ├─ if no → return
  ├─ foreach file in files:
  │     DeleteLocalFile(file)
  │     file.LocalExists = false
  │     await _mediaRepo.UpdateLocalExistsAsync(file.Id, false)
  ├─ ExitMultiSelect()
  └─ ShowToast($"已释放 {files.Count} 个文件")
```

---

## 7. 下载到本地

### 7.1 概念

“下载到本地”是一个与「打开文件」解耦的独立操作——用户可以在不打开文件的情况下，将云端文件拉回本地磁盘。适用于 Gallery 和垃圾筒两个场景。

| 场景 | 触发条件 | 入口 |
|------|---------|------|
| Gallery | `IsUploaded && !LocalExists` | 右键菜单「下载到本地」、双击自动触发（兼容现有行为） |
| 垃圾筒 | `IsUploaded && !LocalExists` | 卡片底部「下载」按钮 |

下载完成后 Gallery 中该文件恢复正常预览（`local_exists=1` → 缩略图再生），垃圾筒中该文件**不自动恢复**（仍在垃圾筒，用户需单独点「恢复」）。

### 7.2 提取共享下载核心逻辑

当前 `OpenFileAsync`（`GalleryViewModel.cs:1271`）的实现内联了下载逻辑。需将下载核心抽为 `GalleryViewModel` 的 private 方法，供 Gallery 内部复用；垃圾筒侧由 `TrashViewModel` 独立实现（两者共享依赖注入的 `IOssStorageFactory` / `IConfigService` / `IThumbnailService`）。

```csharp
// GalleryViewModel.cs 新增 private helper
private async Task<bool> DownloadToLocalCoreAsync(MediaFile file, CancellationToken ct = default)
{
    // 1. 本地已存在 → 跳过
    var localPath = Path.Combine(file.ProjectPath, file.RelativePath);
    if (File.Exists(localPath))
    {
        file.LocalExists = true;
        await _mediaRepo.UpdateLocalExistsAsync(file.Id, true, ct);
        return true;
    }

    // 2. OSS 配置检查
    var storage = _ossFactory.TryCreate();
    if (storage == null)
    {
        await PromptOssNotConfiguredAsync();
        return false;
    }

    // 3. 构建 objectKey + 下载
    var ossCfg = await GetOssConfigSnapshotAsync();
    var objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);

    await storage.DownloadAsync(objectKey, localPath, ct);

    file.LocalExists = true;
    await _mediaRepo.UpdateLocalExistsAsync(file.Id, true, ct);

    // 4. 重新生成缩略图（修复死路径）
    try
    {
        var newThumb = await _thumbnailService.GenerateThumbnailAsync(localPath, file.ProjectPath, ct);
        if (!string.IsNullOrEmpty(newThumb)) file.ThumbnailPath = newThumb;
    }
    catch { /* 缩略图失败不影响主流程 */ }

    return true;
}
```

### 7.3 Gallery 侧命令

#### OpenFileAsync 重构

```
OpenFileAsync(file)
  ├─ if file.LocalExists && File.Exists(localPath):
  │     _systemShell.OpenWithDefaultApp(localPath)
  │     return
  ├─ 弹确认框 "从云端下载？"
  ├─ if no → return
  ├─ success = await DownloadToLocalCoreAsync(file)
  └─ if success → _systemShell.OpenWithDefaultApp(localPath)
```

行为不变——双击仍自动打开。但下载逻辑走共享方法。

#### DownloadSingle（右键「下载到本地」）

```
[RelayCommand]
DownloadSingle(MediaFile? file)
  ├─ if file == null → return
  ├─ if !file.IsUploaded → return   // 云端没有
  ├─ if file.LocalExists → ShowToast("本地已存在") → return
  ├─ ShowToast($"正在下载 {file.FileName}…")
  ├─ success = await DownloadToLocalCoreAsync(file)
  └─ if success → ShowToast($"已下载：{file.FileName}")
```

右键菜单新增第三项：

```xml
<MenuItem Header="下载到本地"
          Command="{Binding ... DownloadSingleCommand}"
          CommandParameter="{Binding}"
          IsVisible="{Binding IsUploaded}"/>  <!-- 仅云端有备份的文件可用 -->
```

#### BatchDownload（多选批量下载）

```
[RelayCommand]
BatchDownload()
  ├─ if !IsBatchActionEnabled → return
  ├─ candidates = SelectedFiles.Where(f => f.IsUploaded && !f.LocalExists).ToList()
  ├─ if candidates.Count == 0:
  │     ShowToast("所选文件均已在本地") → ExitMultiSelect() → return
  ├─ 弹确认框 "将从云端下载 N 个文件，继续？"
  ├─ if no → return
  ├─ using _downloadCts = new CancellationTokenSource()
  ├─ foreach file in candidates:
  │     try:
  │         success = await DownloadToLocalCoreAsync(file, ct)
  │         if success: completed++
  │         else: failed++
  │     catch OperationCanceledException: cancelled++; break
  │     catch: failed++
  ├─ ExitMultiSelect()
  └─ summary = $"下载完成：{completed}/{total} 成功"
      if failed > 0: summary += $"，{failed} 失败"
      ShowToast(summary)
```

多选工具栏新增「批量下载」按钮（多选模式下，仅在存在云端文件时可见）。

### 7.4 垃圾筒侧下载

`TrashViewModel` 中的下载逻辑与 Gallery 侧结构相同，但依赖通过构造注入：

```csharp
// TrashViewModel.cs
private async Task<bool> DownloadToLocalAsync(MediaFile file, CancellationToken ct = default)
{
    var localPath = Path.Combine(file.ProjectPath, file.RelativePath);
    if (File.Exists(localPath))
    {
        file.LocalExists = true;
        await _mediaRepo.UpdateLocalExistsAsync(file.Id, true, ct);
        return true;
    }

    var storage = _ossFactory.TryCreate();
    if (storage == null)
    {
        var went = _onOssNotConfigured != null && await _onOssNotConfigured();
        if (!went) ShowToast("OSS 未配置，无法下载");
        return false;
    }

    var ossCfg = await _configService.GetAsync<OssConfig>(ConfigKeys.Oss) ?? new OssConfig();
    var objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);

    ShowToast($"正在下载 {file.FileName}…");
    await storage.DownloadAsync(objectKey, localPath, ct);

    file.LocalExists = true;
    await _mediaRepo.UpdateLocalExistsAsync(file.Id, true, ct);

    try
    {
        var thumb = await _thumbnailService.GenerateThumbnailAsync(localPath, file.ProjectPath, ct);
        if (!string.IsNullOrEmpty(thumb)) file.ThumbnailPath = thumb;
    }
    catch { }

    ShowToast($"已下载 {file.FileName}（仍在垃圾筒）");
    return true;
}
```

垃圾筒不自动恢复——用户下载后可单独点「恢复」按钮将文件移回 Gallery。

### 7.5 多选工具栏变更

遵循现有 pattern：按钮常驻显示，由 `IsBatchActionEnabled` 控制 `IsEnabled`（不需要动态显隐逻辑）：

```
多选模式工具栏：
  [取消多选] [全选] [反选] [批量上传] [批量下载] [批量释放空间] [开始AI] [删除]
```

各按钮的 `IsEnabled` 条件：

| 按钮 | IsEnabled 条件 |
|------|---------------|
| 批量上传 | `IsMultiSelectMode && SelectedCount > 0 && !IsUploading && !IsTagging` |
| 批量下载 | 同上 + `SelectedFiles.Any(f => f.IsUploaded && !f.LocalExists)` |
| 批量释放空间 | 同上 + `SelectedFiles.Any(f => f.IsUploaded && f.LocalExists)` |
| 删除 | `IsMultiSelectMode && SelectedCount > 0 && !IsUploading && !IsTagging` |

> 注：已有 `IsBatchActionEnabled` = `IsMultiSelectMode && SelectedFiles.Count > 0 && !IsUploading && !IsTagging`，批量下载/释放空间可复用该属性的通知链，内部做二次判断——选中的全是未上传文件时下载按钮自动 disabled。

### 7.6 下载进度

文件级下载（非分片），单文件进度无中间状态——用 toast 通知开始/完成。批量下载时用 `ToastMessage` 渐进更新（"下载中 3/10…"）。

### 7.7 注意事项

| 问题 | 决策 |
|------|------|
| 本地已有同名文件 | `DownloadToLocalCoreAsync` 第一步检查 `File.Exists` → 跳过并标记 `local_exists=1` |
| 下载中被取消 | `OperationCanceledException` → 清理可能创建的半截文件（与 `AliyunOssStorage.DownloadAsync` 现有一致） |
| OSS 上文件已被删除 | SDK `GetObject` 抛 `OssException` → catch 后 ShowToast "云端文件不存在" |
| 磁盘空间不足 | `IOException` 在 `CopyTo(fileStream)` 阶段抛出 → catch 后 `File.Delete(localPath)` 清理 |
| 下载后缩略图生成失败 | 静默吞错，UI 用占位图标兜底（与现有行为一致） |

---

## 8. 垃圾筒（TrashViewModel）

### 8.1 页面状态

```csharp
public partial class TrashViewModel : ObservableObject
{
    private readonly IMediaRepository _mediaRepo;
    private readonly IOssStorageFactory _ossFactory;
    private readonly IConfigService _configService;
    private readonly IThumbnailService _thumbnailService;
    private readonly ISystemShellService _systemShell;
    private readonly Func<Task<bool>>? _onOssNotConfigured;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string? _projectPath;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isCleaning;              // 批量清理中
    [ObservableProperty] private string? _toastMessage;

    // 两组数据
    public ObservableCollection<MediaFile> CloudFiles { get; } = new();   // IsUploaded == true
    public ObservableCollection<MediaFile> LocalFiles { get; } = new();   // IsUploaded == false

    public bool HasCloudFiles => CloudFiles.Count > 0;
    public bool HasLocalFiles => LocalFiles.Count > 0;
}
```

### 8.2 数据加载

```
LoadAsync(projectPath)
  ├─ _cts?.Cancel() + new CTS
  ├─ IsLoading = true
  ├─ CloudFiles.Clear(), LocalFiles.Clear()
  ├─ files = await _mediaRepo.GetDeletedAsync(projectPath, ct)
  ├─ foreach file in files:
  │     if file.IsUploaded → CloudFiles.Add(file)
  │     else              → LocalFiles.Add(file)
  ├─ IsEmpty = files.Count == 0
  └─ IsLoading = false
```

### 8.3 操作命令

#### Restore（恢复）

```
RestoreAsync(file)
  ├─ await _mediaRepo.RestoreAsync(file.Id)
  ├─ 从 CloudFiles 或 LocalFiles 移除
  └─ ShowToast($"已恢复 {file.FileName}")
```

#### Download（下载云上文件）

仅对 `file.IsUploaded && !file.LocalExists` 的云端文件有效。完整实现见 §7.4「垃圾筒侧下载」——逻辑与 Gallery 侧等价，但依赖通过构造注入，且下载后**不自动恢复**（文件仍在垃圾筒，用户需单独点「恢复」回到 Gallery）。

#### CleanSingle（单文件清理）

```
CleanSingleAsync(file)
  ├─ if file.IsUploaded:
  │     → DialogHelper.ShowConfirm("该文件已上传到云端。是否一并从云端删除？",
  │          primaryButtonText: "从云端也删除",
  │          secondaryButtonText: "仅删除本地")
  │     → if 从云端删除:
  │         ossCfg = await _configService.GetAsync<OssConfig>
  │         storage = _ossFactory.TryCreate()
  │         objectKey = BuildObjectKey(prefix, file.RelativePath)
  │         try:
  │             await storage.DeleteObjectAsync(objectKey)
  │         catch (OssException ex):
  │             ShowToast($"云端删除失败: {ex.Message}")
  │             return   // 云端删除失败 → 不删本地和 DB
  │     → if 仅删除本地 AND file.LocalExists:
  │         File.Delete(Path.Combine(file.ProjectPath, file.RelativePath))
  │     → await _mediaRepo.PermanentDeleteAsync(file.Id)
  │     → 从对应集合移除
  │     → ShowToast("已清除")
  │
  └─ else (未上传):
      → DialogHelper.ShowConfirm("将永久删除，不可恢复")
      → if no → return
      → if file.LocalExists:
          File.Delete(Path.Combine(file.ProjectPath, file.RelativePath))
      → await _mediaRepo.PermanentDeleteAsync(file.Id)
      → 从 LocalFiles 移除
      → ShowToast("已清除")
```

#### BatchCleanAll（清空垃圾筒）

```
BatchCleanAllAsync()
  ├─ 云上文件数 = CloudFiles.Count, 本地文件数 = LocalFiles.Count
  ├─ if 云上文件数 > 0:
  │     弹窗：cloudMsg = "其中 N 个文件已上传云端，\n是否一并从云端删除？"
  │     选项："从云端也删除" / "仅删除本地" / "取消"
  │     if "取消" → return
  ├─ else if 本地文件数 > 0:
  │     DialogHelper.ShowConfirm("将永久删除所有垃圾筒文件，不可恢复")
  │     if no → return
  │     deleteFromCloud = false
  ├─ IsCleaning = true
  ├─ 先处理云端文件（如果 deleteFromCloud == true）:
  │     storage = _ossFactory.TryCreate()  // 如果 OSS 未配置 → ShowToast + 跳过云端删除
  │     ossCfg = _configService.GetAsync<OssConfig>
  │     foreach file in CloudFiles:
  │         objectKey = BuildObjectKey(prefix, file.RelativePath)
  │         try: await storage.DeleteObjectAsync(objectKey)
  │         catch: cloudErrors++
  ├─ 删本地文件 + DB:
  │     foreach file in allFiles:
  │         if file.LocalExists:
  │             try: File.Delete(fullPath) catch: /* ignore */
  │         await _mediaRepo.PermanentDeleteAsync(file.Id)
  ├─ CloudFiles.Clear(), LocalFiles.Clear()
  ├─ IsCleaning = false
  ├─ IsEmpty = true
  └─ summary = $"已清除 {total} 个文件"
      if cloudErrors > 0: summary += $"，{cloudErrors} 个云端删除失败"
      ShowToast(summary)
```

---

## 9. 垃圾筒 UI（TrashView.axaml）

### 9.1 布局

```
┌─────────────────────────────────────────────────────────┐
│  ← 返回画廊                          [清空垃圾筒]      │  ← 工具栏 (MainWindow 同位置)
├─────────────────────────────────────────────────────────┤
│                                                         │
│  ▸ 已在云端 (N)                                         │  ← CloudFiles Section
│  ┌────┬────┬────┐                                       │
│  │ 📷 │ 📷 │ 📷 │   缩略图网格（160×120 同 Gallery）    │
│  └────┴────┴────┘                                       │
│                                                         │
│  ▸ 仅本地 (N)                                           │  ← LocalFiles Section
│  ┌────┬────┬────┬────┬────┐                             │
│  │ 📷 │ 📷 │ 📷 │ 📷 │ 📷 │                             │
│  └────┴────┴────┴────┴────┘                             │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

### 9.2 卡片布局

每个缩略图卡片（与 Gallery photo-tile 同尺寸 160×120）：

- **顶部覆盖层**：半透明条显示文件名（截断到 12 字）
- **右上角**：`IsUploaded == true` → 云图标徽章；`IsUploaded == false` → 无徽章
- **底部操作区**（hover 显示）：
  - 「恢复」按钮
  - 「清理」按钮（红色）
  - 云端文件且 `!LocalExists` 时：额外「下载」按钮
- **上传状态同步徽章**：右上角同 Gallery 的云+✓ / 云+↓ / 灰云 徽章逻辑

### 9.3 空态

```
┌─────────────────────────────────────────┐
│                                         │
│              (垃圾筒图标)                │
│          垃圾筒是空的                    │
│                                         │
└─────────────────────────────────────────┘
```

---

## 10. 导航集成

### 10.1 MainWindowViewModel 变更

```csharp
// ViewPage 枚举新增
public enum ViewPage
{
    Gallery,
    Settings,
    UploadServer,
    Trash,   // 新增
}

public partial class MainWindowViewModel
{
    [ObservableProperty] private TrashViewModel trashViewModel;

    public bool IsTrashActive => CurrentPage == ViewPage.Trash;

    // ctor 中注入 TrashViewModel
    TrashViewModel = new TrashViewModel(
        mediaRepository, configService, ossFactory, thumbnailService, systemShell,
        onOssNotConfigured: ShowOssNotConfiguredDialogAsync);

    [RelayCommand]
    private void NavigateToTrash()
    {
        CurrentView = TrashViewModel;
        IsSettingsPage = false;
        CurrentPage = ViewPage.Trash;
        _ = TrashViewModel.LoadAsync(GalleryViewModel.ProjectPath ?? "");
    }
}
```

### 10.2 NavRail 变更

`Controls/NavRail.axaml`：在「上传」和「设置」之间插入垃圾筒按钮，使用已存在的 `Icon.Trash` 图标：

```xml
<Button Grid.Row="1.5"  <!-- 或调整 RowDefinitions -->
        Height="48"
        Classes="nav-item"
        Classes.active="{Binding IsTrashActive}"
        Command="{Binding NavigateToTrashCommand}">
    <StackPanel Orientation="Vertical">
        <Path Data="{DynamicResource Icon.Trash}"
              Stretch="Uniform" Width="20" Height="20"
              Stroke="{DynamicResource Text.Secondary}"
              StrokeThickness="1.5"/>
        <TextBlock Text="垃圾筒" FontSize="11"/>
    </StackPanel>
</Button>
```

### 10.3 MainWindow.axaml DataTemplate

`ContentControl.DataTemplates` 中新增：

```xml
<DataTemplate DataType="{x:Type vm:TrashViewModel}">
    <views:TrashView DataContext="{Binding}"/>
</DataTemplate>
```

---

## 11. 模块边界

```
MainWindowViewModel (顶层)
  ├─ GalleryViewModel（注入）
  │     ├─ 新增: SoftDelete / FreeUpSpace / BatchFreeUpSpace / 右键菜单扩展
  │     └─ 已有: 所有查询 → 过滤 deleted_at IS NULL
  │
  ├─ TrashViewModel（新建）
  │     ├─ IMediaRepository        (GetDeleted / Restore / PermanentDelete / UpdateLocalExists)
  │     ├─ IOssStorageFactory      (删除云上文件 / 下载)
  │     ├─ IConfigService          (读 OssConfig)
  │     ├─ IThumbnailService       (下载后重生成缩略图)
  │     ├─ ISystemShellService     (RevealInFolder)
  │     └─ Func<Task<bool>>?       (OSS 未配置跳设置)
  │
  └─ 共享依赖
        ├─ IOssStorage + DeleteObjectAsync（新增）
        └─ IMediaRepository + 4 个新方法
```

---

## 12. 错误处理

| 场景 | 处理 |
|------|------|
| 软删除失败（DB） | ShowToast + 不回滚 UI（文件仍在） |
| 云上文件不存在（DeleteObject） | 阿里云 SDK 不抛异常，继续执行 |
| OSS 未配置（清理时） | ShowToast "OSS 未配置，仅删除本地" |
| OSS 凭据过期（下载/删除时） | catch OssException → ShowToast 具体错误 |
| 本地文件已被手动删除（清理时） | `File.Delete` 包 try/catch，忽略 FileNotFound |
| 下载目标目录不存在 | 自动 `Directory.CreateDirectory` |
| 批量操作中单个失败 | 累计 errors，最终汇总 toast |
| 垃圾筒加载失败（DB） | IsLoading = false，保持上一批数据不覆盖 |

---

## 13. 文件变更清单

| 文件 | 变更类型 | 核心内容 |
|------|---------|---------|
| `Data/MediaFile.cs` | 修改 | +`DeletedAt` 属性；`ReadMediaFileRow` 映射 |
| `Data/IMediaRepository.cs` | 修改 | +5 个方法声明 |
| `Data/MediaRepository.cs` | 修改 | +`deleted_at` 列迁移；所有查询 + `deleted_at IS NULL`；+5 个方法实现 |
| `Services/IOssStorage.cs` | 修改 | +`DeleteObjectAsync` |
| `Services/AliyunOssStorage.cs` | 修改 | 实现 `DeleteObjectAsync` |
| `Models/Models.cs` | 修改 | 不变（垃圾筒 UI 模型在 TrashViewModel 内直接使用 MediaFile） |
| `ViewModels/GalleryViewModel.cs` | 修改 | 实现 `BatchDelete` / `DeleteSingle` / `FreeUpSpace` / `BatchFreeUpSpace` / `DownloadSingle` / `BatchDownload` + `DownloadToLocalCoreAsync` |
| `ViewModels/MainWindowViewModel.cs` | 修改 | +`Trash` 页导航；注入 `TrashViewModel` |
| `ViewModels/TrashViewModel.cs` | **新建** | 完整垃圾筒 VM（含下载、恢复、清理） |
| `Views/TrashView.axaml` | **新建** | 垃圾筒 UI 页面 |
| `Views/TrashView.axaml.cs` | **新建** | Code-behind（事件处理） |
| `Views/GalleryView.axaml` | 修改 | 右键菜单扩展「删除」「释放本地空间」「下载到本地」 |
| `Views/MainWindow.axaml` | 修改 | +`TrashView` DataTemplate；工具栏新增「批量下载」「释放本地空间」按钮 |
| `Controls/NavRail.axaml` | 修改 | +垃圾筒导航入口 |

---

## 14. 与现有功能的交叉影响

| 受影响模块 | 影响 | 处理 |
|-----------|------|------|
| 扫描（ScanDirectoryAsync） | `deleted_at IS NOT NULL` 的文件扫描不到 | 不受影响——扫描只 INSERT/UPDATE，不管 `deleted_at` |
| 日期分组（GetDateGroupsAsync） | 删除后的文件不再计入日期计数 | 正确行为——Gallery 中不可见 |
| AI 打标 | 不应对已删除文件打标 | 所有 Gallery 查询均过滤 `deleted_at IS NULL`，天然排除 |
| 缩略图生成 | 已删除文件不生成缩略图 | 同扫描——只处理 `thumbnail_path IS NULL` 的未删除文件 |
| 公网接收（sync_for_vps_task） | 接收的文件正常入库，不受 trash 影响 | 无交叉 |
| 上传续传（upload_jobs） | 删除文件时有续传任务怎么办？ | **设计决策**：删除时检查并提示「有未完成上传任务」→ 确认后一并取消 Abort multipart + 删除 job |

---

## 15. 已知陷阱

- **`deleted_at` 与 `local_exists` 的语义独立**：即使文件已软删除，`local_exists` 仍反映真实磁盘状态——垃圾筒需要它决定是否显示占位图或提供下载按钮。
- **恢复时 `local_exists` 不自动修复**：如果用户在垃圾筒期间手动删了本地文件，恢复后该文件 Gallery 中显示为 `local_exists = 0`——与正常「云端有、本地无」状态一致，用户自行决定是否重新下载。
- **OSS 删除的幂等性**：阿里云 `DeleteObject` 对不存在的 key 不会报错——批清理时对云上缺失的文件也安全。
- **垃圾筒跨项目**：`GetDeletedAsync` 按 `project_path` 过滤，切项目后垃圾筒内容不同——属预期行为，与 Gallery 行为一致。
- **免费层 OSS 存储**：删除云端文件后阿里云不会立即清理实际存储块，但不再计费（标准存储最短计费周期适用）。
- **释放本地空间的按钮可见性**：必须条件 `IsUploaded == true && local_exists == 1`，未上传或已不在本地的文件不显示该操作。
