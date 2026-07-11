# 01 — 启动与进程模型

> 对应代码：`Program.cs`、`App.axaml(.cs)`、`Views/MainWindow.axaml(.cs)`、`ViewModels/MainWindowViewModel.cs`、`Services/ThemeManager.cs`、`StartTooler.csproj BeforeBuild`。

---

## 1. 进程模型总览

```
进程入口 Program.Main (WinExe, [STAThread])
  └─► Program.BuildAvaloniaApp() → AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace()
  └─► StartWithClassicDesktopLifetime(args)
        └─► App.OnFrameworkInitializationCompleted()
              ├─► await LoadSavedAppConfigAsync()      // 读 ConfigDb → 主题 + FFmpeg
              │     ├─► ConfigService.GetAsync<AppConfig>(ConfigKeys.App)
              │     ├─► ThemeManager.SetTheme(red?)    // 主题资源立刻合并
              │     └─► FFmpegConfigurator.Apply(...)   // 静态静态字段
              └─► desktop.MainWindow = new MainWindow()
                    └─► MainWindow ctor
                          └─► DataContext = new MainWindowViewModel()
                                └─► ctor: 构造所有 VM + Service
                                └─► _ = InitializeAsync() (fire-and-forget)
                                      ├─► SettingsViewModel.InitializeAsync()
                                      ├─► GalleryViewModel.InitializeAsync()
                                      ├─► UploadServerViewModel.InitializeAsync()
                                      └─► TryPromptResumeInterruptedAsync()
                                              // 启动恢复弹窗

进程退出
  └─► AppDomain.CurrentDomain.ProcessExit (registered in MainWindowViewModel ctor)
        └─► UploadServerViewModel?.PublicRelayViewModel?.EnsureRemoteKilledOnExitAsync()
              // 远端 SSH kill nohup relay（fire-and-forget, 5s timeout）
```

---

## 2. 关键源码定位

### 2.1 `Program.cs` — 入口与 Trace 重定向

```csharp
// Program.cs:14-25 — WinExe 没 console，必须把 Trace 写到文件，否则线上错误抓不到
static Program() {
    try {
        var cwd = Directory.GetCurrentDirectory();
        var logPath = Path.Combine(cwd, "starttooler-debug.log");
        Trace.Listeners.Add(new TextWriterTraceListener(logPath) { Name = "FileLog" });
        Trace.AutoFlush = true;  // 进程崩溃前不丢日志
        Trace.WriteLine($"[starttooler-debug] pid={Environment.ProcessId} cwd={cwd} log={logPath}");
    } catch { /* cwd 不可写时静默（macOS 双击 .app 时 cwd=/ 不可写） */ }
}

[STAThread] public static void Main(string[] args) =>
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
```

**契约**：
- 任何模块写日志用 `System.Diagnostics.Trace.WriteLine(...)`，**不要用 `Console.WriteLine`**，写不见。
- `cwd` 在三种启动场景下分别是：
  - `dotnet run`：仓库根
  - 双击 `.app` / `.exe`：`/` 或安装目录（不可写，Catch 静默）
  - publish 产物：双击场景同样不可写
- 日志文件与可执行文件同级，便于用户反馈时附上来。

### 2.2 `App.axaml.cs` — 启动时加载持久化设置

```csharp
// App.axaml.cs:19-29
public override async void OnFrameworkInitializationCompleted() {
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
        await LoadSavedAppConfigAsync();
        desktop.MainWindow = new MainWindow();
    }
    base.OnFrameworkInitializationCompleted();
}
```

`LoadSavedAppConfigAsync`（`App.axaml.cs:32-54`）**仅做两件事**：
1. `ThemeManager.SetTheme(appConfig.Theme == "RedNight")` — 读 `AppConfig`，立刻把主题资源合入 Application.Resources.MergedDictionaries，让首帧就是正确主题
2. `FFmpegConfigurator.Apply(appConfig.FFmpegPath, appConfig.FFprobePath)` — 把路径写进静态字段，后续 `FfprobeRunner` / `FfmpegSnapshotRunner` 直接读

**注意**：这里是 `async void`，异常一定要 `try/catch`（已实现），否则崩在 Avalonia 启动早期，错误冒到外层不好定位。

### 2.3 `App.axaml` — 主题资源合并入口

```xml
<!-- App.axaml:1-19 -->
<Application RequestedThemeVariant="Default">
  <Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://StartTooler/Themes/Styles.axaml"/>
  </Application.Styles>
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceInclude Source="avares://StartTooler/Themes/Colors.axaml"/>
        <ResourceInclude Source="avares://StartTooler/Themes/Icons.axaml"/>
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
</Application>
```

主题顺序：`Styles.axaml`（控件样式）→ `Colors.axaml`（色板 token）→ `Icons.axaml`（几何图标 `StreamGeometry`）→ `ThemeManager.SetTheme(true)` 时再叠加 `RedNightVision.axaml` 等价于硬覆盖 token。

### 2.4 `Views/MainWindow.axaml` — 三区布局

```
Grid: ColumnDefinitions="80,*"  RowDefinitions="38,40,*"
├─ Row 0 (跨两列) Panel Height=38: 系统标题栏
│   ├─ TextBlock "星助"  +  Border 下边线
│   └─ Components:ScanProgressBar  (DataContext=GalleryViewModel)
├─ Row 1 Col 0 Border 80×40: NavRail 上方留白
└─ Row 1 Col 1 Border: 全局工具栏（DockPanel）
    ├─ Right: 返回/保存按钮（设置页可见）
    ├─ Right: 刷新按钮（媒体页可见）
    └─ Left:  StackPanel 多选/批量操作按钮组
└─ Row 2 Col 0 Border: Controls:NavRail (媒体/设置/上传)
└─ Row 2 Col 1 ContentControl {Binding CurrentView}
    └─ DataTemplates: GalleryVM / SettingsVM / UploadServerVM
└─ Row 2 (跨两列) Panel 右下角 IsHitTestVisible=False
    └─ ItemsControl ItemsSource={Binding Source={x:Static NotificationService.Current}, Path=Items}
        └─ DataTemplate views:NotificationCard
```

**关键点**：
- `NavRail` 80px 宽，居中文字（早期版本是图标+文字，v2.2 起只有文字——见 `Controls/NavRail.axaml`）。
- **全局工具栏**（不是每页独立），绑定到 `MainWindowViewModel.GalleryViewModel.*` / `SettingsViewModel.*`。
- **通知浮层**：跨全窗口定位 `Right + Bottom`，`IsHitTestVisible=False` 防止遮挡按钮。
- `ContentControl` + `DataTemplates` 是 VM-View 路由的核心。

### 2.5 `MainWindowViewModel.cs` — 跨 VM 协作与导航

构造器签名（`MainWindowViewModel.cs:45-94`）：
- 显式实例化所有 Service：`ConfigService / MediaRepository / UploadJobRepository / ThumbnailService / SystemShellService`
- `OssStorageFactory` 用 `Func<OssConfig>` provider —— 延后读 `Config`（OS 配置会被动态改），**永远拿到最新**。
- 三个 VM：SettingsViewModel、GalleryViewModel（注入 7 个依赖 + OSS 未配置回调）、UploadServerViewModel（含 PublicRelayViewModel）。
- 默认 `CurrentView = GalleryViewModel`。
- 退出兜底：注册 `AppDomain.CurrentDomain.ProcessExit` 杀 VPS relay。

#### 关键命令

| 命令 | 行为 |
|---|---|
| `NavigateToGallery` | 设置未保存时弹 Discard 确认；切到 Gallery；自动 `GalleryViewModel.ReloadCommand` |
| `NavigateToSettings` | 切到 Settings（**无** Discard 检查） |
| `NavigateToUploadServer` | 切到 UploadServer（含公网代理折叠面板） |
| `NavigateToMedia` | `= NavigateToGallery`，alias |
| `Refresh` | 调 `GalleryViewModel.RefreshCommand` |
| `ShowOssNotConfiguredDialogAsync` | 给 Gallery 用，弹「去设置」对话框并跳转 Settings 页 |

#### `ShowOssNotConfiguredDialogAsync` 设计

避免 `GalleryViewModel` ↔ `SettingsViewModel` 循环依赖；改用 `Func<Task<bool>>` 注入，由 `MainWindowViewModel` 提供（`MainWindowViewModel.cs:67-69` + `GalleryViewModel.cs:27-79`）。

```csharp
// GalleryVM 调用点（断网/重置都走它）
storage = _ossFactory.TryCreate();
if (storage == null) {
    await PromptOssNotConfiguredAsync();   // 内部调 _onOssNotConfigured?.Invoke()
    return;
}
```

#### `TryPromptResumeInterruptedAsync` 启动恢复弹窗

`MainWindowViewModel.cs:107-144`：启动时扫 `upload_jobs` 表，如有未完成任务弹「恢复 / 稍后」二选一；用户确认后调 `GalleryViewModel.ResumeInterruptedAsync(jobs)`（`GalleryViewModel.cs:548-573`）。

**关键不变量**：`_resumePrompted = true` 设了不再弹；DB 异常静默（启动期不能阻塞 UI）。

### 2.6 `Services/ThemeManager.cs` — Save-First 主题切换

```csharp
public static void SetTheme(bool redNightVision) {
    if (Application.Current is not { } app) return;
    if (_overrideDict != null) {                    // 撤销旧覆盖
        app.Resources.MergedDictionaries.Remove(_overrideDict);
        _overrideDict = null;
    }
    if (redNightVision) {
        _overrideDict = new ResourceDictionary();
        _overrideDict.Add("Bg.Outer", Color.Parse("#000000"));
        // ... 16 个颜色 token + 2 个 brush 全部重写 ...
        app.Resources.MergedDictionaries.Add(_overrideDict);
    }
    IsRedNightVision = redNightVision;
}
```

**契约**：
- **不预览**：UI 上点一下「夜视」不会立即变红，必须「保存」才生效（`SettingsViewModel.cs:387-396`：`ThemeManager.SetTheme(SelectedTheme == 1)` 在 Save 成功后调）。
- **全 token 覆盖**：从 `Bg.Outer` 到 `Text.Disabled` 16 个颜色 + `Gradient.Header`/`Gradient.PrimaryButton` 全写一遍；不靠属性绑定（响应不及时）。
- 撤销逻辑：旧 override 先 Remove 再创建新，避免颜色泄漏。

### 2.7 `StartTooler.csproj BeforeBuild` — Go relay 编译钩子

```xml
<!-- StartTooler.csproj:41-48 -->
<Target Name="RelayGoBuild" BeforeTargets="BeforeBuild">
  <Exec Command='bash "$(MSBuildThisFileDirectory)..\scripts\build-relay.sh"'
        WorkingDirectory="$(MSBuildThisFileDirectory).."
        ContinueOnError="true" IgnoreExitCode="true"
        ConsoleToMSBuild="false" />
</Target>
```

**契约**：
- `ContinueOnError=true`：没 Go 工具链不阻塞 build（dev 机器 / Win CI 不一定有 Go）。
- `IgnoreExitCode=true`：脚本内做了「go 不存在则 exit 0」对齐，但保险。
- `ConsoleToMSBuild="false"`：避免 Go 编译输出刷屏到 MSBuild output。
- 输出落在 `StartTooler/Resources/relay-binaries/upload-relay-linux-{amd64,arm64}`，**自动**被 `EmbeddedResource` 打包（`StartTooler.csproj:26-33`）。

详见 `scripts/build-relay.sh`：
- HTML 单源同步：`StartTooler/Resources/upload.html` → `tools/upload-relay/web/index.html`（`build-relay.sh:27-35`）。
- mtime 检查 + 只重编陈旧的 arch（`build-relay.sh:51-67`）。
- `GOOS=linux GOARCH=$arch CGO_ENABLED=0` 交叉编译。

---

## 3. MainWindow 的手动标题栏拖拽

`MainWindow.axaml:18-20`：
```xml
ExtendClientAreaToDecorationsHint="True"
ExtendClientAreaTitleBarHeightHint="38"
SystemDecorations="Full"
```

让 Avalonia 把原生交通灯隐藏，自绘 38px 高标题栏 + `Panel.PointerPressed="OnTitleBarPointerPressed"` 实现拖拽（macOS Win32 上的标准做法）。

**交通灯位置**：macOS 上 Avalonia 自动把红黄绿按钮放在自绘标题栏最左侧，**`Margin="80,0,0,0"` 留 80px（NavRail 同宽）的避让**，让标题文字不被按钮盖住（`MainWindow.axaml:42-50`）。

---

## 4. 启动顺序约束（必读）

| 顺序 | 行为 | 失败的影响 |
|---|---|---|
| 1 | `Trace` 文件监听器装载 | 无日志，线上 bug 抓不到 |
| 2 | `App` + `OnFrameworkInitializationCompleted` | 框架初始化失败（理论上不应发生） |
| 3 | `LoadSavedAppConfigAsync`（读 ConfigDb 应用主题 + FFmpeg） | 主题 fallback `DeepSpace`；FFmpeg 走 PATH |
| 4 | `MainWindow` 实例化 + `MainWindowViewModel` ctor | VM 不会全部失败，因为 Service ctor 都做了容错 |
| 5 | `_ = InitializeAsync()` fire-and-forget | 失败各自 catch，主流程继续 |
| 6 | `TryPromptResumeInterruptedAsync` | 用户可见弹窗；DB 异常不弹 |

`MainWindowViewModel.InitializeAsync` 必须 **asynchronically** 完成三件事：
1. `SettingsVM.Initialize` — 读 Project/Oss/App 配置到 UI
2. `GalleryVM.Initialize` — 读 DateGroups + 自动选第一个日期
3. `UploadServerVM.Initialize` — `await PublicRelayVM.Initialize`（读 SSH 配置）

---

## 5. 修改这个模块前的检查清单

| 改动 | 涉及 | 验证 |
|---|---|---|
| 加全局 UI 命令（如「主题切换按钮」） | `MainWindowViewModel` + 工具栏 XAML | 启动 / 关闭按钮 IsVisible 切换流畅 |
| 加新页面（如「帮助页」） | `enum ViewPage` + `DataTemplate` + 路由命令 | NavRail 加按钮、NaviGateX Command 调通 |
| 改主题默认色 | `Themes/Colors.axaml` + `RedNightVision.axaml` 配套改 | 两个主题都不能掉色 |
| 加主题（v0.2+） | 新建 `XxxTheme.axaml`、`ThemeManager` 加分支 | `SetTheme("xxx")` / `Save` 后首帧正确 |
| 改退出兜底 | `AppDomain.CurrentDomain.ProcessExit` | 进程退出 5s 内杀完远端进程（看 trace 日志） |
| 加新的 startup 持久化字段 | `AppConfig` 加字段 + `LoadSavedAppConfigAsync` 读 + `SettingsViewModel.Save` 写 | 启动读 → UI 显示正确；保存 → 重启依然 |
| BeforeBuild 改命令 | `StartTooler.csproj` | mac/win 都走对脚本 |

---

## 6. 已知陷阱（详见 `10-trap-book.md`）

- **Trace 不在 console**：所有诊断日志用 Trace 别用 Console（已固化）
- **`async void` 兜底**：`App.OnFrameworkInitializationCompleted` 是 `async void`，异常必须捕获
- **退出兜底的 5s timeout**：`ProcessExit` 后进程随时被 SIGKILL，await 不能太久
- **macOS 双击 .app 的 cwd**：`Program.Program` 静默失败，不影响主流程
- **BeforeBuild 与 dotnet clean**：`dotnet clean` 不会删 `relay-binaries/`，下次 build 误判产物陈旧——`build-relay.sh` 的 mtime 检查天然跳过
