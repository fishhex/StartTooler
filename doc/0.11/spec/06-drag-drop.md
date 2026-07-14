# 0.11 — 全局拖拽支持（Drag & Drop）

> 对应需求量文档 `doc/0.11/demand/07-general-improve.md` §「拖拽支持」。
> 核心改动：Gallery 页支持拖入文件自动导入项目目录；上传页支持拖入文件触发 LAN 本地上传；设置页支持拖入文件夹设为项目目录。

---

## 1. 模块边界

```
┌─────────────────────────────────────────────────┐
│              全局拖拽事件路由                     │
│  MainWindow.axaml                               │
│  └─ AddHandler(DragDrop.DragEnterEvent)         │
│  └─ AddHandler(DragDrop.DragOverEvent)          │
│  └─ AddHandler(DragDrop.DropEvent)              │
│       │                                          │
│       ├─ 当前页 == Gallery → DragDropHandler    │
│       │    └─ 复制文件到项目目录 → 刷新           │
│       │                                          │
│       ├─ 当前页 == UploadServer → DragDropHandler│
│       │    └─ 复制文件到上传目录 → 显示历史       │
│       │                                          │
│       └─ 当前页 == Settings → DragDropHandler   │
│            └─ 验证为文件夹 → 设为项目目录         │
└─────────────────────────────────────────────────┘
```

### 1.1 设计原则

- **不修改现有页面布局**：拖拽事件在 `MainWindow` 层级统一处理，按当前页面路由。
- **非侵入式**：不修改 `GalleryView` / `UploadServerView` / `SettingsView` 的 XAML 结构。
- **视觉反馈**：拖入时半透明遮罩 + 提示文字，让用户明确知道"这里可以放下"。

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Services/DragDropHandler.cs` | 拖拽事件分发 + 文件复制逻辑 | 新增 |
| `Views/MainWindow.axaml` | 注册 DragDrop 事件处理 | 修改 |
| `Views/MainWindow.axaml.cs` | 事件桥接：路由到 DragDropHandler | 修改 |
| `ViewModels/MainWindowViewModel.cs` | 暴露 `CurrentPageId` 供路由判断 | 修改（轻量） |

---

## 3. DragDropHandler

### 3.1 接口

```csharp
namespace StartTooler.Services;

public class DragDropHandler
{
    private readonly IConfigService _configService;
    private readonly IMediaRepository _mediaRepo;
    private readonly Func<GalleryViewModel?> _getGalleryVm;
    private readonly Func<UploadServerViewModel?> _getUploadVm;
    private readonly Func<SettingsViewModel?> _getSettingsVm;
    private readonly Func<string?> _getCurrentPageId;

    public DragDropHandler(
        IConfigService configService,
        IMediaRepository mediaRepo,
        Func<GalleryViewModel?> getGalleryVm,
        Func<UploadServerViewModel?> getUploadVm,
        Func<SettingsViewModel?> getSettingsVm,
        Func<string?> getCurrentPageId)
    {
        // ...
    }

    /// <summary>拖入悬停时判断是否接受此拖放</summary>
    public DragDropEffects OnDragOver(DragEventArgs e);

    /// <summary>放下时执行文件操作</summary>
    public async Task OnDropAsync(DragEventArgs e, Visual visual, CancellationToken ct);
}
```

### 3.2 路由逻辑

```csharp
public DragDropEffects OnDragOver(DragEventArgs e)
{
    var pageId = _getCurrentPageId();
    var hasFiles = e.Data.Contains(DataFormats.Files);

    // 设置页：只接受文件夹拖放
    if (pageId == "settings")
    {
        var paths = e.Data.GetFileNames()?.ToList();
        if (paths is { Count: 1 } && Directory.Exists(paths[0]))
            return DragDropEffects.Link;
        return DragDropEffects.None;
    }

    // Gallery / UploadServer：接受文件拖放
    if (pageId is "gallery" or "uploadServer")
    {
        if (!hasFiles) return DragDropEffects.None;

        // Gallery 需要项目目录已设置
        if (pageId == "gallery")
        {
            var projPath = _configService.GetProjectConfig()?.CurrentDirectory;
            if (string.IsNullOrEmpty(projPath)) return DragDropEffects.None;
        }
        return DragDropEffects.Copy;
    }

    return DragDropEffects.None;
}
```

### 3.3 Gallery 页拖入逻辑

```csharp
// DragDropHandler (Gallery 分支)
private async Task HandleGalleryDropAsync(List<string> filePaths, CancellationToken ct)
{
    var vm = _getGalleryVm();
    if (vm == null) return;

    var projectDir = _configService.GetProjectConfig()?.CurrentDirectory;
    if (string.IsNullOrEmpty(projectDir)) return;

    var copiedCount = 0;
    var skippedCount = 0;
    List<string> errors = new();

    foreach (var sourcePath in filePaths)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(sourcePath))
        {
            skippedCount++;
            continue;
        }

        // 确定目标子目录：按当前日期 YYYY-MM-DD
        var destDir = Path.Combine(projectDir, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(destDir);

        var fileName = Path.GetFileName(sourcePath);
        var destPath = Path.Combine(destDir, fileName);

        // 文件已存在 → 自动加序号
        if (File.Exists(destPath))
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            int suffix = 1;
            do
            {
                destPath = Path.Combine(destDir, $"{nameWithoutExt}_{suffix++}{ext}");
            } while (File.Exists(destPath));
        }

        try
        {
            await Task.Run(() => File.Copy(sourcePath, destPath, overwrite: false), ct);
            copiedCount++;
        }
        catch (Exception ex)
        {
            errors.Add($"{fileName}: {ex.Message}");
        }
    }

    // 刷新 Gallery（debounced，给文件系统一点写入时间）
    if (copiedCount > 0)
    {
        await Task.Delay(300, ct); // 等文件系统刷新
        vm.RequestRefreshDebounced(delayMs: 500);
    }
}
```

**流程**：
```
拖入 N 个文件路径
  ├─ 逐个 File.Copy → 项目目录/YYYY-MM-DD/
  ├─ 重名处理：文件名_{序号}.ext
  ├─ 全部复制完成 → Delay 300ms → RequestRefreshDebounced
  └─ toast 反馈：「已导入 N 张照片」（失败则附带错误数）
```

### 3.4 上传页拖入逻辑

```csharp
// DragDropHandler (UploadServer 分支)
private async Task HandleUploadDropAsync(List<string> filePaths, CancellationToken ct)
{
    var vm = _getUploadVm();
    if (vm == null) return;

    var projectDir = _configService.GetProjectConfig()?.CurrentDirectory;
    if (string.IsNullOrEmpty(projectDir)) return;

    // 复用 UploadServerService 的目录逻辑：项目目录/YYYY-MM-DD/
    var destDir = Path.Combine(projectDir, DateTime.Now.ToString("yyyy-MM-dd"));
    Directory.CreateDirectory(destDir);

    var copiedCount = 0;
    foreach (var sourcePath in filePaths)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath)) continue;

        var fileName = Path.GetFileName(sourcePath);
        var destPath = Path.Combine(destDir, fileName);

        if (File.Exists(destPath))
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            int suffix = 1;
            do { destPath = Path.Combine(destDir, $"{nameWithoutExt}_{suffix++}{ext}"); }
            while (File.Exists(destPath));
        }

        await Task.Run(() => File.Copy(sourcePath, destPath, overwrite: false), ct);
        copiedCount++;

        // 记录上传历史（与手机上传同一通道）
        vm.AddUploadHistoryEntry(new UploadHistoryEntry
        {
            FileName = fileName,
            Timestamp = DateTime.Now,
            Source = "拖拽导入"
        });
    }

    // 通知 Gallery 刷新（上传页拖入的文件同样出现在 Gallery 中）
    var galleryVm = _getGalleryVm();
    if (galleryVm != null && copiedCount > 0)
    {
        await Task.Delay(300, ct);
        galleryVm.RequestRefreshDebounced(delayMs: 500);
    }
}
```

### 3.5 设置页拖入逻辑

```csharp
// DragDropHandler (Settings 分支)
private void HandleSettingsDrop(DragEventArgs e)
{
    var paths = e.Data.GetFileNames()?.ToList();
    if (paths is not { Count: 1 }) return;

    var folderPath = paths[0];
    if (!Directory.Exists(folderPath)) return;

    var vm = _getSettingsVm();
    vm?.SetProjectDirectory(folderPath);
}
```

> 要求：`SettingsViewModel` 已有或新增 `SetProjectDirectory(string path)` 方法，设置 `SelectedProjectDirectory` 并标记 `IsDirty`。

---

## 4. 视觉反馈（拖入遮罩）

### 4.1 拖入状态指示

当拖入文件悬停在有效区域上方时，显示半透明遮罩 + 提示：

```
┌──────────────────────────────────────────┐
│                                          │
│        ┌──────────────────────┐          │
│        │                      │          │
│        │     📁 松开以导入     │          │  ← 半透明深色遮罩背景
│        │     42 个文件         │          │
│        │                      │          │
│        └──────────────────────┘          │
│                                          │
│  (原页面内容，带低透明度)                 │
└──────────────────────────────────────────┘
```

### 4.2 实现

在 `MainWindow.axaml` 最顶层添加遮罩 overlay：

```xml
<!-- 全局拖拽遮罩（置于最顶层） -->
<Grid>
    <!-- 原有页面内容 -->
    <DockPanel>...</DockPanel>

    <!-- 拖拽遮罩 -->
    <Border x:Name="DragDropOverlay"
            IsVisible="False"
            Background="#880A0E1A"
            ZIndex="9999"
            IsHitTestVisible="False">
        <Border HorizontalAlignment="Center"
                VerticalAlignment="Center"
                Background="#CC1A1E2E"
                CornerRadius="12"
                Padding="48,32">
            <StackPanel Spacing="12" HorizontalAlignment="Center">
                <TextBlock Text="📁 松开以导入"
                           FontSize="20"
                           Foreground="{DynamicResource Text.Primary}"
                           HorizontalAlignment="Center" />
                <TextBlock x:Name="DragDropFileCount"
                           Text=""
                           FontSize="14"
                           Foreground="{DynamicResource Text.Secondary}"
                           HorizontalAlignment="Center" />
            </StackPanel>
        </Border>
    </Border>
</Grid>
```

**状态机**：

| 事件 | 行为 |
|---|---|
| `DragEnter`（路径匹配） | 显示遮罩，更新文件数文案 |
| `DragEnter`（路径不匹配） | 遮罩不显示 |
| `DragOver` | 保持遮罩可见 |
| `DragLeave` | 隐藏遮罩 |
| `Drop` | 隐藏遮罩 + 执行复制 |

---

## 5. MainWindow 集成

### 5.1 MainWindow.axaml 添加事件

```xml
<Window ...>
    <Grid>
        <DockPanel x:Name="RootPanel">
            <!-- 原有内容 -->
        </DockPanel>

        <!-- 拖拽遮罩 overlay -->
        ...
    </Grid>
</Window>
```

### 5.2 MainWindow.axaml.cs 事件桥接

```csharp
public partial class MainWindow : Window
{
    private DragDropHandler? _dragDropHandler;

    public MainWindow()
    {
        InitializeComponent();

        // 注册拖拽事件（在窗口级别，子控件不会拦截）
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void SetDragDropHandler(DragDropHandler handler)
    {
        _dragDropHandler = handler;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (_dragDropHandler == null) return;
        var effects = _dragDropHandler.OnDragOver(e);
        e.DragEffects = effects;
        e.Handled = true;

        if (effects != DragDropEffects.None)
        {
            var fileCount = e.Data.GetFileNames()?.Count() ?? 0;
            DragDropFileCount.Text = fileCount > 0 ? $"{fileCount} 个文件" : "";
            DragDropOverlay.IsVisible = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_dragDropHandler == null) return;
        e.DragEffects = _dragDropHandler.OnDragOver(e);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DragDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DragDropOverlay.IsVisible = false;

        if (_dragDropHandler == null) return;
        await _dragDropHandler.OnDropAsync(e, this, CancellationToken.None);
        e.Handled = true;
    }
}
```

> **注意**：`AddHandler` 使用 **routed events**（冒泡路由），需要将 `RoutingStrategies` 设为 `Tunnel` 或注册在顶层容器上以确保拦截。Avalonia 的 `DragDrop` 事件默认是冒泡的，用 `AddHandler` 搭配 `handledEventsToo: true` 确保即使子控件处理了也能收到。

### 5.3 MainWindowViewModel 暴露 CurrentPageId

```csharp
// MainWindowViewModel.cs
public partial class MainWindowViewModel : ObservableObject
{
    // 已有 CurrentPage（枚举/字符串），直接暴露给 DragDropHandler
    [ObservableProperty]
    private string? _currentPageId;  // "gallery" / "uploadServer" / "settings" / "trash"

    // 在导航切换时更新此字段（已有 NavigateTo 等方法中追加赋值）
}
```

> 如当前 `MainWindowViewModel` 使用 `ViewPage` 枚举，需要先完成 v0.12 导航重构（`ViewPage` → `string`）或暂用 enum 映射。**推荐**：本 spec 在 v0.12 导航重构之后实施，直接使用 `string CurrentPageId`。

---

## 6. 文件复制策略

### 6.1 目标目录

| 触发页面 | 目标目录 | 说明 |
|---|---|---|
| Gallery | `{projectDir}/{YYYY-MM-DD}/` | 当天日期子目录，与导入扫描逻辑一致 |
| 上传页 | `{projectDir}/{YYYY-MM-DD}/` | 同上，手机上传也是这个目录 |

### 6.2 重名处理

```
目标文件已存在：
  → 原文件名: M31_120s.NEF
  → 重名追加序号: M31_120s_1.NEF
  → 仍存在 → M31_120s_2.NEF → 继续递增
```

最多递增到 100，超过则报错跳过。

### 6.3 取消支持

拖入复制是长时间操作（大文件可能数秒/个），通过 `CancellationToken` 支持取消：
- 用户按 `Esc` 或切换页面 → 取消剩余复制
- 已复制的文件保留，未复制的跳过
- toast：「已导入 N 张（已取消，M 张未完成）」

### 6.4 复制后刷新策略

```
复制完成
  → Task.Delay(300ms)              // 等文件系统完成写入
  → RequestRefreshDebounced(500ms)  // 触发 Gallery 重新扫描
  → 扫描完成后 toast: "已导入 12 张照片"
```

使用 `RequestRefreshDebounced` 而非立即 `Refresh`，以便多次连续拖入合并为一次扫描。

---

## 7. 禁止拖放的页面

| 页面 | 行为 |
|---|---|
| 回收站 (Trash) | `DragDropEffects.None` — 回收站不支持直接拖入文件 |
| 设置页（文件拖入） | 拖入文件 → `DragDropEffects.None`；只有拖入**文件夹**才接受 |
| 星历（未来） | `DragDropEffects.None` |

---

## 8. 边界情况

| 场景 | 处理 |
|---|---|
| 拖入空文件夹（0 个文件） | `DragDropEffects.None`，遮罩不显示 |
| 拖入混合内容（文件 + 文件夹） | 遍历所有 path，文件夹递归展开为文件列表再处理 |
| 拖入不支持的格式（如 .txt） | 仍然复制（存储所有文件），由 Gallery 扫描阶段按扩展名过滤 |
| Gallery 页面无项目目录 | `DragDropEffects.None`，遮罩不显示 |
| 拖入文件数量 > 1000 | 弹出确认：「你正在导入 1,234 个文件，确定吗？」 |
| 拖入超大文件（单个 > 2GB） | 不检查大小限制，由文件系统决定上限 |
| 复制中磁盘空间不足 | 单个文件复制失败 → 跳过并记录错误，继续下一个 |
| 从 OSS 同步中的目录拖入 | 正常复制，OSS 同步状态由下次扫描更新 |
| Settings 拖入不是文件夹 | `DragDropEffects.None` |
| 用户在拖入过程中切换页面 | `DragLeave` 隐藏遮罩，`Drop` 仅处理当前页面逻辑 |

---

## 9. 与已有功能的关系

| 功能 | 影响 |
|---|---|
| Gallery 扫描 | 拖入后触发 `RequestRefreshDebounced`，复用现有扫描管线 |
| 上传页 LAN 上传 | 拖入后文件直接写入项目目录 + 记录上传历史 |
| 设置页项目目录 | 拖入文件夹 → 等价于打开目录选择器并选择该目录 |
| OSS 自动上传 | 拖入后不自动触发 OSS 上传，由用户手动上传 |
| AI 自动打标 | 拖入后不触发自动打标（与扫描导入行为一致） |

---

## 10. 不做清单

| 内容 | 理由 |
|---|---|
| 从应用内拖出文件到 Finder/桌面 | 属于导出功能，非导入场景 |
| 拖入时自动 OSS 上传 | 保持与扫描导入行为一致，用户手动控制上传 |
| 拖入时自动 AI 打标 | 同上 |
| 从 OSS 云端拖入到本地 | OSS 下载有自己的 UI 流程，拖拽无增益 |
| 拖入到垃圾筒 | 逻辑复杂且需求少 |
| 跨应用拖入照片到灯箱对比 | 对比工具仅支持已导入的照片 |

---

## 11. 决策记录（ADR）

### ADR-01: 路由值用 `ViewPage` enum 还是 `string CurrentPageId`

**日期**: 2026-07-14
**状态**: v0.11 阶段 → enum;v0.12 导航重构 → 重新评估
**决策者**: AI 评审

#### 背景

`MainWindowViewModel.CurrentPage` 当前类型是 `enum ViewPage { Gallery, Settings, UploadServer, Trash }`(见 `ViewModels/MainWindowViewModel.cs:17`)。`DragDropHandler` 需要根据当前页面路由到不同处理逻辑,接收的路由值类型有两种选择:

- `string CurrentPageId`(spec 5.3 原文推荐)
- `ViewPage currentPage`(enum 直接传)

#### 原 spec 推荐 string 的理由

1. **解耦**:`DragDropHandler` 在 `Services/`,`ViewPage` 在 `ViewModels/`,Services 反向依赖 ViewModels 破坏分层。string 是中立的"协议值"。
2. **可序列化**:日志、跨进程、config 持久化直接用。
3. **可扩展**:新增页面不改 handler 类型签名。
4. **可读性**:日志 `CurrentPageId="gallery"` 比 `CurrentPage=0` 直观。

#### v0.11 阶段改用 enum 的理由

1. **改 string 的连锁成本太高**:
   - `MainWindowViewModel` 一堆 `IsSettingsPage` / `IsMediaActive` / `IsGalleryPage` 等布尔派生属性要改。
   - XAML 引用 `ViewPage.Gallery` 等处要改成字符串 DataTrigger。
   - 跨多个 View 文件搜索替换工作量大。
2. **v0.11 不在导航重构窗口**:v0.12 才会做 string 化(导航 + 插件系统),v0.11 改 string 等于重复做一次。
3. **handler 是项目专用**:`DragDropHandler.cs` 在本项目 `Services/` 下,只为 StartTooler 服务,Services 引用 ViewModels 这一点耦合可接受。
4. **类型安全收益**:`switch (page)` 编译器能提示漏分支,string 拼错编译期不报错。

#### 实施决策

**v0.11 阶段**:`DragDropHandler` 接收 `ViewPage currentPage` 参数,内部用 `switch` 路由。Services 层引入对 `ViewModels.ViewPage` 的单向引用,可接受。

**v0.12 阶段**(导航重构时):
- `MainWindowViewModel` 改造:`ViewPage` → `string CurrentPageId`(或其他 string-based 方案)。
- `DragDropHandler` 同步改:参数改为 `string? currentPageId`,内部 `switch` case 改字符串字面量。
- XAML 同步替换。

**v0.13+ 插件系统**:
- 插件页无法编译进 enum,string 协议值是唯一选项。
- 此时 handler 设计已就位,改造成本只改类型签名。

#### 影响范围

| 文件 | v0.11 改动 | v0.12 同步改动 |
|---|---|---|
| `Services/DragDropHandler.cs` | 收 `ViewPage` | 改收 `string?` |
| `Views/MainWindow.axaml.cs` | 传 `MainWindowViewModel.CurrentPage` | 传 `_viewModel.CurrentPageId` |
| `ViewModels/MainWindowViewModel.cs` | 无 | 删 enum,加 `CurrentPageId` |
| 多 View XAML | 无 | `ViewPage.X` → 字符串比较 |

#### 备选方案(未选)

- **Option B**:v0.11 直接改 string,提前付出成本。否决:连锁改动超出 spec 06 范围。
- **Option C**:handler 收 int (`(int)ViewPage.Gallery = 0`)。否决:可读性差,枚举顺序变就崩。

