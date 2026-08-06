# S03 Phase 4 — 设置页实现计划

> 关联：`spec/03-shooting-diary.md`（主规格）、`spec/03-shooting-diary-code-structure.md`（§6 设置页扩展）、`spec/03-shooting-diary-phase3-plan.md`（§3 EnvironmentService 配置键）

---

## 目标

把 Phase 3 的两个用户可配置项暴露到设置页：

1. **会话间隔阈值**（小时）— 控制聚类算法的切分粒度
2. **高德 API Key** — 控制逆地理编码服务优先级

无需新增设置 Tab，复用现有「通用」Tab，在「项目目录」等条目之后追加。

---

## Step 1: Services/AppConfig.cs 扩展

**文件：** `StartTooler/Services/AppConfig.cs`（修改）

### 1.1 新增字段

```csharp
/// <summary>
/// v0.12: 会话间隔阈值（小时）。SessionClusteringService 在两次拍摄间隔超过此时视为不同会话。
/// 范围 1-24，默认 4。
/// </summary>
public int SessionIntervalHours { get; set; } = 4;

/// <summary>
/// v0.12: 高德地图 API Key。空 = EnvironmentService 自动回退到 Nominatim（免费，1 req/s 限速）。
/// 用于逆地理编码（经纬度 → 地名）。
/// </summary>
public string? AmapApiKey { get; set; }
```

### 1.2 实现要点

- 字段使用 `{ get; set; }` 与现有保持一致
- 默认值与 Phase 3 Service 假设一致（4 小时）
- `AmapApiKey` 为空字符串还是 `null` 时 Service 应识别为"无 Key"——VM 保存前 Trim + 空字符串归一为 null

---

## Step 2: Services/ConfigKeys.cs 新增键

**文件：** `StartTooler/Services/ConfigKeys.cs`（修改或新建，取决于是否存在）

### 2.1 检查现状

先 grep `ConfigKeys.Diary` / `ConfigKeys.Project` 等键的命名模式。

### 2.2 新增键常量

```csharp
/// <summary>v0.12: 拍摄日记配置（嵌在 AppConfig 内，无需独立键）</summary>
// 注：会话间隔和高德 Key 都属于 AppConfig，不引入新顶层键。
```

### 2.3 实现要点

- AppConfig 已通过 `ConfigKeys.App` 持久化，新增字段会自动 JSON 序列化
- **无需新增 ConfigKeys 条目**——这与现有"AppConfig 收容所有应用级配置"的模式一致

---

## Step 3: ViewModels/SettingsViewModel.cs 扩展

**文件：** `StartTooler/ViewModels/SettingsViewModel.cs`（修改）

### 3.1 新增 ObservableProperty

```csharp
// === v0.12: 拍摄日记配置 ===
[ObservableProperty] private int _sessionIntervalHours = 4;
[ObservableProperty] private string? _amapApiKey;
```

### 3.2 新增私有快照字段

```csharp
// General Tab 快照（追加，不修改现有结构）
private int _lastSavedSessionIntervalHours;
private string? _lastSavedAmapApiKey;
```

### 3.3 InitializeAsync 加载逻辑

在现有 `appConfig = await _configService.GetAsync<AppConfig>(ConfigKeys.App);` 之后追加：

```csharp
SessionIntervalHours = appConfig.SessionIntervalHours;
LastSavedSessionIntervalHours = SessionIntervalHours;
AmapApiKey = appConfig.AmapApiKey;
LastSavedAmapApiKey = AmapApiKey;
```

### 3.4 HasChanges 检测

修改现有的 `HasChanges` 计算属性（或等价机制），加入两个新字段：

```csharp
// 假设现有结构
public bool HasChanges =>
    _lastSavedDirectory != SelectedProjectDirectory ||
    _lastSavedTheme != ThemeIndex ||
    // ... 现有字段 ...
    _lastSavedSessionIntervalHours != SessionIntervalHours ||
    _lastSavedAmapApiKey != AmapApiKey;
```

### 3.5 保存逻辑

修改现有的 SaveCommand / Save 方法，在写入 AppConfig 时携带新字段：

```csharp
var appConfig = new AppConfig
{
    Theme = ThemeIndex == 1 ? "RedNight" : "DeepSpace",
    FFmpegPath = FFmpegPath,
    FFprobePath = FFprobePath,
    SessionIntervalHours = SessionIntervalHours,
    AmapApiKey = string.IsNullOrWhiteSpace(AmapApiKey) ? null : AmapApiKey.Trim(),
};
await _configService.SetAsync(ConfigKeys.App, appConfig);

// 同步更新快照
_lastSavedSessionIntervalHours = SessionIntervalHours;
_lastSavedAmapApiKey = AmapApiKey;
```

### 3.6 校验逻辑

会话间隔必须 1-24 之间。在 setter 或 PropertyChanged 处理中加范围检查：

```csharp
partial void OnSessionIntervalHoursChanged(int value)
{
    if (value < 1) SessionIntervalHours = 1;
    if (value > 24) SessionIntervalHours = 24;
}
```

### 3.7 实现要点

- 与现有 `LastSavedOss` / `LastSavedAI` 快照模式一致
- 复用现有 ConfigService.GetAsync/SetAsync
- 不修改 SaveAsync 的整体流程

---

## Step 4: Views/SettingsView.axaml 新增表单区

**文件：** `StartTooler/Views/SettingsView.axaml`（修改）

### 4.1 定位

在「通用」Tab 的 Grid 中追加两行。现有结构：

```
Grid RowDefinitions="Auto,Auto,Auto,Auto,Auto,Auto,*"
```

需要把 Grid 改为：

```
Grid RowDefinitions="Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*"
```

并把 `*` 行的索引从 `6` 改为 `8`。

### 4.2 新增 UI 元素

#### 4.2.1 会话间隔（Row 6）

```xml
<Border Grid.Row="6" Padding="40,16" Background="{DynamicResource Bg.Surface}">
    <Grid ColumnDefinitions="160,*" Height="64">
        <TextBlock Grid.Column="0" Text="会话间隔阈值"
                   FontSize="13" Foreground="{DynamicResource Text.Primary}"
                   VerticalAlignment="Center"/>
        <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="12" VerticalAlignment="Center">
            <NumericUpDown Value="{Binding SessionIntervalHours, Mode=TwoWay}"
                            Minimum="1" Maximum="24"
                            MinWidth="120" Height="36"
                            Increment="1"
                            FormatString="0 小时"/>
            <TextBlock Text="同一会话内相邻照片的最大间隔"
                       FontSize="12"
                       Foreground="{DynamicResource Text.Secondary}"
                       VerticalAlignment="Center"/>
        </StackPanel>
    </Grid>
</Border>
```

#### 4.2.2 高德 API Key（Row 7）

```xml
<Border Grid.Row="7" Padding="40,16" Background="{DynamicResource Bg.Surface}">
    <Grid ColumnDefinitions="160,*" Height="64">
        <TextBlock Grid.Column="0" Text="高德 API Key"
                   FontSize="13" Foreground="{DynamicResource Text.Primary}"
                   VerticalAlignment="Center"/>
        <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="12" VerticalAlignment="Center">
            <TextBox Text="{Binding AmapApiKey, Mode=TwoWay}"
                     MinWidth="280" Height="36"
                     Watermark="留空则使用 Nominatim（免费，1 req/s 限速）"
                     Classes.placeholder="{Binding AmapApiKey, Converter={StaticResource NullOrEmptyToPlaceholderClass}}"
                     PasswordChar="•">
                <TextBox.Styles>
                    <Style Selector="TextBox.placeholder">
                        <Setter Property="Foreground" Value="{DynamicResource Text.Tertiary}"/>
                    </Style>
                </TextBox.Styles>
            </TextBox>
            <TextBlock Text="（可选）用于日记页逆地理编码"
                       FontSize="12"
                       Foreground="{DynamicResource Text.Secondary}"
                       VerticalAlignment="Center"/>
        </StackPanel>
    </Grid>
</Border>
```

### 4.3 占位行（Row 8，继承现有 * 逻辑）

原有的 `*` 行保持不变，仅行号从 6 改为 8。

### 4.4 实现要点

- 复用现有 `FormRow` 模式（`160,*` ColumnDefinitions + `64` Height）
- 会话间隔用 `NumericUpDown` 而非 `TextBox`：范围限制在 XAML 即可生效
- API Key 沿用 OSS/AI Tab 现有的 placeholder 样式
- 不引入新的 converter——复用 `NullOrEmptyToPlaceholderClass`

---

## Step 5: 帮助提示与 tooltip

### 5.1 ToolTip 文本（可选）

在两个 TextBlock 上加 ToolTip：

| 字段 | ToolTip |
|---|---|
| 会话间隔 | "同一会话内相邻照片的最大间隔。超出则视为新会话。范围 1-24 小时。" |
| 高德 API Key | "用于日记页逆地理编码。空 = Nominatim 回退。https://lbs.amap.com/dev/key" |

### 5.2 实现要点

- ToolTip 文本放在 `.ToolTip.Tip` 属性
- 不超过 60 字，简短说明

---

## Step 6: 关于 Tab 追加"日记"信息（可选）

**文件：** `StartTooler/Views/SettingsView.axaml`（修改）

### 6.1 内容

在「关于」Tab 末尾追加：

```xml
<TextBlock Text="日记功能" FontSize="13" FontWeight="SemiBold"
           Foreground="{DynamicResource Text.Primary}" Margin="0,16,0,4"/>
<TextBlock TextWrapping="Wrap" FontSize="12"
           Foreground="{DynamicResource Text.Secondary}">
    拍摄日记本以"一次完整的外出拍摄"为粒度，会话间隔阈值可在「通用」Tab 设置。
    天气数据来自 Open-Meteo Archive API（免费，无需 Key）。
    地点反查优先使用高德 API，可在「通用」Tab 配置 Key。
</TextBlock>
```

### 6.2 实现要点

- 仅作为功能说明，不影响设置保存
- 与现有 About Tab 风格保持一致

---

## Step 7: 编译验证

### 7.1 验证清单

- [ ] `dotnet build` 0 错误 0 警告
- [ ] AppConfig.SessionIntervalHours 默认值 = 4
- [ ] AppConfig.AmapApiKey 反序列化兼容老数据（缺字段 → null）
- [ ] 设置保存后能正确读取两个字段
- [ ] 会话间隔超出范围时被钳制
- [ ] HasChanges 正确反映两个新字段的修改

### 7.2 编译命令

```bash
dotnet build StartTooler/StartTooler.csproj
```

---

## Step 8: 手动测试脚本

### 8.1 数据迁移验证

1. 启动应用，打开设置
2. 观察「会话间隔」是否显示默认值 `4`
3. 观察「高德 API Key」是否为空
4. 修改会话间隔为 `6`，点击保存
5. 重启应用，检查是否持久化为 `6`

### 8.2 兼容性验证

1. 在老版本配置基础上升级（手动往 config.db 写一条旧 AppConfig JSON 不含新字段）
2. 启动应用，验证新字段读取为默认值而非抛异常

---

## 文件变更清单

| 操作 | 文件 | 行数(估) |
|------|------|----------|
| 修改 | `Services/AppConfig.cs` | +8 |
| 修改 | `ViewModels/SettingsViewModel.cs` | +25（2 属性 + 2 快照 + 加载/保存） |
| 修改 | `Views/SettingsView.axaml` | +60（2 个 FormRow + 网格行调整） |

总计约 +90 行，无新建文件。

---

## 依赖关系

```
SettingsViewModel ──→ AppConfig (扩展字段)
                    ──→ ConfigService (现有)
AppConfig ──→ System.Text.Json (现有，JSON 序列化自动)
```

无 ViewModel / Service 依赖，本 Phase 后可独立测试。

---

## 关键设计决策

1. **配置归口 AppConfig 而非新建 DiaryConfig**
   - 减少配置层级
   - 与现有 ffmpeg/ffprobe 路径同位（应用级而非项目级）
   - 老数据迁移零成本（新字段为 null/默认值）

2. **会话间隔用 NumericUpDown**
   - XAML 强制范围 1-24
   - VM 二次校验兜底
   - 比 TextBox + Regex 校验更直观

3. **API Key 输入框带 placeholder 但不强制加密**
   - 与 OSS/AI Tab 的现有风格保持一致
   - 应用不重启前不持久化到 DB（依赖现有 SaveCommand 流程）

4. **不动 ConfigKeys**
   - AppConfig 字段是 JSON 序列化整体读写
   - 不需要为新字段新增顶层 key

5. **关于 Tab 追加简述**
   - 帮助用户了解日记数据来源
   - 不引入新控件复杂度