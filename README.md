# StartTooler（星助）

> 天文摄影师的本地照片管家 —— 局域网 / 公网手机上传 + OSS 云端备份，一站式星空后期工作流。

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-11.3-darkblue.svg)](https://avaloniaui.net/)

---

## 功能概览

| 功能 | 说明 |
|---|---|
| **媒体时间轴** | 按拍摄日期自动归档，图片缩略图 + 视频首帧预览 |
| **OSS 云端备份** | 支持阿里云 OSS 私有桶上传/下载，单文件 PUT 与分片上传自动切换（断点续传） |
| **局域网手机上传** | 一键启动局域网 HTTP 服务器，生成二维码，手机扫码即可上传照片到当前项目 |
| **公网远程上传** | 自研 Go 中转服务，部署到 VPS 后手机可通过公网 URL 上传，桌面端自动拉取 |
| **AI 智能标注** | 多厂商 LLM 支持（Claude / GPT / Gemini / DeepSeek / 通义千问 / Kimi / 智谱），自动为照片打标签、评质量分数 |
| **回收站** | 软删除机制，支持恢复或彻底删除，同步管理 OSS 远端文件 |
| **双主题** | 「深空」暗色主题 + 「红光夜视」护眼主题，适配野外拍摄场景 |

---

## 技术栈

| 类别 | 选型 | 版本 |
|---|---|---|
| 语言 / 运行时 | C# (.NET) | 9.0 |
| UI 框架 | Avalonia（跨平台桌面） | 11.3.11 |
| MVVM 工具 | CommunityToolkit.Mvvm | 8.4.0 |
| 图像处理 | SkiaSharp | 2.88.9 |
| 本地数据库 | Microsoft.Data.Sqlite | 8.0.11 |
| 对象存储 | Aliyun.OSS.SDK.NetCore | 2.14.1 |
| QR 码生成 | QRCoder | 1.6.0 |
| SSH 通信 | SSH.NET | 2024.2.0 |
| TOML 解析 | Tomlyn | 2.9.0 |
| 公网中转 | 自研 Go（纯标准库） | 1.23+ |
| 视频处理 | FFmpeg / FFprobe（用户自行配置） | — |

**目标平台**：macOS（主要） / Windows 11 / Linux

---

## 快速开始

### 前置要求

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Go 1.23+](https://go.dev/dl/)（可选：仅编译公网中转二进制时需要，缺失时不阻塞 `dotnet build`）
- FFmpeg / FFprobe（在应用设置页面配置路径，或加入系统 PATH）

### 编译运行

```bash
# 克隆仓库
git clone git@github.com:fishhex/StartTooler.git
cd StartTooler

# 编译（自动触发 Go relay 交叉编译）
dotnet build StartTooler/StartTooler.csproj

# 运行
dotnet run --project StartTooler/StartTooler.csproj
```

### 发布

```bash
# macOS ARM64 自包含发布
dotnet publish StartTooler/StartTooler.csproj -c Release -r osx-arm64

# macOS x64
dotnet publish StartTooler/StartTooler.csproj -c Release -r osx-x64

# Windows x64
dotnet publish StartTooler/StartTooler.csproj -c Release -r win-x64
```

---

## 用户指南

### 基础流程

1. **设置项目目录**：打开「设置」→ 选择存放天文照片的文件夹
2. **扫描媒体**：返回「媒体库」，点击扫描按钮，自动索引图片和视频
3. **浏览时间轴**：左侧按拍摄日期分组，右侧网格显示缩略图
4. **管理文件**：右键菜单支持「打开文件」「在文件夹中显示」；多选后批量上传 OSS / 下载 / 删除

### 局域网上传

1. 切换到「上传」标签页 → 「局域网接收」
2. 点击「启动服务」，生成二维码
3. 手机连接同一 WiFi，扫描二维码访问上传页面
4. 手机选取照片上传，文件直接存入当前项目目录

### 公网远程上传

1. 在「上传」→「公网中转」中配置 VPS SSH 信息
2. 点击「部署中转服务」，自动将 Go relay 二进制上传到 VPS 并启动
3. 手机通过公网 URL 访问上传页面，文件经 VPS 中转 → 桌面端自动 SSH-SCP 拉取到本地

### OSS 云备份

1. 「设置」→ 配置阿里云 OSS（Region、Bucket、AccessKey 等）
2. 媒体库中选择文件，点击「上传到 OSS」
3. 大文件自动分片上传，支持断点续传（应用重启后继续）
4. 已上传文件支持「从 OSS 下载」「从 OSS 删除」

### AI 自动标注

1. 配置 AI 服务商（设置页面 → `ai-providers.toml` 或 UI 配置）
2. 媒体库中选中照片，点击工具栏「AI 标注」按钮
3. AI 自动识别照片主体内容并生成标签、质量评分

支持的 AI 厂商：

- Anthropic Claude（原生 API）
- OpenAI GPT（兼容 API）
- Google Gemini
- DeepSeek
- 通义千问（阿里云百炼）
- 月之暗面 Kimi
- 智谱 GLM
- 自定义 OpenAI/Anthropic 兼容端点

---

## 项目结构

```
StartTooler/
├── StartTooler.sln                          # 解决方案文件
├── StartTooler/                             # .NET 主项目
│   ├── Program.cs                           # 入口，Trace 日志配置
│   ├── App.axaml / App.axaml.cs              # 应用生命周期，主题加载
│   ├── Assets/                              # 图标资源
│   ├── Components/                          # 自定义控件（ScanProgressBar）
│   ├── Controls/                            # 可复用控件（NavRail, StatusLegend）
│   ├── Converters/                          # 15 个 IValueConverter
│   ├── Data/                                # 数据仓储层（SQLite：MediaRepository, UploadJobRepository, ConfigService）
│   ├── Helpers/                             # DialogHelper（确认/警告弹窗）
│   ├── Models/                              # UI 模型、枚举、TimelineEntry
│   ├── Resources/                           # 嵌入资源
│   │   ├── upload.html                      # 上传页面模板（单源）
│   │   ├── ai-providers.default.toml        # AI 厂商默认配置
│   │   └── relay-binaries/                  # 预编译 Go relay 二进制 (linux-amd64, linux-arm64)
│   ├── Services/                            # 28 个业务服务
│   │   ├── ConfigService.cs                 # 键值配置存储
│   │   ├── MediaRepository.cs               # 媒体文件索引 CRUD
│   │   ├── ThumbnailService.cs              # 缩略图生成与缓存
│   │   ├── FfmpegSnapshotRunner.cs          # 视频首帧提取
│   │   ├── FfprobeRunner.cs                 # 视频元数据提取
│   │   ├── AliyunOssStorage.cs              # OSS 上传/下载/删除
│   │   ├── UploadServerService.cs           # 局域网 HTTP 上传服务
│   │   ├── PublicRelayService.cs            # 公网中转（C# 客户端）
│   │   ├── AITagger.cs                      # AI 自动标注引擎
│   │   ├── AIProviderLoader.cs              # AI 厂商配置加载
│   │   └── ...
│   ├── Themes/                              # 主题系统（Colors, Styles, RedNightVision, Icons）
│   ├── ViewModels/                          # 6 个 ViewModel
│   │   ├── MainWindowViewModel.cs           # 应用壳，页面导航
│   │   ├── GalleryViewModel.cs              # 媒体库核心逻辑
│   │   ├── SettingsViewModel.cs             # 设置页配置管理
│   │   ├── UploadServerViewModel.cs         # 局域网上传控制
│   │   ├── PublicRelayViewModel.cs          # 公网中转控制
│   │   └── TrashViewModel.cs                # 回收站管理
│   └── Views/                               # 6 个 XAML 视图
│       ├── MainWindow.axaml
│       ├── GalleryView.axaml
│       ├── SettingsView.axaml
│       ├── UploadServerView.axaml
│       ├── TrashView.axaml
│       └── NotificationCard.axaml
├── tools/
│   └── upload-relay/                        # Go 公网中转服务
│       ├── main.go                          # HTTP + TCP 双服务器
│       ├── go.mod
│       └── web/index.html
├── scripts/
│   ├── build-relay.sh                       # macOS/Linux Go 交叉编译脚本
│   └── build-relay.ps1                      # Windows 版本
├── doc/                                     # 工程规范文档（15 篇）
│   ├── README.md                            # 文档索引
│   ├── 00-project.md                        # 总体架构
│   ├── 01-app-bootstrap.md                  # 启动与进程模型
│   ├── 02-data-layer.md                     # 数据层设计
│   ├── 03-media-pipeline.md                 # 媒体处理管线
│   ├── 04-oss-upload.md                     # OSS 上传机制
│   ├── 05-gallery-view.md                   # 媒体库视图
│   ├── 06-settings.md                       # 设置模块
│   ├── 07-upload-server-lan.md              # 局域网上传
│   ├── 08-public-relay.md                   # 公网中转
│   ├── 09-ui-commons.md                     # UI 通用组件
│   ├── 10-trap-book.md                      # 已知陷阱与决策记录
│   ├── 11-ai-tagging.md                     # AI 标注
│   ├── 12-ai-toolbar-buttons.md             # AI 工具栏按钮
│   ├── 13-tag-quality-split.md              # 标签/质量拆分
│   ├── 14-delete-and-trash.md               # 回收站
│   └── draft/                               # 历史设计稿存档
├── publish/                                 # dotnet publish 产物
└── dist/                                    # 分发包
```

---

## 架构概要

```
Views (AXAML) ── x:Bind ──► ViewModels (CommunityToolkit.Mvvm)
                                │
              ┌─────────────────┼─────────────────┐
              ▼                 ▼                 ▼
        Data/Repos          Services           Helpers
        (SQLite)          (业务逻辑)          (Dialog)
```

- **数据存储**：配置数据（`config.db`）与媒体索引（`media.db`）分离，存放于平台应用数据目录
  - macOS: `~/Library/Application Support/StartTooler/`
  - Linux: `~/.local/share/StartTooler/`
  - Windows: `%LocalAppData%\StartTooler\`
- **MVVM 模式**：所有 ViewModel 使用 `[ObservableProperty]` + `[RelayCommand]` 源码生成器
- **异步优先**：所有 `Process.Start` 调用包装在 `Task.Run` 中，避免阻塞 UI 线程
- **错误隔离**：扫描/上传/下载均按单文件 try/catch，单个失败不影响批次

---

## 许可证

[MIT](LICENSE) © 2026 fishhex

---

## 开发文档

详细的工程规范请参阅 [`doc/`](doc/) 目录。推荐阅读顺序：

| 身份 | 顺序 |
|---|---|
| 首次接触本项目 | `00` → `01` → `02` → `05` → `08` |
| 做 OSS / 上传相关 | `00` → `02` → `04` → `05` |
| 做公网中转相关 | `08` → `01` → `05` |
| 修改 UI / 样式 | `00` → `09` → `06` |
| 排查历史问题 | `10` 起 |
