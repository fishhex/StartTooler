# 0.11 — 加载状态统一 & Toast 系统 & 确认框「不再提示」

> 对应需求量文档 `doc/0.11/demand/07-general-improve.md` §「加载与反馈」+「确认对话框无不再提示」。
> 核心改动：统一三种加载组件（shimmer / 按钮动画 / 全页遮罩）、扩展 NotificationService 统管所有 toast、「不再提示」确认框服务。

---

## 1. 模块边界

```
┌─────────────────────────────────────────────────┐
│  统一加载组件                                    │
│  Controls/ShimmerPlaceholder.axaml   → 骨架屏   │
│  Converters/ButtonLoadingConverter.cs → 按钮动画 │
│  Controls/LoadingOverlay.axaml       → 全页遮罩  │
├─────────────────────────────────────────────────┤
│  Toast 系统统一                                 │
│  Services/NotificationService.cs    → 已有+扩展 │
│  Views/NotificationCard.axaml       → 已有      │
│  新增 NotificationType.Warning                 │
├─────────────────────────────────────────────────┤
│  确认框「不再提示」                             │
│  Services/DontAskAgainService.cs    → 新增      │
└─────────────────────────────────────────────────┘
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Controls/ShimmerPlaceholder.axaml` | 骨架屏占位控件 | 新增 |
| `Controls/ShimmerPlaceholder.axaml.cs` | 骨架屏动画 | 新增 |
| `Controls/LoadingOverlay.axaml` | 全页遮罩 spinner 控件 | 新增 |
| `Converters/ButtonLoadingConverter.cs` | 按钮文字转 spinning 动画 | 新增 |
| `Services/NotificationService.cs` | 新增 `NotificationType.Warning`，增加类型相关参数 | 修改 |
| `Services/DontAskAgainService.cs` | 存储/读取「不再提示」偏好 | 新增 |
| `Views/GalleryView.axaml` | 缩略图网格替换为 shimmer（加载中时） | 修改 |
| `Views/SettingsView.axaml` | 保存按钮替换为 loading 态 | 修改 |
| `Themes/Colors.axaml` | 新增 toast 颜色键（Success/Error/Warning/Info border） | 修改 |
| `Themes/Styles.axaml` | 新增 shimmer 动画样式 | 修改 |

---

## 3. 统一加载组件

### 3.1 Shimmer 骨架屏

**用途**：列表/网格首次加载时替代空白，显示骨架占位。

```xml
<!-- Controls/ShimmerPlaceholder.axaml -->
<ItemsControl x:Class="Controls.ShimmerPlaceholder" ItemsSource="{Binding PlaceholderItems}">
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
            <WrapPanel />
        </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Border Width="160" Height="120" Margin="4"
                    CornerRadius="6">
                <Border.Background>
                    <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                        <GradientStop Color="{DynamicResource Bg.Surface}" Offset="0"/>
                        <GradientStop Color="{DynamicResource Bg.Hover}" Offset="0.5"/>
                        <GradientStop Color="{DynamicResource Bg.Surface}" Offset="1"/>
                    </LinearGradientBrush>
                </Border.Background>
                <Border.Animations>
                    <Animation Duration="0:0:1.5" IterationCount="INFINITE"
                               PlaybackDirection="Alternate">
                        <KeyFrame KeyTime="0:0:0">
                            <Setter Property="Opacity" Value="0.4"/>
                        </KeyFrame>
                        <KeyFrame KeyTime="0:0:1.5">
                            <Setter Property="Opacity" Value="1.0"/>
                        </KeyFrame>
                    </Animation>
                </Border.Animations>
            </Border>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

**使用方式**：
```xml
<!-- GalleryView.axaml -->
<controls:ShimmerPlaceholder x:Name="PhotoGridShimmer"
    IsVisible="{Binding IsLoadingMediaFiles}"
    PlaceholderItemCount="20" />

<ItemsControl IsVisible="{Binding !IsLoadingMediaFiles}">
    <!-- 实际缩略图网格 -->
</ItemsControl>
```

**PlaceholderItemCount**：默认 20，按每行 4 列 * 5 行填充。

> **动画实现**：Avalonia 的 `Animation` 对 `Opacity` 的支持较好，用渐变背景色 + Opacity 动画模拟 shimmer 效果。不引入 `KeySpline`（项目目标 .NET 9，Avalonia 11.3 支持但跨平台差异大）。降级方案：纯 CSS-like 的 `LinearGradientBrush` 循环动画。

### 3.2 按钮加载态

**用途**：提交类按钮点击后显示 loading 文字，防止重复点击。

```csharp
// Converters/ButtonLoadingConverter.cs
public class IsLoadingToButtonTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isLoading && isLoading)
            return "⏳ 处理中...";
        return parameter?.ToString() ?? "保存";
    }

    public object? ConvertBack(...) => throw new NotImplementedException();
}

public class IsLoadingToButtonEnabledConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isLoading && isLoading)
            return false;
        return true;
    }

    public object? ConvertBack(...) => throw new NotImplementedException();
}
```

**使用方式**：
```xml
<Button Content="{Binding IsSaving, Converter={StaticResource IsLoadingToButtonText},
                       ConverterParameter=保存设置}"
        IsEnabled="{Binding IsSaving, Converter={StaticResource IsLoadingToButtonEnabled}}"
        Command="{Binding SaveSettingsCommand}" />
```

> **设计决策**：使用文字变化（`⏳ 处理中...`）而非内嵌动画。理由：Avalonia 不支持 `Button.Content` 热替换 spinner 控件，且文字 spinning 更轻量。高级动画（旋转 icon）留到 Theme 重构。

### 3.3 全页加载遮罩

**用途**：首次进入页面、批量操作（全选打标等）时遮盖整个页面。

```xml
<!-- Controls/LoadingOverlay.axaml -->
<Border x:Class="Controls.LoadingOverlay"
        IsVisible="{Binding IsPageLoading}"
        Background="#880A0E1A"
        IsHitTestVisible="{Binding IsPageLoading}"
        ZIndex="100">
    <StackPanel HorizontalAlignment="Center"
                VerticalAlignment="Center"
                Spacing="16">
        <!-- 转圈 spinner -->
        <ProgressBar IsIndeterminate="True"
                     Width="48" Height="48"
                     Foreground="{DynamicResource Accent.Stellar}" />
        <TextBlock Text="{Binding LoadingMessage}"
                   FontSize="14"
                   Foreground="{DynamicResource Text.Primary}"
                   HorizontalAlignment="Center" />
    </StackPanel>
</Border>
```

**Binding 属性**（由各页面 ViewModel 提供）：
- `IsPageLoading` — 是否显示遮罩
- `LoadingMessage` — 操作说明文字（如「正在扫描文件…」「正在加载缩略图…」）

---

## 4. Toast 系统统一

### 4.1 当前状态 → 目标

| 页面 | 当前反馈 | 目标 |
|---|---|---|
| Gallery | 工具栏文字 | → 全局 toast |
| 设置页 | 无反馈 | → 全局 toast |
| 回收站 | 底部自定义 toast | → 全局 toast |
| 上传页 | 状态消息区域 | → 两者共存（上传页状态区域保留实时日志，成功/失败加 toast） |

### 4.2 NotificationService 扩展

```csharp
public enum NotificationType
{
    Info,       // 已有
    Success,    // 已有
    Error,      // 已有
    Warning,    // 新增 ←
}
```

### 4.3 各类型参数

| 类型 | 左边条颜色 | 自动消失 | 可手动关闭 |
|---|---|---|---|
| Success | `#4CAF50` 绿色 | 3 秒 | 是 |
| Error | `#F44336` 红色 | 8 秒 | 是 |
| Warning | `#FFA726` 橙色 | 5 秒 | 是 |
| Info | `#4FC3F7` 青色 | 4 秒 | 是 |

### 4.4 颜色 tokenization

在 `Colors.axaml` 新增：

```xml
<Color x:Key="Toast.SuccessBorder">#4CAF50</Color>
<Color x:Key="Toast.ErrorBorder">#F44336</Color>
<Color x:Key="Toast.WarningBorder">#FFA726</Color>
<Color x:Key="Toast.InfoBorder">#4FC3F7</Color>
<Color x:Key="Toast.Background">#1E2233</Color>
```

`NotificationCard.axaml` 左边条绑定改为 `DynamicResource`。

### 4.5 批量 toast 合并

短时间内多次相同操作（如连续删除 10 张照片），合并为一条 toast：

```csharp
// NotificationService 追加
public void ShowOrUpdate(string key, string title, string body, NotificationType type)
{
    // 相同 key 的 toast 更新 body 而非新增
    // key 示例："delete_photos" → "已删除 3 张照片" → "已删除 10 张照片"
}
```

### 4.6 迁移路径

| 当前位置 | 迁移方式 |
|---|---|
| Gallery 工具栏文字反馈 | 改用 `NotificationService.Current.Show(...)` |
| 设置页 `StatusMessage` 属性 | 保持 `StatusMessage` 作为表单验证错误，保存成功 toast 走 NotificationService |
| 回收站底部 toast | 改用 NotificationService |
| 上传页状态消息区域 | 保留（实时日志），成功/失败加 toast 补充 |

---

## 5. 确认框「不再提示」

### 5.1 DontAskAgainService

```csharp
namespace StartTooler.Services;

/// <summary>
/// 管理「不再提示」偏好。存入 config.db 的 dont_ask_again 键。
/// 每个操作类型一个 key，值为 ISO 8601 截止时间（勾选后 30 天内有效）。
/// </summary>
public class DontAskAgainService
{
    private readonly IConfigService _configService;
    private const string ConfigKey = "dont_ask_again";

    /// <summary>检查是否需要弹出确认框</summary>
    public async Task<bool> ShouldAskAsync(string operationKey)
    {
        var prefs = await _configService.GetAsync<Dictionary<string, string>>(ConfigKey);
        if (prefs == null || !prefs.TryGetValue(operationKey, out var expiry))
            return true;

        if (DateTime.TryParse(expiry, out var expiryDate))
            return DateTime.UtcNow >= expiryDate;

        return true; // 解析失败，保守弹出
    }

    /// <summary>记录「不再提示」，30 天内有效</summary>
    public async Task SetDontAskAsync(string operationKey)
    {
        var prefs = await _configService.GetAsync<Dictionary<string, string>>(ConfigKey)
                    ?? new Dictionary<string, string>();
        prefs[operationKey] = DateTime.UtcNow.AddDays(30).ToString("O");
        await _configService.SetAsync(ConfigKey, prefs);
    }

    /// <summary>重置，恢复显示确认框</summary>
    public async Task ResetAsync(string? operationKey = null)
    {
        if (operationKey != null)
        {
            var prefs = await _configService.GetAsync<Dictionary<string, string>>(ConfigKey);
            prefs?.Remove(operationKey);
            await _configService.SetAsync(ConfigKey, prefs ?? new());
        }
        else
        {
            await _configService.SetAsync<Dictionary<string, string>?>(ConfigKey, null);
        }
    }
}
```

### 5.2 确认框集成

```csharp
// 删除确认示例
private async Task DeleteSelectedAsync()
{
    var operationKey = "delete_photos_batch";

    if (await _dontAskAgain.ShouldAskAsync(operationKey))
    {
        var dialog = new ConfirmDialog
        {
            Title = "确认删除",
            Message = $"确定要删除 {_selectedFiles.Count} 张照片吗？",
            ShowDontAskAgain = true,    // 显示 CheckBox
            DontAskAgainText = "30 天内不再提示"
        };

        var result = await dialog.ShowAsync<bool>(_parentWindow);
        if (!result) return;

        if (dialog.IsDontAskAgainChecked)
            await _dontAskAgain.SetDontAskAsync(operationKey);
    }

    // 执行删除
}
```

### 5.3 操作类型 Key 列表

| Key | 触发场景 |
|---|---|
| `delete_photos_batch` | 批量删除照片 |
| `empty_trash` | 清空回收站 |
| `release_local_space` | 释放本地空间 |
| `oss_upload_overwrite` | OSS 上传覆盖已有文件 |
| `change_ai_vendor` | 切换 AI 厂商 |

---

## 6. 边界情况

| 场景 | 处理 |
|---|---|
| Toast 同时显示 > 5 条 | 显示最近 5 条，旧的自动 dismiss；状态栏铃铛看历史 |
| Shimmer 渲染时收到新数据 | 立即隐藏 shimmer，显示真实内容（不等动画完成） |
| 按钮 loading 态持续 > 30s | 「处理中...」后追加已用时间（每 5s 更新一次） |
| 同类型 toast 3 秒内多次触发 | 合并为同一个 toast（§4.5） |
| 30 天到期后 | `DontAskAgain` 自动失效，下次恢复弹出 |

---

## 7. 不做清单

| 内容 | 理由 |
|---|---|
| Toast 支持内嵌按钮（如「撤销」） | 撤销已通过独立 toast + 定时消失机制实现 |
| Shimmer 自定义形状（圆形/文字条） | 缩略图网格只需矩形占位 |
| 按钮动画替换为图标旋转 | 需引入额外动画基础设施，Phase 1 用文字变化 |
| 「不再提示」设置页统一管理面板 | Phase 2，当前用 reset API 即可 |
