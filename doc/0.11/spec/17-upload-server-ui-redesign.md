# 0.11 — UploadServerView UI 重设计（LAN / QR 双卡片）

> 基于新版设计稿，将 LAN 上传与 QR 上传从「单 card 左右混排」重构为「左右独立卡片」布局。所有颜色、间距、圆角必须复用现有设计 tokens，禁止引入新的硬编码色值。
>
> 对应需求：`doc/0.11/demand/04-upload-improve.md`。本次 spec 在已有 `spec/03-upload-improve.md` 功能基础上只做视觉层重构，不删除已有行为。

---

## 1. 总体布局

```
ScrollViewer
└── StackPanel (Margin="40,24" Spacing="24")
    ├── Grid (ColumnDefinitions="* *" 间距 24)
    │   ├── Border — LAN 上传卡片
    │   └── Border — 扫码上传卡片
    └── Expander — 公网代理设置（保持现有实现，视觉不动）
```

- 两个顶部卡片等宽，水平间距 `24`（`Space.5`）。
- 窗口宽度不足时改为垂直堆叠（可在外层 Grid 上设置 `MinWidth` 或在 Avalonia 响应式方案中处理；本 spec 以桌面端常见宽度为准）。
- 公网代理 `Expander` 仍放在双卡片下方，内容、交互、ViewModel 不变。

---

## 2. 卡片通用视觉规范

| 属性 | Token | 说明 |
|------|-------|------|
| Background | `Bg.Surface` | `#161B2E` |
| CornerRadius | `Radius.Large` | `8` |
| Padding | — | `24` |
| 内部主间距 | `Space.4` / `Space.5` | `16` / `24` |
| 标题字号 | — | `16` `SemiBold` |
| 副标题/描述 | `Text.Secondary` | `12` |
| 正文/数值 | `Text.Primary` | `13` / `20` |
| 脚注 | `Text.Tertiary` | `11` |

禁止在卡片背景使用设计稿中的紫色氛围渐变；Avalonia 端以纯色 `Bg.Surface` 呈现，保持与应用其他卡片一致。

---

## 3. 状态徽章（StatusBadge）

两个卡片右上角各有一个胶囊状状态徽章，需新增可复用样式（或直接在 `UploadServerView.axaml` 内用 `UserControl.Resources` 定义）。

```xml
<Border Background="{DynamicResource Bg.SurfaceElevated}"
        CornerRadius="{DynamicResource Radius.Pill}"
        Padding="6,3"
        VerticalAlignment="Center">
    <StackPanel Orientation="Horizontal" Spacing="6">
        <Ellipse Width="6" Height="6"
                 VerticalAlignment="Center"
                 Fill="{DynamicResource State.Success}" />  <!-- 或 Accent.Stellar -->
        <TextBlock Text="Running"
                   FontSize="11"
                   FontWeight="SemiBold"
                   Foreground="{DynamicResource State.Success}" />  <!-- 或 Accent.Stellar -->
    </StackPanel>
</Border>
```

| 卡片 | 徽章文案 | 圆点/文字颜色 |
|------|----------|---------------|
| LAN 上传 | `Running` / `Stopped` | `State.Success` |
| 扫码上传 | `Live QR` / `QR Ready` | `Accent.Stellar` |

> 文案颜色与圆点同色，保持设计稿中「彩色文字 + 同色圆点」的语义。

---

## 4. 左侧：局域网上传卡片

### 4.1 头部

- 左侧图标：32×32 圆角容器，背景 `Bg.SurfaceElevated`，内嵌 `Icon.Upload`（或新增右箭头图标），图标色 `Accent.Stellar`。
- 主标题：「局域网上传」`Text.Primary` `16` `SemiBold`。
- 副标题：「LAN Transfer · 同一网络下点对点直传」`Text.Secondary` `12`。
- 右侧：`Running` 状态徽章。

### 4.2 说明文案

「在同一网络下打开链接，上传图片或视频。」
- 颜色 `Text.Secondary`，字号 `12`，`TextWrapping="Wrap"`。

### 4.3 端口与本机 IP

采用「标签在上、数值在下」的垂直布局（替代原横向 Grid）：

```xml
<StackPanel Orientation="Horizontal" Spacing="24">
    <StackPanel>
        <TextBlock Text="PORT" FontSize="11" Foreground="{DynamicResource Text.Tertiary}" />
        <NumericUpDown Value="{Binding Port}" ... />
    </StackPanel>
    <StackPanel>
        <TextBlock Text="本机 IP" FontSize="11" Foreground="{DynamicResource Text.Tertiary}" />
        <TextBlock Text="{Binding PreferredLocalAddress}" FontSize="20" ... />
    </StackPanel>
</StackPanel>
```

- `PORT` 标签使用 `Text.Tertiary` `11`，英文大写。
- 端口输入框：当前 `NumericUpDown` 样式，宽度 `100`，运行中禁用。
- 本机 IP：取 `LocalAddresses.FirstOrDefault()` 作为首选展示（`PreferredLocalAddress` 为 VM 新增计算属性，无地址时显示 `-`）。
- 数值字号 `20` `SemiBold` `Text.Primary`，但**不引入新 token**，直接在局部写死 `20`（字号非设计 token 范畴，可接受）。

### 4.4 操作按钮

- 「复制链接」：使用 `secondary-button`，图标 `Icon.Copy` + 文字，调用 `CopyUrlCommand`。
- 「停止服务」：左侧带 `6px` 红点的危险按钮。
  - 前景 `State.Danger`，背景 `Transparent`，边框 `Bg.Divider`。
  - Hover 边框/前景强化为 `State.Danger`。
  - 可用现有 `toolbar-button-danger` 扩展，或新增 `danger-button` 样式；禁止硬编码红色。

### 4.5 脚注

「修改端口后自动重启」
- `Text.Tertiary` `11`，居左或居中按设计稿。

### 4.6 上传历史（保留现有功能）

设计稿未画出历史列表，但 `spec/03-upload-improve.md` 已要求保留。处理方式：

- 在卡片底部、脚注上方增加可折叠区域「最近上传」。
- 仍用 `MaxHeight="200"` 的 `ScrollViewer` + `ItemsControl`，绑定 `UploadHistory`。
- 空时隐藏（`HasUploadHistory`）。
- 与「清除」按钮同行放置。

---

## 5. 右侧：扫码上传卡片

### 5.1 头部

- 主标题：「扫码上传」`Text.Primary` `16` `SemiBold`。
- 副标题：「QR · 扫码即可访问上传页」`Text.Secondary` `12`。
- 右侧：`Live QR` 状态徽章，颜色 `Accent.Stellar`。
- 标题右侧可增加一个 28×28 的网格/二维码装饰图标，颜色 `Text.Secondary`（可选，若设计稿有则使用现有图标库中的二维码图标；若无则省略）。

### 5.2 二维码

- `Image Source="{Binding QrCodeImage}"` `180×180`，水平居中。
- 未运行时显示占位：一个 180×180 的 `Border`，背景 `Bg.SurfaceElevated`，圆角 `Radius.Medium`，内部显示「启动服务后生成二维码」`Text.Secondary`。

### 5.3 扫码提示

- 「用手机扫码立即访问」`Text.Secondary` `12`，水平居中。

### 5.4 URL 与复制

```xml
<Grid ColumnDefinitions="*,Auto">
    <TextBox Grid.Column="0"
             Text="{Binding UploadUrl}"
             IsReadOnly="True"
             FontSize="12"
             MinWidth="220"
             ToolTip.Tip="{Binding UploadUrl}"
             ScrollViewer.HorizontalScrollBarVisibility="Hidden"
             ScrollViewer.VerticalScrollBarVisibility="Disabled" />
    <Button Grid.Column="1"
            Command="{Binding CopyUrlCommand}"
            Classes="secondary-button"
            Margin="8,0,0,0"
            MinWidth="64"
            ToolTip.Tip="复制 URL">
        <Panel>
            <PathIcon Data="{StaticResource Icon.Copy}" ...
                      IsVisible="..." />
            <TextBlock Text="已复制" IsVisible="..." />
        </Panel>
    </Button>
</Grid>
```

- 保留 v0.11「点击后显示已复制 1.5s」的反馈。
- URL 只读，hover/focus 边框仍走 `TextBox` 默认 `Accent.Stellar`。

### 5.5 多 IP 列表

- 位于 URL 下方，仅当 `HasLocalAddresses` 为真时显示。
- 标题「本机 IP：」`Text.Secondary` `11`，居中。
- 每行显示 IP + 复制图标按钮，保持现有实现。

### 5.6 脚注

「二维码永久有效，同一局域网下均可访问」
- `Text.Tertiary` `11`，可配 `Icon.Help` 或 `Icon.Bell` 小图标（若设计稿有）。

---

## 6. ViewModel 最小改动

现有 `UploadServerViewModel` 已具备所需数据，主要新增/调整如下：

```csharp
// 状态徽章文案
public string LanStatusText => IsRunning ? "Running" : "Stopped";
public string QrStatusText => IsRunning ? "Live QR" : "QR Ready";

// 左侧卡片首选 IP（取第一个 IPv4）
public string PreferredLocalAddress => LocalAddresses.FirstOrDefault() ?? "-";
```

并在 `OnIsRunningChanged`、`OnLocalAddressesChanged` 时触发对应 `PropertyChanged`。

`CopyUrlCommand`、`UseRandomPortCommand`、`StopServerCommand` 等全部复用，不改逻辑。

---

## 7. 设计 Token 对照表

| 设计稿元素 | 应使用的 Token | 备注 |
|------------|----------------|------|
| 页面背景 | `Bg.Outer` | Window 已全局设置 |
| 卡片背景 | `Bg.Surface` | — |
| 图标容器背景 | `Bg.SurfaceElevated` | — |
| 状态徽章背景 | `Bg.SurfaceElevated` | — |
| 主要文字 | `Text.Primary` | 标题、IP、端口 |
| 次要文字 | `Text.Secondary` | 副标题、描述、URL |
| 第三级文字 | `Text.Tertiary` | 标签、脚注 |
| Running 状态 | `State.Success` | 绿色 |
| Live QR 状态 | `Accent.Stellar` | 青色 |
| 停止按钮/危险 | `State.Danger` | 红色 |
| 图标默认 | `Text.Secondary` / `Accent.Stellar` | 按语义 |
| 卡片圆角 | `Radius.Large` | `8` |
| 徽章圆角 | `Radius.Pill` | 胶囊 |
| 间距 | `Space.4` / `Space.5` | 16 / 24 |

---

## 8. 需要新增/调整的样式

建议在 `Themes/Styles.axaml` 中新增以下可选样式，供本页使用，也可复用到后续 dashboard：

```xml
<!-- 危险描边按钮：停止服务 -->
<Style Selector="Button.danger-button">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderBrush" Value="{DynamicResource Bg.Divider}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Foreground" Value="{DynamicResource State.Danger}"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Padding" Value="14,8"/>
    <Setter Property="CornerRadius" Value="{DynamicResource Radius.Medium}"/>
    <Setter Property="Cursor" Value="Hand"/>
</Style>
<Style Selector="Button.danger-button:pointerover">
    <Setter Property="BorderBrush" Value="{DynamicResource State.Danger}"/>
    <Setter Property="Foreground" Value="{DynamicResource State.Danger}"/>
    <Setter Property="Background">
        <Setter.Value>
            <SolidColorBrush Color="{DynamicResource State.Danger}" Opacity="0.12"/>
        </Setter.Value>
    </Setter>
</Style>
```

> hover 背景使用 `State.Danger` 颜色叠加 `Opacity="0.12"`，禁止写死十六进制色值。

---

## 9. 边界与兼容性

| 场景 | 处理 |
|------|------|
| 服务未启动 | 左侧显示 `Stopped`，右侧显示 `QR Ready`，二维码区域显示占位 |
| 多网卡多 IP | 左侧只显示首选 IP，右侧保留完整 IP 列表 |
| 端口冲突 | 左侧卡片内显示 `State.Danger` 提示 + 建议端口按钮（保持现有行为） |
| 上传历史为空 | 隐藏历史区域，卡片保持紧凑 |
| 窗口宽度较小 | 双卡片自动换行（可在外层 Grid 设置 `MinWidth="720"`，或 Avalonia 自适应） |
| 公网模式 | 右侧二维码/URL 自动切换为公网地址，顶部徽章仍显示 `Live QR`（保持 `IsPublicMode` 逻辑） |

---

## 10. 不改动的部分

- `PublicRelayViewModel` 与公网代理 `Expander` 保持原样。
- `UploadServerService` 不改动。
- `upload.html` 不改动。
- 复制链接的 1.5s 反馈、上传历史 50 条限制、项目切换自动停服等逻辑不改动。

---

## 11. 验收 checklist

- [ ] LAN 卡片与 QR 卡片为左右等宽双卡片。
- [ ] 所有颜色均来自 `Colors.axaml`，无十六进制硬编码。
- [ ] 状态徽章圆角为胶囊，文字与圆点同色。
- [ ] 「停止服务」按钮使用 `State.Danger`。
- [ ] 本机 IP 与端口使用大写/小标签 + 大数值的垂直布局。
- [ ] QR 区域在未启动时显示占位而非空白。
- [ ] 复制链接反馈保留。
- [ ] 上传历史保留并可滚动。
- [ ] 公网代理 Expander 仍在下方且功能正常。
