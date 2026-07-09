# 0.11 — 灯箱预览（Lightbox）与视频播放

> 对应需求量文档 `doc/demand/02-gallery-improve.md` §「无大图预览/灯箱」。
> 核心改动：新建 `LightboxWindow` + `LightboxViewModel`，替代双击时调用系统默认应用的行为。

---

## 1. 模块边界

```
GalleryViewModel
  └─ 双击 photo tile → 打开 LightboxWindow（不再调 OpenFileCommand）
       ├─ LightboxViewModel（独立 Window 的 DataContext）
       │    ├─ 接收 List<MediaFile> + 当前索引
       │    ├─ 管理翻页、缩放（图片）、播放控制（视频）
       │    └─ 键盘快捷键处理
       └─ LightboxWindow.axaml
            ├─ Image 控件（MediaType.Image）
            └─ VideoView（LibVLCSharp.Avalonia，MediaType.Video）

依赖链：
  LightboxViewModel
    ├─ ISystemShellService  (RevealInFolder / OpenWithDefaultApp — 兜底)
    └─ FfprobeRunner        (视频元信息：时长、分辨率、编码)

新增 NuGet 依赖：
  LibVLCSharp.Avalonia  (VideoLAN 官方，LGPL 2.1)
  VideoLAN.LibVLC.Mac   (macOS 原生 dylib，随 .app 打包)
  VideoLAN.LibVLC.Windows / VideoLAN.LibVLC.Linux (条件引用)
```

---

## 2. 新增文件清单

| 文件 | 用途 |
|------|------|
| `Views/LightboxWindow.axaml` | 全屏预览窗口 UI |
| `Views/LightboxWindow.axaml.cs` | 键盘事件、窗口生命周期 |
| `ViewModels/LightboxViewModel.cs` | 翻页、缩放、播放状态 |
| `Converters/VideoMetadataConverters.cs` | `VideoProbeResult` → UI 文本转换 |

**修改文件**：

| 文件 | 改动 |
|------|------|
| `StartTooler.csproj` | 加 `LibVLCSharp.Avalonia` + 平台包 |
| `Views/GalleryView.axaml.cs` | `OnPhotoTileDoubleTapped` 改为打开 LightboxWindow |
| `ViewModels/GalleryViewModel.cs` | 加 `PreviewCommand`，替换双击调用 |

---

## 3. LightboxViewModel

### 3.1 属性

| 属性 | 类型 | 用途 |
|------|------|------|
| `Files` | `IReadOnlyList<MediaFile>` | 当前视图的全部文件（日期或标签） |
| `CurrentIndex` | `int` | 当前显示的文件索引 |
| `CurrentFile` | `MediaFile?` | 派生，`Files[CurrentIndex]`（get-only） |
| `IsImage` | `bool` | `CurrentFile?.MediaType == Image`（get-only） |
| `IsVideo` | `bool` | `CurrentFile?.MediaType == Video`（get-only） |
| `Scale` | `double` | 图片缩放倍率，默认 1.0（`FitUniform`） |
| `IsPlaying` | `bool` | 视频播放中（get-only，绑定 VideoView） |
| `Position` | `TimeSpan` | 视频当前播放位置 |
| `Duration` | `TimeSpan` | 视频总时长 |
| `VideoMeta` | `VideoProbeResult?` | ffprobe 解析结果（分辨率和编码） |
| `Title` | `string` | 窗口标题，`{N}/{Total} — {FileName}`（get-only） |

### 3.2 命令

| Command | 行为 |
|---------|------|
| `GoNext` | `CurrentIndex < Files.Count-1` 时 `CurrentIndex++`，停止视频、重置缩放 |
| `GoPrev` | `CurrentIndex > 0` 时 `CurrentIndex--`，停止视频、重置缩放 |
| `ZoomIn` | `Scale += 0.25`，上限 5.0（仅图片模式） |
| `ZoomOut` | `Scale -= 0.25`，下限 0.25（仅图片模式） |
| `ZoomReset` | `Scale = 1.0`（仅图片模式） |
| `Close` | 关闭 LightboxWindow |
| `OpenExternally` | 兜底：调 `SystemShellService.OpenWithDefaultApp` |

### 3.3 缩放逻辑（仅图片）

```
鼠标滚轮：
  WheelUp   → Scale += 0.1（上限 5.0）
  WheelDown → Scale -= 0.1（下限 0.25）

双击：
  若 Scale == 1.0 → Scale = 2.0（100% 放大）
  否则           → Scale = 1.0（Fit 还原）

变换：
  <Image RenderTransform="ScaleTransform(Scale, Scale)"
         RenderTransformOrigin="0.5,0.5" />
```

### 3.4 视频控制逻辑

```
进入视频 → 自动加载 ffprobe 元信息 → 显示在信息面板
用户点击播放按钮 / Space → Play()
翻页 → Stop() + 释放 MediaPlayer
关闭窗口 → Stop() + Dispose LibVLC
```

---

## 4. LightboxWindow UI

### 4.1 窗口属性

```
WindowState = FullScreen
Background  = "#0A0E1A"（Bk.Surface 或纯黑）
WindowStartupLocation = CenterScreen
SystemDecorations = Full  （保留标题栏，Esc 也能关）
ShowInTaskbar = True
```

### 4.2 布局结构

```
┌──────────────────────────────────────────────┐
│ 标题栏: {N}/{Total} — {FileName}    [✕ 关闭] │
├──────────────────────────────────────────────┤
│                                      [缩放]  │
│          ←  [ 媒体内容区域  ]  →     信息面板  │
│                                      [同步]  │
│                                      [标签]  │
├──────────────────────────────────────────────┤
│ 底部栏: ⏮ 上一张  │  ▶/⏸ 播放  │  ⏭ 下一张   │
│        [进度条 / 缩放滑块]        [打开外部]   │
└──────────────────────────────────────────────┘
```

### 4.3 媒体内容区域

**图片模式** (`IsImage == true`)：

```xml
<ScrollViewer HorizontalScrollBarVisibility="Auto"
              VerticalScrollBarVisibility="Auto">
    <Image Source="{Binding CurrentFile.ThumbnailPath, Converter=FilePathToBitmap}"
           Width="{Binding CurrentFile.Width}"   <!-- 需从 ffprobe 或 EXIF 获取 -->
           Height="{Binding CurrentFile.Height}"
           RenderTransform="scaleTransform(Scale)"
           RenderTransformOrigin="0.5,0.5" />
</ScrollViewer>
```

> **注意**：缩略图分辨率和原图不同。灯箱需用原图路径而非缩略图路径。`FilePathToBitmapConverter` 已支持任意路径。

**视频模式** (`IsVideo == true`)：

```xml
<vlc:VideoView x:Name="VideoPlayer"
               MediaPlayer="{Binding MediaPlayer}"
               VerticalAlignment="Center"
               HorizontalAlignment="Center" />
```

视频未加载时显示缩略图占位，加载后自动替换。

### 4.4 右侧信息面板

**图片**：

| 行 | 内容 |
|----|------|
| 文件名 | `CurrentFile.FileName` |
| 拍摄时间 | `CurrentFile.ShotAtDateTime` |
| 文件大小 | `CurrentFile.FileSize`，格式化 |
| 尺寸 | EXIF 或 SkiaSharp 解码得到 |
| 同步状态 | `SyncStatusValue` → 云+✓ / 云+↓ / 灰云 |
| AI 标签 | `CurrentFile.Tags`（逗号分隔） |
| AI 评分 | `CurrentFile.Score`（若存在） |

**视频**：

| 行 | 内容 |
|----|------|
| 文件名 | 同图片 |
| 时长 | `VideoMeta.Duration` → `m:ss` |
| 分辨率 | `1920×1080`（来自 `VideoMeta.Width × Height`） |
| 编码 | `VideoMeta.Codec`（如 h264、hevc） |
| 帧率 | `VideoMeta.FrameRate` → `29.97 fps` |
| 文件大小 | 同图片 |
| 同步状态 | 同图片 |
| AI 标签 | 同图片 |

### 4.5 底部控制栏

```
[⏮ 上一张] [▶/⏸ 播放/暂停] [⏭ 下一张]

  进度条：████████░░░░░░░░  00:32 / 02:15    (仅视频)
  缩放：  ◀──●────────▶  100%               (仅图片)

[在文件夹中显示] [用默认应用打开]
```

---

## 5. 键盘快捷键

| 键 | 图片模式 | 视频模式 |
|----|----------|----------|
| `←` | 上一张（到首张停止） | 上一张（停止当前播放） |
| `→` | 下一张（到尾张停止） | 下一张（停止当前播放） |
| `↑` | 无操作 | 无操作 |
| `↓` | 无操作 | 无操作 |
| `Space` | 无操作 | 播放 / 暂停 |
| `Enter` | 无操作 | 播放 / 暂停 |
| `+` / `=` | 放大 | 无操作 |
| `-` | 缩小 | 无操作 |
| `0` | 重置缩放（Fit） | 无操作 |
| `Esc` | 关闭窗口 | 关闭窗口（先停止播放） |
| `F` | 切换全屏/窗口模式 | 同左 |

---

## 6. LightboxWindow 生命周期

### 6.1 打开

```
GalleryView.OnPhotoTileDoubleTapped(sender, e)
  ├─ file = sender.DataContext as MediaFile
  ├─ vm = DataContext as GalleryViewModel
  ├─ files = vm.CurrentMediaFiles.ToList()
  ├─ index = files.IndexOf(file)
  ├─ lightboxVm = new LightboxViewModel(files, index, systemShell)
  ├─ window = new LightboxWindow { DataContext = lightboxVm }
  └─ window.Show()  // 非模态，允许用户同时看 Gallery
```

### 6.2 翻页

```
GoNext():
  ├─ if IsVideo → VideoPlayer.Stop()
  ├─ CurrentIndex++
  ├─ Scale = 1.0  // 重置缩放
  └─ if IsVideo → 异步 LoadVideoMeta() + 等待用户手动播放

GoPrev():
  ├─ if IsVideo → VideoPlayer.Stop()
  ├─ CurrentIndex--
  ├─ Scale = 1.0
  └─ 同 GoNext
```

### 6.3 关闭

```
Close():
  ├─ if IsVideo && IsPlaying → Stop()
  ├─ MediaPlayer?.Dispose()
  ├─ LibVLC?.Dispose()
  └─ window.Close()
```

---

## 7. LibVLC 初始化

### 7.1 在 App 启动时初始化

```csharp
// Program.cs 或 App.axaml.cs
using LibVLCSharp;

public override void OnFrameworkInitializationCompleted()
{
    Core.Initialize();  // 自动从 NuGet 加载 libvlc dylib/dll
    base.OnFrameworkInitializationCompleted();
}
```

`Core.Initialize()` 默认从输出目录查找原生库。`VideoLAN.LibVLC.Mac` NuGet 包已自动将 `libvlc.dylib` + `libvlccore.dylib` + `plugins/` 复制到输出目录。

### 7.2 LightboxViewModel 中使用

```csharp
private LibVLC? _libVLC;
private MediaPlayer? _mediaPlayer;

public MediaPlayer? MediaPlayer
{
    get => _mediaPlayer;
    set => SetProperty(ref _mediaPlayer, value);
}

// 切换到视频时
async Task LoadVideoAsync(string filePath)
{
    _libVLC?.Dispose();
    _mediaPlayer?.Dispose();

    _libVLC = new LibVLC();
    var media = new Media(_libVLC, new Uri(filePath));
    _mediaPlayer = new MediaPlayer(_libVLC) { Media = media };
    OnPropertyChanged(nameof(MediaPlayer));

    // 异步获取元信息
    VideoMeta = await FfprobeRunner.ProbeAsync(filePath);
}
```

> **决策**：每个视频文件创建新的 `LibVLC` + `MediaPlayer` 实例，翻页时释放旧的。避免跨文件 Seek 状态不一致。`LibVLC` 实例创建成本低（~10ms）。

---

## 8. 边界情况

| 场景 | 处理 |
|------|------|
| 本地文件不存在 | 显示缩略图 + "本地文件缺失" 提示 + "从云端下载" 按钮 |
| 视频文件 ffprobe 失败 | `VideoMeta = null`，信息面板只显示文件名和大小 |
| ffmpeg 未安装 | ffprobe 抛异常 → `VideoMeta = null`，不阻塞播放 |
| 损坏的视频文件 | VLC 内置容错，可能显示黑屏或错误帧，不崩 |
| 窗口打开时 Gallery 切日期 | LightboxWindow 持有 `Files` 快照，不受影响 |
| 用户拖拽窗口到第二个显示器 | WindowState 保持当前模式，不强制全屏 |
| 内存：连续翻 100 张 | 每次翻页释放旧 `LibVLC`/`MediaPlayer`，无泄漏 |
| 长按方向键快速翻页 | 每次翻页 Stop() + Dispose() + new LibVLC()，需加 200ms debounce |

---

## 9. NuGet 依赖详情

```xml
<!-- .csproj 新增 -->
<ItemGroup>
    <PackageReference Include="LibVLCSharp.Avalonia" Version="3.10.0" />
</ItemGroup>

<!-- macOS（当前主要目标平台） -->
<ItemGroup Condition="$([MSBuild]::IsOSPlatform('macOS'))">
    <PackageReference Include="VideoLAN.LibVLC.Mac" Version="3.0.21" />
</ItemGroup>
```

- `LibVLCSharp.Avalonia` 3.10.0 依赖 `LibVLCSharp >= 3.4.9` + `Avalonia >= 0.10.0`（已满足）
- `VideoLAN.LibVLC.Mac` 不依赖任何 .NET 包，纯原生 dylib 分发
- macOS 增量 ~50MB（dylib + plugins），编译产物不增加托管代码体积

---

## 10. 与现有系统的关系

### 10.1 不替换 OpenFileCommand

`OpenFileCommand` 仍保留，用于本地缺失时下载+打开的流程。灯箱仅替代「双击本地已存在文件 → 系统默认应用打开」的行为。

### 10.2 不修改 MediaFile

灯箱不引入新 DB 字段。视频元信息（时长/分辨率/编码）通过 ffprobe **运行时获取**，利用已有 `FfprobeRunner`。

### 10.3 不影响多选模式

双击触发的 `OnPhotoTileDoubleTapped` 先于单击的 `ToggleSelectionCommand`。双击时不进入多选，直接打开灯箱。
