# 0.13 — 插件系统设计规划

> 核心目标：解决可扩展性与用户可玩性。提供「插件实验室」Tab，用户用自然语言描述需求 → AI 生成插件代码 → Roslyn 编译 → 即时热加载运行。

---

## 1. 动机与目标

### 1.1 为什么需要插件系统

| 问题 | 插件系统的应对 |
|------|---------------|
| 天文摄影用户需求长尾（星图、月相、设备控制、特定数据源） | 不可能全部内置，由社区/用户自己造 |
| AI 标注、存储后端、导出格式等可替换组件 | 开放接口，让高级用户自行扩展 |
| 开源项目活跃度 | 插件生态本身就是最好的社区驱动力 |

### 1.2 设计原则

- **零配置上手**：用户只需描述需求，AI 生成成品插件，不需要懂 C#
- **安全可逆**：任何插件都可禁用/卸载，插件崩溃不拖垮主程序
- **最小宿主 API 面**：只暴露经过白名单审查的接口，渐进开放
- **与现有架构正交**：不推翻现有 MVVM 模式，插件只是新的 View/ViewModel 提供者

---

## 2. 整体架构

```
┌──────────────────────────────────────────────────────┐
│                    StartTooler 主进程                  │
│                                                      │
│  ┌─────────────┐  ┌──────────────┐  ┌─────────────┐  │
│  │ PluginLoader │  │PluginCompiler│  │  PluginLab   │  │
│  │ (扫描+加载)  │  │(AI+Roslyn)   │  │   View/VM    │  │
│  └──────┬──────┘  └──────┬───────┘  └─────────────┘  │
│         │                │                            │
│         ▼                ▼                            │
│  ┌──────────────────────────────────────────────┐    │
│  │           StartTooler.PluginSDK               │    │
│  │  IToolerPlugin / IPagePlugin / IPluginContext │    │
│  └──────────────────────────────────────────────┘    │
│         ▲                                             │
│         │ 实现                                        │
│  ┌──────┴───────────────────────────────────────┐    │
│  │              Plugins/ 目录                     │    │
│  │  MoonPhasePlugin.dll  WeatherPlugin.dll  ...  │    │
│  │  manifest.json                                │    │
│  └──────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────┘
```

两层项目结构：

```
StartTooler.sln
├── StartTooler.PluginSDK/       # 新建：插件契约 + 宿主 API 抽象
│   ├── IToolerPlugin.cs
│   ├── IPagePlugin.cs
│   ├── IPluginContext.cs
│   └── PluginManifest.cs
│
├── StartTooler/                  # 修改：集成加载/编译/PluginLab
│   ├── Services/
│   │   ├── PluginLoader.cs       # 新增
│   │   └── PluginCompiler.cs     # 新增
│   ├── ViewModels/
│   │   └── PluginLabViewModel.cs # 新增
│   ├── Views/
│   │   └── PluginLabView.axaml   # 新增
│   ├── Controls/
│   │   └── NavRail.axaml         # 重构为动态绑定
│   └── ViewModels/
│       └── MainWindowViewModel.cs # 重构 ViewPage → 动态路由
│
└── Plugins/                      # 运行时插件目录（.gitignore）
    └── manifest.json
```

### 2.1 依赖方向

```
StartTooler.PluginSDK  ← 无依赖（纯接口，最小程序集引用）
    ▲
    │ 引用
    │
StartTooler  ← 实现 PluginLoader / PluginCompiler / PluginLab
    ▲
    │ 引用 PluginSDK
    │
用户插件 DLL  ← 编译时引用 PluginSDK.dll
```

---

## 3. PluginSDK 接口定义

### 3.1 IToolerPlugin — 所有插件的根接口

```csharp
namespace StartTooler.PluginSDK;

/// <summary>所有插件的基接口。每个插件 DLL 必须有且只有一个实现此接口的类型。</summary>
public interface IToolerPlugin
{
    /// <summary>插件唯一标识。推荐格式：作者.功能名，如 "user.moon-phase-viewer"</summary>
    string Id { get; }

    /// <summary>显示名称，如 "月相查看器"</summary>
    string Name { get; }

    /// <summary>一句话描述，在 PluginLab 列表中展示</summary>
    string Description { get; }

    /// <summary>SemVer 版本号</summary>
    string Version { get; }

    /// <summary>插件类型标记：Page / Tool / Storage</summary>
    PluginType Type { get; }
}

public enum PluginType
{
    Page,    // 新标签页
    Tool,    // 右键菜单工具项
    Storage, // 存储后端
}
```

### 3.2 IPagePlugin — 页面型扩展点（Phase 1 唯一实现）

```csharp
/// <summary>页面型插件：在左侧导航栏新增一个 Tab，点击后显示自定义内容。</summary>
public interface IPagePlugin : IToolerPlugin
{
    /// <summary>导航栏按钮文本，1-4 个汉字，如 "月相"</summary>
    string NavLabel { get; }

    /// <summary>创建 Avalonia 视图。每切换到此 Tab 调用一次。</summary>
    Control CreateView(IPluginContext context);

    /// <summary>创建 ViewModel。每切换到此 Tab 调用一次。</summary>
    object CreateViewModel(IPluginContext context);

    /// <summary>Tab 激活时调用。可用于刷新数据。</summary>
    Task OnActivatedAsync(IPluginContext context);

    /// <summary>Tab 离开时调用。</summary>
    Task OnDeactivatedAsync(IPluginContext context);
}
```

### 3.3 IPluginContext — 宿主 API 白名单

```csharp
/// <summary>注入给插件的宿主能力。只暴露经过安全审查的接口。</summary>
public interface IPluginContext
{
    /// <summary>媒体文件仓储（只读查询 + 标签写入）。不可用于文件系统直接写。</summary>
    IMediaRepository MediaRepository { get; }

    /// <summary>插件独立配置命名空间。自动在 key 前加 "plugin.{pluginId}." 前缀。</summary>
    Task<T?> GetConfigAsync<T>(string key) where T : class;
    Task SetConfigAsync<T>(string key, T value) where T : class;

    /// <summary>显示右下角通知浮层。</summary>
    void ShowNotification(string message, NotificationType type = NotificationType.Info);

    /// <summary>当前项目根路径（只读）。</summary>
    string ProjectPath { get; }

    /// <summary>发送 HTTP 请求（超时 30s，自动 User-Agent）。</summary>
    Task<HttpResponseMessage> SendHttpRequestAsync(HttpRequestMessage request, CancellationToken ct);

    /// <summary>在主线程执行操作。</summary>
    Task RunOnUIThreadAsync(Action action);
}
```

**不暴露的能力**（至少 Phase 1）：

- 文件系统直接读写（除了插件自己的 `Plugins/` 子目录）
- `ConfigService` 全局键读写
- `SystemShellService`（打开外部程序、打开文件夹）
- `OssStorageFactory` / 上传能力
- 原始 `HttpClient`（走 `SendHttpRequestAsync` 加统一超时和 UA）

---

## 4. PluginLoader 设计

### 4.1 职责

- 扫描 `Plugins/*.dll`
- `AssemblyLoadContext` 隔离加载，每个插件独立上下文
- 反射发现 `IToolerPlugin` 实现类型
- 读 `manifest.json` 获取启用/禁用状态
- 提供 `Load(string dllPath)` / `Unload(string pluginId)`

### 4.2 加载流程

```
启动时：
  PluginLoader.LoadAll()
    ├─ 检查 Plugins/ 是否存在，不存在则创建
    ├─ 读 Plugins/manifest.json → 获取已注册插件列表
    ├─ 对每个 enabled: true 的插件：
    │    ├─ 验证 DLL 文件存在
    │    ├─ 创建独立 AssemblyLoadContext
    │    ├─ LoadFromAssemblyPath(dllPath)
    │    ├─ 反射扫描所有 public 类型
    │    ├─ 找到实现了 IToolerPlugin 的类型
    │    ├─ 实例化，验证 Id/Name/Version 非空
    │    └─ 按 Type 分类存入 PagePlugins / ToolPlugins / StoragePlugins
    └─ 返回加载成功的插件列表

对每个加载失败的插件：
  └─ Trace.WriteLine + 通知用户（跳过，不阻塞启动）
```

### 4.3 卸载流程

```
PluginLoader.Unload(string pluginId):
  ├─ 找到对应的弱引用 AssemblyLoadContext
  ├─ context.Unload()
  ├─ 等待 GC 回收（最多 5s）
  ├─ 从活跃插件列表中移除
  └─ 如果插件已添加到 NavRail，移除对应 NavItem
```

### 4.4 崩溃隔离

插件所有对外调用点（`CreateView`、`CreateViewModel`、`OnActivatedAsync` 等）必须包裹 `try-catch`：

```csharp
try
{
    plugin.OnActivatedAsync(context);
}
catch (Exception ex)
{
    Trace.WriteLine($"[PluginLoader] {plugin.Id} OnActivatedAsync 异常: {ex}");
    // 弹通知，该插件本次会话禁用
    DisablePlugin(plugin.Id);
}
```

---

## 5. PluginCompiler 设计（AI + Roslyn）

### 5.1 编译流程

```
用户输入自然语言描述
  │
  ▼
[Step 1] 构建 AI prompt：
  - System: 接口定义 + Avalonia 示例 + 约束规则（见 §5.3）
  - User: 用户的需求描述 + 选定的扩展点类型
  │
  ▼
[Step 2] 调用 AI 生成 C# 源码（复用现有 AITagger 的 HTTP 链路）
  │
  ▼
[Step 3] Roslyn CSharpCompilation 编译
  ├─ 引用：PluginSDK.dll + Avalonia 程序集 + System.Runtime 等
  ├─ 输出：内存流 → byte[] → 写入 Plugins/{pluginId}.dll
  ├─ 成功 → 返回 CompileResult { Success, DllPath, Warnings }
  └─ 失败 → 返回 CompileResult { Success: false, Errors }
  │
  ▼ (如果失败，最多迭代 3 轮)
[Step 4] 把编译错误回传给 AI，让 AI 生成修正后的代码
  │
  ▼
[Step 5] 写入 manifest.json + 可选自动热加载
```

### 5.2 编译上下文

Roslyn 编译需要引用的程序集清单：

| 程序集 | 来源 |
|--------|------|
| `StartTooler.PluginSDK.dll` | 编译输出目录 |
| `Avalonia.Base.dll` | NuGet 缓存 |
| `Avalonia.Controls.dll` | NuGet 缓存 |
| `System.Runtime.dll` | .NET 运行时 |
| `System.Collections.dll` | .NET 运行时 |
| `System.Net.Http.dll` | .NET 运行时 |
| `CommunityToolkit.Mvvm.dll` | NuGet 缓存 |

```csharp
// 关键实现
var compilation = CSharpCompilation.Create(
    assemblyName,
    syntaxTrees: new[] { syntaxTree },
    references: GetMetadataReferences(),  // 上述列表
    options: new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release));
```

### 5.3 AI Prompt 模板（System Prompt）

```
你是一个 C# Avalonia MVVM 插件代码生成器。用户描述需求，你生成一个完整的插件。

你必须遵守以下规则：

1. 【命名空间】必须是 StartTooler.Plugins.UserGenerated
2. 【类名】必须叫 GeneratedPlugin，实现 IPagePlugin 接口
3. 【视图】必须生成一个完整的 Avalonia UserControl（.axaml 代码作为字符串嵌入，在 CreateView 中用 AvaloniaRuntimeXamlLoader.Load 解析）
4. 【ViewModel】必须生成 ViewModel 类，使用 CommunityToolkit.Mvvm（[ObservableProperty] + [RelayCommand]）
5. 【禁止】禁止访问文件系统、禁止启动外部进程、禁止使用反射
6. 【输出】只输出纯 C# 代码，不要 markdown 代码块包裹，不要解释文字

接口定义如下：
[插入 §3 的 IPagePlugin / IPluginContext 接口完整定义]

典型示例：
[插入 §5.4 的 few-shot 示例]
```

### 5.4 Few-shot 示例代码

用户需求："帮我在新标签页显示当前日期和时间"

```csharp
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using StartTooler.PluginSDK;

namespace StartTooler.Plugins.UserGenerated;

public class GeneratedPlugin : IPagePlugin
{
    public string Id => "user.clock";
    public string Name => "时钟";
    public string Description => "显示当前日期和时间";
    public string Version => "1.0.0";
    public PluginType Type => PluginType.Page;
    public string NavLabel => "时钟";

    public Control CreateView(IPluginContext context) => new ClockView();

    public object CreateViewModel(IPluginContext context) => new ClockViewModel();

    public Task OnActivatedAsync(IPluginContext context) => Task.CompletedTask;
    public Task OnDeactivatedAsync(IPluginContext context) => Task.CompletedTask;
}

public partial class ClockViewModel : ObservableObject
{
    [ObservableProperty] private string _currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    public ClockViewModel()
    {
        var timer = new System.Timers.Timer(1000);
        timer.Elapsed += (_, _) => CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        timer.Start();
    }
}

public class ClockView : UserControl
{
    public ClockView()
    {
        var grid = new Grid();
        var textBlock = new TextBlock
        {
            Text = "Hello from Clock Plugin!",
            FontSize = 24,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        grid.Children.Add(textBlock);
        Content = grid;
    }
}
```

> **注意**：Phase 1 只支持纯 C# 构建 UI（不使用 AXAML 文件），因为 AI 生成 AXAML + Code-behind 双文件成功率低。

---

## 6. 导航系统重构

### 6.1 现状

[MainWindowViewModel.cs](/Users/hex/code/StartTooler/StartTooler/ViewModels/MainWindowViewModel.cs) `ViewPage` 是固定枚举：

```csharp
public enum ViewPage { Gallery, Settings, UploadServer, Trash }
```

[NavRail.axaml](/Users/hex/code/StartTooler/StartTooler/Controls/NavRail.axaml) 是 4 个固定 `Button`，每个按钮 `Command="{Binding NavigateToXxxCommand}"`。

### 6.2 目标

`NavRail` 变成 `ItemsControl`，绑定到 `ObservableCollection<NavItem>`，内置页和插件页统一管理：

```csharp
public partial class NavItem
{
    public string Id { get; init; }           // "trash" / "user.moon-phase"
    public string Label { get; init; }         // "垃圾筒" / "月相"
    public bool IsBuiltIn { get; init; }       // 内置页不可删除
    public IRelayCommand NavigateCommand { get; init; }
    public bool IsActive { get; set; }
    public int? BadgeCount { get; set; }       // 垃圾筒数量徽章
    public string? PluginId { get; init; }     // 插件 Id，内置页为 null
}
```

### 6.3 影响范围

| 文件 | 改动 |
|------|------|
| `ViewModels/MainWindowViewModel.cs` | `ViewPage` 枚举废弃，`CurrentPage` → `string CurrentPageId`，导航命令统一为 `NavigateTo(string id)` |
| `Controls/NavRail.axaml` | 固定按钮 → `ItemsControl` + `ItemTemplate` |
| `Controls/NavRail.axaml.cs` | `CacheButtons()` 删除，改为监听 `MainWindowViewModel.NavItems` 集合变化 |
| `Views/MainWindow.axaml` | `CurrentView` 绑定改为字典查找 |

---

## 7. PluginLab UI 设计

### 7.1 布局

```
┌──────────────────────────────────────────────────────────┐
│  🔌 插件实验室                                            │
├────────────────┬────────────────────┬────────────────────┤
│  已安装插件     │    AI 代码生成      │   预览 / 日志       │
│                │                    │                    │
│  ┌──────────┐  │ "请输入你想要的     │  生成的代码：       │
│  │ 📦 时钟   │  │  功能描述..."      │                    │
│  │   已启用  │  │                    │  public class      │
│  │   [禁用]  │  │ [选择扩展类型 ▼]   │  GeneratedPlugin   │
│  │   [删除]  │  │   Page 页面型      │  : IPagePlugin {   │
│  └──────────┘  │                    │    ...             │
│  ┌──────────┐  │ [⚡ 生成代码]      │                    │
│  │ 📦 月相   │  │                    │  [编译状态]        │
│  │   已禁用  │  │                    │  ✅ 编译成功        │
│  │   [启用]  │  │                    │                    │
│  │   [删除]  │  │                    │  [▶ 试运行]        │
│  └──────────┘  │                    │  [📦 安装插件]     │
│                │                    │                    │
└────────────────┴────────────────────┴────────────────────┘
```

### 7.2 左栏：已安装插件列表

- `ItemsControl` 绑定 `PluginLabViewModel.InstalledPlugins`
- 每项显示：名称 + 状态（启用/禁用）+ 启用/禁用/删除按钮
- 点击「+ 新建」切换到中栏「AI 生成」模式

### 7.3 中栏：AI 代码生成

- `TextBox` 多行输入需求描述
- `ComboBox` 选择扩展点类型（Phase 1 只有 Page）
- 「生成代码」按钮 → 调 `PluginCompiler.GenerateAsync()`
- 生成过程中显示 spinner + "AI 正在生成代码…" 文案

### 7.4 右栏：预览与安装

- `TextBlock` / `SelectableTextBlock` 显示生成的源码（等宽字体）
- 编译状态：绿色 ✅ / 红色 ❌ + 错误列表
- 「试运行」按钮：编译成功后可点击，临时加载插件（不写入 manifest）
- 「安装插件」按钮：写入 `Plugins/{id}.dll` + 写入 `manifest.json` + 注册到 NavRail

### 7.5 迭代修正流程

```
用户输入描述 → [生成代码] → 编译失败 → 显示错误

右栏错误区域：
  ❌ error CS0246: 找不到类型或命名空间名称 "ObservableObject"
  💡 建议：添加 using CommunityToolkit.Mvvm.ComponentModel;

  [🔄 AI 自动修正]  ← 把错误信息 + 上次代码一起发给 AI
  [✏️ 手动编辑代码]  ← 用户自己改（高级模式）
```

修正按钮实现：将 `{上一次 prompt + 上一次代码 + 编译错误列表}` 作为上下文，追加 `"请修正以下编译错误"` 后重新发给 AI。最多 3 轮，超过则提示用户手动介入。

---

## 8. PluginLabViewModel

### 8.1 属性

| 属性 | 类型 | 用途 |
|------|------|------|
| `InstalledPlugins` | `ObservableCollection<PluginEntryVM>` | 左栏已安装列表 |
| `UserPrompt` | `string` | 中栏用户输入 |
| `SelectedPluginType` | `PluginType` | 选择的扩展点类型 |
| `GeneratedCode` | `string` | 右栏生成的代码 |
| `CompileResult` | `CompileResult?` | 编译结果（成功/失败 + 错误列表） |
| `IsGenerating` | `bool` | 正在生成中（spinner 绑定） |
| `IsCompiled` | `bool` | 编译完成（显示试运行/安装按钮） |

### 8.2 命令

| Command | 行为 |
|---------|------|
| `GenerateCode` | 调 PluginCompiler 生成代码 |
| `FixCode` | 发错误信息给 AI 修正，轮次 +1 |
| `Compile` | 执行 Roslyn 编译 |
| `TryRun` | 临时加载插件到 NavRail（不持久化） |
| `InstallPlugin` | 写 DLL + manifest + 注册 |
| `UninstallPlugin(PluginEntryVM)` | 删 DLL + manifest 移除 + 卸载 |
| `TogglePlugin(PluginEntryVM)` | 启用/禁用 |

### 8.3 状态机

```
空闲 → [生成代码] → 生成中 → AI 返回 → 已生成代码
                                         ├→ [编译] → 编译中 → 编译成功
                                         │                    ├→ [试运行] → 临时启用中
                                         │                    └→ [安装] → 已安装
                                         └→ 编译失败
                                              ├→ [AI 修正] → 生成中（轮次 ≤ 3）
                                              └→ 轮次 > 3 → 提示手动介入
```

---

## 9. 数据流

### 9.1 插件安装（端到端）

```
PluginLabViewModel.InstallPlugin()
  ├─ 1. 检查 Plugins/ 目录存在
  ├─ 2. 写入 Plugins/{pluginId}.dll
  ├─ 3. 读 Plugins/manifest.json
  ├─ 4. 追加新条目 { id, name, version, dll, type, enabled: true }
  ├─ 5. 写回 manifest.json
  ├─ 6. PluginLoader.Load(dllPath) → 返回 IToolerPlugin 实例
  ├─ 7. 按 Type 分类：
  │    ├─ Page → MainWindowVM.RegisterPagePlugin(plugin)
  │    │         └─ NavItems.Add(new NavItem { Id = plugin.Id, Label = plugin.NavLabel, ... })
  │    ├─ Tool → 暂无（Phase 2）
  │    └─ Storage → 暂无（Phase 2）
  └─ 8. 通知用户 "插件 xxx 已安装"
```

### 9.2 启动时恢复

```
MainWindowViewModel.InitializeAsync() 追加：
  ├─ PluginLoader.LoadAll()
  ├─ 按 Type 注册到对应宿主组件
  └─ 对失败的插件：Trace 日志 + 不阻塞启动
```

### 9.3 插件卸载

```
PluginLabViewModel.UninstallPlugin(entry)
  ├─ PluginLoader.Unload(entry.Id)
  ├─ MainWindowVM.UnregisterPagePlugin(entry.Id)
  │    └─ NavItems.Remove(id匹配项)
  ├─ 删除 Plugins/{entry.Id}.dll
  ├─ manifest.json 移除该条目
  └─ 通知用户
```

---

## 10. 边界情况

| 场景 | 处理 |
|------|------|
| 插件 DLL 损坏或被删 | 跳过加载，manifest 中标记 `load_error`，用户可在 PluginLab 看到错误状态并删除 |
| 插件 Id 冲突 | 安装时检查 manifest，已存在则弹出"覆盖/取消"确认 |
| 同一 DLL 包含多个 IToolerPlugin 实现 | 报告错误，不加载（一个 DLL = 一个插件） |
| 插件引用了不存在的程序集 | AssemblyLoadContext 在 Resolving 事件中返回 null，加载失败 |
| 用户删除 Plugins/ 目录 | 启动时重建空目录 + 空 manifest.json |
| NavRail 插件过多（>10 个） | NavRail 改为 ScrollViewer，按钮高度压缩 |
| AI 生成非编译代码 | Roslyn 编译捕获所有错误，回传给 AI 修正 |
| AI API Key 未配置 | 中栏显示"请先在设置页配置 AI 厂商"提示，禁用「生成代码」按钮 |
| 编译时引用的 SDK 程序集版本不匹配 | PluginCompiler 使用 ReflectionOnlyLoad 探测，与实际运行环境一致 |
| 插件引用第三方 NuGet 包 | Phase 1 不支持。AI 生成的代码只能使用已引用的框架程序集 + PluginSDK |

---

## 11. 新增/修改文件清单

### 新增文件

| 文件 | 用途 |
|------|------|
| `StartTooler.PluginSDK/PluginSDK.csproj` | SDK 项目文件 |
| `StartTooler.PluginSDK/IToolerPlugin.cs` | 插件根接口 + PluginType 枚举 |
| `StartTooler.PluginSDK/IPagePlugin.cs` | 页面型扩展点 |
| `StartTooler.PluginSDK/IPluginContext.cs` | 宿主 API 上下文 |
| `StartTooler.PluginSDK/PluginManifest.cs` | manifest 数据模型 |
| `StartTooler/Services/PluginLoader.cs` | 插件加载/卸载 |
| `StartTooler/Services/PluginCompiler.cs` | AI 代码生成 + Roslyn 编译 |
| `StartTooler/Services/PluginContextImpl.cs` | IPluginContext 实现 |
| `StartTooler/ViewModels/PluginLabViewModel.cs` | PluginLab Tab VM |
| `StartTooler/Views/PluginLabView.axaml` | PluginLab Tab UI |
| `StartTooler/Views/PluginLabView.axaml.cs` | 对应 code-behind |
| `StartTooler/Converters/PluginConverters.cs` | 插件状态转换器 |

### 修改文件

| 文件 | 改动 |
|------|------|
| `StartTooler.sln` | 添加 `StartTooler.PluginSDK` 项目 |
| `StartTooler.csproj` | 添加 `PluginSDK` 项目引用 + Roslyn NuGet 包 |
| `ViewModels/MainWindowViewModel.cs` | 废弃 `ViewPage` enum → `CurrentPageId: string`；统一导航命令；添加 `NavItems` 集合；集成 `PluginLoader` |
| `Controls/NavRail.axaml` | 固定按钮 → `ItemsControl` |
| `Controls/NavRail.axaml.cs` | 删除 `CacheButtons()`，改为集合监听 |
| `Views/MainWindow.axaml` | `CurrentView` 绑定改为字典查找 |

---

## 12. 新增 NuGet 依赖

| 包 | 版本要求 | 用途 |
|----|---------|------|
| `Microsoft.CodeAnalysis.CSharp` | ≥ 4.12.0 | Roslyn 编译 |
| `Microsoft.CodeAnalysis.Common` | ≥ 4.12.0 | 编译基础设施 |
| `System.Reflection.MetadataLoadContext` | ≥ 9.0.0 | 插件 DLL 元数据只读扫描 |

---

## 13. 实施分阶段计划

### Phase 1（本设计范围）：页面型插件 + PluginLab

| 步骤 | 内容 | 预估风险 |
|------|------|---------|
| 1 | 建 `StartTooler.PluginSDK` 项目，定义全部接口 | 低 |
| 2 | 重构 `NavRail` + `MainWindowViewModel` 导航系统 | **高**（涉及所有页面切换逻辑） |
| 3 | 写 `PluginLoader`（AssemblyLoadContext） | 中 |
| 4 | 写 `PluginCompiler`（Roslyn + AI prompt 模板） | 中（prompt 调优迭代） |
| 5 | 实现 `PluginContextImpl`（宿主 API 注入） | 低 |
| 6 | 建 `PluginLabView` + `PluginLabViewModel` | 中 |
| 7 | 端到端集成测试：用户描述 → AI 生成 → 编译 → 安装 → 运行 | — |

### Phase 2（后续）：工具型 + 存储后端插件

- `IToolPlugin`：右键菜单注册、工具栏按钮
- `IStoragePlugin`：`IOssStorage` 接口升级为插件扩展点

### Phase 3（远期）：插件市场

- 在线插件仓库
- 一键安装
- 版本更新检测

---

## 14. 与现有系统的关系

### 14.1 不修改现有页面

Gallery / Settings / UploadServer / Trash 四个内置页逻辑不变，仅注册方式从 `ViewPage` 枚举改为 `NavItem` 列表。它们是 `IsBuiltIn = true` 的特殊条目。

### 14.2 复用现有 AI 调用链路

`PluginCompiler` 复用 `AITagger` 的 `SendOpenAIAsync` / `SendAnthropicAsync` 方法（或抽取公共 `AIApiClient`）。不重复造 HTTP 轮子。

### 14.3 不引入 DI 容器

Phase 1 保持手动构造依赖（与现有 `MainWindowViewModel` 构造函数风格一致）。`IPluginContext` 的实现直接传已有服务实例。

### 14.4 不修改 MediaFile / 数据库 Schema

插件系统不引入新 DB 表。插件元数据存在 `Plugins/manifest.json`（纯文件），插件配置通过 `IPluginContext.GetConfigAsync` 存在 `config.db` 的 `plugin.{id}.xxx` 键下。
