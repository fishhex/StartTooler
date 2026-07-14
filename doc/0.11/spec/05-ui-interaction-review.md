# 0.11 — UI & 交互审计改进

> 对应需求：全项目 UI/交互审计（25 项改进）。
> 核心改动：导航图标补充、照片 tile 信息精简、多选 UI 统一、灯箱视频交互修复、加载态骨架屏、拖拽框选、键盘导航、视觉一致性修复。

---

## 1. 模块边界

```
NavRail（导航栏）
  ├─ 4 个导航按钮增加 PathIcon（图像/云/回收站/齿轮）
  ├─ ToolTip 追加快捷键提示（Ctrl+1~4）
  └─ 激活态加粗 + hover 前景色变化

GalleryView（画廊——核心改动区）
  ├─ 照片 Tile 信息精简
  │    ├─ 同步状态 6→1 徽章合并
  │    ├─ AI 评分 + 内容标签合并为单条底部信息栏
  │    └─ 质量标签仅 hover 显示
  ├─ 非多选模式：单击 → 直接进入灯箱预览
  ├─ 键盘左右箭头导航网格照片
  ├─ 拖拽矩形框选（多选模式下）
  ├─ 骨架屏替代 "加载中..." 纯文字
  ├─ 时间轴节点追加照片数量
  └─ 标签列表选中态文字变色

TrashView（垃圾筒）
  ├─ 多选圆圈统一为 Gallery 风格（替换原生 CheckBox）
  └─ 卡片尺寸统一为 160×120

LightboxWindow（灯箱）
  ├─ 视频模式 overlay 可点击 → 直接触发 OpenExternally
  ├─ 侧面板新增 EXIF 信息区
  ├─ 键盘 +/- 缩放 + 100% 重置按钮
  └─ Space 键视频播放（已有，确认有效）

SettingsView（设置）
  ├─ Tab 栏从 Button 改为原生 TabControl
  ├─ 新增「关于」Tab
  └─ 表单验证增加 TextChanged debounce（500ms）

UploadServerView（上传服务）
  ├─ 🎲 emoji → 纯文字 "随机端口"
  ├─ 📋 emoji → Icon.Copy 图标
  ├─ 📷 emoji → Icon.Photo 图标
  └─ 上传历史增加 "清除历史" 按钮

MainWindow（壳）
  ├─ 工具栏多选按钮过多 → "更多 ▾" 下拉收拢低频操作
  └─ 状态栏增加通知历史 🔔 入口

全局视觉一致性
  ├─ State.Error → State.Danger 统一命名
  ├─ 硬编码 # 颜色 → Colors.axaml 语义化 Token（Overlay.* / Tag.* / Cloud.* / Card.* / Chip.*）
  └─ Emoji 全面替换为 PathIcon

依赖链：
  所有改动集中在 Views/ 和 Controls/ 层，ViewModel 仅需小量补充
    ├─ GalleryViewModel：ToggleSelection 非多选改预览、TagGroupItem 新增 IsSelected
    ├─ TrashViewModel：无改动（CheckBox→自定义圆圈，绑定不变）
    ├─ LightboxViewModel：Keyboard +/- 缩放、ResetZoom 命令
    ├─ SettingsViewModel：SelectedTabIndex int 属性、About 页静态数据、debounce 验证
    ├─ UploadServerViewModel：ClearUploadHistory 命令
    ├─ MainWindowViewModel：NotificationHistory 绑定
    ├─ NotificationService：History 列表（最近 10 条）
    └─ Converters：新增 SingleSyncBadgeConverter、ExifInfoConverter
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Themes/Icons.axaml` | 新增 `Icon.Copy` 图标 | 修改 |
| `Themes/Colors.axaml` | 新增 Overlay.* / Tag.* / Cloud.* / Card.* / Chip.* 颜色 Token | 修改 |
| `Themes/Styles.axaml` | nav-item.active 加粗 + hover 色、skeleton 动画样式、settings-tabs 样式 | 修改 |
| `Converters/SingleSyncBadgeConverter.cs` | 合并 6 种同步状态为单徽章（icon + color + tooltip） | 新增 |
| `Converters/ExifInfoConverter.cs` | 文件路径 → EXIF 元数据读取 | 新增 |

**修改文件**：

| 文件 | 改动 |
|------|------|
| `Controls/NavRail.axaml` | 4 个按钮增加 PathIcon + ToolTip 追加快捷键 |
| `Views/GalleryView.axaml` | 照片 tile ControlTemplate 精简（合并同步徽章、合并评分+标签、hover 质量标签）；时间轴追加数量；标签列表选中色；骨架屏替代加载文字；拖拽矩形 Canvas 层 |
| `Views/GalleryView.axaml.cs` | 键盘左右箭头导航；拖拽框选 PointerPressed/Moved/Released；PointerEnter/Leave 控制质量标签可见性 |
| `Views/TrashView.axaml` | CheckBox → 自定义圆圈；卡片尺寸 180→120 |
| `Views/LightboxWindow.axaml` | 视频 overlay IsHitTestVisible=true + Tapped→OpenExternally；侧面板新增 EXIF 区；底部100%重置按钮；空间键→OpenExternally（确认） |
| `Views/LightboxWindow.axaml.cs` | OnKeyDown 处理 +/-/0 缩放 |
| `Views/SettingsView.axaml` | Button.tab-item → TabControl；新增「关于」Tab；FFmpeg TextBox 增加 TextChanged debounce |
| `Views/SettingsView.axaml.cs` | OnFfmpegPathTextChanged debounce 逻辑 |
| `Views/UploadServerView.axaml` | emoji 替换为纯文字/PathIcon；上传历史增加清除按钮 |
| `Views/MainWindow.axaml` | 工具栏 "更多" 下拉按钮；状态栏通知铃铛 + Flyout |
| `ViewModels/GalleryViewModel.cs` | `ToggleSelection` 非多选 → PreviewCommand；`TagGroupItem` 新增 `IsSelected` 并按选中更新 |
| `ViewModels/LightboxViewModel.cs` | `ZoomIn`/`ZoomOut`（keyboard 映射已有 Scale 属性）；`ResetZoom` 命令 |
| `ViewModels/SettingsViewModel.cs` | `SelectedTabIndex` int 属性替代 `SelectedTab` 枚举；`AboutVersion` 静态属性；debounce 验证 |
| `ViewModels/UploadServerViewModel.cs` | `ClearUploadHistory` 命令 |
| `ViewModels/MainWindowViewModel.cs` | NotificationHistory 属性绑定 NotificationService.History |
| `Models/Models.cs` | `TagGroupItem` 新增 `IsSelected` 属性 |
| `Services/NotificationService.cs` | 新增 `History` 列表 + `Dismiss` 时记录历史 |

> **不引入新 NuGet 包。**

---

## 3. NavRail 导航栏改进

### 3.1 按钮增加图标

当前 4 个按钮只有 `TextBlock` 纯文字，改为 `StackPanel` 垂直排布 PathIcon + TextBlock：

```xml
<!-- 媒体按钮 -->
<Button Grid.Row="0" Height="56" Classes.nav-item="{Binding IsMediaActive}"
        Command="{Binding NavigateToGalleryCommand}"
        ToolTip.Tip="{Binding NavMediaTooltip}">
    <StackPanel Spacing="4" HorizontalAlignment="Center" VerticalAlignment="Center">
        <PathIcon Data="{DynamicResource Icon.Constellation}"
                  Width="18" Height="18" />
        <TextBlock Text="媒体" FontSize="11" TextAlignment="Center" />
    </StackPanel>
</Button>
```

4 个按钮分别使用：
- `Icon.Constellation`（媒体）
- `Icon.Upload`（上传）
- `Icon.Trash`（垃圾筒）
- `Icon.Settings`（设置）

按钮 `Height` 从 48 → 56 以容纳图标+文字。`PathIcon` 前景色自动跟随按钮 `Foreground`（激活态自动变 `Accent.Stellar`）。

### 3.2 快捷键提示

`MainWindowViewModel` 新增属性：

```csharp
public string NavMediaTooltip => OperatingSystem.IsMacOS() ? "媒体 (⌘1)" : "媒体 (Ctrl+1)";
public string NavUploadTooltip => OperatingSystem.IsMacOS() ? "上传 (⌘2)" : "上传 (Ctrl+2)";
public string NavTrashTooltip => OperatingSystem.IsMacOS() ? "垃圾筒 (⌘3)" : "垃圾筒 (Ctrl+3)";
public string NavSettingsTooltip => OperatingSystem.IsMacOS() ? "设置 (⌘4)" : "设置 (Ctrl+4)";
```

### 3.3 激活态加强

在 [Styles.axaml](/Users/hex/code/StartTooler/StartTooler/Themes/Styles.axaml) 的 `Button.nav-item.active` 中追加：

```xml
<Setter Property="FontWeight" Value="SemiBold" />
```

`Button.nav-item:pointerover` 追加：

```xml
<Setter Property="Foreground" Value="{DynamicResource Text.Primary}" />
```

---

## 4. 工具栏多选按钮收拢

### 4.1 按钮分组

当前全部按钮平铺，改为核心 6 个 + "更多"下拉：

```
[取消多选] [全选] [批量上传] [开始AI] [编辑标签] [删除] [更多 ▾]
                                                         ├─ 反选
                                                         ├─ 批量下载
                                                         └─ 释放空间
```

```xml
<!-- "更多 ▾" 下拉按钮 (接在删除按钮后面) -->
<Button Classes="toolbar-button" Content="更多 ▾"
        IsVisible="{Binding IsMultiSelectMode}"
        Margin="6,0,0,0">
    <Button.Flyout>
        <MenuFlyout>
            <MenuItem Header="反选" Command="{Binding InvertSelectionCommand}" />
            <MenuItem Header="批量下载" Command="{Binding BatchDownloadCommand}" />
            <MenuItem Header="释放本地空间" Command="{Binding BatchFreeUpSpaceCommand}" />
        </MenuFlyout>
    </Button.Flyout>
</Button>
```

---

## 5. 照片 Tile 信息精简

### 5.1 同步状态徽章合并（6→1）

现状：6 个独立 `Border`（3 持久态 + 3 瞬时态），每次只有 1 个可见。合并为单个 `Border`。
新增 `Converters/SingleSyncBadgeConverter.cs`：

```csharp
public sealed class SyncBadgeInfo
{
    public string IconKey { get; init; }       // e.g. "Icon.Cloud"
    public string ColorKey { get; init; }      // e.g. "State.Success" / "State.Warning"
    public string Tooltip { get; init; }
    public bool IsVisible { get; init; }
}

public sealed class SingleSyncBadgeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MediaFile mf) return new SyncBadgeInfo { IsVisible = false };

        return (mf.SyncStatus, mf.UploadStatus) switch
        {
            // Uploading（瞬时态优先）
            (_, "Uploading") => new SyncBadgeInfo { IsVisible = true, IconKey = "Icon.Cloud", ColorKey = "Accent.Stellar", Tooltip = "上传中..." },
            (_, "Failed") => new SyncBadgeInfo { IsVisible = true, IconKey = "Icon.Cloud", ColorKey = "State.Danger", Tooltip = "上传失败" },
            (_, "Paused") => new SyncBadgeInfo { IsVisible = true, IconKey = "Icon.Cloud", ColorKey = "Text.Disabled", Tooltip = "已暂停" },
            // 持久态
            (SyncStatus.Uploaded, _) => new SyncBadgeInfo { IsVisible = true, IconKey = "Icon.Cloud", ColorKey = "State.Success", Tooltip = "已同步" },
            (SyncStatus.ModifiedLocally, _) => new SyncBadgeInfo { IsVisible = true, IconKey = "Icon.Cloud", ColorKey = "State.Warning", Tooltip = "本地已修改" },
            _ => new SyncBadgeInfo { IsVisible = false },
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotSupportedException();
}
```

在 GalleryView tile 中简化为：

```xml
<Border Grid.Row="0" Grid.Column="0" VerticalAlignment="Top" HorizontalAlignment="Right"
        Background="{Binding ., Converter={StaticResource SyncBadgeToBg}}"
        CornerRadius="4" Padding="4,2" Margin="4" Height="20"
        IsVisible="{Binding ., Converter={StaticResource SyncBadgeToVis}}"
        ToolTip.Tip="{Binding ., Converter={StaticResource SyncBadgeToTooltip}}">
    <PathIcon Data="{Binding ., Converter={StaticResource SyncBadgeToIcon}}"
              Width="12" Height="12" />
</Border>
```

> 由于 Avalonia Converter 不支持多路输出，改为直接在每个属性绑定调不同的静态方法 Converter。详见 §11 Converter 设计。

### 5.2 AI 评分 + 内容标签合并

现状：评分角标在左下（独立 Border），内容标签条在底部居中（独立 Border），两行冗余。

合并为一条底部信息栏：

```xml
<!-- 底部信息栏（替代原来的评分角标 + 内容标签条） -->
<Border Grid.Row="0" Grid.Column="0" VerticalAlignment="Bottom"
        Background="#CC0A0E1A" CornerRadius="0" Padding="4,2">
    <StackPanel Orientation="Horizontal" Spacing="8">
        <!-- 评分 -->
        <TextBlock Text="{Binding Score, Converter={StaticResource ScoreToDisplay}}"
                   FontSize="10" FontWeight="Bold"
                   Foreground="{Binding Score, Converter={StaticResource ScoreToBrush}}"
                   IsVisible="{Binding Score, Converter={StaticResource GreaterThanZero}}" />
        <!-- 内容标签 -->
        <TextBlock Text="{Binding Tags, Converter={StaticResource TagsToShortText}}"
                   FontSize="10"
                   Foreground="{DynamicResource Text.Primary}"
                   TextTrimming="CharacterEllipsis" MaxLines="1"
                   MaxWidth="100"
                   ToolTip.Tip="{Binding Tags, Converter={StaticResource TagsToFullText}}" />
    </StackPanel>
</Border>
```

### 5.3 质量标签仅 hover 显示

质量标签条绑定 `IsVisible` 到 tile 的 hover 状态。在 `GalleryView.axaml.cs` 中维护 hover 集合：

```csharp
private readonly HashSet<MediaFile> _hoveredFiles = new();

private void OnPhotoTilePointerEntered(object? sender, PointerEventArgs e)
{
    if (sender is Button btn && btn.DataContext is MediaFile mf)
    {
        mf.IsHovered = true;
        _hoveredFiles.Add(mf);
    }
}

private void OnPhotoTilePointerExited(object? sender, PointerEventArgs e)
{
    if (sender is Button btn && btn.DataContext is MediaFile mf)
    {
        mf.IsHovered = false;
        _hoveredFiles.Remove(mf);
    }
}
```

`MediaFile` 模型新增 `[ObservableProperty] private bool _isHovered;`。

质量标签条绑定：

```xml
<Border IsVisible="{Binding IsHovered}" ...>
    <TextBlock Text="{Binding QualityTags, Converter={StaticResource QualityTagsToShortText}}" ... />
</Border>
```

---

## 6. 非多选模式单击进入预览

修改 [GalleryViewModel.cs](/Users/hex/code/StartTooler/StartTooler/ViewModels/GalleryViewModel.cs) `ToggleSelection`：

```csharp
[RelayCommand]
private void ToggleSelection(MediaFile? file)
{
    if (file == null) return;

    if (!IsMultiSelectMode)
    {
        // 非多选模式：单击直接进入灯箱预览
        PreviewCommand.Execute(file);
        return;
    }

    // 多选模式：切换选中
    if (SelectedFiles.Contains(file))
        SelectedFiles.Remove(file);
    else
        SelectedFiles.Add(file);
}
```

双击行为不变（仍触发 `OnPhotoTileDoubleTapped` → `PreviewCommand`）。

---

## 7. 键盘导航照片网格

在 [GalleryView.axaml.cs](/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml.cs) 中处理左右箭头：

```csharp
private int _focusedIndex = -1;

protected override void OnKeyDown(KeyEventArgs e)
{
    if (ViewModel?.CurrentMediaFiles is not { Count: > 0 } files) return;
    base.OnKeyDown(e);

    int newIndex = e.Key switch
    {
        Key.Left => Math.Max(0, _focusedIndex - 1),
        Key.Right => Math.Min(files.Count - 1, _focusedIndex + 1),
        _ => -1,
    };

    if (newIndex < 0) return;

    // 更新焦点高亮
    if (_focusedIndex >= 0 && _focusedIndex < files.Count)
        files[_focusedIndex].IsKeyboardFocused = false;
    files[newIndex].IsKeyboardFocused = true;
    _focusedIndex = newIndex;

    // 滚动到可见区域
    ScrollPhotoIntoView(newIndex);

    e.Handled = true;
}
```

`MediaFile` 模型新增 `[ObservableProperty] private bool _isKeyboardFocused;`。
GalleryView tile 增加键盘焦点边框：

```xml
<Border BorderBrush="{DynamicResource Accent.Stellar}" BorderThickness="2"
        CornerRadius="6" IsVisible="{Binding IsKeyboardFocused}" />
```

---

## 8. 拖拽框选

在 [GalleryView.axaml](/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml) 的 `ScrollViewer` 内叠加透明 Canvas 层：

```xml
<ScrollViewer x:Name="PhotoScrollViewer" ...>
    <Grid>
        <!-- 原有照片网格 -->
        <ItemsControl ... />
        <!-- 拖拽矩形层 -->
        <Canvas x:Name="SelectionCanvas" IsHitTestVisible="False" />
    </Grid>
</ScrollViewer>
```

在 [GalleryView.axaml.cs](/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml.cs) 中：

```csharp
private Point _dragStart;
private bool _isMarqueeSelecting;
private Border? _marqueeRect;

private void OnPhotoGridPointerPressed(object sender, PointerPressedEventArgs e)
{
    if (ViewModel is not { IsMultiSelectMode: true }) return;
    _dragStart = e.GetPosition(PhotoGrid);
    _isMarqueeSelecting = true;

    _marqueeRect = new Border
    {
        Background = new SolidColorBrush(Color.Parse("#204FC3F7")),
        BorderBrush = new SolidColorBrush(Color.Parse("#4FC3F7")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(2),
        IsHitTestVisible = false,
        IsVisible = false,
    };
    SelectionCanvas.Children.Add(_marqueeRect);
}

private void OnPhotoGridPointerMoved(object sender, PointerEventArgs e)
{
    if (!_isMarqueeSelecting || _marqueeRect == null) return;
    var current = e.GetPosition(PhotoGrid);

    var x = Math.Min(_dragStart.X, current.X);
    var y = Math.Min(_dragStart.Y, current.Y);
    var w = Math.Abs(current.X - _dragStart.X);
    var h = Math.Abs(current.Y - _dragStart.Y);

    Canvas.SetLeft(_marqueeRect, x);
    Canvas.SetTop(_marqueeRect, y);
    _marqueeRect.Width = w;
    _marqueeRect.Height = h;
    _marqueeRect.IsVisible = w > 5 || h > 5;

    // 实时更新选中状态
    var rect = new Rect(x, y, w, h);
    foreach (var mf in ViewModel.CurrentMediaFiles)
    {
        // 通过 item 的 Bounds 计算是否在矩形内
        var isInside = IsItemInRect(mf, rect);
        if (isInside && !ViewModel.SelectedFiles.Contains(mf))
            ViewModel.SelectedFiles.Add(mf);
        else if (!isInside && ViewModel.SelectedFiles.Contains(mf))
            ViewModel.SelectedFiles.Remove(mf);
    }
}

private void OnPhotoGridPointerReleased(object sender, PointerReleasedEventArgs e)
{
    _isMarqueeSelecting = false;
    if (_marqueeRect != null)
    {
        SelectionCanvas.Children.Remove(_marqueeRect);
        _marqueeRect = null;
    }
}
```

> 最小可行版本仅支持矩形框选，不实现 marquee 动画。

---

## 9. 骨架屏替代加载文字

在 [GalleryView.axaml](/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml) 中将 `"加载中..."` 替换为骨架屏：

```xml
<!-- 骨架屏（替代 IsLoadingDateGroups 时的 "加载中..."） -->
<ScrollViewer IsVisible="{Binding IsLoadingDateGroups}">
    <StackPanel Margin="24" Spacing="16">
        <!-- 模拟标题行 -->
        <Border Classes="skeleton" Width="180" Height="16" CornerRadius="4" />
        <!-- 模拟 3 行照片 placeholder -->
        <WrapPanel>
            <Border Classes="skeleton pulse" Width="160" Height="120" CornerRadius="8" Margin="4" />
            <Border Classes="skeleton pulse" Width="160" Height="120" CornerRadius="8" Margin="4" />
            <Border Classes="skeleton pulse" Width="160" Height="120" CornerRadius="8" Margin="4" />
            <Border Classes="skeleton pulse" Width="160" Height="120" CornerRadius="8" Margin="4" />
            <Border Classes="skeleton pulse" Width="160" Height="120" CornerRadius="8" Margin="4" />
        </WrapPanel>
        <Border Classes="skeleton" Width="140" Height="16" CornerRadius="4" Margin="0,12,0,0" />
        <WrapPanel>
            <Border Classes="skeleton pulse" Width="160" Height="120" CornerRadius="8" Margin="4" />
            <Border Classes="skeleton pulse" Width="160" Height="120" CornerRadius="8" Margin="4" />
            <Border Classes="skeleton pulse" Width="160" Height="120" CornerRadius="8" Margin="4" />
        </WrapPanel>
    </StackPanel>
</ScrollViewer>
```

[Styles.axaml](/Users/hex/code/StartTooler/StartTooler/Themes/Styles.axaml) 中新增：

```xml
<Style Selector="Border.skeleton">
    <Setter Property="Background" Value="{DynamicResource Bg.SurfaceElevated}" />
</Style>

<Style Selector="Border.skeleton.pulse">
    <Style.Animations>
        <Animation Duration="0:0:1.5" IterationCount="INFINITE" Easing="SineEaseInOut">
            <KeyFrame Cue="0%">
                <Setter Property="Opacity" Value="0.3" />
            </KeyFrame>
            <KeyFrame Cue="50%">
                <Setter Property="Opacity" Value="0.6" />
            </KeyFrame>
            <KeyFrame Cue="100%">
                <Setter Property="Opacity" Value="0.3" />
            </KeyFrame>
        </Animation>
    </Style.Animations>
</Style>
```

---

## 10. TrashView 多选 & 尺寸统一

### 10.1 CheckBox → 自定义圆圈

替换 [TrashView.axaml](/Users/hex/code/StartTooler/StartTooler/Views/TrashView.axaml) 中 `CloudFiles` 和 `LocalFiles` DataTemplate 的 `CheckBox` 为与 Gallery 相同的自定义圆圈：

```xml
<!-- 替换原有的 CheckBox -->
<Panel HorizontalAlignment="Center" VerticalAlignment="Center"
       Width="28" Height="28"
       IsVisible="{Binding $parent[ItemsControl].DataContext.IsMultiSelectMode}">
    <!-- 未选中：空心圆 -->
    <Border Width="28" Height="28"
            Background="#AA000000"
            BorderBrush="#E6FFFFFF"
            BorderThickness="2"
            CornerRadius="14"
            IsVisible="{Binding !IsSelected}" />
    <!-- 已选中：实心青蓝圆 + 对勾 -->
    <Border Width="28" Height="28"
            Background="{DynamicResource Accent.Stellar}"
            CornerRadius="14"
            IsVisible="{Binding IsSelected}">
        <PathIcon Data="{StaticResource Icon.Check}"
                  Width="16" Height="16"
                  Foreground="#0A0E1A" />
    </Border>
</Panel>
```

### 10.2 卡片高度 180 → 120

将 Trash 卡片从 `Height="180"` 改为 `Height="120"`，底部信息区压缩为单行：

```xml
<Border Grid.Row="2" Background="{DynamicResource Card.FooterBg}" Padding="4,2">
    <StackPanel Spacing="2">
        <TextBlock Text="{Binding FileName}" FontSize="10"
                   Foreground="{DynamicResource Card.FooterText}"
                   TextTrimming="CharacterEllipsis" MaxLines="1" />
        <TextBlock Text="{Binding DeletedAtMs, Converter={StaticResource UnixMsToDate}}"
                   FontSize="8" Foreground="{DynamicResource Text.Tertiary}" />
    </StackPanel>
</Border>
```

操作按钮移到 hover 浮层，不在常态卡片上显示。

---

## 11. 灯箱改进

### 11.1 视频模式点击 overlay 直接播放

[LightboxWindow.axaml](/Users/hex/code/StartTooler/StartTooler/Views/LightboxWindow.axaml) 视频 overlay 从 `IsHitTestVisible="False"` 改为可点击：

```xml
<!-- 居中 ▶ 播放 overlay（可点击） -->
<Border HorizontalAlignment="Center" VerticalAlignment="Center"
        Background="#CC0A0E1A" CornerRadius="48"
        Padding="40"
        Cursor="Hand"
        Tapped="OnVideoPlayTapped">
    <StackPanel Spacing="12" HorizontalAlignment="Center">
        <PathIcon Data="{StaticResource Icon.Play}"
                  Width="64" Height="64"
                  Foreground="#E6FFFFFF" />
        <TextBlock Text="点击播放视频"
                   FontSize="13"
                   Foreground="#E6FFFFFF"
                   HorizontalAlignment="Center" />
    </StackPanel>
</Border>
```

[LightboxWindow.axaml.cs](/Users/hex/code/StartTooler/StartTooler/Views/LightboxWindow.axaml.cs)：

```csharp
private void OnVideoPlayTapped(object? sender, TappedEventArgs e)
{
    if (DataContext is LightboxViewModel vm)
        vm.OpenExternallyCommand.Execute(null);
}
```

### 11.2 侧面板新增 EXIF 信息

新增 `Converters/ExifInfoConverter.cs`：

```csharp
public sealed class ExifInfo
{
    public string? CameraModel { get; set; }
    public string? Aperture { get; set; }
    public string? ShutterSpeed { get; set; }
    public string? Iso { get; set; }
    public string? FocalLength { get; set; }
    public bool HasExif => CameraModel != null || Aperture != null || Iso != null;
}

public sealed class FilePathToExifConverter : IValueConverter
{
    // 使用 SkiaSharp SKCodec 读取 EXIF
    // 或通过 System.Drawing / MetadataExtractor
}
```

在灯箱侧面板「文件大小」之后、「同步状态」之前插入 EXIF 区域：

```xml
<StackPanel Spacing="4" IsVisible="{Binding CurrentFile.Path, Converter={StaticResource ExifToVis}}">
    <TextBlock Text="拍摄参数" FontSize="12" FontWeight="SemiBold"
               Foreground="{DynamicResource Text.Primary}" />
    <StackPanel Spacing="2" Foreground="{DynamicResource Text.Secondary}" FontSize="11">
        <TextBlock Text="{Binding CurrentFile.Path, Converter={StaticResource ExifToCamera}}" />
        <TextBlock>
            <Run Text="{Binding CurrentFile.Path, Converter={StaticResource ExifToAperture}}" />
            <Run Text=" · " />
            <Run Text="{Binding CurrentFile.Path, Converter={StaticResource ExifToShutter}}" />
            <Run Text=" · ISO " />
            <Run Text="{Binding CurrentFile.Path, Converter={StaticResource ExifToIso}}" />
        </TextBlock>
        <TextBlock Text="{Binding CurrentFile.Path, Converter={StaticResource ExifToFocalLength}}" />
    </StackPanel>
</StackPanel>
```

> 如果不想引入新库，可先用 `System.IO.File.ReadAllBytes` + JPEG header 手工解析（EXIF IFD0 中的 Make/Model/FNumber/ISOSpeedRatings/FocalLength/ExposureTime）。

### 11.3 键盘 +/- 缩放 + 100% 重置

在 [LightboxWindow.axaml.cs](/Users/hex/code/StartTooler/StartTooler/Views/LightboxWindow.axaml.cs) 的 `OnKeyDown` 中追加：

```csharp
case Key.OemPlus or Key.Add:
    if (vm.Scale < 5.0) vm.Scale += 0.25;
    e.Handled = true;
    break;
case Key.OemMinus or Key.Subtract:
    if (vm.Scale > 0.25) vm.Scale -= 0.25;
    e.Handled = true;
    break;
case Key.D0 or Key.NumPad0:
    vm.Scale = 1.0;
    e.Handled = true;
    break;
```

底部工具栏滑块旁增加 100% 重置按钮：

```xml
<Button Classes="LightboxToolButton" Command="{Binding ResetZoomCommand}"
        ToolTip.Tip="重置缩放" IsVisible="{Binding IsImage}">
    <TextBlock Text="100%" FontSize="11" />
</Button>
```

---

## 12. 设置页改进

### 12.1 Tab 栏改为原生 TabControl

替换 [SettingsView.axaml](/Users/hex/code/StartTooler/StartTooler/Views/SettingsView.axaml) 的 `Button.tab-item` 为：

```xml
<TabControl Grid.Row="0"
            SelectedIndex="{Binding SelectedTabIndex}"
            Classes="settings-tabs">
    <TabItem Header="通用" />
    <TabItem Header="OSS 配置" />
    <TabItem Header="AI" />
    <TabItem Header="关于" />
</TabControl>
```

`SettingsViewModel` 新增：

```csharp
[ObservableProperty]
private int _selectedTabIndex;

// 保持兼容原有的 SettingsTab 枚举 → 索引映射
```

[Styles.axaml](/Users/hex/code/StartTooler/StartTooler/Themes/Styles.axaml) 新增：

```xml
<Style Selector="TabControl.settings-tabs">
    <Setter Property="TabStripPlacement" Value="Top" />
</Style>
<Style Selector="TabControl.settings-tabs TabItem">
    <Setter Property="FontSize" Value="13" />
    <Setter Property="Foreground" Value="{DynamicResource Text.Secondary}" />
    <Setter Property="Padding" Value="16,10" />
</Style>
<Style Selector="TabControl.settings-tabs TabItem:selected">
    <Setter Property="Foreground" Value="{DynamicResource Accent.Stellar}" />
    <Setter Property="FontWeight" Value="SemiBold" />
</Style>
```

### 12.2 关于页

```xml
<StackPanel Margin="40,24" Spacing="16"
            IsVisible="{Binding SelectedTabIndex, Converter={StaticResource IntEquals}, ConverterParameter=3}">
    <TextBlock Text="星助 StartTooler" FontSize="20" FontWeight="SemiBold"
               Foreground="{DynamicResource Text.Primary}" />
    <TextBlock Text="{Binding AppVersion}" FontSize="13"
               Foreground="{DynamicResource Text.Secondary}" />
    <TextBlock Text="跨平台桌面媒体管理工具" FontSize="13"
               Foreground="{DynamicResource Text.Secondary}" />
    <TextBlock Text="Avalonia UI + .NET 9.0 构建 · 支持照片/视频浏览、阿里云 OSS 云存储、AI 智能打标、局域网上传。"
               TextWrapping="Wrap" FontSize="12"
               Foreground="{DynamicResource Text.Tertiary}" MaxWidth="400" />
    <TextBlock Text="{Binding AppLicense}" FontSize="11"
               Foreground="{DynamicResource Text.Disabled}" MaxWidth="400" />
</StackPanel>
```

`SettingsViewModel` 新增：

```csharp
public string AppVersion =>
    $"版本 {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.11.0"}";

public string AppLicense => "MIT License · 开源许可";
```

### 12.3 表单验证 debounce

在 [SettingsView.axaml.cs](/Users/hex/code/StartTooler/StartTooler/Views/SettingsView.axaml.cs) 中：

```csharp
private CancellationTokenSource? _validateCts;
private readonly TimeSpan _validateDebounce = TimeSpan.FromMilliseconds(500);

private void OnFfmpegPathTextChanged(object? sender, TextChangedEventArgs e)
{
    _validateCts?.Cancel();
    _validateCts = new CancellationTokenSource();
    var ct = _validateCts.Token;

    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(_validateDebounce, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (DataContext is SettingsViewModel vm)
                    vm.ValidateFfmpegPath();
            });
        }
        catch (OperationCanceledException) { }
    }, ct);
}
```

保留 `LostFocus` 立即验证作为补充。

---

## 13. 上传服务页改进

### 13.1 Emoji 替换

```xml
<!-- 🎲 随机端口 → 纯文字 -->
<Button Content="随机端口" Classes="link-button" Command="{Binding RandomPortCommand}" Margin="8,0,0,0" />

<!-- 📋 复制 → Icon.Copy -->
<Button Margin="4,0,0,0" Classes="toolbar-button" Command="{Binding CopyUrlCommand}">
    <PathIcon Data="{StaticResource Icon.Copy}" Width="12" Height="12" />
</Button>
```

新增 `Icon.Copy` 到 [Icons.axaml](/Users/hex/code/StartTooler/StartTooler/Themes/Icons.axaml)：

```xml
<StreamGeometry x:Key="Icon.Copy">M16 1H4a2 2 0 00-2 2v14h2V3h12V1zm3 4H8a2 2 0 00-2 2v14a2 2 0 002 2h11a2 2 0 002-2V7a2 2 0 00-2-2zm0 16H8V7h11v14z</StreamGeometry>
```

`📷` 在编辑标签弹窗中替换为 `Icon.Photo`：

```xml
<PathIcon Data="{StaticResource Icon.Photo}" Width="24" Height="24"
          Foreground="{DynamicResource Text.Disabled}" />
```

### 13.2 上传历史清除按钮

```xml
<Grid ColumnDefinitions="*,Auto" Margin="0,12,0,8">
    <TextBlock Grid.Column="0" Text="最近上传"
               FontSize="12" FontWeight="SemiBold"
               Foreground="{DynamicResource Text.Primary}" />
    <Button Grid.Column="1" Content="清除"
            Classes="toolbar-button" FontSize="11"
            Command="{Binding ClearUploadHistoryCommand}" />
</Grid>
```

`UploadServerViewModel` 新增：

```csharp
[RelayCommand]
private void ClearUploadHistory()
{
    UploadHistory.Clear();
}
```

---

## 14. 通知历史

### 14.1 NotificationService 增加历史

```csharp
public class NotificationService
{
    public ObservableCollection<NotificationItem> Items { get; } = new();
    public ObservableCollection<NotificationItem> History { get; } = new(); // 新增

    public void Dismiss(NotificationItem item)
    {
        Items.Remove(item);
        History.Insert(0, item);
        if (History.Count > 10) History.RemoveAt(History.Count - 1);
    }
}
```

### 14.2 状态栏通知铃铛

[MainWindow.axaml](/Users/hex/code/StartTooler/StartTooler/Views/MainWindow.axaml) 状态栏增加：

```xml
<Button DockPanel.Dock="Right" Classes="toolbar-button"
        ToolTip.Tip="通知历史">
    <PathIcon Data="{StaticResource Icon.Bell}" Width="14" Height="14" />
    <Button.Flyout>
        <Flyout Placement="Top">
            <Border MinWidth="280" MaxWidth="360" MaxHeight="400"
                    Background="{DynamicResource Bg.SurfaceElevated}"
                    CornerRadius="{DynamicResource Radius.Medium}" Padding="8">
                <StackPanel Spacing="4">
                    <TextBlock Text="通知历史" FontSize="14" FontWeight="SemiBold"
                               Foreground="{DynamicResource Text.Primary}" Margin="0,0,0,8" />
                    <ScrollViewer MaxHeight="360">
                        <ItemsControl ItemsSource="{Binding NotificationHistory}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate x:DataType="services:NotificationItem">
                                    <!-- 简化版通知卡片（不含关闭按钮） -->
                                    <Border Background="{DynamicResource Bg.Surface}"
                                            CornerRadius="4" Padding="8,6" Margin="0,2">
                                        <StackPanel Spacing="2">
                                            <TextBlock Text="{Binding Title}" FontSize="12"
                                                       Foreground="{DynamicResource Text.Primary}" />
                                            <TextBlock Text="{Binding Body}" FontSize="11"
                                                       Foreground="{DynamicResource Text.Secondary}"
                                                       TextTrimming="CharacterEllipsis" MaxLines="2" />
                                        </StackPanel>
                                    </Border>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </ScrollViewer>
                </StackPanel>
            </Border>
        </Flyout>
    </Button.Flyout>
</Button>
```

如果 `Icons.axaml` 中没有铃铛图标，新增：

```xml
<StreamGeometry x:Key="Icon.Bell">M18 8A6 6 0 006 8c0 7-3 9-3 9h18s-3-2-3-9m-4.27 13a1.98 1.98 0 01-3.46 0</StreamGeometry>
```

---

## 15. 全局视觉一致性

### 15.1 State.Error → State.Danger 统一

搜索全部 `State.Error` 引用，替换为 `State.Danger`：

```bash
grep -rn "State\.Error" StartTooler/Views/ StartTooler/Controls/ StartTooler/Themes/
```

逐文件替换。

### 15.2 硬编码颜色 → 语义化 Token

在 [Colors.axaml](/Users/hex/code/StartTooler/StartTooler/Themes/Colors.axaml) 中新增：

```xml
<!-- ============ 叠加层颜色 ============ -->
<Color x:Key="Overlay.Dark">#CC0A0E1A</Color>
<Color x:Key="Overlay.Light">#CCFFFFFF</Color>
<Color x:Key="Overlay.Selection">#334FC3F7</Color>

<!-- ============ 标签条专用色 ============ -->
<Color x:Key="Tag.QualityBg">#CC3A1F1A</Color>
<Color x:Key="Tag.QualityFg">#FFCC8888</Color>

<!-- ============ 云端/状态色 ============ -->
<Color x:Key="Cloud.Accent">#4DD0E1</Color>
<Color x:Key="Cloud.MissingBg">#33A5D6A7</Color>
<Color x:Key="Cloud.MissingFg">#A5D6A7</Color>

<!-- ============ 卡片用色 ============ -->
<Color x:Key="Card.FooterBg">#CC0A0E1A</Color>
<Color x:Key="Card.FooterText">#E6FFFFFF</Color>

<!-- ============ Chip 用色 ============ -->
<Color x:Key="Chip.DeleteHover">#E63946</Color>
```

然后逐一替换各文件中的硬编码值，如：

| 文件 | 硬编码 | 替换为 |
|------|--------|--------|
| GalleryView.axaml | `#CC0A0E1A` | `{DynamicResource Card.FooterBg}` |
| TrashView.axaml | `#4DD0E1` | `{DynamicResource Cloud.Accent}` |
| TrashView.axaml | `#CC0A0E1A` | `{DynamicResource Card.FooterBg}` |
| TagChipEditor.axaml | `#E63946` | `{DynamicResource Chip.DeleteHover}` |
| LightboxWindow.axaml | `#CC0A0E1A` | `{DynamicResource Overlay.Dark}` |
| LightboxWindow.axaml | `#CCFFFFFF` | `{DynamicResource Overlay.Light}` |
| SettingsView.axaml | `#334FC3F7` | `{DynamicResource Overlay.Selection}` |

### 15.3 时间轴追加照片数量

[GalleryView.axaml](/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml) 时间轴节点从单行改为双行：

```xml
<StackPanel Canvas.Left="32" Canvas.Top="14" Spacing="2">
    <TextBlock Text="{Binding Date, StringFormat='{}{0:yyyy-MM-dd}'}"
               FontFamily="{DynamicResource Font.Mono}" FontSize="13"
               FontWeight="..." Foreground="..." />
    <TextBlock Text="{Binding Count, StringFormat='{}{0} 张'}"
               FontSize="10"
               Foreground="{DynamicResource Text.Tertiary}" />
</StackPanel>
```

调整 `Canvas.Top` 从 16 到 14 微调居中。`Height="48"` 改为 `Height="56"` 以容纳双行。

### 15.4 标签列表选中态

在 [GalleryView.axaml](/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml) 标签列表中为 `TextBlock` 应用选中态变色：

```xml
<Button Classes="tag-node" ...>
    <Grid>
        <TextBlock Text="{Binding Tag, Converter={StaticResource EmptyTagToUntitled}}"
                   HorizontalAlignment="Left" FontSize="13"
                   Foreground="{Binding IsSelected, Converter={StaticResource BoolToAccentOrSecondary}}"
                   FontWeight="{Binding IsSelected, Converter={StaticResource BoolToFontWeight}}" />
        <TextBlock Text="{Binding Count, StringFormat='{}{0}'}"
                   HorizontalAlignment="Right" FontSize="12"
                   Foreground="{Binding IsSelected, Converter={StaticResource BoolToAccentOrSecondary}}" />
    </Grid>
</Button>
```

`TagGroupItem` 模型新增 `[ObservableProperty] private bool _isSelected;`。

在 `GalleryViewModel.SelectTagAsync` 中同步更新 `TagGroups` 各项的 `IsSelected`：

```csharp
private async Task SelectTagAsync(TagGroupItem? tag)
{
    if (tag == null) return;
    foreach (var t in TagGroups) t.IsSelected = t.Tag == tag.Tag;
    await FilterByTagAsync(tag.Tag);
}
```

---

## 16. Converter 设计（SyncBadge 多路输出）

由于 Avalonia `IValueConverter` 只支持单一输出，同步徽章合并需要 3 个独立 Converter：

```csharp
// Converters/SingleSyncBadgeConverter.cs

public sealed class SyncStatusToSingleBadgeIcon : IValueConverter { ... }    // → Icon key
public sealed class SyncStatusToSingleBadgeColor : IValueConverter { ... }   // → Color key  
public sealed class SyncStatusToSingleBadgeVis : IValueConverter { ... }     // → bool
public sealed class SyncStatusToSingleBadgeTooltip : IValueConverter { ... } // → string
```

核心逻辑在共享的静态方法中：

```csharp
private static (string IconKey, string ColorKey, string Tooltip, bool Visible) GetBadgeInfo(MediaFile mf)
{
    if (mf.UploadStatus == "Uploading")
        return ("Icon.Cloud", "Accent.Stellar", "上传中...", true);
    if (mf.UploadStatus == "Failed")
        return ("Icon.Cloud", "State.Danger", "上传失败", true);

    return mf.SyncStatus switch
    {
        SyncStatus.Uploaded => ("Icon.Cloud", "State.Success", "已同步", true),
        SyncStatus.ModifiedLocally => ("Icon.Cloud", "State.Warning", "本地已修改", true),
        _ => ("", "", "", false),
    };
}
```

---

## 17. 边界情况

| 场景 | 处理 |
|------|------|
| NavRail 图标主题切换 | PathIcon.Foreground 自动跟随按钮 Foreground，不需额外处理 |
| 拖拽框选时滚动 | ScrollViewer 不自动滚动（v1 限制）——用户需要手动滚到目标区域再框选 |
| 键盘导航超出范围 | 左边界停留在索引 0，右边界停留在 Count-1 |
| 骨架屏在扫描完成后 | `IsLoadingDateGroups=false` → 骨架屏隐藏，实际内容显示 |
| Trash 卡片缩略图缺失 | `FilePathToBitmap` converter 返回 null → Image 空白，底栏信息仍可见 |
| 标签列表无选中项（初始态） | 所有 `IsSelected=false`，Foreground 均为 `Text.Secondary` |
| 通知历史有 0 条 | 铃铛 Flyout 内显示 "暂无通知" |
| 拖拽距离 < 5px | 不显示矩形，不触发选中变化（区分单击和拖拽） |
| EXIF 读取失败（非 JPEG） | HasExif=false，UI 区域不显示 |
| FFmpeg 路径 debounce 中切 Tab | `CancellationTokenSource.Cancel()` 取消等待，避免操作已卸载的 VM |

---

## 18. 与现有系统的关系

### 18.1 不修改数据库

所有改动不涉及 `MediaFile` 数据库字段（`IsHovered`、`IsKeyboardFocused` 是 ViewModel 运行时状态，不持久化）。

### 18.2 不破坏现有功能

- 双击 → 灯箱行为不变
- 多选逻辑不变（仅增加非多选模式的路由）
- 上传/下载/打标流程不变
- 设置保存/丢弃不变

### 18.3 GalleryViewModel 新增属性

| 属性 | 类型 | 说明 |
|------|------|------|
| — | — | `ToggleSelection` 逻辑修改，不新增属性 |
| `TagGroupItem.IsSelected` | `bool` | 标签列表选中态 |
| `MediaFile.IsHovered` | `bool` | tile hover 态（质量标签显示控制） |
| `MediaFile.IsKeyboardFocused` | `bool` | 键盘导航焦点态 |

### 18.4 不引入新 NuGet 包

- EXIF 读取使用 `System.IO` 手工解析或 `SkiaSharp.SKCodec`（已有依赖）
- 骨架屏动画使用 Avalonia 原生 `Animation`
- 拖拽框选不依赖第三方库
