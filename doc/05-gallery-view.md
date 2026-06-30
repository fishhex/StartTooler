# 05 — Gallery 视图与状态机

> 对应代码：`ViewModels/GalleryViewModel.cs`、`Views/GalleryView.axaml`，以及辅助的 `Models/Models.cs`、`Data/MediaFile.cs`。

---

## 1. 模块边界

```
MainWindowViewModel (顶层)
  └─ GalleryViewModel（注入）
       ├─ IMediaRepository      (扫 + 查日期 + 查文件 + 缩略图 + 改上传字段)
       ├─ IThumbnailService     (缩略图生成)
       ├─ IConfigService        (ProjectConfig 读项目目录 + OssConfig 快照)
       ├─ ISystemShellService   (OpenInFolder / OpenWithDefaultApp)
       ├─ IOssStorageFactory    (上传 / 下载)
       ├─ IUploadJobRepository  (续传 job 持久化)
       └─ Func<Task<bool>> _onOssNotConfigured   (MainWindowVM 注入跳设置)

UI 绑定：
  Views/GalleryView.axaml (时间轴 + 缩略图网格 + 右键菜单)
  MainWindow.xaml 工具栏 (多选/批量/刷新)
```

---

## 2. ViewModel 状态总览

### 2.1 数据

| 属性 | 类型 | 用途 |
|---|---|---|
| `DateGroups` | `ObservableCollection<TimelineEntry>` | 左栏时间轴（按 `shot_at` 日期分组） |
| `CurrentMediaFiles` | `ObservableCollection<MediaFile>` | 当前选中日期的文件列表 |
| `SelectedDate` | `TimelineEntry?` | 当前选中日期（[ObservableProperty]） |
| `ProjectPath` | `string?`（get-only）| 当前项目目录（从 Config 读） |

### 2.2 加载 / 错误

| 属性 | 含义 |
|---|---|
| `IsLoadingDateGroups` | 日期列表加载中 |
| `IsLoadingMedia` | 单日文件加载中 |
| `LoadErrorMessage` | DB 读错误（含 stack-trace 摘要） |
| `IsScanning` | 扫描中（Repository.ScanDirectoryAsync + GenerateThumbnailsAsync 阶段） |
| `ScanProgress` | `ScanProgress { Total, Processed, Failed, CurrentFile }` |
| `ScanStatusMessage` | "扫描中 N/T" / "扫描完成" |
| `RefreshState` | `Idle / Scanning / Completed / Stopped` |

### 2.3 多选模式

| 属性 | 含义 |
|---|---|
| `IsMultiSelectMode` | 是否在多选 |
| `SelectedFiles` | `ObservableCollection<MediaFile>` 多选池 |
| `SelectedCount` | SelectedFiles.Count (get-only) |
| `IsBatchActionEnabled` | `= IsMultiSelectMode && SelectedFiles.Count > 0 && !IsUploading` |
| `ToastMessage` | 一行提示（3s 后自动消失） |

### 2.4 上传状态

| 属性 | 含义 |
|---|---|
| `IsUploading` | `UploadManyAsync` 在跑 |
| `UploadTotalCount` / `UploadCompletedCount` | 用于 "上传中 N/M" 进度文本 |
| `UploadProgressText` | 派生属性，bindable |

---

## 3. 生命周期

### 3.1 启动（`InitializeAsync` — `GalleryViewModel.cs:117-168`）

```
GalleryViewModel.InitializeAsync (MainWindowVM 在 ctor 后 fire-and-forget 调)
  ├─ _cts?.Cancel() + new CTS  (上次的全停)
  ├─ IsLoadingDateGroups = true
  ├─ DateGroups.Clear() / ExitMultiSelect() / CurrentMediaFiles.Clear()
  ├─ _configService.GetOrCreateAsync<ProjectConfig>(ConfigKeys.Project)
  │   └─ _projectPath = projectConfig.CurrentDirectory ?? ""
  ├─ if string.IsNullOrEmpty(_projectPath) → return (空态)
  ├─ _mediaRepo.GetDateGroupsAsync(_projectPath, ct) → foreach add to DateGroups
  ├─ if DateGroups.Count == 0 → return (空态)
  └─ await SelectAsync(DateGroups[0])   // 自动选第一个日期
```

### 3.2 选日期（`SelectAsync(TimelineEntry)` — `GalleryViewModel.cs:170-191`）

```
SelectAsync(entry)
  ├─ if entry == null → return
  ├─ _cts?.Cancel() + new CTS
  ├─ ExitMultiSelect()                   // 切日期时退出多选
  ├─ if SelectedDate != null → 旧.IsSelected = false
  ├─ entry.IsSelected = true → SelectedDate = entry
  └─ await LoadDateAsync(entry, ct)
```

### 3.3 加载单日（`LoadDateAsync` — `GalleryViewModel.cs:193-248`）

```
LoadDateAsync(entry, ct)
  ├─ IsLoadingMedia = true
  ├─ _mediaRepo.GetByDateAsync(_projectPath, entry.Date, ct)
  │   └─ LIMIT 1000（LIMIT 一日最多 1000 条，超出需分页 → 待 v0.2+）
  ├─ _uploadJobRepo.GetInProgressAsync(_projectPath, ct)
  │   └─ catch → Array.Empty<UploadJob>（DB 错误不阻塞 UI）
  ├─ pausedSet = HashSet(jobs.Select(j => j.RelativePath), OrdinalIgnoreCase)
  ├─ CurrentMediaFiles.Clear()
  ├─ foreach file:
  │     file.UploadStatus =
  │         file.IsUploaded            ? UploadStatus.Uploaded
  │       : pausedSet.Contains(relPath) ? UploadStatus.Paused
  │                                     : UploadStatus.NotUploaded
  │     CurrentMediaFiles.Add(file)
  └─ IsLoadingMedia = false
```

### 3.4 扫描（`RefreshAsync` — `GalleryViewModel.cs:256-308`）

```
RefreshAsync (工具栏「刷新」)
  ├─ if _projectPath == "" → return
  ├─ RefreshState = Scanning, IsScanning = true, ScanProgress = new()
  ├─ new Progress<ScanProgress>(p => { ScanProgress = p; ScanStatusMessage = ... })
  ├─ await _mediaRepo.ScanDirectoryAsync(_projectPath, progress, _cts?.Token ?? default)
  │     （两遍扫，详见 02-data-layer.md §3.3）
  ├─ ScanStatusMessage = "正在生成缩略图..."
  ├─ await _mediaRepo.GenerateThumbnailsAsync(_projectPath, _thumbnailService, progress, ct)
  │     （逐文件串行，详见 03-media-pipeline.md §1）
  ├─ await InitializeAsync()         // 重新加载
  ├─ RefreshState = Completed
  └─ ScanStatusMessage = "扫描完成 · 共 N · 新增 X · 更新 Y"
  catch OperationCanceledException → RefreshState = Idle
  catch Exception ex              → ScanStatusMessage = "扫描失败: msg"
```

### 3.5 多选（`ToggleSelection` / `EnterMultiSelect` / `ExitMultiSelect`）

```
EnterMultiSelect     → IsMultiSelectMode = true
ExitMultiSelect      → IsMultiSelectMode = false
                        └─ OnIsMultiSelectModeChanged(false) 清空 SelectedFiles
                                  └─ SelectedFiles 触发 CollectionChanged
                                            └─ 所有 file.IsSelected = false（同步）

ToggleSelection(file) (GridItem 单击)
  ├─ if !IsMultiSelectMode → return (v2.3: 单击仅做选中预览，不做操作)
  └─ if SelectedFiles.Contains(file) → Remove(file)
                                  → Add(file)

OnSelectedFilesChanged (CollectionChanged)
  ├─ NewItems:  foreach (MediaFile mf) mf.IsSelected = true
  ├─ OldItems:  foreach (MediaFile mf) mf.IsSelected = false
  └─ OnPropertyChanged(SelectedCount / IsBatchActionEnabled)
```

### 3.6 上传入口

| 入口 | 行为 |
|---|---|
| **单文件右键 → 上传** | `UploadSingleCommand(MediaFile)` — 直接调 `UploadManyAsync(new[]{file})` |
| **批量上传** | `BatchUploadCommand` — `SelectedFiles.ToList()` → `UploadManyAsync` |
| **启动恢复** | `ResumeInterruptedAsync(jobs)` — 由 `MainWindowVM.TryPromptResumeInterruptedAsync` 调 |

### 3.7 下载 / 打开（`OpenFileAsync` — `GalleryViewModel.cs:413-510`）

```
OpenFileAsync(file)
  ├─ if file.LocalExists && File.Exists(localPath)
  │     └─ _systemShell.OpenWithDefaultApp(localPath)  → return
  ├─ else  (本地缺失 / 从 OSS 拉)
  │     ├─ 弹 DialogHelper.ShowConfirmAsync("从云端下载?")
  │     ├─ if no → return
  │     ├─ _ossFactory.TryCreate() → null → PromptOssNotConfiguredAsync → return
  │     ├─ ossCfg = _configService.GetAsync<OssConfig> 快照
  │     ├─ objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath)
  │     ├─ ShowToast("正在下载 ...")
  │     ├─ await storage.DownloadAsync(objectKey, localPath)
  │     ├─ file.LocalExists = true
  │     ├─ await _thumbnailService.GenerateThumbnailAsync  // 重新生成（缩略图可能丢）
  │     ├─ if newThumb != null → file.ThumbnailPath = newThumb
  │     └─ _systemShell.OpenWithDefaultApp(localPath) (成功后自动打开)
  └─ 失败 → ShowToast("下载失败: msg")
```

> **承认**（`GalleryViewModel.cs:469-488`）：视频缩略图经常和视频文件一起被删 / 缓存清掉。下载完顺手重新生成缩略图，避免「表里有 ThumbnailPath 字符串但文件不存在」导致卡片显示空 Image。

### 3.8 在文件夹中打开（`OpenInFolder` — `GalleryViewModel.cs:397-411`）

```
OpenInFolder(file)
  ├─ absolutePath = Path.Combine(file.ProjectPath, file.RelativePath)
  └─ _systemShell.RevealInFolder(absolutePath)
       （macOS Finder 高亮 / Windows Explorer 高亮 / Linux xdg-open 打开所在目录）
```

异常 catch → ShowToast。

---

## 4. UI 状态机（按 v2.3 spec）

### 4.1 单张照片 5 态 UploadStatus

```
                     ┌─ Uploading ─┬─► Uploaded
   NotUploaded ──────┤             ├─► Failed
                     └─ 取消       │   (UploadError 显示)
                                  └─► Paused
                                       (app 崩溃/退出时 job 留底)

   Uploaded (已上传 + 下载完成)
   Paused (有 upload_jobs 留底，下次手动续传)
   Failed (上传失败，错误 toast/对话框)
```

**反推逻辑**：进 Gallery 时 `LoadDateAsync` 根据 `IsUploaded` + `upload_jobs.pausedSet` 决定（详见 `02-data-layer.md` §5.2）。

### 4.2 多选模式显隐

| 组件 | 多选 OFF | 多选 ON | 上传中 |
|---|---|---|---|
| 「多选」按钮 | ✅ | ❌ | ❌（IsEnabled=false）|
| 「取消多选」按钮 | ❌ | ✅ | ✅ |
| 「批量上传」按钮 | ❌ | ✅ (IsBatchActionEnabled) | ❌ |
| 「删除」按钮 | ❌ | ✅ (IsBatchActionEnabled) | ❌ |
| 「取消上传」按钮 | ❌ | ❌ | ✅ |
| 「已选 N 项」计数 | ❌ | ✅ | ❌ |
| 「上传中 N/M」进度 | ❌ | ❌ | ✅ |

### 4.3 工具栏 IsEnabled

- 「刷新」: `!string.IsNullOrEmpty(GalleryViewModel.ProjectPath)`
- 「多选」: `!GalleryViewModel.IsUploading`
- 「批量上传/删除」: `IsBatchActionEnabled`（详见上）
- 「保存」/「返回」按钮（设置页）: 切到设置页时显示

### 4.4 ScanProgressBar 状态

`RefreshState` 转换 + 2 秒自动清除（`GalleryViewModel.cs:314-321`）：

```csharp
partial void OnRefreshStateChanged(RefreshState value) {
    if (value == Completed) {
        ScanStatusMessage = $"扫描完成 · 共 {ScanProgress?.Total} 个文件";
        _ = Task.Delay(2000).ContinueWith(_ => ScanStatusMessage = null);
    }
}
```

---

## 5. UploadStatus 与 Uploaded 的协作

### 5.1 字段所有权（详见 `02-data-layer.md` §5.1）

| 字段 | 类型 | 入 / 出 |
|---|---|---|
| `IsUploaded`（DB 列 `is_uploaded`） | DB | 持久化；上传成功 → DB 写 1；重扫不会改 |
| `UploadStatus`（UI 瞬时态）| 仅内存 | 入 Gallery 时反推 |
| `UploadError`（UI 瞬时态）| 仅内存 | 上传失败时设；下次刷掉 |

### 5.2 反推时序

```
用户扫项目目录
   ↓
MediaRepository.ScanDirectoryAsync: 新文件 row, IsUploaded=0
   ↓
GalleryVM.LoadDateAsync(fileList, jobs):
   foreach file {
     file.UploadStatus = file.IsUploaded          ? Uploaded
                       : pausedSet.Contains(rel)   ? Paused
                                                  : NotUploaded
   }
   ↓
用户点上传 → UploadOneAsync
   ↓
UploadMultipartNewAsync → Initiate job record → Parts → Complete
   ↓ 成功
ApplyUploadSuccessAsync:
  file.IsUploaded = true        ← 这是 DB-backed property
  file.UploadStatus = Uploaded  ← UI 瞬时
  ↓
_mediaRepo.UpdateUploadStateAsync(file.Id, true, uploadedAt, remoteUrl)
                                 ← 写回 DB
```

---

## 6. 命令清单

| Command | 来源 | 行为 |
|---|---|---|
| `ReloadAsync` | `MainWindowVM.NavigateToGallery` 末尾 | `await InitializeAsync()` |
| `RefreshAsync` | 工具栏「刷新」 | 扫描 + 缩略图 + 重新加载 |
| `SelectAsync(TimelineEntry)` | 时间轴点击 | 切日期 + 加载 |
| `EnterMultiSelect` / `ExitMultiSelect` | 工具栏 | 多选开关 |
| `ToggleSelection(MediaFile)` | GridItem 单击 | 多选 add/remove |
| `BatchUpload` | 工具栏「批量上传」 | OSS 检查 + 多选 → UploadManyAsync |
| `BatchDelete` | 工具栏「删除」 | **v0.1 仅 toast**（待实现）|
| `UploadSingle(MediaFile)` | 右键 → 上传 | 单个文件上传 |
| `CancelUpload` | 上传中「取消」 | `_uploadCts.Cancel()` |
| `OpenFileAsync(MediaFile)` | GridItem 双击 / 右键查看 | 本地存在 → OpenWithDefaultApp；否则下载 |
| `OpenInFolder(MediaFile)` | 右键 → 在文件夹中打开 | RevealInFolder |
| `DeleteSingle(MediaFile)` | 右键 → 删除 | **v0.1 仅 toast** |
| `ResumeInterruptedAsync(jobs)` | `MainWindowVM.TryPromptResumeInterruptedAsync` | 启动恢复弹窗确认后 |

`GalleryViewModel.cs` 上面 `[RelayCommand]` 全列。

---

## 7. 与其他 VM 的协作

### 7.1 与 `MainWindowViewModel`

- **构造期**：`MainWindowVM` 创建 `GalleryVM` 注入 7 个依赖（含 `_onOssNotConfigured = ShowOssNotConfiguredDialogAsync`）
- **导航**：切到 Gallery 页时调 `GalleryVM.ReloadCommand` 自动刷新
- **退出恢复弹窗**：MainWindowVM 弹窗 → 用户确认 → 调 `GalleryVM.ResumeInterruptedAsync`
- **OSS 未配置**：GalleryVM 通过 `_onOssNotConfigured?.Invoke()` 回调让 MainWindowVM 跳 Settings

### 7.2 与 `SettingsViewModel`

- **间接耦合**：共享 `IConfigService`、`IOssStorageFactory`。Settings 改 OSS 后，下次 `OssFactory.TryCreate()` 立即拿到新配置
- **不直接订阅**：Settings VM 不订阅 Gallery VM

### 7.3 与 `PublicRelayViewModel`

- **间接耦合**：通过 `PublicRelayService.FileReceived` 事件（`PublicRelayService.cs:39` + `FileReceived?.Invoke(finalPath)`）— 但目前画廊**没**订阅该事件（刚加，see commit），后续「公网接收后自动刷新 Gallery」可加一行 `+= (path) => ReloadCommand.Execute(null)`

---

## 8. 错误处理表

| 场景 | 处理 |
|---|---|
| ProjectConfig 缺失 | `GetOrCreateAsync` 自动 new ProjectConfig（空白）|
| 项目目录不存在 | InitializeAsync 不报，Gallery 显示空态 |
| GetDateGroupsAsync 异常 | `LoadErrorMessage = "加载失败: msg"`，UI 红字 |
| GetByDateAsync 异常 | 同上 |
| GetInProgressAsync 异常 | `Array.Empty<UploadJob>()` —— DB 错误不该阻塞 UI |
| 扫描单文件失败 | `ScanProgress.Failed++`，不抛 |
| 缩略图单文件失败 | 该文件 `ThumbnailPath` 留空，UI 用占位符 |
| OSS 未配置 | `PromptOssNotConfiguredAsync` → MainWindowVM 弹框跳设置 |
| 上传单文件失败 | `UploadError` 字段 + errors 列表 |
| 上传全程取消 | cancelled++，summary toast 含「已取消」|
| DB 写上传状态失败 | catch silent —— UI 已是 Uploaded |
| 启动恢复时找不到对应 file | ShowToast("找不到可恢复的媒体文件（可能已删除）") |

---

## 9. 修改这个模块前的检查清单

| 改动 | 涉及 | 验证 |
|---|---|---|
| 加新命令 | `GalleryViewModel` 加 `[RelayCommand] private async Task Foo()` | 工具栏按钮可见 + 状态机正确 |
| 加新字段（持久化） | `MediaFile` + `MediaRepository` + DB migration | 现有 DB 兼容（加 nullable 列） |
| 加 UI 瞬时字段 | `MediaFile` 加 `[ObservableProperty] private ...` | 重启清空，符合预期 |
| 改上传策略 | `UploadOneAsync` 决策树 | 三种 path 都有 happy/cancel/fail 路径 |
| 加多选可视化 | GalleryView.xaml 绑 `IsSelected` 触发器 | 多选模式进入/退出同步 |
| 改 LIMIT 1000 | `MediaRepository.GetByDateAsync` | 单日 >1000 文件时怎么分页？ |
| 加右键菜单项 | GalleryView.xaml 加 ContextFlyout | macOS 上右键事件能触发？ |

---

## 10. 已知陷阱（详见 `10-trap-book.md`）

- **`OnSelectedFilesChanged` 不处理 `Reset`** —— 已记 Reset 不带 NewItems，依赖 VM 显式 Clear
- **`IsSelected` 单向同步** —— 反向写 `mf.IsSelected = true` 不应绕过 `SelectedFiles` CollectionChanged 触发器
- **启动恢复一次性** —— `_resumePrompted = true` 设了不再弹，但用户重启后又能弹一次（新进程）
- **`LoadDateAsync` 在 `SelectAsync` 中重入** —— 每次 new CTS，旧 ct 取消可能晚一拍
- **`OpenFileAsync` 失败吞缩略图** —— 已 try/catch silent，符合预期
- **`RelativePath` 大小写**：macOS HFS+/APFS 默认大小写不敏感，但底层区分——务必 `OrdinalIgnoreCase`
- **`Limit 1000` 截断**：单日超 1000 文件时无声丢失，要做分页就要先 `MediaRepository.GetByDatePageAsync(project, date, skip, take)`
