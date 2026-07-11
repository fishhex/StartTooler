# 09 — UI 通用件

> 对应代码：`Components/`、`Controls/`、`Converters/`、`Themes/`、`Services/NotificationService.cs`、`Services/NotificationTypeToBrushConverter.cs`、`Services/IDirectoryPickerService.cs`、`Services/DirectoryPickerService.cs`、`Services/IFilePickerService.cs`、`Services/FilePickerService.cs`、`Services/ISystemShellService.cs`、`Services/SystemShellService.cs`、`Helpers/DialogHelper.cs`、`Views/NotificationCard.axaml`。

---

## 1. 主题资源（`Themes/`）

### 1.1 文件清单

| 文件 | 角色 |
|---|---|
| `Colors.axaml` | 主色板（DeepSpace 主题）— token 名 → Color |
| `Icons.axaml` | 图标 `StreamGeometry`（不用 PathGeometry，Bounds 延迟问题） |
| `Styles.axaml` | 控件样式 class（`primary-button` / `toolbar-button` / `dialog-*` 等） |
| `RedNightVision.axaml` | **等价于「夜视主题下要覆盖的 token 全集」**（固化在 `Services/ThemeManager.cs:30-66`） |

### 1.2 主题 token（主色板）

**DeepSpace 主题色板（默认）** 在 `Themes/Colors.axaml`：

| Token | 含义（约定） | 默认值（深空） |
|---|---|---|
| `Bg.Outer` | 应用最外层背景 | `#0A0F1A` 等极深 |
| `Bg.Surface` | 卡片 / 容器 | 深一档蓝灰 |
| `Bg.SurfaceElevated` | 二级卡片 / 对话框 | 进一步浅 |
| `Bg.Divider` | 1px 分隔线 | 半透蓝灰 |
| `Bg.Hover` / `Bg.HoverSubtle` | hover 状态 | 浅一档 |
| `Text.Primary` / `Secondary` / `Tertiary` / `Disabled` | 文本四级 | 蓝白 → 暗下去 |
| `Accent.Stellar` / `Accent.Nebula` / `Accent.Aurora` | 主强调色 | 蓝紫粉渐变 |
| `State.Success` / `Warning` / `Danger` | 状态色 | 绿 / 黄 / 红 |
| `Gradient.Header` / `Gradient.PrimaryButton` | 渐变笔刷 | linear gradient |

**`RedNightVision` 主题**（夜间观星）—— `Services/ThemeManager.cs:30-66`：

```csharp
_overrideDict.Add("Bg.Outer",             Color.Parse("#000000"));
_overrideDict.Add("Bg.Surface",           Color.Parse("#0A0000"));
_overrideDict.Add("Bg.SurfaceElevated",   Color.Parse("#140000"));
_overrideDict.Add("Bg.Divider",           Color.Parse("#2A0000"));
_overrideDict.Add("Bg.Hover",             Color.Parse("#1F0000"));
_overrideDict.Add("Bg.HoverSubtle",       Color.Parse("#3A0010"));
_overrideDict.Add("Text.Primary",         Color.Parse("#FF6B6B"));
_overrideDict.Add("Text.Secondary",       Color.Parse("#B53030"));
_overrideDict.Add("Text.Tertiary",        Color.Parse("#802020"));
_overrideDict.Add("Text.Disabled",        Color.Parse("#4D0000"));
_overrideDict.Add("Accent.Stellar",       Color.Parse("#FF3030"));
_overrideDict.Add("Accent.Nebula",        Color.Parse("#FF6060"));
_overrideDict.Add("Accent.Aurora",        Color.Parse("#FF8080"));
_overrideDict.Add("State.Success",        Color.Parse("#FF8080"));
_overrideDict.Add("State.Warning",        Color.Parse("#FFAA80"));
_overrideDict.Add("State.Danger",         Color.Parse("#FF3030"));
```

**契约**：
- **不预览**主题：UI 上 radio 切到「夜视」**不立即变红**，必须「保存」才生效（详见 `06-settings.md` §3.6）
- **覆盖所有相关 token**：撤旧 → 加新；如果某 token 没在 overrideDict 里，深空色会保留 → 不彻底黑红
- **动态 Resource 引用**：`{DynamicResource Bg.Outer}` 必须；`{StaticResource}` 不响应主题切换

### 1.3 图标（`Themes/Icons.axaml`）

| Icon | 用途 |
|---|---|
| `Icon.Refresh` | 工具栏「刷新」|
| `Icon.Cloud` | 「已上传」 状态徽章 |
| `Icon.CloudOff` | 「云端缺失」状态 |
| `Icon.Photo` / `Icon.Video` | 单文件图标 |
| `Icon.ChevronRight` | NavRail 子菜单 |

> **注意**：用 `StreamGeometry` 而非 `PathGeometry` —— 后者 `Bounds` 延迟，前者立刻可计算（v2.0 spec 断言）

---

## 2. 自定义控件

### 2.1 `Components/ScanProgressBar.axaml(.cs)`

进度条 + 当前文件名，绑 `GalleryViewModel.{ IsScanning, ScanProgress, ScanStatusMessage, RefreshState }`。

- `RefreshState.Completed` 时显示最终数字 + 2 秒后自动消（`GalleryViewModel.cs:314-321`）
- 其它状态隐藏或显示进度条

### 2.2 `Controls/NavRail.axaml(.cs)`

固定 80px 宽，垂直堆叠 3 项：「媒体 / 设置 / 上传服务」。

```xml
<TextBlock Text="媒体"     Classes="nav-rail-item" IsVisible="{Binding IsMediaActive}"/>
<TextBlock Text="设置"     Classes="nav-rail-item" IsVisible="{Binding IsSettingsActive}"/>
<TextBlock Text="上传服务"  Classes="nav-rail-item" IsVisible="{Binding IsUploadServerActive}"/>
```

> 文字垂直排列、无图标（v2.2 起只有文字——`MainWindowViewModel.cs:39-43`）

### 2.3 `Controls/StatusLegend.axaml(.cs)`

状态图例（本文件存在，目前不一定在主窗口展示）—— 描述「已上传 / 未上传 / 上传失败」等徽章颜色。预留，未来用作帮助页或工具提示。

---

## 3. Converters（`Converters/` — 12 个）

按职责分组：

### 3.1 缩略图 / 图像

| 文件 | 职责 |
|---|---|
| `FilePathToBitmapConverter.cs` | `string?` → `Bitmap?`（路径为空/不存在 → null，让 Image 自动隐藏）|
| `AsyncImageConverter.cs` | 异步版本，不缓存，**首选 `FilePathToBitmapConverter`** |
| `FilePathToBitmapConverter` 内部用 `ImageCacheService.LoadImageAsync` | — |

### 3.2 UploadStatus / SyncStatus

| 文件 | 职责 |
|---|---|
| `SyncStatusConverters.cs` | `SyncStatus { UploadedAndLocal / UploadedButMissingLocal / NotUploaded }` → bool/visibility |
| `MediaFileConverters.cs` | `MediaFile.IsUploaded` / `IsLocal` / `UploadStatus` 各种判断 |
| `MediaTypeConverters.cs` | `MediaType { Image / Video }` → 是否视频（icon） |

### 3.3 状态机

| 文件 | 职责 |
|---|---|
| `RefreshStateConverters.cs` | `RefreshState { Idle / Scanning / Completed / Stopped }` → IsVisible / icon |
| `ScanProgressConverters.cs` | `ScanProgress` → 文本 / 进度条值 |
| `RefreshButtonConverters.cs` | 按钮 enabled / hover 效果 |

### 3.4 数值 / UI 状态

| 文件 | 职责 |
|---|---|
| `Int32Converters.cs` | int → StringFormat / Visibility |
| `GreaterThanOneConverter.cs` | `> 1` → true（多选计数 > 1 才显示「批量操作」） |
| `SettingsConverters.cs` | `SettingsTab` ↔ ComboBox SelectedIndex |
| `TimelineBoolConverters.cs` | 时间轴选中态 |

### 3.5 Converter 注册约定

```csharp
public class FilePathToBitmapConverter : IValueConverter {
    public static readonly FilePathToBitmapConverter Instance = new();
    public object? Convert(...) { ... }
    public object? ConvertBack(...) => throw new NotSupportedException();
}
```

> XAML 直接用 `Converter={x:Static converters:FooConverter.Instance}` —— 不用 `IValueConverter` 静态 instance 的需要 `Ioc.Get` 框架。

---

## 4. 通知系统

### 4.1 `NotificationService.Current`（`Services/NotificationService.cs`）

应用级单例 + `ObservableCollection<NotificationItem>`。

```csharp
public static NotificationService Current { get; } = new();
public ObservableCollection<NotificationItem> Items { get; } = new();

public void Show(string title, string body, NotificationType type = Info) {
    Dispatcher.UIThread.Post(() => {
        var item = new NotificationItem(title, body, type);
        Items.Add(item);
        DispatcherTimer.RunOnce(() => {
            if (Items.Contains(item)) Items.Remove(item);
        }, TimeSpan.FromSeconds(5));
    });
}
```

### 4.2 `NotificationItem`（`Services/NotificationService.cs:18-31`）

```csharp
public class NotificationItem {
    public string Title { get; }
    public string Body { get; }
    public NotificationType Type { get; }
    public DateTime CreatedAt { get; } = DateTime.Now;
}
```

### 4.3 `NotificationType`（Info / Success / Error）

### 4.4 主窗口绑定（`Views/MainWindow.axaml:181-199`）

```xml
<Panel Grid.Column="0" Grid.ColumnSpan="2" Grid.Row="2"
       IsHitTestVisible="False"
       HorizontalAlignment="Right" VerticalAlignment="Bottom"
       Margin="0,0,0,16">
    <ItemsControl ItemsSource="{Binding Source={x:Static services:NotificationService.Current}, Path=Items}">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><StackPanel Orientation="Vertical" Spacing="0"/></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
            <DataTemplate DataType="services:NotificationItem">
                <views:NotificationCard/>
            </DataTemplate>
        </ItemsControl.ItemsPanel>
    </ItemsControl>
</Panel>
```

- `IsHitTestVisible=False` 防遮挡下层按钮
- 永远在右下角（跨全窗口 Grid）
- `NotificationCard` 是 `views:` namespace 自绘组件

### 4.5 `NotificationCard.axaml(.cs)`

左侧色条 + 标题 + Body，按 `NotificationType` 取色：

- Info → `Accent.Stellar`（蓝紫）
- Success → `State.Success`
- Error → `State.Danger`

色值通过 `Services/NotificationTypeToBrushConverter.cs:23-29`：

```csharp
var colorKey = type switch {
    Info    => "Accent.Stellar",
    Success => "State.Success",
    Error   => "State.Danger",
    _       => "Text.Tertiary",
};
```

> 颜色取自主题 token — 主题切换后通知颜色自动适配

### 4.6 使用约定

| 场景 | 用什么 |
|---|---|
| **自动消失反馈** | `NotificationService.Current.Show` |
| **阻塞决策**（必选 yes/no）| `DialogHelper.ShowConfirmAsync` |
| **完整信息 / 不可恢复错误**| `DialogHelper.ShowAlertAsync` |
| **UI 临时状态 / 多选提示** | VM 内 `ToastMessage` 字段 |
| **状态栏底部信息**| 各 VM 自有 `StatusMessage` |

> **`NotificationService` 不写 Trace** —— UI 唯一表现，写日志无意义

---

## 5. 对话框（`Helpers/DialogHelper.cs`）

### 5.1 集中式对话框构造

```csharp
public static class DialogHelper {
    public static Window? GetMainWindow();
    public static Task<bool> ShowConfirmAsync(Window owner, string title, string message, string primaryButtonText, string? secondaryButtonText = "取消");
    public static Task ShowAlertAsync(Window owner, string title, string message, string buttonText = "知道了");
}
```

### 5.2 风格契约

- `Window.Classes = { "dialog-window" }` —— Styles.axaml 给样式
- 按钮 `Classes = { "dialog-primary" } / "dialog-secondary" }`
- 文本 `Classes = { "dialog-title" } / "dialog-message" }`
- **不**直接 `new Window` 拼 dialog —— 全部走 `DialogHelper`
- 380-420px 宽，SizeToContent.Height 自动包裹

### 5.3 调用约定

```csharp
var goSettings = await DialogHelper.ShowConfirmAsync(
    window,
    title: "OSS 未配置",
    message: "上传前需要先配置 OSS（Region / Bucket / AccessKey），是否前往设置页？",
    primaryButtonText: "去设置",
    secondaryButtonText: "取消");
if (goSettings) NavigateToSettings();
```

> 主窗口阻塞 ≠ app busy，UI 不会卡；但 `ShowDialog` 期间当前 VM 不能多次打开

---

## 6. 系统服务（`Services/`）

### 6.1 文件 / 目录选择器

```csharp
public interface IDirectoryPickerService {
    Task<string?> PickFolderAsync(string title = "选择文件夹");
}
public class DirectoryPickerService : IDirectoryPickerService {
    // 用 Avalonia.Platform.Storage IStorageProvider.OpenFolderPickerAsync
}

public interface IFilePickerService {
    Task<string?> PickFileAsync(string title, params string[]? extensions);
}
public class FilePickerService : IFilePickerService {
    // IStorageProvider.OpenFilePickerAsync + FileTypeFilter
}
```

> **选文件扩展示例**（`FilePickerService.cs:25-36`）：`extensions = new[] { "exe" }` → `FileTypeFilter = "*.exe"`

**契约**：
- 取消返回 `null`，调用方负责处理
- Linux/Windows/macOS 上 Avalonia 都支持 StorageProvider
- `IStorageProvider` 必须从主窗口拿（`Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime`）

### 6.2 系统 Shell 调用（`SystemShellService`）

| 操作 | macOS | Windows | Linux |
|---|---|---|---|
| `RevealInFolder(path)` | `open -R <path>` Finder 高亮 | `explorer /select,"<path>"` 资源管理器高亮 | `xdg-open <dir>` 打开所在目录 |
| `OpenWithDefaultApp(path)` | `open <path>` LaunchServices 关联 | `ShellExecute=true` | `xdg-open <path>` 关联 |

> Linux `xdg-open` 不能高亮文件（无标准），已记 fallback 为打开所在目录

接口：

```csharp
public interface ISystemShellService {
    void RevealInFolder(string filePath);     // 抛 FileNotFoundException / SystemShellException
    void OpenWithDefaultApp(string filePath); // 同上
}
```

`SystemShellException` 包一切非 file-not-found 失败。

---

## 7. Trace 日志约定（项目级）

| 操作 | 用什么 |
|---|---|
| 写诊断日志（process 退出后日志仍可查）| `System.Diagnostics.Trace.WriteLine(...)` → `cwd/starttooler-debug.log` |
| IDE 调试输出可见 | `System.Diagnostics.Debug.WriteLine(...)` |
| 关键路径日志规范 | `[ComponentName] opName: param=value` |
| Toast / 用户反馈 | `NotificationService.Current.Show`（不写日志）|
| 上传状态 | `UploadStatus` enum field |

> **绝不** 用 `Console.WriteLine`（WinExe 无 console）

---

## 8. 修改这个模块前的检查清单

| 改动 | 涉及 | 验证 |
|---|---|---|
| 加新 token | `Colors.axaml` (+ `RedNightVision.axaml`/ThemeManager 同步加) | 切换主题不掉色 |
| 加新图标 | `Icons.axaml` 加 `StreamGeometry` | 用 `{DynamicResource Icon.Xxx}` 引用 |
| 加新 Converter | 文件 + `Instance` static + XAML 用 Instance 引用 | 双向 Convert 抛 NotSupported |
| 加新对话框 | `DialogHelper` 加方法 | **不**直接 `new Window` |
| 改系统 Shell 行为 | `SystemShellService` 加 OS 分支 | 三平台测试（至少 macOS） |
| 改通知形式 | `NotificationService` 内部 | 5s 自动消失、Dispatcher 切线程 |
| 加扫文件类型 | 见 `03-media-pipeline.md` §7 | — |

---

## 9. 已知陷阱（详见 `10-trap-book.md`）

- **`StaticResource` vs `DynamicResource`** — 主题切换必须用 `DynamicResource`
- **`StreamGeometry` 图标** — 不要用 `PathGeometry`（Bounds 延迟，combo/radio 显示错位）
- **NotificationCard `IsHitTestVisible=False`** 父 Panel 必须设，否则点击穿透失败（已固化）
- **`DialogHelper.ShowConfirmAsync` 不阻塞 Application.Run** — `ShowDialog` 是真阻塞主窗口；**不** vs `Show` 异步非阻塞
- **`FilePickerService` 扩展名匹配**：传入 `"exe"` 自动过滤成 `"*.exe"`，**不能**传 `"*.exe"`（已 trimStart 处理），保持传入裸扩展
- **`FilePathToBitmapConverter` 路径不存在** → 返回 null，不是抛异常 → UI 用 `IsVisible` 绑定隐藏
- **Token** 用 `DynamicResource` 引用主题色 — `Static` 在主题切换时不重读（多年踩坑）
