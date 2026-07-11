# 0.11 — Upload Service（上传服务）UI & 交互改进

> 对应需求量文档 `doc/demand/04-upload-improve.md`。
> 核心改动：LAN 上传（上传历史日志、复制链接、端口冲突处理、多 IP 显示）、WAN 公网中转（分步卡片、表单防抖、向导按钮组、多行日志、彩色状态指示器）、通用（upload.html 触控优化）。

---

## 1. 模块边界

```
UploadServerView.axaml
  ├─ LAN 上传 Card
  │    ├─ 端口配置 + "使用随机端口"按钮
  │    ├─ 启动/停止按钮
  │    ├─ 状态消息（端口冲突时 State.Danger 颜色）
  │    ├─ 上传历史 ScrollViewer（ObservableCollection<UploadHistoryEntry>）
  │    ├─ URL 文本框 + 复制按钮（短暂 "已复制" 反馈）
  │    └─ 多 IP 地址列表（每个带复制按钮）
  ├─ 右侧 QR 区域（已有，不变）
  └─ 公网代理 Expander
       ├─ 步骤卡片（VPS 连接 / 服务配置）
       ├─ 认证切换区域固定高度（MinHeight 防抖）
       ├─ 向导按钮组（保存→部署→启动 线性启用）
       ├─ 多行日志 TextBox（最近 100 行 + 清空按钮）
       └─ 彩色状态指示器（绿/黄/红/灰圆点）

依赖链：
  UploadServerViewModel
    ├─ GalleryViewModel（项目路径）
    ├─ UploadServerService（HTTP 服务器）
    └─ PublicRelayViewModel（公网代理）

  PublicRelayViewModel
    ├─ ConfigService（读写 PublicRelayConfig）
    ├─ PublicRelayService（SSH + TCP client）
    └─ IFilePickerService（浏览 SSH Key）

外部文件：
  Resources/upload.html（PWA 触控优化）
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Resources/upload.html` | 触控目标 ≥ 44px | 修改 |

| 文件 | 改动 |
|------|------|
| `ViewModels/UploadServerViewModel.cs` | 上传历史集合、复制链接、端口冲突检测、多 IP 列表、GetPreferredPort(检测空闲) |
| `Views/UploadServerView.axaml` | 上传历史 ScrollViewer、复制按钮、端口冲突 Danger 颜色、建议端口列表、"使用随机端口"按钮、多 IP 地址面板 |
| `ViewModels/PublicRelayViewModel.cs` | 分步属性（Step1Done/Step2Done）、多行日志集合、按钮启用状态链、彩色状态指示器属性 |
| `Views/UploadServerView.axaml`（Expander 内部） | 拆分为两个 GroupBox 步骤卡片、认证切换区 MinHeight 固定、线性按钮组、多行日志 TextBox、状态圆点 |

> **不引入新 NuGet 包。**

---

## 3. LAN 上传改进

### 3.1 上传历史日志

**ViewModel**：

```csharp
// 上传历史条目
public partial class UploadHistoryEntry
{
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsSuccess { get; set; }
}

[ObservableProperty]
private ObservableCollection<UploadHistoryEntry> _uploadHistory = new();

// 在 OnUploadSuccess 回调中追加：
UploadHistory.Add(new UploadHistoryEntry
{
    FileName = Path.GetFileName(path),
    FileSize = new FileInfo(path).Length,
    Timestamp = DateTime.Now,
    IsSuccess = true
});
```

**XAML**：QR 码下方替换单行 `RecentUploadMessage` 为 ScrollViewer + ItemsControl：

```xml
<ScrollViewer MaxHeight="200" IsVisible="{Binding UploadHistory, Converter={x:Static IsNotEmpty}}">
  <ItemsControl ItemsSource="{Binding UploadHistory}">
    <ItemsControl.ItemTemplate>
      <DataTemplate>
        <StackPanel Orientation="Horizontal" Spacing="8">
          <TextBlock Text="{Binding FileName}" FontSize="11" />
          <TextBlock Text="{Binding FileSize, StringFormat={}{0:N0} B}" FontSize="11" Foreground="Text.Secondary" />
          <TextBlock Text="✓" FontSize="11" Foreground="State.Success"
                     IsVisible="{Binding IsSuccess}" />
        </StackPanel>
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>
</ScrollViewer>
```

### 3.2 复制链接按钮

URL `TextBlock` 改为可读 `TextBox` + 复制按钮：

```xml
<Grid ColumnDefinitions="*,Auto">
  <TextBox Grid.Column="0" Text="{Binding UploadUrl}" IsReadOnly="True"
           FontSize="12" TextWrapping="Wrap" />
  <Button Grid.Column="1" Content="📋"
          Command="{Binding CopyUrlCommand}" Margin="4,0,0,0" />
</Grid>
```

```csharp
[RelayCommand]
private async Task CopyUrl()
{
    if (string.IsNullOrEmpty(UploadUrl)) return;
    await Clipboard.SetTextAsync(UploadUrl);
    CopyButtonText = "已复制";
    await Task.Delay(1500);
    CopyButtonText = "📋";
}
```

### 3.3 端口冲突处理

**冲突检测**：`StartServer` 失败时若异常包含 "address already in use" / `HttpListenerException` → 错误消息变色 + 自动检测 3 个空闲端口作为建议。

```csharp
// UploadServerViewModel 新增
[ObservableProperty] private bool _isPortConflict;
[ObservableProperty] private List<int> _suggestedPorts = new();

// 检测空闲端口
private static List<int> FindFreePorts(int count)
{
    var ports = new List<int>();
    for (int p = 8765; p <= 65535 && ports.Count < count; p++)
    {
        try { using var s = new TcpListener(IPAddress.Loopback, p); s.Start(); s.Stop(); ports.Add(p); }
        catch { /* 被占用 */ }
    }
    return ports;
}

[RelayCommand]
private void UseRandomPort()
{
    var free = FindFreePorts(1);
    if (free.Count > 0) Port = free[0];
}
```

**XAML** 端口冲突时：

```xml
<!-- 端口冲突时的建议 -->
<ItemsControl ItemsSource="{Binding SuggestedPorts}"
              IsVisible="{Binding IsPortConflict}">
  <!-- 每个建议端口按钮 -->
</ItemsControl>
<Button Content="使用随机端口" Command="{Binding UseRandomPortCommand}"
        IsVisible="{Binding IsPortConflict}" />
```

### 3.4 多 IP 地址显示

**ViewModel**：

```csharp
[ObservableProperty] private List<string> _localAddresses = new();

// StartServer 成功后：
LocalAddresses = Dns.GetHostEntry(Dns.GetHostName())
    .AddressList
    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
    .Select(ip => ip.ToString())
    .ToList();
```

**XAML**：

```xml
<ItemsControl ItemsSource="{Binding LocalAddresses}">
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <StackPanel Orientation="Horizontal" Spacing="4">
        <TextBlock Text="{Binding .}" FontSize="12" />
        <Button Content="📋" FontSize="10" Command="{Binding $parent[UserControl].DataContext.CopyAddressCommand}"
                CommandParameter="{Binding .}" />
      </StackPanel>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
```

---

## 4. WAN 公网中转改进

### 4.1 分步卡片（取代 Expander 内平铺表单）

```xml
<!-- 第一步：VPS 连接 -->
<Border Background="Bg.Surface" CornerRadius="8" Padding="16" Margin="0,8,0,0">
  <StackPanel Spacing="10">
    <TextBlock Text="第一步：VPS 连接" FontSize="14" FontWeight="SemiBold" />
    <Grid ColumnDefinitions="100,*"><!-- SSH Host --></Grid>
    <Grid ColumnDefinitions="100,*"><!-- SSH Port --></Grid>
    <Grid ColumnDefinitions="100,*"><!-- User --></Grid>
    <!-- 认证方式 ComboBox -->
    <!-- Password / Key 区域（MinHeight 固定） -->
    <Grid ColumnDefinitions="100,*"><!-- Remote Path --></Grid>
    <Button Content="保存" Command="SaveCommand" Classes="primary-button"
            IsEnabled="{Binding IsDirty}" />
    <TextBlock Text="已保存 ✓" IsVisible="{Binding Step1Done}"
               Foreground="{DynamicResource State.Success}" />
  </StackPanel>
</Border>

<!-- 第二步：服务配置 -->
<Border Background="Bg.Surface" CornerRadius="8" Padding="16" Margin="0,8,0,0"
        IsVisible="{Binding Step1Done}">
  <StackPanel Spacing="10">
    <TextBlock Text="第二步：服务配置" FontSize="14" FontWeight="SemiBold" />
    <Grid ColumnDefinitions="100,*"><!-- HTTP Port --></Grid>
    <Grid ColumnDefinitions="100,*"><!-- TCP Port --></Grid>
    <Grid ColumnDefinitions="100,*"><!-- Public Host --></Grid>
    <!-- 线性按钮组 -->
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Content="部署" Classes="secondary-button" Command="DeployCommand"
              IsEnabled="{Binding CanDeploy}" />
      <Button Content="启动" Classes="primary-button" Command="StartCommand"
              IsEnabled="{Binding CanStart}" />
      <Button Content="停止" Classes="primary-button" Command="StopCommand"
              IsVisible="{Binding IsPublicRelayRunning}" />
    </StackPanel>
  </StackPanel>
</Border>
```

**ViewModel 新增属性**：

```csharp
[ObservableProperty] private bool _step1Done;   // Save 成功后 true
[ObservableProperty] private bool _canDeploy;    // Step1Done && !IsBusy
[ObservableProperty] private bool _canStart;     // step2 部署成功后 true
```

### 4.2 认证切换防抖

Password 和 Key 两个区域的容器 `MinHeight="60"`，切换时布局不抖动：

```xml
<Border MinHeight="60" IsVisible="{Binding ShowPasswordFields}">
  <!-- Password TextBox -->
</Border>
<Border MinHeight="60" IsVisible="{Binding ShowKeyFields}">
  <!-- Key Path TextBox + Browse -->
</Border>
```

### 4.3 多行日志 + 清空

```xml
<TextBox Text="{Binding LogText}" IsReadOnly="True" TextWrapping="Wrap"
         AcceptsReturn="True" MinHeight="80" MaxHeight="200"
         VerticalScrollBarVisibility="Auto"
         FontFamily="Cascadia Code,Consolas,monospace" FontSize="11" />
<Button Content="清空日志" Command="{Binding ClearLogCommand}" HorizontalAlignment="Right" />
```

**ViewModel**：

```csharp
[ObservableProperty] private string _logText = "";

private void AppendLog(string msg)
{
    var ts = DateTime.Now.ToString("HH:mm:ss");
    LogText += $"[{ts}] {msg}\n";
    // 保留最近 100 行
    var lines = LogText.Split('\n');
    if (lines.Length > 100)
        LogText = string.Join('\n', lines[^100..]);
}
```

### 4.4 彩色状态指示器

```xml
<!-- 圆点 + 文字 -->
<StackPanel Orientation="Horizontal" Spacing="6" VerticalAlignment="Center">
  <Ellipse Width="10" Height="10" Fill="{Binding RelayStateColor}" />
  <TextBlock Text="{Binding RelayStateText}" FontSize="13" />
</StackPanel>
```

**ViewModel**：

```csharp
// 根据 RelayService.State 返回对应颜色
public IBrush RelayStateColor => _relayService.State switch
{
    RelayState.Running => Brushes.Green,
    RelayState.Deploying or RelayState.Starting or RelayState.Stopping => Brushes.Orange,
    RelayState.Error => Brushes.Red,
    _ => Brushes.Gray,
};
```

> `RelayState` 枚举已存在于 `PublicRelayService`，直接复用。

---

## 5. upload.html PWA 触控优化

`upload.html` 已有 `viewport meta` 和 `max-width: 480px` 响应式布局，但需强化触控目标：

```css
/* 追加：所有按钮最小 44×44px */
button, .drop-zone { min-height: 44px; }
.drop-zone { padding: 24px 16px; }  /* 增大触控区域 */
.file-item .remove { min-width: 44px; min-height: 44px; }  /* × 删除按钮 */
```

> `upload.html` 已有拖放区域、进度条、百分比，不需再添加。

---

## 6. 边界情况

| 场景 | 处理 |
|------|------|
| 上传历史为空 | ItemsControl 隐藏，无 "暂无上传记录" 占位（简洁优先） |
| 启动服务器后切项目目录 | 停止服务器（`_gallery.ProjectPath` 变化时检测并自动执行 `StopServer`） |
| 端口冲突 + 所有端口都被占用 | `SuggestedPorts` 为空 → 显示 "未找到可用端口" |
| 多网卡只有 loopback | `LocalAddresses` 只包含 127.0.0.1，正常显示 |
| 复制链接时 URL 为空 | 按钮 IsVisible 绑定 `UploadUrl` 非空 |
| 公网 Expander 折叠状态下状态变化 | `RelayStateColor` 通过 property change 通知更新（不影响折叠态） |
| 清空日志后重新部署 | `LogText` 重新从 `AppendLog` 积累 |
| upload.html 离线使用 | 不引入 ServiceWorker / PWA manifest（超出本次范围） |

---

## 7. 与现有系统的关系

### 7.1 不影响 OSS / AI / Gallery

仅改动 `UploadServerView.axaml`、`UploadServerViewModel`、`PublicRelayViewModel`、`upload.html`，不触碰其他模块。

### 7.2 PublicRelayService 不改动

`PublicRelayService` 的 `RelayState` 枚举、`StateChanged` 事件、`PendingCountChanged` 事件已在现有代码中存在，ViewModel 仅新增 UI 层属性和绑定，不修改 Service 层逻辑。

### 7.3 upload.html 不改动 JavaScript

仅修改 CSS 触控尺寸，不改 JS 逻辑。拖放、进度条、上传流程均不变。

### 7.4 RecentUploadMessage 保留

`RecentUploadMessage` 属性保留不删，避免其他引用处编译报错。`UploadHistory` 作为新增集合提供滚动历史，两者共存。
