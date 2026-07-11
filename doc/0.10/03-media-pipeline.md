# 03 — 媒体管线（扫描、缩略图、FFmpeg/FFprobe）

> 对应代码：`Services/ThumbnailService.cs`、`Services/FfmpegSnapshotRunner.cs`、`Services/FfprobeRunner.cs`、`Services/FFmpegConfigurator.cs`、`Services/ImageCacheService.cs`、以及 `Data/MediaRepository.ScanDirectoryAsync` 与 `GenerateThumbnailsAsync`。

---

## 1. 流程总览

```
GalleryViewModel.RefreshAsync
  ├─ MediaRepository.ScanDirectoryAsync       // 两遍：枚举文件 + INSERT
  │     ├─ 第一遍 Task.Run: Directory.EnumerateFiles + 扩展名过滤 + 报 Total
  │     └─ 第二遍: foreach 算 file_size/last_modified/shot_at → INSERT ON CONFLICT
  │
  └─ MediaRepository.GenerateThumbnailsAsync  // 找 thumbnail_path IS NULL 的文件
        └─ foreach (id, relativePath) → ThumbnailService.GenerateThumbnailAsync
              ├─ 命中缓存（File.Exists(thumbnailPath)）→ 直接返回
              ├─ 图片 (.jpg/.png/.raw 等) → SkiaSharp 解码 + resize + jpeg 编码
              └─ 视频 (.mp4/.mov/.avi 等)
                    ├─ Step 1/3: FfprobeRunner.ProbeAsync → VideoProbeResult
                    │     失败 → 尝试 SkiaSharp（兜底，几乎肯定失败但写日志）
                    ├─ Step 2/3: 解析 duration/width/height/codec/frameRate
                    └─ Step 3/3: FfmpegSnapshotRunner.SnapshotAsync
                          └─ 命令: ffmpeg -y -i input -vf "thumbnail=N,scale=W:H"
                                          -frames:v 1 -update 1 output.jpg

Gallery 显示
  └─ 每张 MediaFile.ThumbnailPath → Converter FilePathToBitmapConverter → Image
        └─ ImageCacheService.LoadImageAsync: ConcurrentDictionary 缓存 + 信号量限并发
```

---

## 2. ThumbnailService（`Services/ThumbnailService.cs`）

### 2.1 公共契约

```csharp
public interface IThumbnailService {
    Task<string?> GenerateThumbnailAsync(string sourcePath, string projectPath, CancellationToken ct = default);
}
```

返回：缩略图绝对路径（已生成或缓存命中），失败 / 异常 → `null`。

### 2.2 缓存策略

- 路径前缀：`{LocalAppData}/StartTooler/thumbnails/`（`ThumbnailService.cs:23-28`）
- 文件名：`<path-hash>.jpg`，hash 用字符串哈希：`unchecked (17; foreach c: hash = hash*31 + c)` → 16 进制（`ThumbnailService.cs:206-218`）。
- 命中即返回（`File.Exists(thumbnailPath) ? return : continue`，`ThumbnailService.cs:47-51`）。
- 缩略图写入 DB 的 `thumbnail_path` 是绝对路径 — 跨进程/重启有效。
- **缓存清理场景**：用户清理缓存后 ThumbnailPath 是死路径。UI 层 `FilePathToBitmapConverter`（见 `09-ui-commons.md`）会自动隐藏。

### 2.3 图片缩略图（`GenerateImageThumbnailAsync`）

```csharp
await Task.Run(() => {
    using var inputStream = File.OpenRead(sourcePath);
    using var original = SKBitmap.Decode(inputStream);
    if (original == null) return;                       // 解码失败 → 不写输出
    var scale = Math.Min(W/original.W, H/original.H);    // 保持比例
    using var resized = original.Resize(new SKImageInfo(W', H'), SKFilterQuality.High);
    if (resized == null) return;
    using var image = SKImage.FromBitmap(resized);
    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
    using var outputStream = File.OpenWrite(thumbnailPath);
    data.SaveTo(outputStream);
}, ct);
```

- 目标尺寸 `320×240`（常量 `ThumbnailService.cs:18-19`），保持宽高比。
- JPEG 质量 85。
- SkiaSharp 跨平台一致（macOS 上 macOS Native assets 已绑定，Linux/Windows 用普通 SkiaSharp.NativeAssets）。

### 2.4 视频缩略图 — 三步日志（`GenerateVideoThumbnailAsync`）

**这是天文学 rawvideo 抓帧的关键路径**。详见 `FfmpegSnapshotRunner.cs:10-31` 注释，必须用 `thumbnail` filter 而非 `-ss T -i input`：

```
Step 1/3: FfprobeRunner.ProbeAsync(input)
   ├─ exit ≠ 0 → throw InvalidOperationException
   └─ exit = 0 但 no video stream → return null
Step 2/3: 解析 JSON stdout → VideoProbeResult(Duration, Width, Height, Codec, FrameRate)
Step 3/3: FfmpegSnapshotRunner.SnapshotAsync(input, output, duration*0.05, W, H)
   └─ 命令: ffmpeg -y -i {input} -vf "thumbnail=10000,scale=W:H"
                     -frames:v 1 -update 1 {output}
```

#### 为什么用 thumbnail filter 而不是 `-ss T`

> 关键点（详 `FfmpegSnapshotRunner.cs:10-31`）：**rawvideo AVI 没有关键帧表**。
> - `-ss` 在 `-i` 之前 → ffmpeg input seek 走 demuxer → demuxer 找不到关键帧 → ffmpeg **强制忽略**，从头解码
> - 用 thumbnail filter：扫 N 帧（默认 10000） → 找出色彩变化最大的那一帧 → 自动跳过相机初始化阶段的黑帧
> - 对 rawvideo（76fps 视频）覆盖 130 秒；对 25fps 视频覆盖 400 秒；足够天文 capture

#### ffprobe 失败降级

`ThumbnailService.cs:131-148`：ffprobe 抛错 → catch 后 **尝试用 SkiaSharp 解码视频**（对视频几乎肯定失败，但写日志能看到尝试过）。最后统一返回 `null` 让 UI 用占位符。

---

## 3. FFmpeg / FFprobe 二进制路径（`FFmpegConfigurator.cs`）

### 3.1 设计

```csharp
public static class FFmpegConfigurator {
    private static string? _ffmpegPath;   // 静态，进程级单例
    private static string? _ffprobePath;

    public static void Apply(string? ffmpegPath, string? ffprobePath) {
        _ffmpegPath = Normalize(ffmpegPath);
        _ffprobePath = Normalize(ffprobePath);
        ValidateBinary("ffmpeg", _ffmpegPath);
        ValidateBinary("ffprobe", _ffprobePath);
    }

    public static string GetFFmpegBinaryPath() =>
        !string.IsNullOrEmpty(_ffmpegPath) ? _ffmpegPath : ResolveFromPath(...);

    public static string GetFFprobeBinaryPath() =>
        !string.IsNullOrEmpty(_ffprobePath) ? _ffprobePath : ResolveFromPath(...);
}
```

- 配置：用户在 Settings 页填路径 → `SettingsViewModel.Save()` 调 `FFmpegConfigurator.Apply(...)`（`SettingsViewModel.cs:398`）— **不需要重启**。
- Fallback：空字符串 → `ResolveFromPath` 扫 `PATH`（`FFmpegConfigurator.cs:75-93`）。
- ValidateBinary：非空路径但文件不存在 → `Trace.WriteLine` WARN（不阻塞用户保存）。
- `ffmpeg` 和 `ffprobe` **各自独立**字段，允许两个二进制在不同目录（`AppConfig.cs:9-15`）。

### 3.2 关键调用点

| 调用方 | 取路径方式 |
|---|---|
| `FfprobeRunner.ProbeAsync` | `FFmpegConfigurator.GetFFprobeBinaryPath()` |
| `FfmpegSnapshotRunner.SnapshotAsync` | `FFmpegConfigurator.GetFFmpegBinaryPath()` |
| `App.OnFrameworkInitializationCompleted` | 启动时调 `Apply` |
| `SettingsViewModel.Save` | 保存时调 `Apply` |

### 3.3 路径校验（`SettingsViewModel.cs:336-365`）

保存前两道防御：
- 是不是目录？（`Directory.Exists`）→ 报错「FFmpeg 路径不能是目录」
- 是不是文件？（`File.Exists`）→ 报错「FFmpeg 文件不存在」

### 3.4 已废弃：`FFMpegCore` 包

`FFmpegConfigurator.cs:7-18` 自承认账：早期用 `FFMpegCore` 5.1.0，但 `SnapshotAsync` **强制 PNG codec + 自动改 .png 后缀**。已**彻底移除**该包，改直接 Process.Start 调命令行。

---

## 4. FfprobeRunner（`Services/FfprobeRunner.cs`）

### 4.1 命令

```
ffprobe -v quiet -print_format json -show_format -show_streams <input>
```

stdout 是标准 JSON，stderr 是诊断信息。退出码非 0 抛 `InvalidOperationException(stderr)`。

### 4.2 解析（`ParseProbeJson`）

从 JSON 拿：
- `format.duration`（字符串如 "24.040000"）→ `double.TryParse(NumberStyles.Float, InvariantCulture)`
- 第一个 video stream：`codec_type == "video"`，取 `width / height / codec_name / r_frame_rate`
- `r_frame_rate` 是 `"25/1"` 或 `"30000/1001"`，自定义 `ParseFrameRate` 解

### 4.3 跨平台性

- **同一份命令** 三平台一致
- 默认走 `Process.Start`，不依赖任何跨平台 wrapper
- `CreateNoWindow = true`（Windows 下隐藏控制台窗）

---

## 5. FfmpegSnapshotRunner（`Services/FfmpegSnapshotRunner.cs`）

### 5.1 命令

```
ffmpeg -y -i {input} -vf "thumbnail=10000,scale={W}:{H}" -frames:v 1 -update 1 {output}
```

- `-y`：覆盖输出（必须有，否则文件已存在会失败）
- `-vf "thumbnail=N"`：扫前 N 帧，挑色彩变化最大的那帧
- `thumbnail=10000`：对 rawvideo 一帧 6MB，扫 1 万帧 ≈ 60GB 磁盘读，但 rawvideo 解码只是字节拷贝（秒级返回）；对 76fps 视频覆盖 ~130s，对 25fps 覆盖 ~400s
- `-frames:v 1`：保险起见再限制（thumbnail filter 选完只剩 1 帧，但显式更稳）
- `-update 1`：image2 muxer 默认期望序列帧 (`%03d` 等)，**单图输出必须告诉它"这是单张"**

### 5.2 `captureTime` 参数保留

`FfmpegSnapshotRunner.cs:35-38`：接口仍然保留 `TimeSpan captureTime`，但**实际不使用**——thumbnail filter 自动选最佳帧。保留是为调用方接口稳定。

### 5.3 退出码处理

- `proc.ExitCode != 0` → throw InvalidOperationException(stderr)
- 进程必须 `await proc.WaitForExitAsync(ct)` —— CT 触发会把 ffmpeg 强杀，半截输出文件保留
- stderr 截断打印到 Trace

---

## 6. ImageCacheService（`Services/ImageCacheService.cs`）

### 6.1 内存 Bitmap 缓存

```csharp
private static readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();
private static readonly SemaphoreSlim _semaphore = new(Environment.ProcessorCount * 2);

public async Task<Bitmap?> LoadImageAsync(string? path, CancellationToken ct = default) {
    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
    if (_cache.TryGetValue(path, out var cachedTask)) return await cachedTask;
    await _semaphore.WaitAsync(ct);
    try {
        if (_cache.TryGetValue(path, out cachedTask)) return await cachedTask;  // double-check
        var task = Task.Run(() => {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }, ct);
        _cache.TryAdd(path, task);
        return await task;
    } finally { _semaphore.Release(); }
}
```

### 6.2 设计要点

- **Task 缓存而非 Bitmap**：按 path 锁，第一个 await 进 Bitmap；后面所有 await 共享同一个 Task → 不会重复 IO
- **双检查**（先 `TryGetValue` 再进锁，再 `TryGetValue`）— 标准双重锁，单线程竞争零开销
- **信号量限并发**：最多 `CPU*2` 个同时解码（解码 SkiaSharp bitmap 吃 CPU）
- **进程级单例**：`_cache` 和 `_semaphore` 都是 static — 全程共享
- **`ClearCache()` 静态方法**：清 cache + 释放已完成 Bitmap（不强制每次退出调；GC 自然回收）

### 6.3 调用方

`Converters/FilePathToBitmapConverter.cs`（在 `09-ui-commons.md`）— 缩略图展示走这个 cache。

---

## 7. 支持的扩展名

### 7.1 图片（`MediaRepository.cs:162-166` + `ThumbnailService.cs:197-204`）

```csharp
{ ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp", ".heic",
  ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".pef" }
```

> 相机 RAW 格式：CR2/CR3 (Canon)、NEF (Nikon)、ARW (Sony)、DNG (通用)、RAF (Fuji)、ORF (Olympus)、RW2 (Panasonic)、PEF (Pentax)

SkiaSharp 解码 RAW 格式依赖编译时绑定 — 天文用户多数用 `.cr2/.nef/.arw`，已通过实测。

### 7.2 视频（`MediaRepository.cs:168-171` + `ThumbnailService.cs:198-204`）

```csharp
{ ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".mpg", ".mpeg" }
```

> **rawvideo AVI** 是天文 capture 主力，上面 thumbnail filter 方案专门为此。

### 7.3 上传扩展名白名单（`UploadServerService.cs:24-27` + `PublicRelayService` Go relay）

LAN 上传独立白名单（更宽）：

```csharp
{ ".jpg", ".jpeg", ".png", ".raw", ".avi", ".mp4", ".mov", ".mkv", ".webm", ".m4v", ".mpg", ".mpeg" }
```

公网上传由 Go relay 服务端接受，HTML 前端 `accept` 限定 `image/*,video/*`，后端无显式白名单（粗暴接收任意 multipart 文件）— 后续可加。

---

## 8. 性能特征

| 阶段 | 1k 文件（500 张 + 500 视频）|
|---|---|
| 第一遍枚举 | ~0.5s（SSD 上的 `EnumerateFiles`）|
| 第二遍 INSERT | ~3-5s（单连接串行）|
| 缩略图（500 张图片）| ~10s（SkiaSharp resize 多线程 = CPU 数）|
| 缩略图（500 段视频）| **分钟级**（每个 ffmpeg 进程 1-3s，单文件串行！）|
| 重扫（缓存命中）| 仅第一遍快，第二遍全部更新 `scanned_at` |

视频缩略图**故意串行**（参见 `02-data-layer.md` §3.3）—— 不并行，避免 ffmpeg 同时跑把 CPU 吃满。

---

## 9. 改这个模块前的检查清单

| 改动 | 涉及 | 验证 |
|---|---|---|
| 新加 RAW 格式 | `MediaRepository.ImageExtensions` + 测试解码 | SkiaSharp 真的能解（拿一张该格式文件实测） |
| 改 thumbnail filter N 值 | `FfmpegSnapshotRunner.ThumbnailScanFrames` | rawvideo 130s 够不够？大文件覆盖率？ |
| 改缩略图尺寸 | `ThumbnailService.ThumbnailWidth/Height` + UI Image Width/Height | 网格密度看着舒服，磁盘占用别太大 |
| 换 SkiaSharp → ImageSharp | 包替换 + 大量代码 | Windows 上 ImageSharp 解码更稳但 Linux 依赖更多 native |
| 加 rawvideo 之外的 seek 策略 | `FfmpegSnapshotRunner` 新增命令分支 | 长视频（>10 分钟）thumbnail filter 不够怎么办？ |
| 缩略图并行化 | `GenerateThumbnailsAsync` | CPU 是否够吃？4C/8T 跑 4 个 ffmpeg 会不会卡 |
| 缩略图加密 / 水印 | `Generate*Thumbnail` 后置 | 性能影响、批量重新生成策略 |

---

## 10. 已知陷阱（详见 `10-trap-book.md`）

- **rawvideo AVI input seek 无效** → thumbnail filter 兜底（已固化）
- **FFMpegCore 强制 PNG** → 已经移除，自己 Process.Start
- **ffprobe 失败时静默返回 null** → 必须 fallback 到 SKBitmap.Decode 才写日志
- **SkiaSharp 在 macOS 上需要 `SkiaSharp.NativeAssets.macOS`**（已加包依赖）
- **缩略图命名冲突**：hash 用简单 string hash 而非 SHA1/CRC — 极端项目目录结构下可能冲突
- **空文件（0 字节）img**：`SKBitmap.Decode` 返 null，`File.Exists(thumb)==false` 不报错
- **视频文件未结束写入**（相机还在录）：ffprobe 可能挂在 global_header 解析 → 设 Process timeout
