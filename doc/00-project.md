# 00 — 项目总体规范

> **来源**：直接对应仓库代码现状（2026-06-30）。所有 `file:line` 引用是规范的一部分，doc 与 code 必须同步。

---

## 1. 一句话定位

> StartTooler 是**天文摄影师的本地照片管家 + 局域网/公网手机端上传 + OSS 备份** 桌面应用。

核心工作流：用户选择项目目录 → 递归扫描 → 按拍摄日期归档到左栏时间轴 → 显示缩略图（图片 / 视频首帧）→ 单选「打开文件」/「在文件夹中显示」/「下载到本地」→ 多选「上传到 OSS」→ 启动「局域网 / 公网 Web 接收」让手机扫码上传到当前项目。

---

## 2. 技术栈（实际依赖，参见 `StartTooler/StartTooler.csproj`）

| 类别 | 选型 | 版本 |
|---|---|---|
| UI 框架 | Avalonia（Desktop）| 11.3.11 |
| MVVM | CommunityToolkit.Mvvm | 8.4.0 |
| 运行时 | .NET | 9.0 |
| 图像处理 | SkiaSharp | 2.88.9（含 macOS 原生资产） |
| 本地数据库 | Microsoft.Data.Sqlite | 8.0.11 |
| 对象存储 | Aliyun.OSS.SDK.NetCore | 2.14.1 |
| 二维码 | QRCoder | 1.6.0 |
| SSH | SSH.NET | 2024.2.0 |
| **公网中转** | **自研 Go**（`tools/upload-relay`） | go 1.23，纯标准库 |

跨平台：macOS（主）/ Windows 11 / Linux。**FFmpeg / FFprobe 由用户在设置页配置**或走 PATH，不是项目依赖。

---

## 3. 仓库结构

```
StartTooler/                        # .sln
├── StartTooler/                    # .NET 主项目
│   ├── App.axaml(.cs)              # 启动 + 主题加载
│   ├── Program.cs                  # WinExe entry，Trace→文件
│   ├── StartTooler.csproj          # 包引用 + EmbeddedResource + BeforeBuild 调 Go build
│   ├── Assets/                     # 图标 + 字体
│   ├── Components/                 # ScanProgressBar（自定义控件）
│   ├── Controls/                   # NavRail / StatusLegend
│   ├── Converters/                 # 12 个 IValueConverter
│   ├── Data/                       # 仓储层（SQLite）
│   ├── Helpers/                    # DialogHelper
│   ├── Models/                     # UI 模型 + ScanProgress + 枚举
│   ├── Resources/                  # upload.html + relay-binaries/ + 编译时嵌入
│   ├── Services/                   # 28 个业务服务
│   ├── Themes/                     # Colors / Icons / Styles / RedNightVision
│   ├── ViewModels/                 # Main + Gallery + Settings + UploadServer + PublicRelay
│   └── Views/                      # MainWindow + Gallery/Settings/UploadServer + NotificationCard
├── tools/
│   └── upload-relay/               # Go 服务（公网接收端）
│       ├── main.go                 # HTTP + TCP 双 server
│       ├── go.mod                  # module github.com/starttooler/upload-relay
│       └── web/index.html          # 由 build-relay 自动同步自 Resources/upload.html
├── scripts/
│   ├── build-relay.sh              # mac/linux，bash
│   └── build-relay.ps1             # windows，PowerShell
├── doc/                            # 本规范的归属目录
│   ├── README.md                   # 索引
│   ├── 00..10*.md                  # 总体 + 11 篇模块规范
│   └── draft/star-helper-spec*.md  # 旧 UI 设计规格，保留为决策档案
├── publish/                        # dotnet publish 产物
└── dist/                           # 其它分发产物
```

---

## 4. 架构总览（一张图走全流程）

```
┌─────────────────────────── macOS / Windows / Linux 桌面 ───────────────────────────┐
│                                                                                     │
│  ┌────────────────────── MainWindow.axaml (View 层，3 页切换) ─────────────────────┐ │
│  │   NavRail    │   GalleryView        SettingsView        UploadServerView       │ │
│  │   (媒体/     │   (时间轴 +          (项目目录 +         (局域网 QR +           │ │
│  │    设置/     │    缩略图网格 +       OSS + FFmpeg +      公网 relay 配置 +     │ │
│  │    上传)     │    多选/上传)        主题)                部署/启停)             │ │
│  └────────────────────┬─────────────────────────┬─────────────────────┬─────────────┘ │
│                       │ DataBinding (x:Bind)  │                     │               │
│  ┌────────────────────▼─────────────────────────▼─────────────────────▼─────────────┐ │
│  │                              ViewModel 层 (CommunityToolkit)                       │ │
│  │   MainWindowViewModel   GalleryViewModel   SettingsViewModel   UploadServerVM     │ │
│  │                                                  ↕                  + PublicRelayVM│ │
│  └───────┬──────────────┬──────────────┬───────────┬──────────────┬─────────────────┘ │
│          │              │              │           │              │                   │
│  ┌───────▼─────────┐ ┌──▼───────────┐ ┌▼────────┐ ┌▼────────────┐ ┌▼──────────────┐ │
│  │ Data / Repos    │ │ Services     │ │ Helpers │ │ 公共服务     │ │ UI 通用件       │ │
│  │ IMediaRepo      │ │ Thumbnail    │ │ Dialog  │ │ ConfigSvc    │ │ Components     │ │
│  │ MediaRepo(SQLite│ │ Ffmpeg/      │ │ Helper  │ │ FilePicker   │ │ Controls       │ │
│  │ UploadJobRepo   │ │ FfprobeRun   │ │         │ │ DirPicker    │ │ Converters     │ │
│  │ ConfigService   │ │ ImageCache   │ │         │ │ ShellService │ │ Themes         │ │
│  │                 │ │              │ │         │ │ ThemeManager │ │ NotificationSrv│ │
│  │                 │ │ OssStorage   │ │         │ │ FFmpegConfig │ │                │ │
│  │                 │ │ + Factory    │ │         │ │ RelayExtr.   │ │                │ │
│  └─────────────────┘ └──────┬───────┘ └─────────┘ └────┬─────────┘ └────────────────┘ │
│                              │                         │                            │
│                              │                         │                            │
│   ┌──────────────────────────▼─────────────────────────▼──────────────────────────┐  │
│   │                              文件系统 + 平台 API                                │  │
│   │   SQLite DB  │  ffmpeg/ffprobe  │  Aliyun OSS SDK  │  SSH.NET  │  Avalonia tray  │  │
│   └──────────────────────┬──────────────────────────────────────────────────────┘   │
│                          │                                                              │
└──────────────────────────┼──────────────────────────────────────────────────────────────┘
                           │
                           │ HTTPS (OSS / 签名 URL)         │ SSH + SCP                   │
                           ▼                                 ▼                             │
                ┌─────────────────────┐           ┌──────────────────────────┐           │
                │  Aliyun OSS (远程)   │           │  VPS 用户部署的           │           │
                │  私有 bucket         │           │  upload-relay (Go)        │           │
                │  multipart + 单 PUT   │           │  - HTTP: 上传页 + /ack    │           │
                └─────────────────────┘           │  - TCP:  file_pending 通知│           │
                                                  └──────────────┬───────────────┘           │
                                                                 │                            │
                                                                 │ file_pending (JSON line)   │
                                                                 ▼                            │
                                                         ┌──────────────┐                     │
                                                         │   手机浏览器  │ ←─ 局域网 IP ────┘   │
                                                         │   / 公网 URL  │                        │
                                                         └──────────────┘                        │
                                                                                                  │
                                              （局域网 IP = HTTP, 公网 = HTTP 中转）             │
                                                                                                  │
──────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 5. 模块清单（按职责）

| # | 模块名 | 关键文件 | 对应 spec |
|---|---|---|---|
| 1 | **启动与进程模型** | `Program.cs`、`App.axaml.cs`、`App.axaml` | `01-app-bootstrap.md` |
| 2 | **主题** | `Services/ThemeManager.cs`、`Themes/*` | `01` + `09-ui-commons.md` |
| 3 | **配置服务（Kv 存储）** | `Services/ConfigService.cs`、`IConfigService.cs`、`ConfigKeys.cs` | `02-data-layer.md` |
| 4 | **媒体仓储** | `Data/MediaRepository.cs`、`IMediaRepository.cs`、`MediaFile.cs`、`DateCount.cs`、`Models.cs` | `02` |
| 5 | **上传任务仓储** | `Data/UploadJobRepository.cs`、`UploadJob.cs` | `02` |
| 6 | **FFmpeg / FFprobe** | `Services/FFmpegConfigurator.cs`、`FfprobeRunner.cs`、`FfmpegSnapshotRunner.cs` | `03-media-pipeline.md` |
| 7 | **缩略图** | `Services/ThumbnailService.cs`、`ImageCacheService.cs` | `03` |
| 8 | **对象存储（OSS）** | `Services/IOssStorage.cs`、`AliyunOssStorage.cs`、`OssStorageFactory.cs`、`OssConfig.cs` | `04-oss-upload.md` |
| 9 | **系统 Shell / 文件选择器** | `ISystemShellService.cs`、`SystemShellService.cs`、`I(Directory|File)PickerService.cs` | `09-ui-commons.md` |
| 10 | **通知 / 对话框** | `NotificationService.cs`、`NotificationTypeToBrushConverter.cs`、`Helpers/DialogHelper.cs` | `09` |
| 11 | **UI 通用件** | `Components/*`、`Controls/*`、`Converters/*` | `09` |
| 12 | **MainWindowViewModel** | `ViewModels/MainWindowViewModel.cs` | `01` |
| 13 | **GalleryViewModel** | `ViewModels/GalleryViewModel.cs` | `05-gallery-view.md` |
| 14 | **SettingsViewModel** | `ViewModels/SettingsViewModel.cs` | `06-settings.md` |
| 15 | **局域网上传服务** | `Services/UploadServerService.cs`、`UploadServerViewModel.cs` | `07-upload-server-lan.md` |
| 16 | **公网代理服务（C# 端）** | `Services/PublicRelayService.cs`、`PublicRelayConfig.cs`、`PublicRelayViewModel.cs`、`RelayBinaryExtractor.cs` | `08-public-relay.md` |
| 17 | **Go relay 服务端** | `tools/upload-relay/main.go`、`web/index.html` | `08` |
| 18 | **构建脚本 + 资源嵌入** | `scripts/build-relay.{sh,ps1}`、`StartTooler.csproj BeforeBuild` | `01`（交叉引用） |

---

## 6. 跨模块契约（必须遵守）

> 散落在多个模块的铁律，新人/AI 写代码前先看这里。

### 6.1 持久化与路径

- **所有 DB 路径走 `Environment.SpecialFolder.LocalApplicationData`**（见 `Data/MediaRepository.cs:23-29`、`Data/UploadJobRepository.cs:23-29`、`Services/ConfigService.cs:16-27`）。
  - macOS：   `~/Library/Application Support/StartTooler/`
  - Linux：   `~/.local/share/StartTooler/`
  - Windows：`C:\Users\<u>\AppData\Local\StartTooler\`
- **设置走 `Config.db`**（单表 KV JSON），**业务数据走 `media.db`**（两个表：`media_files` + `upload_jobs`）。两者不混。
- **`project_path` 列必须 `Path.GetFullPath(...).TrimEnd(DirectorySeparatorChar)`**（`MediaRepository.cs:70/111/233/297`）—— 写到 DB 前规范化，读取后再规范化，**保证 round-trip 一致**。这条铁律破过一次，体现在 `10-trap-book`。

### 6.2 数据模型

- **DB 持久化字段**：`media_files.{is_uploaded, uploaded_at, remote_url, thumbnail_path, ...}` —— `IMediaRepository.UpdateUploadStateAsync` 唯一写入入口。
- **UI 瞬时字段**：`MediaFile.{UploadStatus, UploadError, IsSelected, LocalExists, ThumbnailPath}`（`[ObservableProperty]`）—— 入 Gallery 时从 `upload_jobs` 反推 `UploadStatus`，绝不入 DB。
- **`MediaFile.IsSelected` 单向同步**：通过 `SelectedFiles.CollectionChanged`（`GalleryViewModel.cs:88-115`）由 Gallery 集中控制，反向不要维护。
- **UploadStatus 状态机**（`Data/MediaFile.cs:21-28`）：
  ```
  NotUploaded → Uploading → Uploaded       (happy)
  NotUploaded → Uploading → Failed         (失败)
  Uploading   → Paused                     (app 退出/job 留底)
  Paused      → Uploading → ...            (续传)
  ```

### 6.3 异步与线程

- **任何** `Process.Start`（ffmpeg/ffprobe/ssh/explorer/open）必须包在 `Task.Run` 里——同步阻塞调用会让 UI 线程卡死。`FfmpegSnapshotRunner.cs:68`、`FfprobeRunner.cs:53`、`SystemShellService.cs:33+`。
- **RelayCommand CanExecute**：每次改 `CanExecute` 输入字段（`IsRunning`、`IsBusy`、`IsProjectPathSet`），**必须** 调 `Command.NotifyCanExecuteChanged()`，否则按钮永远 disabled。例子：`UploadServerViewModel.cs:24-26`、`PublicRelayViewModel.cs:126-128`。
- **取消令牌**：每个长任务（扫描、上传、TCP 循环、HTTP listener）都接收 `CancellationToken`；UI 取消通过 `CancellationTokenSource.Cancel()`。

### 6.4 错误隔离

- **扫描循环**：每个文件 try/catch，**单文件失败不阻塞其他文件**（`MediaRepository.cs:239-279`）。
- **缩略图生成**：单文件失败返回 `null`，调用方处理（`ThumbnailService.cs:65-70`）。
- **上传循环**：失败累计到 `errors` 列表，最后一次性 toast + 弹窗（`GalleryViewModel.cs:602-684`）。
- **TCP 长连接**：异常断线捕获，**指数退避重连**（1s → 2s → 4s → ... 上限 10s），不复接 socket（`PublicRelayService.cs:271-342`）。

### 6.5 用户反馈

- **临时反馈**：用 `NotificationService.Current.Show(title, body, type)`，5 秒自动消失（`NotificationService.cs:45-59`）。
- **重要决策点**：用 `DialogHelper.ShowConfirmAsync`，主窗口阻塞（`Helpers/DialogHelper.cs:24-102`）。
- **错误汇总 / 不可恢复**：`DialogHelper.ShowAlertAsync`，列出每个失败项（`DialogHelper.cs:124-179`）。
- **UI 瞬时状态**：VM 用 `ToastMessage`（Set → 3s 后 Set null）；不要用 NotificationService 干这事。

### 6.6 配置 / 凭据 / 安全

- **凭据明文持久化**（v0.1）：`OssConfig.AccessKeySecret` 写到 `Config.db`。下次应迁到 Keychain / DPAPI（`AliyunOssStorage.cs:20-22` 自承认账）。
- **SSH 凭据**：密码 / Key 路径同样明文（`PublicRelayConfig.cs:11-14`），走 `SSH.NET` 的 `PrivateKeyAuthenticationMethod` 或 `PasswordAuthenticationMethod`（`PublicRelayService.cs:48-63`）。
- **可见性**：UI 上的 `PasswordChar="•"`，但 OTel 里仍可见——别把测试凭据放到生产配置里。

### 6.7 跨平台行为

- **默认 Shell**：
  - macOS：   `open -R <path>`（高亮）/ `open <path>`（默认 app）
  - Windows： `explorer /select,<path>` / `ShellExecute=true`
  - Linux：   `xdg-open <dir>`（不亮，只能打开所在目录）
  见 `Services/SystemShellService.cs:14-136`。
- **HTTP listener 前缀**：统一用 `http://+:<port>/`（所有网卡可访问）；Windows 上需 `netsh http add urlacl`（`UploadServerService.cs:55-58`），mac/linux 不需要。
- **FFmpeg 路径**：空字符串 = 走 PATH；非空必须是文件，不能是目录（`SettingsViewModel.cs:336-365` 验证）。

### 6.8 资产嵌入

- **Go relay 二进制**：以 `EmbeddedResource` 嵌入到 dll，运行时由 `RelayBinaryExtractor` 解压到 `{Temp}/starttooler/upload-relay-linux-{arch}`（`StartTooler.csproj:25-34` + `Services/RelayBinaryExtractor.cs:37-63`）。
- **upload.html 单源**：仓库根 `StartTooler/Resources/upload.html` 同时是 `UploadServerService` 模板和 Go relay `//go:embed` 源；`build-relay.sh:27-35` 自动同步到 `tools/upload-relay/web/index.html`。

### 6.9 协议契约（公网代理）

- **HTML → Go**：浏览器 POST `/upload`（multipart）。
- **Go → C# (TCP JSON line)**：每行一条 JSON `{"type":"file_pending","id":"...","name":"...","size":N}\n`。**无 4 字节长度前缀**，C# 端 `ReadLineAsync` 按行读（`PublicRelayService.cs:286-301`）。
- **C# → Go (HTTP)**：`POST /ack/{id}`，Go 删 pending + rm tmp 文件（`main.go:Ack`）。
- **C# → Go (SSH scp)**：`{remote}/tmp/{sanitized_original_name}` 拉本地（v0.4+ 用原文件名直接落盘，`PublicRelayService.cs:529-535`）。

---

## 7. 构建与运行

```bash
# mac/linux：dotnet build 会自动调 scripts/build-relay.sh 编译 Go 二进制（BeforeBuild 钩子）
dotnet build StartTooler/StartTooler.csproj

# 单独手动编译 Go relay（无 Go 工具链也不阻塞，但二进制会陈旧）
bash scripts/build-relay.sh

# Windows 上 .NET SDK 会自动调 scripts/build-relay.ps1
dotnet build StartTooler.sln
```

运行：

```bash
dotnet run --project StartTooler/StartTooler.csproj
# 或直接跑 publish 出来的可执行文件
```

详细参见 `01-app-bootstrap.md` §「BeforeBuild 钩子行为」。

---

## 8. 代码组织约定

- **namespace**：根 `StartTooler`，子命名空间按文件夹分类（`StartTooler.Data`、`StartTooler.Services`、`StartTooler.ViewModels`、`StartTooler.Views`、`StartTooler.Controls`、`StartTooler.Converters`、`StartTooler.Components`、`StartTooler.Helpers`、`StartTooler.Models`）。
- **接口 → 实现 同文件夹**：`IFoo.cs` 与 `Foo.cs` 放一起。接口给 VM / 跨模块消费用；测试用 mock 也方便。
- **`partial class` 给 CommunityToolkit MVVM 生成器**：所有 VM 都 `[ObservableProperty] private ...` + `[RelayCommand]` partial（生成器会自动生成 public 属性 / Command）。
- **`IValueConverter` 用 `Instance` 静态字段**：`public static readonly FooConverter Instance = new();`——XAML 直接 `Converter={x:Static converters:FooConverter.Instance}`（例子：`NotificationTypeToBrushConverter.cs:15`）。
- **Trace 写日志**：项目跑在 WinExe 下无 console，**所有诊断日志走 `System.Diagnostics.Trace.WriteLine(...)`，不要 `Console.WriteLine`**。`Program.cs:14-25` 已把 Trace 改写到 `cwd/starttooler-debug.log`。
- **`Debug.WriteLine`**：只在调试输出窗口可见，**不取代 Trace**。CI 跑测试拿不到。

---

## 9. 不做清单（v0.1 明确范围外）

| 主题 | 不做 / 待 v0.2+ |
|---|---|
| OSS 凭据加密 | Keychain / DPAPI；当前 SQLite 明文 |
| 多端同步 | 多设备间的相册列表同步 |
| OSS 多云 | 仅阿里云实现，`IOssStorageFactory.TryCreate` 已留入口（`OssStorageFactory.cs:42-46`） |
| 视频编辑 / 转码 | 只生成单帧缩略图，不做编码 |
| 图层 / 调色 | 仅展示，不编辑 |
| 国际化 | 中文为主；EN 待定 |
| OSS 桶类型 | 仅私有（Upload 走服务端凭据，Download 走签名 URL） |

---

## 10. 看到这个文档的人…

- **改代码**：先 `00` 看整体 → 跳到对应模块 → `10-trap-book.md` 确认不踩旧坑
- **加新模块**：参考 `05-gallery-view.md` 的「VM → Service → Repository → DB」分层
- **遇到奇怪的报错**：`10-trap-book.md` 大概率已有记录

任何变更请同步更新对应 spec 文件，别让 doc 跟代码漂移。
