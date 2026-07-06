# 12 — AI 工具栏按钮（开始AI / 评分）

> 对应代码：`Views/MainWindow.axaml`、`ViewModels/GalleryViewModel.cs`、`Data/MediaFile.cs`、`Data/MediaRepository.cs`、`Models/Models.cs`、`Services/AITagger.cs`。
>
> 关联文档：`05-gallery-view.md`（Gallery 视图）、`11-ai-tagging.md`（AI 打标完整 spec）、`06-settings.md`（AI 配置）、`09-ui-commons.md`（UI 铁律）。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v1.1**（v0.6.1 实施期补丁：补 GetByTagAsync SortMode + TagGroupItem class + OnSortModeChanged Tag 分支 + Phase 7 左栏 TabControl） |
| 目标用户 | 天文摄影爱好者 |
| 实施版本 | StartTooler v0.6.1 |
| 问题描述 | GalleryView 工具栏缺少「开始AI」和「评分」按钮；左栏缺「标签」分类 tab；tag 视图排序联动缺失 |
| 文档状态 | **plan（v0.6.1 patch）** — 主 spec 见 `doc/11-ai-tagging.md` §5/§6/§12 |

---

## 1. 需求

### 1.1 按钮定义

| 按钮 | 功能 | 显示条件 | 交互 |
|---|---|---|---|
| **开始AI** | 对已选文件批量 AI 打标（图片 vision + 视频抽帧） | 多选模式 + 至少选中 1 个文件 + 非打标中 | 点击 → 串行调用 AI → 更新 tags/score → toast 汇总 |
| **评分** | 排序方式切换：按时间↓ 或 按评分↓ | 非多选模式（Gallery 常规视图） | 下拉选择 → 当前日期文件重新排序 |

### 1.2 用户故事

| ID | 故事 | 验收点 |
|---|---|---|
| US-1 | 多选 N 张照片后点「开始AI」，一键打标 | 进度条跑完 → 卡片显示评分角标和标签小条 |
| US-2 | 按评分排序，找出最好的片子 | 工具栏排序切到「评分↓」→ 分数高的排前面，未打标排最后 |
| US-3 | AI 未配置时有友好提示 | 点「开始AI」→ toast "AI 未配置，请在设置页填写 API Key" |

---

## 2. 当前状态 vs 目标

### 2.1 已就绪（不需要改动）

| 组件 | 文件 | 说明 |
|---|---|---|
| AI 打标服务 | `Services/AITagger.cs` (555行) | `IAITagger.TagFileAsync` 已实现，支持 OpenAI / Anthropic 双协议 |
| AI 配置模型 | `Services/AIConfig.cs` | `ApiKey` / `Model` / `Provider` / `Protocol` 字段完整 |
| AI 设置页 | `Views/SettingsView.axaml` + `SettingsViewModel.cs` | Provider / Model / ApiKey 可配置 |
| 工具栏按钮样式 | `Themes/Styles.axaml` | `.toolbar-button` / `.toolbar-button-danger` / `.refresh-button` 已定义 |
| 多选模式 | `GalleryViewModel.cs` | `IsMultiSelectMode` / `SelectedFiles` / `IsBatchActionEnabled` 已实现 |

### 2.2 需新建 / 修改

| 文件 | 状态 | 改动量 |
|---|---|---|
| `Data/MediaFile.cs` | **需修改** | 加 4 个属性：Tags、Score、TaggedAt、TagError |
| `Data/IMediaRepository.cs` | **需修改** | 加 3 个方法签名 |
| `Data/MediaRepository.cs` | **需修改** | DB migration (4列+2索引) + 3 个方法实现 |
| `Models/Models.cs` | **需修改** | 加 GroupMode / SortMode 枚举 |
| `ViewModels/GalleryViewModel.cs` | **需修改** | 加 ~300 行：属性、BatchTag/CancelTag/TagSingle 命令、排序联动 |
| `Views/MainWindow.axaml` | **需修改** | 工具栏加 2 个按钮 + 排序 ComboBox + 进度文本 |
| `Views/GalleryView.axaml` | **需修改** | photo tile 加评分角标 + 标签条 + 右键菜单 "AI 打标" |
| `Converters/` | **需新建** | 3 个 Converter：ScoreToBrush、ScoreToDisplay、TagsToShortText |
| `Themes/Icons.axaml` | **可选** | 加 AI 图标 SVG（Sparkles / Wand） |

---

## 3. 实施方案

### 3.1 Phase 1: 数据层

#### 3.1.1 `MediaFile.cs` 新增字段

```csharp
// Data/MediaFile.cs

// === v0.6 AI 打标字段 ===

/// <summary>AI 标签列表（JSON 数组，DB 存 TEXT）</summary>
public List<string> Tags { get; set; } = new();

/// <summary>AI 评分 0-100，null 表示未打标</summary>
public int? Score { get; set; }

/// <summary>最近一次打标时间（unix ms），null 表示未打标</summary>
public long? TaggedAt { get; set; }

/// <summary>打标失败原因：有值 = 显示红色徽章 + hover tooltip</summary>
[ObservableProperty]
private string? _tagError;

// UI 便捷属性（用于 XAML 绑定）
public bool HasScore => Score.HasValue;
public bool HasTags => Tags is { Count: > 0 };
```

#### 3.1.2 `IMediaRepository.cs` 新增接口

```csharp
// 保存 AI 打标结果
Task UpdateTagAsync(long fileId, IEnumerable<string> tags, int score,
    long taggedAt, string? tagError, CancellationToken ct);

// 获取标签分组（标签名 → 文件数）
Task<List<(string Tag, int Count)>> GetTagGroupsAsync(string projectPath, CancellationToken ct);

// 按标签筛选文件（v0.6.1: 加 SortMode 参数，切评分↓在 tag 视图也生效）
Task<List<MediaFile>> GetByTagAsync(string projectPath, string tag, SortMode sortMode, CancellationToken ct);
```

#### 3.1.3 `MediaRepository.cs` DB Migration

在 `EnsureDatabase` 中幂等添加 4 列：

```sql
ALTER TABLE media_files ADD COLUMN tags TEXT NOT NULL DEFAULT '[]';
ALTER TABLE media_files ADD COLUMN score INTEGER;
ALTER TABLE media_files ADD COLUMN tagged_at INTEGER;
ALTER TABLE media_files ADD COLUMN tag_error TEXT;
```

加 2 个索引：

```sql
CREATE INDEX IF NOT EXISTS idx_media_files_score ON media_files(score);
CREATE INDEX IF NOT EXISTS idx_media_files_tags ON media_files(tags);
```

`GetByDateAsync` SELECT 需扩展读取这 4 列。排序逻辑：
- 时间↓：`ORDER BY shot_at DESC, file_name ASC`
- 评分↓：`ORDER BY score IS NULL, score DESC, shot_at DESC, file_name ASC`

---

### 3.2 Phase 2: Models 枚举

```csharp
// Models/Models.cs

/// <summary>Gallery 左栏分类模式</summary>
public enum GroupMode
{
    Date,   // 按日期分组（现有行为）
    Tag,    // 按标签分组（v0.6 新增）
}

/// <summary>文件排序方式</summary>
public enum SortMode
{
    TimeDesc,   // 时间倒序（现有行为）
    ScoreDesc,  // 评分降序（v0.6 新增）
}
```

---

### 3.3 Phase 3: GalleryViewModel

#### 3.3.1 新增属性

```csharp
// === v0.6 AI 打标状态 ===

[ObservableProperty] private bool _isTagging;
[ObservableProperty] private int _tagCompletedCount;
[ObservableProperty] private int _tagTotalCount;

public string TagProgressText => IsTagging && TagTotalCount > 0
    ? $"打标 {TagCompletedCount}/{TagTotalCount}"
    : string.Empty;

// === v0.6 分类与排序 ===

[ObservableProperty] private GroupMode _groupMode = GroupMode.Date;
[ObservableProperty] private SortMode _sortMode = SortMode.TimeDesc;

public int SortModeIndex
{
    get => SortMode == SortMode.TimeDesc ? 0 : 1;
    set => SortMode = value == 0 ? SortMode.TimeDesc : SortMode.ScoreDesc;
}

// TagGroups 用于左栏"标签"tab（v0.6.1: 改为 TagGroupItem class，XAML x:DataType 更顺）
public ObservableCollection<TagGroupItem> TagGroups { get; } = new();

// Models/Models.cs 新增
public sealed class TagGroupItem
{
    public string Tag { get; init; } = "";
    public int Count { get; init; }
}
```

#### 3.3.2 属性联动

```csharp
partial void OnIsTaggingChanged(bool value)
{
    OnPropertyChanged(nameof(TagProgressText));
    OnPropertyChanged(nameof(IsBatchActionEnabled));
    SelectAllCommand.NotifyCanExecuteChanged();
    InvertSelectionCommand.NotifyCanExecuteChanged();
}

partial void OnTagCompletedCountChanged(int value) => OnPropertyChanged(nameof(TagProgressText));
partial void OnTagTotalCountChanged(int value) => OnPropertyChanged(nameof(TagProgressText));

partial void OnSortModeChanged(SortMode value)
{
    // v0.6.1: 按 GroupMode 分支重载当前视图
    _ = GroupMode switch
    {
        GroupMode.Date when SelectedDate != null => SelectAsync(SelectedDate),
        GroupMode.Tag when SelectedTag != null   => SelectTagAsync(TagGroups.FirstOrDefault(g => g.Tag == SelectedTag)),
        _ => Task.CompletedTask,
    };
}
```

#### 3.3.3 修改现有约束

```csharp
// IsBatchActionEnabled: 打标中不可用
public bool IsBatchActionEnabled => IsMultiSelectMode
    && SelectedFiles.Count > 0
    && !IsUploading
    && !IsTagging;   // ← v0.6 新增
```

#### 3.3.4 新增命令

**BatchTag — "开始AI" 按钮核心逻辑：**

```csharp
[RelayCommand]
private async Task BatchTag()
{
    if (IsTagging || !IsBatchActionEnabled) return;

    // 1. AI 配置检查
    var aiCfg = await _configService.GetAsync<AIConfig>(ConfigKeys.AI);
    if (aiCfg == null || string.IsNullOrWhiteSpace(aiCfg.ApiKey))
    {
        ShowToast("AI 未配置，请在设置页填写 API Key");
        return;
    }

    // 2. 过滤本地不存在的文件
    var files = SelectedFiles.Where(f => f.LocalExists).ToList();
    var skipped = SelectedFiles.Count - files.Count;
    ExitMultiSelect();

    if (files.Count == 0)
    {
        ShowToast(skipped > 0 ? "所选文件本地不存在，无法打标" : "未选中文件");
        return;
    }

    // 3. 开始打标
    IsTagging = true;
    TagTotalCount = files.Count;
    TagCompletedCount = 0;
    _tagCts = new CancellationTokenSource();
    var ct = _tagCts.Token;
    var errors = new List<(string Name, string Reason)>();

    ShowToast($"开始打标 {files.Count} 个文件…");

    foreach (var file in files)
    {
        if (ct.IsCancellationRequested) break;
        try
        {
            var (result, failure) = await _aiTagger.TagFileAsync(file, aiCfg, ct);
            if (failure != null)
            {
                if (failure.IsFatal) { ShowToast($"AI 错误：{failure.Reason}"); break; }
                file.TagError = Truncate(failure.Reason);
                errors.Add((file.FileName, failure.Reason));
            }
            else if (result != null)
            {
                file.Tags = result.Tags.ToList();
                file.Score = result.Score;
                file.TagError = null;
                await _mediaRepo.UpdateTagAsync(file.Id, result.Tags, result.Score,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, ct);
            }
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            file.TagError = Truncate(ex.Message);
            errors.Add((file.FileName, ex.Message));
        }
        TagCompletedCount++;
        await Task.Delay(200, ct);  // 节流
    }

    IsTagging = false;

    var ok = TagCompletedCount - errors.Count;
    ShowToast(errors.Count == 0
        ? $"打标完成：{ok} 个文件"
        : $"打标完成：成功 {ok}，失败 {errors.Count}");

    if (errors.Count > 0)
    {
        var window = DialogHelper.GetMainWindow();
        var sb = new StringBuilder();
        foreach (var (name, reason) in errors.Take(20))
            sb.AppendLine($"• {name}: {reason}");
        if (errors.Count > 20) sb.AppendLine($"…及其他 {errors.Count - 20} 项");
        await DialogHelper.ShowAlertAsync(window, $"打标失败（{errors.Count}）", sb.ToString());
    }
}
```

**CancelTag — 取消打标：**

```csharp
[RelayCommand(CanExecute = nameof(CanCancelTag))]
private void CancelTag()
{
    _tagCts?.Cancel();
}
private bool CanCancelTag() => IsTagging;
```

**TagSingle — 右键菜单单文件打标（独立 _tagCts，不被切日期取消）：**

```csharp
[RelayCommand]
private async Task TagSingle(MediaFile? file)
{
    if (file == null || !file.LocalExists || IsTagging) return;

    var aiCfg = await _configService.GetAsync<AIConfig>(ConfigKeys.AI);
    if (aiCfg == null || string.IsNullOrWhiteSpace(aiCfg.ApiKey))
    {
        ShowToast("AI 未配置");
        return;
    }

    _tagCts = new CancellationTokenSource();
    IsTagging = true;
    TagTotalCount = 1;
    TagCompletedCount = 0;

    try
    {
        var (result, failure) = await _aiTagger.TagFileAsync(file, aiCfg, _tagCts.Token);
        if (failure != null) { file.TagError = Truncate(failure.Reason); ShowToast($"失败：{failure.Reason}"); }
        else if (result != null)
        {
            file.Tags = result.Tags.ToList();
            file.Score = result.Score;
            file.TagError = null;
            await _mediaRepo.UpdateTagAsync(file.Id, result.Tags, result.Score,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, default);
            ShowToast($"打标完成：{file.FileName}");
        }
    }
    finally
    {
        _tagCts?.Cancel(); _tagCts?.Dispose(); _tagCts = null;
        IsTagging = false;
    }
}
```

#### 3.3.5 构造函数注入

```csharp
private readonly IAITagger _aiTagger;

public GalleryViewModel(
    ...,
    IAITagger aiTagger,  // ← v0.6 新增
    ...)
{
    _aiTagger = aiTagger;
    ...
}
```

#### 3.3.6 状态机约束

```
[Idle] ──BatchTag──► [Tagging]
   ▲                    │
   │  ◄── 完成/取消 ────┘
```

| 按钮 | IsTagging=false | IsTagging=true |
|---|---|---|
| 开始AI | IsBatchActionEnabled | ❌ 隐藏 |
| 取消打标 | ❌ 隐藏 | ✅ 启用 |
| 多选/全选/反选 | ✅ | ❌ |
| 批量上传 | ✅ | ❌ |

---

### 3.4 Phase 4: MainWindow 工具栏 XAML

在 `Views/MainWindow.axaml:106-166` 区域，现有按钮组之后追加：

```xml
<!-- v0.6: 排序 ComboBox -->
<ComboBox SelectedIndex="{Binding GalleryViewModel.SortModeIndex}"
          IsVisible="{Binding IsGalleryPage}"
          ToolTip.Tip="排序方式"
          Classes="sort-combo"
          Width="80"
          Margin="12,0,0,0">
    <ComboBoxItem Content="时间↓"/>
    <ComboBoxItem Content="评分↓"/>
</ComboBox>

<!-- v0.6: 开始AI 按钮 -->
<Button Classes="toolbar-button"
        Content="开始AI"
        Command="{Binding GalleryViewModel.BatchTagCommand}"
        IsEnabled="{Binding GalleryViewModel.IsBatchActionEnabled}"
        IsVisible="{Binding GalleryViewModel.IsMultiSelectMode}"/>

<!-- v0.6: 取消打标 按钮 -->
<Button Classes="toolbar-button-danger"
        Content="取消打标"
        Command="{Binding GalleryViewModel.CancelTagCommand}"
        IsVisible="{Binding GalleryViewModel.IsTagging}"/>

<!-- v0.6: 打标进度文本 -->
<TextBlock Text="{Binding GalleryViewModel.TagProgressText}"
           FontSize="12"
           Foreground="{DynamicResource Accent.Stellar}"
           VerticalAlignment="Center"
           Margin="8,0,0,0"
           IsVisible="{Binding GalleryViewModel.IsTagging}"/>
```

**ComboBox 样式**（`Themes/Styles.axaml` 新增）：

```xml
<Style Selector="ComboBox.sort-combo">
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="Height" Value="28"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
    <Setter Property="HorizontalContentAlignment" Value="Center"/>
</Style>
```

---

### 3.5 Phase 5: GalleryView 卡片 + 右键

#### 3.5.1 评分角标（photo tile 左下）

```xml
<!-- 评分角标 -->
<Border IsVisible="{Binding HasScore}"
        HorizontalAlignment="Left"
        VerticalAlignment="Bottom"
        Margin="6,6,10,6"
        Padding="6,2"
        Background="{Binding Score, Converter={StaticResource ScoreToBrush}}"
        CornerRadius="8">
    <TextBlock Text="{Binding Score, Converter={StaticResource ScoreToDisplay}}"
               FontSize="11"
               FontWeight="SemiBold"
               Foreground="#FFFFFF"
               VerticalAlignment="Center"/>
</Border>
```

#### 3.5.2 标签小角标条（底部居中）

```xml
<Border IsVisible="{Binding HasTags}"
        HorizontalAlignment="Center"
        VerticalAlignment="Bottom"
        Margin="0,0,0,6"
        Padding="6,2"
        MaxWidth="140"
        Background="#CC0A0E1A"
        CornerRadius="8">
    <ToolTip.Tip>
        <ToolTip>
            <ItemsControl ItemsSource="{Binding Tags}"/>
        </ToolTip>
    </ToolTip.Tip>
    <TextBlock Text="{Binding Tags, Converter={StaticResource TagsToShortText}}"
               FontSize="10"
               Foreground="#E6FFFFFF"
               TextTrimming="CharacterEllipsis"
               MaxLines="1"/>
</Border>
```

#### 3.5.3 TagError 徽章（替代已有 Failed 位置）

```xml
<Border IsVisible="{Binding TagError, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
        HorizontalAlignment="Right"
        VerticalAlignment="Bottom"
        Margin="6,6,10,6"
        Width="20" Height="20"
        Background="#CCFFFFFF"
        CornerRadius="10"
        ToolTip.Tip="{Binding TagError}">
    <Path Data="{DynamicResource Icon.AlertTriangle}"
          Width="12" Height="12"
          Fill="{DynamicResource State.Danger}"
          Stretch="Uniform"/>
</Border>
```

#### 3.5.4 右键菜单

在现有 ContextFlyout 中追加：

```xml
<Separator/>
<MenuItem Header="AI 打标"
          Command="{Binding $parent[UserControl].DataContext.GalleryViewModel.TagSingleCommand}"
          CommandParameter="{Binding}"/>
```

#### 3.5.5 Converter（新建 3 个文件）

| Converter | 输入 | 输出 | 规则 |
|---|---|---|---|
| `ScoreToBrushConverter` | `int? score` | `IBrush` | ≥80 → #4CAF50 绿 / 60-79 → #FFA726 黄 / <60 → #90A4AE 灰 |
| `ScoreToDisplayConverter` | `int? score` | `string` | null → "" / 其它 → `(score/10.0):F1` (如 82 → "8.2") |
| `TagsToShortTextConverter` | `List<string>?` | `string` | 空 → "" / ≤3 → " · ".join / >3 → 前2个 + " +N" |

---

### 3.6 Phase 6: AI 图标（可选）

```xml
<!-- Themes/Icons.axaml -->
<StreamGeometry x:Key="Icon.Sparkles">M9.813 15.904L...</StreamGeometry>
```

按钮可选择加入图标：

```xml
<StackPanel Orientation="Horizontal" Spacing="6">
    <Path Data="{DynamicResource Icon.Sparkles}" Width="14" Height="14"
          Fill="{DynamicResource Text.Secondary}" Stretch="Uniform"/>
    <TextBlock Text="开始AI"/>
</StackPanel>
```

---

## 3.7 Phase 7 (v0.6.1): Gallery 左栏 TabControl + 标签分类

> 完整 spec 见 **`doc/11-ai-tagging.md` §5.5 / §5.6 / §6.2**。本节只列本 spec 视角的改动摘要。

### 3.7.1 新增属性 + 命令

```csharp
// === v0.6.1 左栏分类状态 ===
[ObservableProperty] private string? _selectedTag;

public bool IsDateGroupMode => GroupMode == GroupMode.Date;
public bool IsTagGroupMode => GroupMode == GroupMode.Tag;

public int GroupModeIndex
{
    get => GroupMode == GroupMode.Date ? 0 : 1;
    set => GroupMode = value == 0 ? GroupMode.Date : GroupMode.Tag;
}

partial void OnGroupModeChanged(GroupMode value)
{
    switch (value)
    {
        case GroupMode.Date: _ = InitializeAsync(); break;
        case GroupMode.Tag:  _ = LoadTagGroupsAsync(_cts?.Token ?? default); break;
    }
}

[RelayCommand]
private async Task LoadTagGroupsAsync(CancellationToken ct)
{
    if (string.IsNullOrEmpty(ProjectPath)) return;
    var groups = await _mediaRepo.GetTagGroupsAsync(ProjectPath, ct);
    TagGroups.Clear();
    foreach (var g in groups) TagGroups.Add(g);
    if (TagGroups.Count > 0) await SelectTagAsync(TagGroups[0]);
    else { SelectedTag = null; CurrentMediaFiles.Clear(); }
}

[RelayCommand]
private async Task SelectTagAsync(TagGroupItem? group)
{
    if (group == null) return;
    _cts?.Cancel();
    _cts = new CancellationTokenSource();
    var ct = _cts.Token;
    ExitMultiSelect();
    SelectedTag = group.Tag;

    IsLoadingMedia = true;
    var files = await _mediaRepo.GetByTagAsync(ProjectPath!, group.Tag, SortMode, ct);
    // 反推 UploadStatus（跟 LoadDateAsync 一致）
    var jobs = await _uploadJobRepo.GetInProgressAsync(ProjectPath, ct);
    var pausedSet = new HashSet<string>(jobs.Select(j => j.RelativePath), StringComparer.OrdinalIgnoreCase);
    CurrentMediaFiles.Clear();
    foreach (var file in files)
    {
        if (file.IsUploaded) file.UploadStatus = UploadStatus.Uploaded;
        else if (pausedSet.Contains(file.RelativePath)) file.UploadStatus = UploadStatus.Paused;
        else file.UploadStatus = UploadStatus.NotUploaded;
        CurrentMediaFiles.Add(file);
    }
    IsLoadingMedia = false;
}
```

### 3.7.2 `OnSortModeChanged` 更新（v0.6.1）

```csharp
partial void OnSortModeChanged(SortMode value)
{
    // 重新加载当前视图（Date 或 Tag）
    _ = GroupMode switch
    {
        GroupMode.Date when SelectedDate != null => SelectAsync(SelectedDate),
        GroupMode.Tag when SelectedTag != null   => SelectTagAsync(TagGroups.FirstOrDefault(g => g.Tag == SelectedTag)),
        _ => Task.CompletedTask,
    };
}
```

### 3.7.3 Converter（新建 `Converters/TagConverters.cs`）

| Converter | 输入 | 输出 | 规则 |
|---|---|---|---|
| `EmptyTagToUntitledConverter` | `string?` | `string` | null/空 → "未分类"；其他原值 |

### 3.7.4 GalleryView.axaml 左栏 XAML 改动

`Views/GalleryView.axaml:30-75` 把 `<Border Grid.Column="0">` 内部从「单 ScrollViewer 时间轴」改为：

```xml
<DockPanel>
    <TabControl DockPanel.Dock="Top" SelectedIndex="{Binding GroupModeIndex}" Classes="group-tabs">
        <TabItem Header="时间"/>
        <TabItem Header="标签"/>
    </TabControl>
    <ScrollViewer IsVisible="{Binding IsDateGroupMode}">
        <ItemsControl ItemsSource="{Binding DateGroups}" .../>  <!-- 沿用现有 -->
    </ScrollViewer>
    <ScrollViewer IsVisible="{Binding IsTagGroupMode}">
        <ItemsControl ItemsSource="{Binding TagGroups}">
            <DataTemplate x:DataType="models:TagGroupItem">
                <Button Classes="tag-node" Command="...SelectTagCommand" CommandParameter="{Binding}">
                    <Grid>
                        <TextBlock Text="{Binding Tag, Converter={StaticResource EmptyTagToUntitled}}"/>
                        <TextBlock Text="{Binding Count, StringFormat='{}{0} 张'}"/>
                    </Grid>
                </Button>
            </DataTemplate>
        </ItemsControl>
    </ScrollViewer>
</DockPanel>
```

UserControl.Resources 加 `<converters:EmptyTagToUntitledConverter x:Key="EmptyTagToUntitled"/>`。

### 3.7.5 Themes/Styles.axaml 新增

```xml
<Style Selector="TabControl.group-tabs">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Padding" Value="12,8"/>
</Style>
<Style Selector="TabControl.group-tabs > TabItem">
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Padding" Value="12,6"/>
</Style>
<Style Selector="Button.tag-node">
    <!-- 沿用 timeline-node 视觉语言（圆点 + 文字） -->
</Style>
```

---

## 4. 实施检查清单

### 4.1 数据层

| # | 改动 | 文件 |
|---|---|---|
| ☐ | `MediaFile` 加 Tags/Score/TaggedAt/TagError + HasScore/HasTags | `Data/MediaFile.cs` |
| ☐ | `IMediaRepository` 加 3 方法签名（v0.6.1: `GetByTagAsync` 加 SortMode 参数，`GetTagGroupsAsync` 返回 `TagGroupItem`） | `Data/IMediaRepository.cs` |
| ☐ | `EnsureDatabase` ALTER TABLE 4列 + 2索引 | `Data/MediaRepository.cs` |
| ☐ | `GetByDateAsync` 扩展 SELECT + 排序 | `Data/MediaRepository.cs` |
| ☐ | `GetByTagAsync` SQL ORDER BY 按 SortMode 分支（v0.6.1 补） | `Data/MediaRepository.cs` |
| ☐ | `UpdateTagAsync` / `GetTagGroupsAsync` / `GetByTagAsync` 实现 | `Data/MediaRepository.cs` |

### 4.2 Models

| # | 改动 | 文件 |
|---|---|---|
| ☐ | 加 `GroupMode` / `SortMode` 枚举 | `Models/Models.cs` |
| ☐ | **v0.6.1** 加 `TagGroupItem` sealed class | `Models/Models.cs` |

### 4.3 ViewModel

| # | 改动 | 文件 |
|---|---|---|
| ☐ | 加 IsTagging/TagCompletedCount/TagTotalCount/TagProgressText | `GalleryViewModel.cs` |
| ☐ | 加 GroupMode/SortMode/SortModeIndex/TagGroups | `GalleryViewModel.cs` |
| ☐ | **v0.6.1** 加 SelectedTag / GroupModeIndex / IsDateGroupMode / IsTagGroupMode | `GalleryViewModel.cs` |
| ☐ | **v0.6.1** 加 OnGroupModeChanged / LoadTagGroupsAsync / SelectTagAsync 命令 | `GalleryViewModel.cs` |
| ☐ | 加 OnSortModeChanged → SelectAsync 联动（v0.6.1 加 Tag 分支） | `GalleryViewModel.cs` |
| ☐ | 修改 IsBatchActionEnabled 加 `&& !IsTagging` | `GalleryViewModel.cs` |
| ☐ | 加 BatchTag/CancelTag/TagSingle 命令 | `GalleryViewModel.cs` |
| ☐ | 构造函数注入 IAITagger | `GalleryViewModel.cs` |
| ☐ | DI 注册 IAITagger（若未注册） | `Program.cs` |

### 4.4 UI

| # | 改动 | 文件 |
|---|---|---|
| ☐ | 工具栏加排序 ComboBox + "开始AI" + "取消打标" + 进度 | `Views/MainWindow.axaml` |
| ☐ | photo tile 加评分角标 + 标签条 + TagError 徽章 | `Views/GalleryView.axaml` |
| ☐ | 右键菜单加 "AI 打标" | `Views/GalleryView.axaml` |
| ☐ | **v0.6.1** 左栏改 TabControl + 时间/标签双 ScrollViewer | `Views/GalleryView.axaml` |
| ☐ | **v0.6.1** 注册 EmptyTagToUntitledConverter 到 UserControl.Resources | `Views/GalleryView.axaml` |
| ☐ | 新建 ScoreToBrushConverter | `Converters/` |
| ☐ | 新建 ScoreToDisplayConverter | `Converters/` |
| ☐ | 新建 TagsToShortTextConverter | `Converters/` |
| ☐ | **v0.6.1** 新建 EmptyTagToUntitledConverter（合并到 `TagConverters.cs`） | `Converters/` |
| ☐ | 注册 Converter 到 App.axaml ResourceDictionary | `App.axaml` |
| ☐ | 加 .sort-combo 样式 | `Themes/Styles.axaml` |
| ☐ | **v0.6.1** 加 .group-tabs / TabItem / .tag-node 样式 | `Themes/Styles.axaml` |

### 4.5 验证

| # | 验证项 |
|---|---|
| ☐ | 编译无错 |
| ☐ | 旧 DB 打开不报错（AddColumnIfMissing 幂等） |
| ☐ | AI 未配置点"开始AI" → toast 提示 |
| ☐ | 选 3 张图点"开始AI" → 进度条 → 卡片显示评分 + 标签 |
| ☐ | 排序切"评分↓" → 有分数排前面，null 排最后 |
| ☐ | 打标中 "多选/全选/反选/批量上传" 按钮灰态 |
| ☐ | 点"取消打标" → 中断 + toast "已取消" |
| ☐ | 失败文件显示红色徽章 + hover 看错误原因 |
| ☐ | 打分标后刷新 → 数据持久化不丢失 |
| **v0.6.1 专项** | |
| ☐ | 切「时间」tab → 沿用 v0.6 行为（DateGroups + SelectedDate） |
| ☐ | 切「标签」tab → 自动选中第一个 tag → 右侧渲染该 tag 的文件 |
| ☐ | tag 视图下切排序「评分↓」 → 文件按 score DESC NULL LAST 重排 |
| ☐ | tag 视图下多选 → 「批量打标」可用 → 打标完成 tag 分组自动刷新 |
| ☐ | 项目从未打过标 → 切到「标签」tab 显示空态 |
| ☐ | TabControl 样式沿用 theme token，无硬编码颜色 |

---

## 5. 依赖关系

```
Phase 1 (数据层)
  └─ Phase 2 (Models 枚举)
       └─ Phase 3 (ViewModel 命令 + 属性)
            ├─ Phase 4 (工具栏 UI)
            └─ Phase 5 (卡片 UI + Converter)
                 └─ Phase 6 (图标)

v0.6.1 patch:
  Phase 1a (Repository SortMode + TagGroupItem) ─┐
                                                 ├─→ Phase 7 (左栏 TabControl + VM 联动 + Converter)
                                                 │
            Phase 1b (无 schema 变更) ───────────┘
```

Phase 4 / 5 / 7 可并行；Phase 7 依赖 Phase 1a 的 SortMode 签名对齐。

---

## 6. 风险评估

| 风险 | 影响 | 缓解 |
|---|---|---|
| AI 调用慢（5-10s/张） | 用户体验差 | 串行 + 200ms 节流 + 进度条 + 可取消 |
| 视频抽帧 ffmpeg 缺失 | 视频打标失败 | D.1 先只做图片；D.2 再加视频抽帧 |
| SQLite JSON LIKE 误匹配 | 标签筛选不准 | 用 `LIKE '%"标签名"%'` 精确匹配 JSON 数组 |
| 旧 DB 无新列 | 启动崩溃 | AddColumnIfMissing 幂等迁移 |
| 大量文件打标被切日期中断 | 数据不一致 | `_tagCts` 独立于 `_cts`，切日期不取消打标 |

---

> **本文档状态**：plan（v0.6.1 patch）。v0.6 主计划已实施完数据/AI/photo tile；本 patch 补左栏 tab + tag 视图排序联动。
>
> **下一步**：用户审 v0.6.1 patch → 确认 → 开 Step 1 Repository.SortMode → Step 2 VM 联动 → Step 3 Converter → Step 4 GalleryView XAML → Step 5 编译验证。

---

## 7. v0.6.1 patch 偏差对照

> 与 `doc/11-ai-tagging.md` §12 一致。本 spec 视角的额外偏差：

| # | 偏差 | 原 v1.0 spec | v1.1 (v0.6.1 patch) |
|---|---|---|---|
| P1 | `GetByTagAsync` 签名 | `(projectPath, tag, ct)` | `(projectPath, tag, sortMode, ct)` |
| P2 | `TagGroups` 元素类型 | `(string Tag, int Count)` tuple | `TagGroupItem` sealed class |
| P3 | `OnSortModeChanged` | 只重载 SelectedDate | switch GroupMode (Date/Tag 都重载) |
| P4 | `OnGroupModeChanged` | 不存在 | 加：Tag → LoadTagGroupsAsync + 自动选第一个 |
| P5 | 左栏 tab UI | 不在本文档范围（属 doc/11 §5.5） | 加 Phase 7 引用 doc/11 spec |
