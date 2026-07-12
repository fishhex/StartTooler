# 0.11 — Trash（垃圾筒）UI & 交互改进

> 对应需求量文档 `doc/0.11/demand/05-trash-improve.md`。
> 核心改动：多选批量操作、卡片信息增强、操作撤销、导航跳转、容量统计、色彩区分。

---

## 1. 模块边界

```
TrashView.axaml
  ├─ 标题栏（统计信息：文件数 + 总大小）
  ├─ 多选工具栏（全选/取消全选/批量恢复/批量清理）
  ├─ Toast 横幅（顶部，含操作按钮：撤销/跳转）
  ├─ 已在云端 Section（青蓝色顶条，32px 间距）
  │    └─ ItemsControl → WrapPanel 卡片
  │         ├─ 复选框（多选模式）
  │         ├─ 缩略图
  │         ├─ 青蓝色顶部细条（2px）
  │         ├─ 文件名 + 删除日期 + 文件大小
  │         ├─ 原始路径（云端文件）
  │         └─ 操作按钮：恢复（绿）/ 下载 / 清理（红）
  └─ 仅本地 Section（灰色顶条）
       └─ ItemsControl → WrapPanel 卡片（同结构，灰色顶条，无下载按钮）

依赖链：
  TrashViewModel
    ├─ IMediaRepository（RestoreAsync / PermanentDeleteAsync / UpdateLocalExistsAsync）
    ├─ IUploadJobRepository（DeleteByFileAsync）
    ├─ IOssStorageFactory（云端删除）
    ├─ IConfigService（OssConfig）
    └─ IThumbnailService（缩略图）

外部依赖：
  MainWindowViewModel — 提供 NavigateToGalleryAndLocateFile(long mediaId) 用于跳转定位
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Views/TrashView.axaml` | 卡片重构（信息增强 + 彩色顶条 + 复选框）、Toast 位置改为顶部横幅、多选工具栏、标题栏统计 | 修改 |
| `ViewModels/TrashViewModel.cs` | 多选状态机、批量操作命令、撤销机制、跳转回调、容量统计 | 修改 |
| `ViewModels/MainWindowViewModel.cs` | 新增 `NavigateToGalleryAndLocateFile(long)` 方法 | 修改 |

> **不引入新 NuGet 包。不新增文件。**

---

## 3. 卡片信息增强

### 3.1 追加字段

`MediaFile` 已有 `FileSize`（long，字节）、`DeletedAt`（long?，unix ms）、`RelativePath`（string），直接绑定即可，无需改 Model。

**XAML 卡片底部覆盖层改为两行**：

```xml
<!-- 第一行：文件名 -->
<TextBlock Text="{Binding FileName}" FontSize="11"
           Foreground="#E6FFFFFF" TextTrimming="CharacterEllipsis" MaxLines="1"/>

<!-- 第二行：删除日期 + 文件大小 -->
<StackPanel Orientation="Horizontal" Spacing="6">
    <TextBlock Text="{Binding DeletedAt, Converter={StaticResource UnixMsToDateString}}"
               FontSize="9" Foreground="#99FFFFFF"/>
    <TextBlock Text="{Binding FileSize, Converter={StaticResource BytesToHumanReadable}}"
               FontSize="9" Foreground="#99FFFFFF"/>
</StackPanel>
```

**新增两个 Converter**（已有类似 Converter 在 `Converters/` 目录）：

```csharp
// UnixMsToDateStringConverter — long? → "2024-07-09 删除"
public class UnixMsToDateStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long ms && ms > 0)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
            return $"{dt:yyyy-MM-dd} 删除";
        }
        return "";
    }
}

// BytesToHumanReadableConverter — long → "12.3 MB"
public class BytesToHumanReadableConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
            return order == 0 ? $"{len:N0} {sizes[order]}" : $"{len:N1} {sizes[order]}";
        }
        return "";
    }
}
```

### 3.2 云端文件原始路径

"已在云端"段卡片在文件名上方追加一行原始路径：

```xml
<TextBlock Text="{Binding RelativePath}" FontSize="9"
           Foreground="#80FFFFFF" TextTrimming="CharacterEllipsis" MaxLines="1"
           IsVisible="{Binding IsUploaded}"/>
```

---

## 4. 彩色顶条——两段视觉区分

### 4.1 已在云端：青蓝色顶条

卡片 Border 顶部加 2px 色条：

```xml
<Border Background="#4DD0E1" Height="2" VerticalAlignment="Top"/>
```

### 4.2 仅本地：灰色顶条

```xml
<Border Background="{DynamicResource Text.Disabled}" Height="2" VerticalAlignment="Top" Opacity="0.4"/>
```

### 4.3 两段间距

原 `Spacing="24"` 改为 `Spacing="32"`。

---

## 5. 多选模式

### 5.1 ViewModel 新增

```csharp
// 多选状态
[ObservableProperty] private bool _isMultiSelectMode;

// 云端和本地各自独立的选中集合
public HashSet<long> SelectedCloudIds { get; } = new();
public HashSet<long> SelectedLocalIds { get; } = new();

// 选中计数（绑定工具栏）
[ObservableProperty] private int _selectedCloudCount;
[ObservableProperty] private int _selectedLocalCount;
public int TotalSelectedCount => SelectedCloudCount + SelectedLocalCount;

[RelayCommand]
private void EnterMultiSelect()
{
    IsMultiSelectMode = true;
    SelectedCloudIds.Clear();
    SelectedLocalIds.Clear();
    SelectedCloudCount = 0;
    SelectedLocalCount = 0;
}

[RelayCommand]
private void ExitMultiSelect()
{
    IsMultiSelectMode = false;
    SelectedCloudIds.Clear();
    SelectedLocalIds.Clear();
    SelectedCloudCount = 0;
    SelectedLocalCount = 0;
}

// 切换单张选中
[RelayCommand]
private void ToggleSelect(MediaFile? file)
{
    if (file == null) return;
    var set = file.IsUploaded ? SelectedCloudIds : SelectedLocalIds;
    if (set.Contains(file.Id)) { set.Remove(file.Id); }
    else { set.Add(file.Id); }
    if (file.IsUploaded) SelectedCloudCount = SelectedCloudIds.Count;
    else SelectedLocalCount = SelectedLocalIds.Count;
    OnPropertyChanged(nameof(TotalSelectedCount));
}
```

### 5.2 多选触发

- **右键**：卡片上 `PointerPressed` 右键 → `EnterMultiSelect` + `ToggleSelect(this)`
- 右键后自动进入多选模式并选中该卡片，符合桌面习惯。

### 5.3 卡片复选框

多选模式下卡片左上角显示 `CheckBox`（绑定 `IsSelected` 属性，`MediaFile` 已有）：

```xml
<CheckBox IsChecked="{Binding IsSelected}"
          IsVisible="{Binding $parent[ItemsControl].((vm:TrashViewModel)DataContext).IsMultiSelectMode}"
          HorizontalAlignment="Left" VerticalAlignment="Top"
          Margin="8"/>
```

通过 `ToggleSelectCommand` 同步 `IsSelected` 与 `SelectedCloudIds/SelectedLocalIds`。

### 5.4 多选工具栏

标题栏下方（`Grid.Row="0"` 与内容之间）新增一行工具栏，仅多选模式下可见：

```xml
<Border Grid.Row="1" IsVisible="{Binding IsMultiSelectMode}"
        Background="{DynamicResource Bg.Surface}"
        BorderBrush="{DynamicResource Bg.Divider}" BorderThickness="0,0,0,1"
        Padding="24,8">
    <StackPanel Orientation="Horizontal" Spacing="8">
        <Button Classes="toolbar-button" Content="全选"
                Command="{Binding SelectAllCommand}"/>
        <Button Classes="toolbar-button" Content="取消全选"
                Command="{Binding DeselectAllCommand}"/>
        <Button Classes="toolbar-button" Content="批量恢复"
                Command="{Binding BatchRestoreCommand}"
                IsEnabled="{Binding TotalSelectedCount, Converter={x:Static IntConverters.GreaterThanZero}}"/>
        <Button Classes="toolbar-button-danger" Content="批量清理"
                Command="{Binding BatchCleanSelectedCommand}"
                IsEnabled="{Binding TotalSelectedCount, Converter={x:Static IntConverters.GreaterThanZero}}"/>
        <Button Classes="secondary-button" Content="退出多选"
                Command="{Binding ExitMultiSelectCommand}"
                HorizontalAlignment="Right"/>
    </StackPanel>
</Border>
```

对应主体内容 `Grid.Row` 下移（原 `RowDefinitions="Auto,*"` → `RowDefinitions="Auto,Auto,*"`）。

### 5.5 全选/取消全选

```csharp
[RelayCommand]
private void SelectAll()
{
    foreach (var f in CloudFiles) { SelectedCloudIds.Add(f.Id); f.IsSelected = true; }
    foreach (var f in LocalFiles) { SelectedLocalIds.Add(f.Id); f.IsSelected = true; }
    SelectedCloudCount = SelectedCloudIds.Count;
    SelectedLocalCount = SelectedLocalIds.Count;
    OnPropertyChanged(nameof(TotalSelectedCount));
}

[RelayCommand]
private void DeselectAll()
{
    foreach (var f in CloudFiles) f.IsSelected = false;
    foreach (var f in LocalFiles) f.IsSelected = false;
    SelectedCloudIds.Clear();
    SelectedLocalIds.Clear();
    SelectedCloudCount = 0;
    SelectedLocalCount = 0;
    OnPropertyChanged(nameof(TotalSelectedCount));
}
```

### 5.6 批量恢复

逐个遍历 `SelectedCloudIds ∪ SelectedLocalIds`，调用现有 `RestoreAsync` 逻辑，成功后从集合中移除。全部完成后自动退出多选模式，Toast 汇总。

### 5.7 批量清理

弹出确认对话框（含统计），确认后逐个处理。云端文件走 OSS 删除 → 本地删除 → DB 永久删除；本地文件直接确认后删除。与现有 `CleanSingle` / `BatchCleanAll` 逻辑一致。

---

## 6. 操作反馈改进

### 6.1 清理撤销

仅适用于本地文件清理（`!file.IsUploaded`）。云端文件清理不可撤销（涉及 OSS API），仅弹出确认对话框。

**ViewModel 撤销状态**：

```csharp
// 撤销栈：记录最近一次清理的本地文件
private record UndoEntry(long MediaId, string FileName, string ProjectPath, string RelativePath, long? DeletedAt);
private UndoEntry? _lastUndoEntry;
private CancellationTokenSource? _undoCts;

private async Task CleanLocalWithUndo(MediaFile file)
{
    // 1. 记录撤销信息
    _lastUndoEntry = new UndoEntry(file.Id, file.FileName, file.ProjectPath, file.RelativePath, file.DeletedAt);

    // 2. 删本地文件 + DB 永久删除
    DeleteLocalFile(file);
    await DeleteFileAndJobAsync(file);
    LocalFiles.Remove(file);

    // 3. 显示带撤销按钮的 Toast
    ShowActionToast($"已清理 {file.FileName}", "撤销", async () =>
    {
        // 重新创建 .trashed 软删除标记（重新插入 media_files 行）
        // 注意：本地文件已删，无法恢复文件内容，只恢复 DB 记录标记为已删除
        await _mediaRepo.UndoDeleteAsync(_lastUndoEntry.MediaId,
            _lastUndoEntry.DeletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        LocalFiles.Add(await _mediaRepo.GetByIdAsync(_lastUndoEntry.MediaId));
        _lastUndoEntry = null;
    });

    // 4. 5 秒后自动清除撤销入口
    _undoCts?.Cancel();
    _undoCts = new CancellationTokenSource();
    _ = Task.Delay(5000, _undoCts.Token).ContinueWith(_ =>
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _lastUndoEntry = null;
            ClearActionToast();
        });
    });
}
```

> **注意**：由于本地文件物理删除后无法恢复文件内容，撤销仅恢复 DB 记录（重新标记为软删除）。用户若想恢复文件，需在此之前已上传云端。对"仅本地"文件清理，Toast 需额外提示"文件将永久删除，无法恢复"。

**简化方案**：对"仅本地"文件，清理时先不立即删除 DB，而是先 `RestoreAsync` 回 Gallery，然后提示"可前往 Gallery 重新删除"。实际操作是：
  - `CleanSingle` → 删除本地文件 → `RestoreAsync`（deleted_at = NULL）→ 文件回到 Gallery 显示"云端无、本地无"
  - 撤销 → 重新 `DeleteAsync`（标记 deleted_at），文件回到垃圾筒

这样撤销更有意义——文件内容没了但记录可以回到垃圾筒。具体的：对于"仅本地"文件清理，直接永久删除本地+DB，Toast 显示"已清理 xxx，无法恢复"——不做撤销（因为文件内容确实没了）。

**最终方案**：撤销仅用于清理云端已上传但本地也有的文件（`file.IsUploaded && file.LocalExists`）时选择了"仅删除本地"。此时：
  - 删本地文件 → `RestoreAsync`（回到 Gallery，云端有本地无状态）
  - 撤销 → 重新软删除（deleted_at 恢复），回到垃圾筒

```csharp
// 云端文件选择「仅删除本地」后 → Toast「已释放本地空间」+ 撤销按钮
// 撤销 → DB 重新标记 deleted_at，文件回到垃圾筒「已在云端」段
```

### 6.2 Toast 位置——顶部横幅

原 Toast 在 `Grid.Row="1" VerticalAlignment="Bottom"`，改为顶部横幅：

```xml
<!-- 顶部横幅 Toast（标题栏下方，卡片上方） -->
<Border Grid.Row="0" IsVisible="{Binding ToastMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
        Background="{DynamicResource Bg.SurfaceElevated}"
        BorderBrush="{DynamicResource Bg.Divider}" BorderThickness="0,0,0,1"
        Padding="16,10">
    <StackPanel Orientation="Horizontal" Spacing="12">
        <TextBlock Text="{Binding ToastMessage}" FontSize="13"
                   Foreground="{DynamicResource Text.Primary}" VerticalAlignment="Center"/>
        <!-- 操作按钮（撤销/跳转），仅 ToastActionText 非空时显示 -->
        <Button Content="{Binding ToastActionText}"
                Classes="link-button"
                Command="{Binding ToastActionCommand}"
                IsVisible="{Binding ToastActionText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
    </StackPanel>
</Border>
```

### 6.3 "清空垃圾筒"统计

对话框已包含统计信息（第 369 行 `message: $"将永久删除 {total} 个文件。\n其中 {cloudCount} 个文件已上传云端。"`），需求要求"共有 N 个云端文件、M 个本地文件"——当前已基本满足，微调措辞：

```csharp
message: $"共有 {cloudCount} 个云端文件、{localCount} 个本地文件。\n确认清空？"
```

---

## 7. 导航

### 7.1 容量统计

标题栏 `TextBlock` 由"垃圾筒"扩展为统计信息：

```xml
<TextBlock FontSize="20" FontWeight="SemiBold"
           Foreground="{DynamicResource Text.Primary}" VerticalAlignment="Center">
    <Run Text="垃圾筒"/>
    <Run Text="{Binding CapacityStats, StringFormat=' · {0}'}"
         FontSize="14" FontWeight="Normal"
         Foreground="{DynamicResource Text.Secondary}"/>
</TextBlock>
```

**ViewModel**：

```csharp
public string CapacityStats
{
    get
    {
        int total = CloudFiles.Count + LocalFiles.Count;
        long totalSize = CloudFiles.Sum(f => f.FileSize) + LocalFiles.Sum(f => f.FileSize);
        return $"{total} 个文件 · {FormatBytes(totalSize)}";
    }
}
```

`OnPropertyChanged(nameof(CapacityStats))` 在 LoadAsync / Restore / CleanSingle / BatchCleanAll 末尾调用。

### 7.2 跳转到文件

恢复成功后，Toast 显示"已恢复 xxx"并附带"跳转"链接。点击切换到 Gallery 并定位到该文件。

**TrashViewModel 新增回调**：

```csharp
private readonly Action<long>? _onNavigateToFile;

// 构造函数新增参数
public TrashViewModel(..., Action<long>? onNavigateToFile = null)
{
    _onNavigateToFile = onNavigateToFile;
}

// Restore 成功后
ShowActionToast($"已恢复 {file.FileName}", "跳转", () =>
{
    _onNavigateToFile?.Invoke(file.Id);
});
```

**MainWindowViewModel 注册回调**：

```csharp
TrashViewModel = new TrashViewModel(
    ...,
    onNavigateToFile: mediaId => NavigateToGalleryAndLocateFile(mediaId));

private void NavigateToGalleryAndLocateFile(long mediaId)
{
    // 1. 切换到 Gallery
    CurrentView = GalleryViewModel;
    CurrentPage = ViewPage.Gallery;
    // 2. 通知 Gallery 定位
    GalleryViewModel.LocateAndScrollTo(mediaId);
}
```

**GalleryViewModel 新增**：

```csharp
public void LocateAndScrollTo(long mediaId)
{
    // 设置需要定位的 mediaId，GalleryView 滚动到对应卡片
    _pendingScrollToId = mediaId;
    // 在下一次 ScrollViewer 布局完成后执行 ScrollTo
}
```

> 若项目路径已变更（垃圾筒是全局的），`LocateAndScrollTo` 需要先切换到该文件所在项目再定位。本次简化：仅当 Gallery 当前项目包含该文件时才定位，否则 Toast 仅显示文字不带跳转链接。

---

## 8. 按钮色彩区分

### 8.1 恢复按钮——绿色调

将 `Classes="trash-action"`（白色半透明）改为 `Classes="trash-action-restore"`：

```css
/* App.axaml 或全局样式 */
.trash-action-restore {
    background: #33AABB88;  /* 绿色半透明 */
    color: #A5D6A7;
    border: 1px solid #66BB6A40;
}
.trash-action-restore:hover {
    background: #4CAABB88;
}
```

### 8.2 清理按钮——保持红色

`Classes="trash-action-danger"` 保持红色基调不动。

### 8.3 下载按钮

保持不变，`Classes="trash-action"`。

---

## 9. Toast 操作按钮机制

`ShowToast(string)` 改为带可选操作按钮的新方法：

```csharp
[ObservableProperty] private string? _toastMessage;
[ObservableProperty] private string? _toastActionText;
[ObservableProperty] private IRelayCommand? _toastActionCommand;

private void ShowToast(string message)
{
    ToastMessage = message;
    ToastActionText = null;
    ToastActionCommand = null;
    _ = Task.Delay(3000).ContinueWith(_ =>
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ToastMessage = null);
    });
}

private void ShowActionToast(string message, string actionText, Action action)
{
    ToastMessage = message;
    ToastActionText = actionText;
    ToastActionCommand = new RelayCommand(action);
    _ = Task.Delay(5000).ContinueWith(_ =>
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ToastMessage = null;
            ToastActionText = null;
            ToastActionCommand = null;
        });
    });
}
```

---

## 10. 边界情况

| 场景 | 处理 |
|------|------|
| 多选模式下切换到其他页 | 退出多选模式（`ExitMultiSelect`） |
| 批量恢复部分失败 | 逐个 try-catch，汇总失败数到 Toast |
| 多选后执行批量操作中，用户再次点击 | `IsCleaning` 防重（已有） |
| 撤销超时（5秒） | 自动清除 `_lastUndoEntry`，Toast 按钮消失 |
| 撤销时文件已被其他操作影响 | `UndoDeleteAsync` 幂等处理 |
| 跳转定位时文件不在当前项目 | 跳转链接不显示（仅显示恢复成功文字） |
| 垃圾筒为空时容量统计 | 显示"0 个文件 · 0 B" |
| 撤销按钮在 Toast 消失前用户切换页面 | Toast 随 TrashView 卸载消失，操作回调失效（无副作用） |
| 右键卡片时已在多选模式 | 仅 ToggleSelect，不重复 EnterMultiSelect |

---

## 11. 与现有系统的关系

### 11.1 不新增数据库迁移

`MediaFile.DeletedAt`、`MediaFile.FileSize`、`MediaFile.RelativePath` 已存在，无需改表。撤销机制复用现有 `RestoreAsync` / `PermanentDeleteAsync`。

### 11.2 不影响 Gallery 多选

Trash 多选是独立状态机，与 Gallery 的 `IsMultiSelectMode` 互不干扰。

### 11.3 不影响 OSS / AI / Settings

仅改动 `TrashView.axaml`、`TrashViewModel.cs`、`MainWindowViewModel.cs`，不触碰其他模块。

### 11.4 新增 Converter

`UnixMsToDateStringConverter` 和 `BytesToHumanReadableConverter` 放入现有 `Converters/` 目录，注册到 `TrashView.axaml` Resources。两者是通用 Converter，后续其他页面也可复用。

### 11.5 样式新增

`trash-action-restore` 样式类追加到全局 `App.axaml` 样式字典。
