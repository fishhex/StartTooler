# StartTooler — 项目规范索引

> 本目录是 StartTooler 的工程级规范（架构、模块边界、对外接口、关键流程、已知陷阱）。
> 与 `doc/draft/star-helper-spec*.md`（UI 视觉规格 v2.x 迭代历史）分开放置，互不替代。

---

## 命名约定

| 文件 | 角色 |
|---|---|
| `00-project.md` | **总体规范**：项目定位、技术栈、架构总览、模块清单、跨模块契约 |
| `01-app-bootstrap.md` | 启动与进程模型：Program / App 生命周期 / 主题 |
| `02-data-layer.md` | 数据层：ConfigService / MediaRepository / UploadJobRepository + 配置模型 |
| `03-media-pipeline.md` | 媒体管线：扫描 / 缩略图 / FFmpeg / FFprobe / ImageCache |
| `04-oss-upload.md` | 对象存储：AliyunOssStorage + 断点续传 + Gallery 上传状态机 |
| `05-gallery-view.md` | GalleryViewModel：数据流、UI 状态机、多选/扫描/上传/下载 |
| `06-settings.md` | 设置页：SettingsViewModel + 配置校验 + 主题切换 |
| `07-upload-server-lan.md` | 局域网上传：UploadServerService + QR + 二进制模板 |
| `08-public-relay.md` | 公网代理：C# 端 PublicRelayService + Go relay |
| `09-ui-commons.md` | UI 通用件：Components / Controls / Converters / Themes / 通知 / 对话框 |
| `10-trap-book.md` | 已知陷阱与决策记录（ADR 风格） |
| `14-delete-and-trash.md` | 删除、垃圾筒与释放本地空间：软删除 / TrashViewModel / 彻底删除 / OSS 删除 |

---

## 推荐阅读顺序

| 你是… | 顺序 |
|---|---|
| **第一次接触这个项目** | `00` → `01` → `02` → `05` → `08` |
| **做 OSS / 上传相关改动** | `00` → `02` → `04` → `05` |
| **做公网代理 / VPS 部署相关改动** | `08` → `01` → `05` |
| **改 UI / 主题 / 样式** | `00` → `09` → `06` |
| **修 bug / 看历史踩坑** | `10` 起 |
| **新加一个 ViewModel / Service** | `01`（启动模型）→ `02`（数据契约）→ `05`（VM 模板） |

---

## 单一事实来源（Single Source of Truth）

| 主题 | 文件 / 表 |
|---|---|
| 设置 | `Config` 表（JSON KV by `ConfigKeys.*`）— 见 `02-data-layer.md` |
| 媒体文件索引 | `media_files` 表 — 见 `02-data-layer.md` |
| 未完成 multipart upload | `upload_jobs` 表 — 见 `02-data-layer.md` |
| 缩略图 | `~/.local/share/StartTooler/thumbnails/`（mac/linux）/ `%LocalAppData%\StartTooler\thumbnails\` |
| Go relay 二进制 | `StartTooler/Resources/relay-binaries/upload-relay-linux-{amd64,arm64}`（嵌入资源） |
| 上传页面 HTML | `StartTooler/Resources/upload.html`（编译时同步到 `tools/upload-relay/web/index.html`） |
| Trace 日志 | `cwd/starttooler-debug.log`（WinExe 无 console） |

---

## 文档维护约定

1. 改代码必须同步改对应 spec 文件，写一句「为什么」比写做法重要。
2. `10-trap-book.md` 是「为什么不要这样写」的集合——踩过的坑立刻沉淀，避免重蹈。
3. 旧 `draft/star-helper-spec*.md` 是 UI 视觉迭代历史，**保留**作为决策档案，新规范不重复其内容。
