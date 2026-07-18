# 0.11 — 灯箱图片居中显示修复（方案 A）

> 关联 spec：`01-lightbox-preview.md`（灯箱预览）
> 问题：图片在灯箱中显示在左上角，下方和右侧出现大片黑色区域，无法居中自适应窗口。

---

## 1. 问题根因

当前布局中，`Panel` 的 `Width/Height` 被硬绑到原图尺寸（如 4096×1840）：

```xml
<Panel Width="{Binding ImageWidth}" Height="{Binding ImageHeight}">
    <Image Stretch="Uniform" ...>
        <Image.RenderTransform>
            <ScaleTransform ScaleX="{Binding Scale}" ScaleY="{Binding Scale}"/>
        </Image.RenderTransform>
    </Image>
</Panel>
```

`Image.Stretch="Uniform"` 在 **固定尺寸容器** 内做 Uniform 缩放，但 Panel 已经是原图尺寸，Uniform 不会缩小图片。`ScaleTransform` 只改变 Image 的渲染大小，不影响 Panel 的布局尺寸。结果：

- Scale=1.0 时：Panel = 4096×1840，ScrollViewer 认为内容 > 视口，居中无效，图片贴左上角
- Scale<1.0 时：Image 被 ScaleTransform 缩小，但 Panel 尺寸不变，缩小的图片仍贴在 Panel 左上角
- 下方/右侧黑色 = Panel 背景 `#000000` 暴露

---

## 2. 修复方案（方案 A）

**核心思路**：让 ScrollViewer 看到的是"缩放后的真实尺寸"。当 Scale=0.25 时 Panel 就是 1024×460，ScrollViewer 会说"内容比视口小，居中"。

### 2.1 原理

```
原来：Panel(4096×1840) → Image(ScaleTransform 0.25) → 视觉上 1024×460 但 Panel 还是 4096×1840
修复：Panel(1024×460) → Image(Stretch=Fill) → 视觉上 1024×460，Panel 也是 1024×460
```

ScrollViewer 看到 Panel 的真实尺寸，自动处理：
- Panel < Viewport → 居中显示
- Panel > Viewport → 出现滚动条，可拖拽查看

---

## 3. 改动文件清单

| 文件 | 改动 | 类型 |
|------|------|------|
| `ViewModels/LightboxViewModel.cs` | 新增 `ScaledWidth` / `ScaledHeight` 计算属性 | 修改 |
| `Views/LightboxWindow.axaml` | Panel 绑定改为 `ScaledWidth`/`ScaledHeight`，Image 改为 `Stretch=Fill` | 修改 |

---

## 4. LightboxViewModel 改动

### 4.1 新增属性

```csharp
/// <summary>
/// 缩放后的图片宽度 = ImageWidth × Scale。ScrollViewer 布局用。
/// null 时（图片未探测完成）返回 0，由 FallbackValue 兜底。
/// </summary>
public double ScaledWidth => (ImageWidth ?? 0) * Scale;

/// <summary>
/// 缩放后的图片高度 = ImageHeight × Scale。
/// </summary>
public double ScaledHeight => (ImageHeight ?? 0) * Scale;
```

### 4.2 PropertyChanged 通知

`ScaledWidth` / `ScaledHeight` 是派生属性，不自动通知。需要在以下入口手动触发：

| 触发时机 | 通知方式 |
|---|---|
| `OnScaleChanged` 回调 | `OnPropertyChanged(nameof(ScaledWidth))` + `OnPropertyChanged(nameof(ScaledHeight))` |
| `RefreshImageDimensionsAsync` 完成后 | 同上 |
| `LoadCurrentAsync` 重置 `ImageWidth/Height=null` 后 | 同上 |

```csharp
// Scale 变化时通知布局属性
partial void OnScaleChanged(double value)
{
    OnPropertyChanged(nameof(ScaledWidth));
    OnPropertyChanged(nameof(ScaledHeight));
}

// LoadCurrentAsync 中重置尺寸后
ImageWidth = null;
ImageHeight = null;
OnPropertyChanged(nameof(ScaledWidth));
OnPropertyChanged(nameof(ScaledHeight));

// RefreshImageDimensionsAsync 中设置尺寸后
ImageWidth = dim.Value.Width;
ImageHeight = dim.Value.Height;
OnPropertyChanged(nameof(ScaledWidth));
OnPropertyChanged(nameof(ScaledHeight));
```

---

## 5. LightboxWindow.axaml 改动

### 5.1 图片模式布局

**旧**（§4.3 原文）：

```xml
<Panel HorizontalAlignment="Center"
       VerticalAlignment="Center"
       Width="{Binding ImageWidth, FallbackValue=0}"
       Height="{Binding ImageHeight, FallbackValue=0}"
       MinWidth="100" MinHeight="100">
    <Image Stretch="Uniform"
           RenderTransformOrigin="0.5,0.5">
        <Image.RenderTransform>
            <ScaleTransform ScaleX="{Binding Scale}" ScaleY="{Binding Scale}" />
        </Image.RenderTransform>
    </Image>
</Panel>
```

**新**：

```xml
<Panel HorizontalAlignment="Center"
       VerticalAlignment="Center"
       Width="{Binding ScaledWidth, FallbackValue=0}"
       Height="{Binding ScaledHeight, FallbackValue=0}"
       MinWidth="100" MinHeight="100">
    <Image Source="{Binding CurrentFile, Converter={x:Static conv:LightboxConverters.MediaFileToOriginalBitmap}}"
           Stretch="Fill" />
</Panel>
```

### 5.2 改动说明

| 元素 | 旧 | 新 | 理由 |
|------|-----|-----|------|
| Panel.Width | `{Binding ImageWidth}` | `{Binding ScaledWidth}` | 绑定缩放后的尺寸 |
| Panel.Height | `{Binding ImageHeight}` | `{Binding ScaledHeight}` | 同上 |
| Image.Stretch | `Uniform` | `Fill` | Panel 已经是精确尺寸，不需要 Image 再计算比例 |
| Image.RenderTransform | `ScaleTransform` | 移除 | 缩放由 Panel 尺寸体现，不再需要 ScaleTransform |
| Image.RenderTransformOrigin | `0.5,0.5` | 移除 | 同上 |

---

## 6. 行为验证清单

| 场景 | 预期行为 |
|------|----------|
| 打开灯箱（Scale=1.0，图片 > 视口） | 图片原始尺寸显示，ScrollViewer 出现滚动条，可滚动查看 |
| 打开灯箱（Scale=1.0，图片 < 视口） | 图片居中显示 |
| 缩小到 25% | 图片缩小到 25%，居中显示，无黑边 |
| 放大到 200% | 图片放大，ScrollViewer 出现滚动条，可滚动查看 |
| 翻页后 | Scale 重置为 1.0，新图片居中 |
| 滚轮缩放 | 缩放围绕视口中心（ScrollViewer 自动处理） |
| 图片尺寸探测未完成 | ScaledWidth=0 → FallbackValue=0 → MinWidth=100 兜底，小方块居中 |
| 视频模式 | 不受影响（视频模式不绑 ImageWidth/Height，独立布局） |

---

## 7. 边界情况

| 场景 | 处理 |
|------|------|
| 图片尺寸探测失败（ImageWidth=null） | ScaledWidth=0，FallbackValue=0，MinWidth=100 兜底 |
| 极小 Scale（0.25） | Panel 为原图 25%，视口内居中 |
| 极大 Scale（5.0） | Panel 为原图 500%，ScrollViewer 滚动 |
| 浮点精度 | Scale 用 `Math.Round(Scale, 2)` 控制，ScaledWidth 不会溢出 |

---

## 8. 不影响现有功能

- 键盘快捷键（缩放/翻页/关闭）不变
- 视频模式（独立 Panel，不绑 ImageWidth/Height）不变
- 右侧信息面板不变
- 底部控制栏不变
- `ImageDimensionProbe` 不变
- `LightboxConverters` 不变