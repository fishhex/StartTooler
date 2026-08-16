# 04 · 媒体库

星助把所有媒体文件抽象成一个统一的"媒体库"——一个本地 SQLite 表 `media_files`，每行对应一张照片或一段视频。这个文档解释媒体库长什么样、字段怎么用、状态怎么流转。

## 一、为什么用一张表

天文摄影一次外出产出 30–500 张照片，里面混杂：

- **亮场**（俗称 light）：实际拍的目标
- **暗场**（dark）/ **平场**（flat）/ **偏置场**（bias）：校准帧
- **RAW**（厂商私有格式）/ **FITS**（天文专用）/ **PNG**（后期导出）
- **SER**：一种视频序列容器，里面存一帧帧连拍

用户**只关心一件事**："我项目里有几张可用照片"。所以我们用一张统一表承载所有这些类型——不分类建表。

> 在用户能看到的 UI 上，没有"亮场 / 暗场"这种分类（参见 [01-objects.md](01-objects.md)）。

## 二、媒体对象的身份

每张文件（即 `media_files` 一行）由两个字段唯一确定：

| 字段 | 含义 | 例子 |
|---|---|---|
| `project_path` | 这个媒体属于哪个项目目录（绝对路径） | `/Users/hex/Astro/m42-2025-12-13` |
| `relative_path` | 在项目目录下的相对路径 | `2025-12-13/M42_Light_012.fit` |

完整文件路径 = `project_path + '/' + relative_path`。**项目目录是边界**——切换项目就隔离媒体库的全部状态。

代码：[MediaFile.cs:39-40](../../StartTooler/Data/MediaFile.cs#L39)

## 三、字段一览

| 字段 | 业务含义 | 在 UI 哪里体现 |
|---|---|---|
| `id` | 数据库自增 ID | 不可见 |
| `project_path` | 所属项目根目录 | 不可见 |
| `relative_path` | 项目内相对路径 | 列表显示文件名 |
| `file_name` | 文件名（不含目录） | 同上 |
| `media_type` | 图像 / 视频 / 采集序列 | 缩略图左上角徽章 |
| `file_size` | 字节数 | 悬浮信息 |
| `last_modified` | 文件最后修改时间 | 排序默认 |
| `shot_at` | 拍摄时间（来自 EXIF 或文件名） | 日历视图 / 时间轴 |
| `is_uploaded` | 是否已上传到云端 | 同步徽章 |
| `local_exists` | 本地文件是否存在 | 同步徽章 |
| `remote_url` | 云端 URL（上传后有值） | 点击打开 |
| `uploaded_at` | 上传完成时间 | 备份历史 |
| `tags` | AI 标记的主体标签 | 标签条 |
| `quality_tags` | AI 标记的质量标签 | 红色质量徽章 |
| `score` | AI 评分 0–100 | 评分角标 |
| `tagged_at` | 打标时间 | 排序 / 筛选 |
| `tag_error` | 打标失败原因 | 红色三角徽章 + tooltip |
| `tag_edited_manually` | 是否被人手动编辑过 | 控制批量打标是否覆盖 |
| `deleted_at` | 软删除时间 | 垃圾筒可见 |
| `session_id` | 所属拍摄会话 ID | 日记本 |
| `is_diary_featured` | 是否在日记本精选 | 日记本展示 |
| `focal_length_35mm` / `iso` / `exposure_time_seconds` | EXIF 冗余字段 | 统计仪表盘 |
| `capture_*` | SER 元数据（宽/高/帧数/位深/色彩/观察者/望远镜/观测时间） | SER 详情 |
| `thumbnail_path` | 缩略图缓存绝对路径 | 缩略图渲染 |
| `is_selected` / `is_hovered` / `is_keyboard_focused` | UI 临时态 | 选中 / 焦点 |
| `upload_status` | 瞬时上传状态 | 进度徽章 |
| `upload_error` | 瞬时上传错误 | 进度徽章 |
| `created_at` / `updated_at` | 业务时间戳 | 数据库维护 |

代码：[MediaFile.cs:36-256](../../StartTooler/Data/MediaFile.cs#L36)

## 四、媒体的发现流程

```
打开项目
  ↓
扫描目录（递归）
  ↓
对每个文件：
  - 解析 EXIF / 文件头 → media_type、shot_at、EXIF 字段
  - 解析 SER → capture_* 字段
  - 生成缩略图（视频取首帧；SER 取首帧；图像缩放）
  - INSERT 或 UPDATE media_files
  ↓
扫描完成
```

代码触发：[GalleryViewModel](../../StartTooler/ViewModels/GalleryViewModel.cs) 启动时；用户点"刷新"按钮时；上传文件落盘后异步扫描。

扫描函数：[IMediaRepository.ScanDirectoryAsync](../../StartTooler/Data/IMediaRepository.cs)。

## 五、媒体的同步状态

`media_files` 一行不是只反映本地状态，还要同时反映"云端同步状态"。

### 三种持久态

| 状态 | 含义 | UI 体现 |
|---|---|---|
| `SyncStatus.UploadedAndLocal` | 已上传且本地文件存在 | 绿色 ✓ 徽章 |
| `SyncStatus.UploadedButMissingLocal` | 已上传但本地文件丢了（释放空间 / 移动硬盘拔了） | 红色 ⚠ 徽章 |
| `SyncStatus.NotUploaded` | 未上传 | 灰色↑ 徽章 |

这三种状态由 `IsUploaded` + `LocalExists` 两个字段派生（[MediaFile.cs:247-255](../../StartTooler/Data/MediaFile.cs#L247)）。

### 临时上传态

用户上传文件过程中，状态变成临时：

| 状态 | 含义 | UI 体现 |
|---|---|---|
| `UploadStatus.Uploading` | 正在上传 | 进度条 |
| `UploadStatus.Failed` | 上传失败 | 红色 ✗ 徽章 + tooltip |
| `UploadStatus.Paused` | 上传暂停 | 暂停图标 |

这三个状态下**同步徽章隐藏**，由进度徽章接管（[MediaFile.cs:112-117](../../StartTooler/Data/MediaFile.cs#L112)）。

## 六、标签 vs 质量标签

同张媒体有**两类标签**：

| 类型 | 字段 | 内容 | UI 颜色 |
|---|---|---|---|
| 主体标签 | `tags` | "昴星团"、"猎户座"、"M42" | 蓝色 |
| 质量标签 | `quality_tags` | "欠曝"、"拉直"、"过曝"、"噪点" | 暖红 |

批量 AI 打标会同时打两类标签，但用户**手动编辑**只动 `tags`（不让人工干预 AI 质量判断）。

代码：[MediaFile.cs:131-142](../../StartTooler/Data/MediaFile.cs#L131)

## 七、删除 vs 软删除

删除文件**不真的删**——只设 `deleted_at`（unix 毫秒）。文件还在磁盘上，只是从 Gallery 视图里消失。

垃圾筒视图：列出所有 `deleted_at IS NOT NULL` 的文件，**保留 30 天**。30 天后清理会真删磁盘文件。

为什么不真删：天文照片"删除"经常是误删（点评分低了，心血扔了心疼）。保留 30 天给用户后悔。

代码：[MediaFile.cs:174-183](../../StartTooler/Data/MediaFile.cs#L174)

## 八、Session（拍摄会话）

`media_files.session_id` 把同一晚上连续拍摄的照片归到一组。算法：

- 按 `shot_at` 排序
- 间隔超过 N 小时（默认 4）视为不同会话
- Session 自身是个对象（`sessions` 表），有关联的目标 / 天气 / 笔记

代码：[SessionClusteringService](../../StartTooler/Services/SessionClusteringService.cs)。

这是日记本的基础——一次会话 = 一次外出拍摄。

## 九、采集序列（SER）

SER 是一种视频格式，但里面存的是连拍帧（比如 50 张月亮）。星助把它当作"序列媒体"：

- `media_type = CaptureSequence`
- 字段 `capture_*` 记录宽度 / 高度 / 帧数 / 位深 / 色彩 / 观察者 / 望远镜 / 观测时间
- 缩略图取首帧
- 播放时按帧跳转

代码：[MediaFile.cs:220-244](../../StartTooler/Data/MediaFile.cs#L220)

## 十、媒体库的边界

### 媒体库能做什么

- 浏览 / 排序 / 筛选（按时间 / 标签 / 评分）
- 多选 + 批量操作（删除 / 评分 / 上传）
- 拖拽导入（拖到 App 窗口）
- 拍照文件直接被 EXIF 解析
- 上传 / 共享到 OSS / LAN / 公网
- 备份 / 释放空间（保留云端副本）
- 删除 / 恢复（30 天垃圾筒）

### 媒体库不能做什么

- **不能**编辑照片本身（不提供修图工具）
- **不能**远程浏览相册（除 OSS 公开链接）
- **不能**多用户协作（无账号系统）
- **不能**版本管理（标签编辑无历史）

## 十一、与数据库交互

媒体库的所有读写都通过 `IMediaRepository` 接口（[IMediaRepository.cs](../../StartTooler/Data/IMediaRepository.cs)）。**绝不**直接 SQL。

接口方法覆盖：

- `InsertOrUpdateAsync` / `UpsertManyAsync`：入库
- `QueryByPathAsync` / `QueryByProjectAsync` / `QueryByTagAsync`：查询
- `UpdateTagsAsync` / `UpdateScoreAsync` / `UpdateDeletedAtAsync`：单字段更新
- `ScanDirectoryAsync`：扫描 + 写入
- `GetLocalSizeAsync` / `CountByProjectAsync`：统计

为什么这样：上层 ViewModel 永远不接触 SQL，方便后续切换存储（SQLite → PostgreSQL 等）。

## 十二、跨设备场景

当用户从多台设备传文件到同一项目时（比如 PC 端拍了 + 移动端拍了），星助靠**项目目录 + 相对路径**判定"同一文件"——同一项目目录下相对路径相同 → 视为同一文件，不会重复入库。

跨设备项目名（`project_name`）字段由 D02 引入，详见 [06-cross-device-sync.md](06-cross-device-sync.md)。
