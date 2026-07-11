# 0.11 — 灯箱预览（Lightbox）

> 对应需求量文档 `doc/demand/02-gallery-improve.md` §「无大图预览/灯箱」。
> 核心改动：新建 `LightboxWindow` + `LightboxViewModel`，统一预览图片 + 视频缩略图。
> 图片全尺寸加载 + 缩放；视频只显示缩略图（不内嵌播放 —— 天文 rawvideo AVI 单文件 GB 级，不适合 in-app 播放），用户可在灯箱底栏点「打开外部」走系统播放器。

---

## 1. 模块边界

```
GalleryViewModel
  └─ 双击任意 media tile（图片 / 视频）→ 打开 LightboxWindow
        ├─ LightboxViewModel（独立 Window 的 DataContext）
        │    ├─ 接收 List<MediaFile> + 当前索引（图片 + 视频混合）
        │    ├─ 管理翻页、缩放（仅图片）、信息面板
        │    └─ 键盘快捷键处理
        └─ LightboxWindow.axaml
             ├─ 图片模式：ScrollViewer + 原图 Image + ScaleTransform
             └─ 视频模式：缩略图 Image + 居中 ▶ overlay + 「打开外部播放」提示

依赖链：
  LightboxViewModel
    └─ ISystemShellService  (OpenWithDefaultApp — 兜底外放用)

新增 NuGet 依赖：无（不引入 LibVLC）
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Views/LightboxWindow.axaml` | 灯箱窗口 UI（图片 + 视频两种模式） | 新增 |
| `Views/LightboxWindow.axaml.cs` | 键盘事件、滚轮缩放、双击缩放、Closed 事件 | 新增 |
| `ViewModels/LightboxViewModel.cs` | 翻页、缩放、信息面板派生属性 | 新增 |
| `Services/ImageDimensionProbe.cs` | SkiaSharp `SKCodec` 异步读图片 header 取原图宽高，带进程内缓存 | 新增 |
| `Converters/LightboxConverters.cs` | 灯箱专用：原图加载/文件大小/时间/同步状态 | 新增 |

**修改文件**：

| 文件 | 改动 |
|------|------|
| `StartTooler.csproj` | 不需要改动（不引入新包） |
| `Views/GalleryView.axaml.cs` | `OnPhotoTileDoubleTapped` 统一调 `PreviewCommand`（图片/视频均进灯箱） |
| `ViewModels/GalleryViewModel.cs` | `PreviewCommand` 不过滤视频（混合预览）；`OpenInExternalPlayerCommand` 移除（lightbox 内置「打开外部」按钮即可） |
| `App.axaml.cs` | 不引入 LibVLC 初始化（与原状态一致） |
| `Themes/Icons.axaml` | 补灯箱 UI 需要的图标：ChevronLeft / ChevronRight / X / ZoomIn / ZoomOut / ExternalLink / FolderOpen |

---

## 3. LightboxViewModel

### 3.1 属性

| 属性 | 类型 | 用途 |
|------|------|------|
| `Files` | `IReadOnlyList<MediaFile>` | 当前视图的全部文件（图片 + 视频混合） |
| `CurrentIndex` | `int` | 当前显示的文件索引 |
| `CurrentFile` | `MediaFile?` | 派生，`Files[CurrentIndex]`（get-only） |
| `IsImage` | `bool` | `CurrentFile?.MediaType == Image`（get-only） |
| `IsVideo` | `bool` | `CurrentFile?.MediaType == Video`（get-only） |
| `Scale` | `double` | 图片缩放倍率，默认 1.0（仅图片模式生效） |
| `ImageWidth` | `double?` | 图片模式原图宽（`ImageDimensionProbe` 异步探测；null 时退化） |
| `ImageHeight` | `double?` | 图片模式原图高（同上） |
| `Title` | `string` | 窗口标题，`{N}/{Total} — {FileName}`（get-only） |

### 3.2 命令

| Command | 行为 |
|---------|------|
| `GoNext` | `CurrentIndex < Files.Count-1` 时 `CurrentIndex++`，`Scale = 1.0`，图片模式重探测原图尺寸 |
| `GoPrev` | `CurrentIndex > 0` 时 `CurrentIndex--`，同上 |
| `ZoomIn` | `Scale += 0.25`，上限 5.0（仅图片模式） |
| `ZoomOut` | `Scale -= 0.25`，下限 0.25（仅图片模式） |
| `ZoomReset` | `Scale = 1.0`（仅图片模式） |
| `Close` | 关闭 LightboxWindow |
| `OpenExternally` | 调 `SystemShellService.OpenWithDefaultApp`（图片：系统看图器；视频：系统视频播放器） |
| `RevealInFolder` | 在 Finder/Explorer 中显示文件 |

### 3.3 图片模式缩放逻辑

```
鼠标滚轮：
  WheelUp   → Scale += 0.1（上限 5.0）
  WheelDown → Scale -= 0.1（下限 0.25）

双击图片：
  若 Scale == 1.0 → Scale = 2.0（200% 放大）
  否则           → Scale = 1.0（Fit 还原）

变换：
  <Image Source="{Binding CurrentFile, Converter=FilePathToBitmap}"
         Width="{Binding ImageWidth}"
         Height="{Binding ImageHeight}"
         Stretch="Uniform">
    <Image.RenderTransform>
      <ScaleTransform ScaleX="{Binding Scale}" ScaleY="{Binding Scale}" />
    </Image.RenderTransform>
  </Image>
```

> **视频模式不缩放**：缩略图本身就是压缩图，再放大糊。固定渲染在 viewport 中心，居中显示 ▶ overlay（spec §4.3 视频模式）。

### 3.4 翻页节流

```
GoNext / GoPrev:
  ├─ _lastNavTick = Environment.TickCount64
  ├─ if (now - _lastNavTick) < 200ms → 丢弃请求（[Lightbox] nav throttled 日志）
  └─ 否则正常翻页
```

避免长按方向键瞬间触发 10+ 次缩放重置 + 原图尺寸探测。

---

## 4. LightboxWindow UI

### 4.1 窗口属性

```
WindowState = Maximized            （macOS 原生 FullScreen 会隐藏标题栏，与「Esc 关闭」冲突；用 Maximized 折中）
Background  = "#0A0E1A"            （近黑）
WindowStartupLocation = CenterScreen
SystemDecorations = Full           （保留标题栏）
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
│                                      [评分]  │
├──────────────────────────────────────────────┤
│ 底部栏: ⏮ 上一张  │  ⊕/⊖ 缩放  │  ⏭ 下一张   │
│        [缩放滑块 100%]           [外部打开]   │
└──────────────────────────────────────────────┘
```

### 4.3 媒体内容区域

**图片模式**（`IsImage == true`）：

```xml
<Panel IsVisible="{Binding IsImage}">
  <ScrollViewer HorizontalScrollBarVisibility="Auto"
                VerticalScrollBarVisibility="Auto"
                PointerWheelChanged="OnImagePointerWheelChanged"
                DoubleTapped="OnImageDoubleTapped">
    <Panel HorizontalAlignment="Center"
           VerticalAlignment="Center"
           Width="{Binding ImageWidth, FallbackValue=0}"
           Height="{Binding ImageHeight, FallbackValue=0}"
           MinWidth="100" MinHeight="100">
      <Image Source="{Binding CurrentFile, Converter={x:Static conv:LightboxConverters.MediaFileToOriginalBitmap}}"
             Stretch="Uniform"
             RenderTransformOrigin="0.5,0.5">
        <Image.RenderTransform>
          <ScaleTransform ScaleX="{Binding Scale}" ScaleY="{Binding Scale}" />
        </Image.RenderTransform>
      </Image>
    </Panel>
  </ScrollViewer>
</Panel>
```

**视频模式**（`IsVideo == true`）：

```xml
<Panel IsVisible="{Binding IsVideo}">
  <!-- 缩略图本体：thumbnail path 直接绑（已生成好的 256x192/类似尺寸） -->
  <Image Source="{Binding CurrentFile.ThumbnailPath, Converter={x:Static conv:LightboxConverters.FilePathToBitmap}}"
         Stretch="Uniform"
         MinWidth="400" MinHeight="300"
         HorizontalAlignment="Center"
         VerticalAlignment="Center" />

  <!-- 居中 ▶ 播放 overlay（视频标记 + 引导打开外部） -->
  <Border HorizontalAlignment="Center" VerticalAlignment="Center"
          Background="#CC0A0E1A" CornerRadius="48"
          Padding="40" IsHitTestVisible="False">
    <StackPanel Spacing="12" HorizontalAlignment="Center">
      <PathIcon Data="{StaticResource Icon.Play}"
                Width="64" Height="64"
                Foreground="#E6FFFFFF" />
      <TextBlock Text="视频文件 · 点击底栏「打开外部」播放"
                 FontSize="13"
                 Foreground="#E6FFFFFF"
                 HorizontalAlignment="Center" />
    </StackPanel>
  </Border>
</Panel>
```

> **不缩放**：视频模式固定渲染，缩放控件对视频隐藏（IsVisible="{Binding IsImage}" 控制底栏缩放滑块）。
> **缩略图加载失败**：Image Source 为 null 时自动空白，居中 overlay 仍可见，用户照样能看到「视频文件」提示。
> **打开外部**：底栏的「打开外部」按钮对图片/视频均生效。视频调系统默认播放器（VLC/QuickTime/IINA 等用户自选）。

### 4.4 右侧信息面板

**图片**：

| 行 | 内容 |
|----|------|
| 文件名 | `CurrentFile.FileName` |
| 拍摄时间 | `CurrentFile.ShotAtDateTime` |
| 文件大小 | `CurrentFile.FileSize`，格式化 |
| 尺寸 | `ImageWidth × ImageHeight`（来自 ImageDimensionProbe） |
| 同步状态 | `SyncStatusValue` → 云+✓ / 云+↓ / 灰云 |
| AI 标签 | `CurrentFile.Tags`（逗号分隔） |
| AI 评分 | `CurrentFile.Score`（若存在） |

**视频**：同图片，但「尺寸」行隐藏（视频没探测原始分辨率，thumbnail 尺寸对用户没意义）。

### 4.5 底部控制栏

```
[⏮ 上一张] [⊖ 缩小] [缩放: 100%] [⊕ 放大] [⏭ 下一张]    ← 仅图片模式可见缩放控件

[在 Finder 中显示] [用默认应用打开]
```

---

## 5. 键盘快捷键

| 键 | 图片模式 | 视频模式 |
|----|----------|----------|
| `←` | 上一张 | 上一张 |
| `→` | 下一张 | 下一张 |
| `+` / `=` | 放大 | 无操作 |
| `-` | 缩小 | 无操作 |
| `0` | 重置缩放（Fit） | 无操作 |
| `Esc` | 关闭窗口 | 关闭窗口 |
| `F` | 切换 Maximized / Normal | 同左 |
| `Space` | 无操作 | **绑定「打开外部」**（视频场景下 Space 直接播放） |

> **视频 Space 映射 Space → 打开外部**：用户对视频的最自然期望就是「Space 播放」，但实际是调外部播放器。键盘映射等价于点底栏「打开外部」按钮。

---

## 6. LightboxWindow 生命周期

### 6.1 打开

```
GalleryView.OnMediaTileDoubleTapped(sender, e)
  ├─ file = sender.DataContext as MediaFile
  ├─ vm = DataContext as GalleryViewModel
  ├─ files = vm.CurrentMediaFiles.ToList()    // 图片 + 视频混合
  ├─ index = files.IndexOf(file)
  ├─ lightboxVm = new LightboxViewModel(files, index, systemShell)
  ├─ window = new LightboxWindow { DataContext = lightboxVm }
  └─ window.Show()  // 非模态，允许用户同时看 Gallery
```

### 6.2 翻页

```
GoNext() / GoPrev():
  ├─ 200ms 节流检查（见 §3.4）
  ├─ 边界检查（首张/末张停止）
  ├─ Scale = 1.0
  ├─ CurrentIndex 改动 → 触发 CurrentFile 变化 → Image 重新绑定
  ├─ LightboxViewModel 订阅 CurrentFile.PropertyChanged
  │    ├─ IsImage=true  → ImageDimensionProbe.ProbeAsync(newPath) → 更新 ImageWidth/Height
  │    └─ IsVideo=true  → 不探测（缩略图已是生成好的）
  └─ Title 更新
```

### 6.3 关闭

```
LightboxWindow.OnClosed:
  └─ LightboxViewModel.Current = null（静态引用清理，避免泄漏）

RelayCommand Close:
  └─ window.Close()
```

> 不需要 Dispose LibVLC/MediaPlayer（spec 不引入）。`ImageDimensionProbe` 的进程内缓存随 app 退出自动释放。

---

## 7. ImageDimensionProbe

### 7.1 用途

图片模式 `Image` 的 `Width/Height` 需要原图实际尺寸（而非缩略图尺寸）。`MediaFile` 没存这个字段，运行时用 `SKCodec` 解码图片 header 取 `Info.Width/Height`，避免把整个原图加载进内存。

### 7.2 实现要点

```csharp
public sealed class ImageDimensionProbe
{
    private readonly ConcurrentDictionary<string, (int W, int H)> _cache = new();

    public async Task<(int Width, int Height)> ProbeAsync(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;

        var result = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            return codec is null
                ? (0, 0)
                : ((int)codec.Info.Width, (int)codec.Info.Height);
        });

        if (result.W > 0) _cache[path] = result;
        return result;
    }
}
```

- **缓存策略**：进程内 `ConcurrentDictionary` 永久常驻（灯箱场景文件数有限，不需要 LRU）。
- **线程安全**：probe 在 `Task.Run` 中执行，不阻塞 UI 线程。
- **失败处理**：返回 `(0, 0)`，UI 退化到 `MinWidth/MinHeight=100`，不崩。
- **依赖**：`SkiaSharp`（项目已有，无新增包）。

---

## 8. 视频文件处理

### 8.1 灯箱显示策略

视频文件进入灯箱后只显示 **缩略图**（`MediaFile.ThumbnailPath`，扫描阶段已生成）+ 居中的 ▶ overlay 提示用户「点击外部打开播放」。

不调 ffprobe、不显示原始分辨率 / 时长 / 帧率。理由：视频不在灯箱里播放，元信息对用户做"打开决策"没帮助（用户已知这是视频文件），打开外部后外部播放器会自己显示。

### 8.2 双击行为

```
OnMediaTileDoubleTapped → 调 vm.PreviewCommand.Execute(file):
  └─ lightboxVm = new LightboxViewModel(files, index, systemShell)
        ├─ files = vm.CurrentMediaFiles.ToList()  // 图片 + 视频
        └─ lightboxWindow.Show()
              ├─ 图片 → 全尺寸 + 缩放
              └─ 视频 → 缩略图 + ▶ overlay
```

### 8.3 打开外部

底栏「打开外部」按钮 + 视频模式下 Space 键均触发 `_systemShell.OpenWithDefaultApp(file.Path)`，由 LaunchServices 选默认 app（VLC / QuickTime / IINA 等用户自选）。

### 8.4 Gallery 缩略图标记

视频缩略图左上角叠加 ▶ 角标（已存在，spec 不重复实现 —— 见 `GalleryView.axaml` line 261-275）。

---

## 9. 边界情况

| 场景 | 处理 |
|------|------|
| 本地图片文件不存在 | 显示缩略图 + "本地文件缺失" 提示 + "下载" 按钮 |
| 本地视频文件不存在 | 缩略图仍显示（来自扫描时已上传到 thumbnails/ 目录），底栏"打开外部"无效但灯箱本身可继续翻 |
| 原图尺寸探测失败 | `ImageWidth/Height = null` → UI 退化为最小 100×100 + Stretch Uniform |
| 缩略图文件不存在 | `FilePathToBitmap` converter 返回 null → Image 空白，▶ overlay 仍可见 |
| 损坏的图片 | `SKCodec.Create` 抛异常 → 缓存空结果 → UI 退化同上 |
| 长按方向键快速翻页 | 200ms 节流，日志 `[Lightbox] nav throttled: ...` |
| 窗口打开时 Gallery 切日期 | LightboxWindow 持有 `Files` 快照（图片 + 视频混合），不受影响 |
| 用户拖拽窗口到第二个显示器 | WindowState 保持当前模式，不强制全屏 |
| 内存：连续翻 100 张 | `ImageDimensionProbe` 缓存常驻，重复翻同一张不再探测 |
| 视频文件 ffprobe 失败 | **不适用**（灯箱内不调 ffprobe） |

---

## 10. 与现有系统的关系

### 10.1 不替换 OpenFileCommand

`OpenFileCommand` 仍保留，用于本地缺失时下载+打开的流程。灯箱仅替代「双击本地已存在媒体 → 系统默认应用打开」的行为 —— 现在灯箱也覆盖视频场景，但用户可通过灯箱底栏「打开外部」按钮再走系统应用。

### 10.2 不修改 MediaFile

灯箱不引入新 DB 字段。原图尺寸运行时探测（`ImageDimensionProbe`）。缩略图复用 `MediaFile.ThumbnailPath`（已存在）。

### 10.3 不影响多选模式

双击触发的 `OnMediaTileDoubleTapped` 先于单击的 `ToggleSelectionCommand`。双击时不进入多选，直接打开灯箱。

### 10.4 不引入新 NuGet 包

- 不引入 LibVLC（spec §8 决策 —— 视频不进灯箱播放，移除 ~50MB 依赖）
- `SkiaSharp` 已在项目（用于缩略图生成 + ImageDimensionProbe）

---

## 11. WindowState 决策记录

- **Spec 原计划**：`WindowState = FullScreen`（macOS 原生全屏）
- **问题**：macOS 原生 FullScreen 会进入自己的 Space、隐藏标题栏、隐藏 Dock；跟「保留标题栏让 Esc 也能关」+「底栏/侧栏可见」矛盾
- **最终**：`WindowState = Maximized`（窗口铺满工作区但保留标题栏 + 底栏 + 信息面板可见）
- **回退方案**：若用户想要原生全屏感，按 `F` 切到 `WindowState.Normal`（手动拉满）+ 可选 `ExtendClientAreaToDecorationsHint` + 自定义标题栏 overlay