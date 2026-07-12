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
| **灯箱预览** | 双击图片进入大图预览，支持缩放、翻页、全屏、标签编辑 |
| **OSS 云端备份** | 支持阿里云 OSS 私有桶上传/下载，单文件 PUT 与分片上传自动切换（断点续传） |
| **局域网手机上传** | 一键启动局域网 HTTP 服务器，生成二维码，手机扫码即可上传照片到当前项目 |
| **公网远程上传** | 自研 Go 中转服务，部署到 VPS 后手机可通过公网 URL 上传，桌面端自动拉取 |
| **AI 智能标注** | 多厂商 LLM 支持（Claude / GPT / Gemini / DeepSeek / 通义千问 / Kimi / 智谱），自动为照片打标签、评质量分数 |
| **手动标签编辑** | 单文件 / 灯箱内 / 批量三种方式编辑标签，支持替换、追加、删除作用域 |
| **回收站** | 软删除机制，支持恢复或彻底删除，提供撤销窗口，同步管理 OSS 远端文件 |
| **双主题** | 「深空」暗色主题 + 「红光夜视」护眼主题，适配野外拍摄场景 |

---

## 版本里程碑

### v0.1 — 基石

搭建项目骨架，确立 MVVM 架构、SQLite 数据层、Avalonia UI 框架。

- 媒体时间轴：按拍摄日期归档，缩略图网格浏览
- 本地文件扫描：递归目录，自动索引图片/视频，生成缩略图
- OSS 云端上传：阿里云 OSS 私有桶，单文件 PUT + 大文件分片断点续传
- 局域网手机上传：内置 HTTP 服务器 + 二维码，手机扫码即传
- 设置页基础：项目目录、主题切换（深空 / 红光夜视）、FFmpeg 路径配置

### v0.11 — 体验

完善核心交互链路，补齐操作反馈与配置迁移能力。

- 灯箱预览：非模态大图预览窗口，鼠标滚轮缩放、方向键翻页、全屏、幻灯片
- 设置页增强：配置导出/导入（JSON 含密钥）、AI/OSS 连接测试、保存反馈（已保存 ✓）、未保存离开三选守卫、数据目录说明
- 上传体验：扫描/上传/打标进度通知卡片、启动时自动探测未完成任务并提示续传
- 回收站增强：云端/本地两段分区、彩色顶条区分、卡片信息增强（删除日期 + 文件大小）、多选批量操作、撤销窗口、容量统计、恢复跳转
- 导航增强：Ctrl+1~4 数字键切换页面、返回箭头、标题栏动态标题、状态栏（OSS 配置状态 + 上次刷新时间）

### v0.12 — 生态（开发中）

跨设备协作与可扩展性。

| 功能 | 状态 | 文档 |
|------|------|------|
| 跨设备云端同步 | 设计中 | [demand](doc/0.12/demand/02-cross-device-sync.md) · [spec](doc/0.12/spec/01-cross-device-sync.md) |
| 星图媒体墙 | 需求 | [demand](doc/0.12/demand/01-star-map-media-wall.md) |
| 插件系统 | 规划 | [demand](doc/0.12/demand/01-plugin-system.md) |

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
4. **管理文件**：右键菜单支持「打开文件」「在文件夹中显示」「AI 打标」「编辑标签」「删除」等操作；多选后批量上传 OSS / 下载 / 删除 / AI 打标 / 编辑标签

### 媒体库浏览

**分类与排序**

- **时间轴（左侧 Tab 1）**：按拍摄日期自动分组，每个节点显示日期和照片数量
- **标签分类（左侧 Tab 2）**：按 AI 标签分组，可快速筛选某一类天体（星云、星系、行星等）
- **排序方式**：工具栏下拉框可选「时间 ↓」（默认）或「评分 ↓」，切换后自动重新加载

**文件卡片状态徽章**

每张卡片右上角显示 OSS 同步状态：

| 徽章 | 含义 |
|------|------|
| 青蓝云 + 对勾 | 已上传且本地存在 |
| 橙色云 + 下载箭头 | 已上传但本地缺失（可重新下载） |
| 灰色云 + 斜杠 | 未上传 |
| 闪烁青色云 | 上传中 |
| 红色云 + 感叹号 | 上传失败 |
| 黄色云 | 有待恢复的续传任务 |

卡片左下角显示 AI 评分（渐变色彩），底部显示标签条（主体标签白色底，质量标签暖红色底）。

**右键菜单**

在网格中右键点击任意文件卡片：

| 菜单项 | 说明 |
|--------|------|
| 打开文件 | 使用系统默认程序打开 |
| 在文件夹中显示 | 在资源管理器中定位文件 |
| AI 打标 | 对单个文件执行 AI 自动标注 |
| 编辑标签 | 手动编辑该文件的标签 |
| 删除 | 移入回收站 |
| 释放本地空间 | 删除本地文件，云端保留（仅已上传文件可见） |
| 下载到本地 | 从 OSS 下载回本地（仅已上传且本地缺失时可见） |

**多选模式**

点击工具栏「多选」按钮进入，单击卡片切换选中态。工具栏提供：

- **批量上传**：上传选中文件到 OSS（自动跳过已上传）
- **批量下载**：从 OSS 下载选中的文件
- **释放本地空间**：删除本地副本，云端保留
- **删除**：将选中文件移入回收站
- **AI 打标**：批量对选中文件执行 AI 标注
- **编辑标签**：批量编辑标签，支持三种作用域 —— **替换**（用新标签完全覆盖）、**追加**（与原有标签合并）、**删除**（移除指定标签），并提供实时 diff 预览

点击「取消多选」退出；切换日期 / 标签页 / 页面时自动退出多选。

### 灯箱预览

双击媒体库中任意图片打开灯箱预览窗口（视频双击则使用系统默认播放器打开）。

| 操作 | 快捷键 / 方式 |
|------|--------------|
| 翻页 | `←` / `→` 方向键 |
| 缩放 | 鼠标滚轮；`+` / `-` 键；底部滑块拖拽（范围 0.25x–5.0x） |
| 重置缩放 | `0` 键；双击图片 |
| 全屏切换 | `F` 键 |
| 关闭灯箱 | `Esc` 键 |
| 视频播放 | `Space` 键（调用系统默认播放器） |

灯箱为**非模态窗口**，可同时打开多个，切换 Gallery 日期不影响已打开的灯箱。右侧面板显示文件名、拍摄时间、文件大小、图片尺寸、OSS 同步状态，并支持直接在灯箱内编辑 AI 标签和评分（翻页时自动保存）。

### 手动编辑标签

除了 AI 自动标注，也可手动管理标签：

- **单文件**：右键菜单 →「编辑标签」→ 输入标签后回车提交，点击 chip 上的 × 移除
- **灯箱内**：右侧面板直接编辑，翻页时自动保存
- **批量**：多选模式 → 工具栏「编辑标签」→ 选择替换 / 追加 / 删除作用域，实时预览每张文件的标签变化

标签最大 20 字符，大小写不敏感自动去重。保存采用乐观更新策略，失败时自动回滚并提示。

### 局域网上传

1. 切换到「上传」标签页 → 「局域网接收」
2. 点击「启动服务」，生成二维码（自动列出本机所有 IPv4 地址，可逐个复制）
3. 手机连接同一 WiFi，扫描二维码访问上传页面
4. 手机拖拽或选取文件上传（支持 jpg / png / raw / avi / mp4 / mov 等格式）
5. 文件直接存入当前项目目录，上传成功后 2 秒自动刷新 Gallery

启动失败时自动检测连续空闲端口并提供候选端口；点击复制链接后按钮显示「已复制 ✓」1.5 秒恢复。

### 公网远程上传

公网中转采用**三步向导**式部署：

1. **保存配置（Step 1）**：填写 VPS SSH 信息（Host、Port、User，支持密码或私钥认证）、远端路径、HTTP/TCP 端口、PublicHost
2. **部署中转服务（Step 2）**：自动上传 Go relay 二进制到 VPS，`chmod +x`，使用 `setsid nohup` 启动
3. **启动（Step 3）**：必须先有项目目录才能启动

界面提供**彩色状态指示器**（绿 = 运行中，橙 = 过渡态，红 = 错误，灰 = 空闲/停止）和**多行实时日志**（保留最近 100 行）。桌面端通过 TCP 长连接与 VPS 保持通信，收到上传通知后自动 SSH-SCP 拉取文件到本地。应用退出时自动杀掉远端 relay 进程（5s 超时 fire-and-forget）。

### OSS 云备份

1. 「设置」→ 配置阿里云 OSS（Region、Bucket、AccessKey 等），支持「测试连接」（15s 超时）
2. 媒体库中选择文件，点击「上传到 OSS」
3. 大文件自动分片上传，支持**断点续传**：应用重启后弹出恢复提示，自动跳过已传分片
4. 已上传文件支持「从 OSS 下载」「释放本地空间」（删除本地，云端保留）

上传过程中可随时取消；每个操作完成后会弹出统计（成功 X，失败 Y），失败详情可展开查看具体原因。`upload_jobs` 表持久化记录所有分片进度，文件大小变更时自动删除旧任务重新上传。

### AI 自动标注

1. 配置 AI 服务商（设置页面 → AI 配置，或编辑 `ai-providers.toml`），支持「连接测试」验证 API Key 和端点
2. 媒体库中选中照片，点击工具栏「AI 标注」按钮（支持单张或批量）
3. AI 自动识别照片主体内容并生成标签、质量评分

**标签体系**：18 个主体标签（星云、星系、星团、行星、月亮、太阳、彗星、猎户座大星云、仙女座星系、昴星团、银河、土星、木星、火星、金星等）+ 10 个质量标签（拖线、失焦、噪点、过曝、欠曝、色差、大气抖动等）。标签不在白名单内的自动过滤，无有效标签时 fallback 为「未分类」；非天文内容返回空标签 + 评分 0。

**处理机制**：图片长边 resize 到 512px 后 base64 发送给 LLM，每次调用 200ms 节流。失败自动重试（HTTP 429 最多 3 次 backoff、5xx 最多 2 次），401/403 立即终止整批（API Key 失效）。打标过程中可随时取消。完成后如有失败文件弹出详情列表。

**支持的 AI 厂商**：

- Anthropic Claude（原生 API）
- OpenAI GPT（兼容 API）
- Google Gemini
- DeepSeek
- 通义千问（阿里云百炼）
- 月之暗面 Kimi
- 智谱 GLM
- 自定义 OpenAI/Anthropic 兼容端点

### 回收站

**两分区展示**——云端文件（可从 OSS 重新下载）和本地文件（仅本地存在）。

| 操作 | 说明 |
|------|------|
| 恢复 | 将 `deleted_at` 置空，Toast 提供「跳转」按钮直达 Gallery 中该文件所在日期 |
| 下载 | 仅云端文件，从 OSS 拉回本地 + 重新生成缩略图 |
| 彻底删除 | 云端文件弹出三选项对话框：从云端也删除 / 仅删除本地（文件回到 Gallery 标记为"云端有、本地无"）/ 取消 |

「仅删除本地」提供 **5 秒撤销窗口**，撤销后文件重回回收站。支持批量恢复、批量清理、清空回收站。导航栏回收站图标显示当前文件数角标，头部显示容量统计（X 个文件 · Y MB/GB）。

### 设置页

- **导入 / 导出配置**：导出为 JSON 文件（含密钥），用于备份迁移；导入后 UI 立即刷新
- **AI 连接测试**：支持自定义测试 Prompt（可持久化），OK 显示延迟毫秒数，失败显示 HTTP 状态码
- **OSS 连接测试**：一键验证 Region / 凭据 / Bucket（15s 超时）
- **厂商切换确认**：切换 AI 厂商时弹出三选对话框（确认切换 / 取消 / 无需再次提示），自动填入默认 Base URL 和推荐模型列表
- **路径校验**：FFmpeg / FFprobe 路径失焦自动校验文件存在性；OSS PathPrefix 自动补末尾 `/`
- **保存反馈**：保存按钮文字 1.5 秒变为「已保存 ✓」
- **未保存提醒**：在设置页有修改时切换页面，弹出「保存并离开」「放弃更改」「取消」三选对话框
- **数据目录**：所有数据（配置、索引、缩略图缓存）存储于
  - macOS: `~/Library/Application Support/StartTooler/`
  - Linux: `~/.local/share/StartTooler/`
  - Windows: `%LocalAppData%\StartTooler\`

### 键盘快捷键

| 快捷键 | 作用 |
|--------|------|
| `Ctrl+1` | 切换到媒体库 |
| `Ctrl+2` | 切换到上传服务 |
| `Ctrl+3` | 切换到回收站 |
| `Ctrl+4` | 切换到设置页 |

灯箱内快捷键见上文「灯箱预览」章节。

### 主题切换

- **DeepSpace**（默认）：深空暗色主题
- **RedNightVision**：红光夜视主题，背景纯黑、红色调文字，专为野外拍摄场景设计，保护暗适应能力

切换即时生效，无需重启。通过「设置」→「外观」切换。

### 状态栏与通知

主窗口底部状态栏实时显示：左侧「已上传 X / Y」当前视图统计，中间「N 分钟前刷新」上次刷新时间，右侧 OSS 配置状态（绿色圆点「OSS 已配置」/ 红色圆点「OSS 未配置」）。

右下角浮动通知卡片支持三类：
- **简单通知**：5 秒自动消失（上传完成等）
- **操作 Toast**：带按钮的 toast 5 秒自动消失（回收站「跳转」「撤销」按钮）
- **进度通知**：由调用方控制生命周期（扫描、上传、AI 打标），完成后 2 秒自动消失

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
