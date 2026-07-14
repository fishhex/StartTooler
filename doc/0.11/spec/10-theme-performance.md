# 0.11 — 主题图标去硬编码 & 性能感知提示

> 对应需求量文档 `doc/0.11/demand/07-general-improve.md` §「主题」+「性能感知」。
> 核心改动：将硬编码颜色改为 DynamicResource 以兼容 Red Night Vision；大图库性能提示；缩略图缓存管理 UI。

---

## 1. 模块边界

```
┌─────────────────────────────────────────────────┐
│  图标颜色 tokenization                           │
│  StatusLegend.axaml 硬编码 Fill → DynamicResource│
│  RedNightVision.axaml 覆盖为红光兼容色             │
│  Colors.axaml 新增 StatusIcon Colors              │
├─────────────────────────────────────────────────┤
│  缩略图缓存管理                                  │
│  ImageCacheService.cs 新增 GetCacheStats()       │
│  SettingsView.axaml 通用 Tab 新增缓存行            │
├─────────────────────────────────────────────────┤
│  大图库性能提示                                  │
│  GalleryViewModel 扫描完成后检测文件数             │
│  MainWindow 状态栏显示提示                        │
└─────────────────────────────────────────────────┘
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Controls/StatusLegend.axaml` | 硬编码 Fill → DynamicResource | 修改 |
| `Themes/Colors.axaml` | 新增 StatusIcon 系列 Color 键 | 修改 |
| `Themes/RedNightVision.axaml` | 覆盖 StatusIcon 键为红光兼容色 | 修改 |
| `Services/ImageCacheService.cs` | 新增 `GetCacheStats()` | 修改 |
| `Views/SettingsView.axaml` | 通用 Tab 新增「缩略图缓存」行 | 修改 |
| `ViewModels/SettingsViewModel.cs` | 新增 `CacheSizeText` + `ClearCacheCommand` | 修改 |
| `ViewModels/GalleryViewModel.cs` | 扫描完成后检测大图库 | 修改 |
| `ViewModels/MainWindowViewModel.cs` | 新增 `LargeLibraryHint` 属性（状态栏） | 修改 |
| `Views/MainWindow.axaml` | 状态栏显示大图库提示 | 修改 |

---

## 3. 图标颜色 tokenization

### 3.1 当前问题

[StatusLegend.axaml](file:///Users/hex/code/StartTooler/StartTooler/Controls/StatusLegend.axaml) 中三处硬编码颜色：

| 行号 | 当前值 | 含义 | 影响 |
|---|---|---|---|
| L48 | `Fill="#4DD0E1"` | 已上传且本地存在（云+勾图标） | Red Night Vision 下仍是青色 |
| L86 | `Fill="#FFA726"` | 已上传但本地不存在（警告三角） | Red Night Vision 下仍是橙色 |
| L124 | `Fill="#4A5273"` | 未上传（空心圆） | Red Night Vision 下仍是灰蓝 |

### 3.2 新增 Color 键

在 `Colors.axaml` 新增：

```xml
<!-- Status Icon Colors (Deep Space 默认值) -->
<Color x:Key="StatusIcon.Synced">#4DD0E1</Color>         <!-- 云同步：青蓝 -->
<Color x:Key="StatusIcon.RemoteOnly">#FFA726</Color>     <!-- 仅云端：橙色 -->
<Color x:Key="StatusIcon.LocalOnly">#4A5273</Color>      <!-- 仅本地：灰蓝 -->
<Color x:Key="StatusIcon.Attention">#F44336</Color>       <!-- 错误/注意：红色（新增） -->
```

### 3.3 修改 StatusLegend.axaml

```xml
<!-- L48: -->
<Path ... Fill="{DynamicResource StatusIcon.Synced}" .../>

<!-- L86: -->
<Path ... Fill="{DynamicResource StatusIcon.RemoteOnly}" .../>

<!-- L124: -->
<Path ... Fill="{DynamicResource StatusIcon.LocalOnly}" .../>
```

### 3.4 Red Night Vision.axaml 覆盖

```xml
<!-- 红光兼容色 — 保留语义但适配红光主题 -->
<Color x:Key="StatusIcon.Synced">#E57373</Color>         <!-- 暖红 → 替代青蓝 -->
<Color x:Key="StatusIcon.RemoteOnly">#EF9A9A</Color>     <!-- 浅红 → 替代橙色 -->
<Color x:Key="StatusIcon.LocalOnly">#37474F</Color>      <!-- 深灰 → 保持低可见 -->
<Color x:Key="StatusIcon.Attention">#EF5350</Color>      <!-- 亮红 → 错误强调 -->
```

### 3.5 同步状态图标的全局替换

除 `StatusLegend.axaml` 外，全局搜索 `Fill=` 硬编码中与同步状态相关的颜色值：

```bash
# 需要替换的位置（全部改为 DynamicResource）
rg 'Fill="?#[0-9A-Fa-f]{6}"' --glob "*.axaml"
```

对以下位置逐一审查并替换：

| 文件 | 场景 | 替换为 |
|---|---|---|
| `Controls/StatusLegend.axaml` (3处) | 状态说明图标 | `StatusIcon.*` |
| `GalleryView.axaml` (photo-tile 角标) | 同步状态小图标 | `StatusIcon.*` |
| `LightboxWindow.axaml` (信息面板) | 同步状态图标 | `StatusIcon.*` |

> **原则**：只替换同步状态相关的颜色硬编码。UI 布局颜色（如 `Background`、`BorderBrush`）已用 `DynamicResource`，不在本次范围。

---

## 4. 性能感知

### 4.1 大图库提示

扫描完成后，若 `media_files` 总数 > 2000，在状态栏显示提示：

```csharp
// GalleryViewModel.cs — 扫描完成回调
internal Action<string?>? OnLargeLibraryHint;

private void OnScanComplete(int totalFiles)
{
    if (totalFiles > 2000)
    {
        var hint = $"大图库模式 · 滚动浏览更流畅（已加载 {totalFiles:N0} 张）";
        OnLargeLibraryHint?.Invoke(hint);
    }
}
```

```csharp
// MainWindowViewModel.cs — 构造函数中桥接
[ObservableProperty] private string? _largeLibraryHint;

// 初始化时：
_galleryVm.OnLargeLibraryHint = hint =>
{
    Dispatcher.UIThread.Post(() => LargeLibraryHint = hint);
};

// 项目切换或刷新时清除
partial void OnCurrentPageIdChanged(string? value)
{
    if (value != "gallery")
        LargeLibraryHint = null;
}
```

**状态栏显示**：

```
┌────────────────────────────────────────────────────────────┐
│  14,256 张  │  🔵 在线  │  💾 123.5 GB  │  📦 大图库模式  │
└────────────────────────────────────────────────────────────┘
```

- 使用 `Text.Tertiary` 颜色（不抢夺主视觉注意力）
- 只显示 10 秒，然后淡出（一次性提示）
- 或常驻状态栏（用户可通过设置关闭）

> **决策建议**：常驻状态栏，文字简单（如 `大图库 · 5,234张`），不自动消失。理由：这是一条有用信息，不是广告。

### 4.2 缩略图缓存管理

#### 4.2.1 ImageCacheService 扩展

```csharp
// ImageCacheService.cs 追加
public static CacheStats GetStats()
{
    var bitmapCount = 0;
    long estimatedMemory = 0;

    foreach (var kvp in s_cache)
    {
        if (kvp.Value.IsCompletedSuccessfully && kvp.Value.Result is Bitmap bitmap)
        {
            bitmapCount++;
            estimatedMemory += (long)bitmap.PixelSize.Width
                              * bitmap.PixelSize.Height
                              * 4; // RGBA 4 bytes per pixel
        }
    }

    return new CacheStats
    {
        CachedImageCount = bitmapCount,
        EstimatedMemoryBytes = estimatedMemory
    };
}

public record CacheStats
{
    public int CachedImageCount { get; init; }
    public long EstimatedMemoryBytes { get; init; }
    public string FormattedSize => estimatedMemoryBytes switch
    {
        >= 100L << 20 => $"{estimatedMemoryBytes / (1 << 20)} MB",
        >= 1L << 20   => $"{estimatedMemoryBytes / (1.0 / (1 << 20)):F1} MB",
        _             => $"{estimatedMemoryBytes / 1024} KB"
    };
}
```

#### 4.2.2 SettingsView 新增缓存行

在「通用」Tab 底部添加：

```xml
<!-- 缩略图缓存 -->
<Border Background="{DynamicResource Bg.Surface}"
        CornerRadius="8" Padding="16,12">
    <Grid ColumnDefinitions="*,Auto">
        <StackPanel>
            <TextBlock Text="缩略图缓存"
                       FontSize="13"
                       Foreground="{DynamicResource Text.Primary}"/>
            <TextBlock Text="{Binding CacheSizeText}"
                       FontSize="11"
                       Foreground="{DynamicResource Text.Tertiary}"/>
        </StackPanel>
        <Button Grid.Column="1"
                Content="清理缓存"
                Command="{Binding ClearCacheCommand}"
                IsEnabled="{Binding HasCache}"
                Classes="outlined"/>
    </Grid>
</Border>
```

#### 4.2.3 SettingsViewModel 追加

```csharp
[ObservableProperty] private string _cacheSizeText = "计算中…";
[ObservableProperty] private bool _hasCache;

[RelayCommand]
private void ClearCache()
{
    ImageCacheService.ClearCache();
    RefreshCacheStats();
    // toast
    NotificationService.Current.Show("缓存已清理", "", NotificationType.Success);
}

private void RefreshCacheStats()
{
    var stats = ImageCacheService.GetStats();
    HasCache = stats.CachedImageCount > 0;
    CacheSizeText = stats.CachedImageCount > 0
        ? $"已缓存 {stats.CachedImageCount} 张 · 约 {stats.FormattedSize}"
        : "缓存为空";
}

// 在 OnGeneralTabActivated 或页面加载时调用 RefreshCacheStats()
```

---

## 5. 边界情况

| 场景 | 处理 |
|---|---|
| `ImageCacheService` 空缓存 | `CacheSizeText` = "缓存为空"，清理按钮禁用 |
| Red Night Vision 主题下硬编码颜色遗漏 | 逐步迁移，未迁移的硬编码颜色仍显示原色（视觉不一致但可接受） |
| `DriveInfo.AvailableFreeSpace` 抛出异常（如权限不足） | 状态栏显示 `—`，不弹错误 |
| 大图库提示在项目切换后 | 清除旧提示，扫描新项目后重新判断 |
| 同时操作清除缓存 + 正在加载缩略图 | `ClearCache()` 清空了正在加载中的 Task → 缩略图重新加载（无副作用） |

---

## 6. 不做清单

| 内容 | 理由 |
|---|---|
| 全局替换所有 XAML 硬编码颜色 | 范围太大，本 spec 只关注主题相关的同步状态图标颜色 |
| 主题自动切换（日落后切 Red Night Vision） | 已在 demand 文档中记录，但不在本轮实施 |
| 缩略图缓存自动 LRU 淘汰 | 当前 `ConcurrentDictionary` 常驻，简化设计 |
| 大图库模式自动启用虚拟化 | 虚拟化是 Gallery spec（02-gallery-improve）的范围 |
| 状态栏性能图表（CPU/内存监控） | 非用户需求 |
