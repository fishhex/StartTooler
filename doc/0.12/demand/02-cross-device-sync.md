# D02 — 跨设备云端同步

> 关联文档：`02-data-layer.md`（数据层）、`04-oss-upload.md`（OSS 上传）、`06-settings.md`（设置）

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.1（需求稿）** |
| 目标用户 | 有多台设备的天文摄影爱好者 |
| 文档状态 | **需求 — 待评审** |
| 关联模块 | Data Layer (02)、OSS Upload (04)、Settings (06)、Gallery (05) |

---

## 1. 需求总览

### 1.1 背景

当前星助的配置导出/导入功能（v0.11）只导出 `config.db` 中的设置项（OSS 凭据、AI 配置、主题等），**不包含 `media.db` 文件索引**。

核心矛盾：

| 问题 | 根因 |
|---|---|
| 换设备后 Gallery 为空 | `media.db` 无法导出，且未随配置一起迁移 |
| 即使复制 `media.db` 也无法工作 | 所有查询以 `project_path`（绝对路径）为分组键，新设备路径必然不同 |
| 无 OSS 对象列表能力 | `IOssStorage` 只有 Put/Get/Delete，无法知道云端有哪些文件 |
| 无从云端重建索引的流程 | 只能逐个/批量下载已索引的文件，不能从零恢复 |

用户上传到 OSS 的 `objectKey` 格式为 `{PathPrefix}/{relative_path}`，**云端数据本身已经是设备无关的**——问题是本地索引无法重建。

### 1.2 核心价值

| 现在 | 将来 |
|---|---|
| 换设备需要手动迁移 `media.db` 并修改所有 `project_path` | 导入配置 → 设置新本地目录 → 一键从云端重建索引 |
| `project_path` 是唯一项目标识 | `ProjectName` 是跨设备唯一标识，`project_path` 降级为纯本地路径 |
| 不知道云端有哪些文件 | OSS 对象列表 → 重建完整的媒体文件索引 |
| 设备间数据割裂 | 选择"从云端下载全部"即可在新设备上获得完整项目 |

### 1.3 一句话概括

**引入 `ProjectName` 作为跨设备项目标识，新增 OSS 对象列表能力，实现"导出配置 → 新设备导入 → 从云端重建索引 → 按需下载"的完整跨设备工作流。**

---

## 2. 用户场景

### 场景一：换了新 Mac

> 用户在旧 Mac 上拍了 200+ 张深空照片，全部上传到 OSS。换了新 Mac 后：
>
> 1. 从旧设备导出 `starttooler-config.json`（含 ProjectName = `"deepsky-2025"`）
> 2. 新设备安装星助 → 导入配置
> 3. 设置本地项目目录为 `/Users/newuser/Pictures/Astro`（与旧设备路径不同）
> 4. 点击「从云端恢复」→ 星助列出 OSS 上 `deepsky-2025/` 下的所有文件
> 5. 重建索引：200+ 个文件以"云端有、本地无"状态出现在 Gallery
> 6. 批量选中 → 下载全部到本地 → 开始在新设备上继续编辑标签

### 场景二：桌面 + 笔记本双设备协作

> 用户在台式机上拍了照片上传云端。出差时带笔记本：
>
> 1. 笔记本已导入过配置（同一个 ProjectName）
> 2. 打开星助 → 点击「同步云端」→ 自动列出本地缺失的云端文件
> 3. 选择性下载需要处理的几张 → AI 打标 → 结果随下次上传自动同步回云端

---

## 3. 功能需求

### 3.1 ProjectName — 跨设备项目标识

| 需求 | 说明 |
|---|---|
| 生成时机 | 首次设置项目目录时，自动根据目录名生成默认 ProjectName（如 `AstroPhotos_2025`），允许用户手动修改 |
| 存储位置 | `ProjectConfig.ProjectName`，持久化到 `config.db` |
| 唯一性 | 不做全局唯一校验，由用户自行确保不与其他项目重复（OSS 路径隔离） |
| 导出/导入 | `ExportConfigAsync` 随 project key 一起导出，导入时恢复 |
| 变更处理 | 用户可在设置页修改 ProjectName，仅作为显示标识。不影响 OSS 路径（OSS 路径由 `PathPrefix` 控制） |

### 3.2 OSS 对象列表

| 需求 | 说明 |
|---|---|
| 接口 | `IOssStorage` 新增 `ListObjectsAsync(string prefix, CancellationToken ct)` |
| 返回 | 对象列表（key + size + lastModified），支持分页（阿里云 SDK 默认 100 条/页） |
| 过滤 | 按 `PathPrefix` 前缀过滤，只列出本项目文件 |
| 幂等 | 可重复调用，结果一致（OSS 侧数据不变时） |

### 3.3 从云端重建索引

| 需求 | 说明 |
|---|---|
| 入口 | Gallery 空态页面（无项目时）/ 设置页"从云端恢复"按钮 / 右键菜单"同步云端文件" |
| 流程 | ① ListObjects(PathPrefix) → ② 解析 objectKey 得到 relative_path → ③ 逐条写入/更新 `media_files` 表（此时 `local_exists=0`） |
| 增量 | 如果本地已有同 `project_name + relative_path` 的记录 → 跳过或更新 `remote_url` |
| 状态 | 重建后文件以"云端有、本地无"状态出现在 Gallery，徽章显示云图标 |

### 3.4 全量/增量下载

| 需求 | 说明 |
|---|---|
| 触发 | Gallery 工具栏新增「下载全部」（下拉菜单：「下载全部云端文件」/「下载选中」） |
| 全量下载 | 列出所有 `is_uploaded=true && local_exists=false` 的文件 → 逐个调用现有 `DownloadToLocalCoreAsync` |
| 进度 | Toast 渐进通知「下载中 3/200」，不做进度条 |
| 取消 | 复用现有 `_downloadCts` 取消机制 |
| 冲突 | 本地已存在 → 跳过（`DownloadToLocalCoreAsync` 已处理） |

### 3.5 增量同步（双向感知）

| 需求 | 说明 |
|---|---|
| 「从云端同步」 | 对比 OSS 对象列表 与本地 `media_files` 表 → 列出本地缺失的云端文件 → 用户选择下载 |
| 差异报告 | Toast 显示「云端有 X 个文件尚未同步到本地」 |
| 不与现有上传冲突 | 上传仍走现有流程；同步只做"从云端到本地"的单向拉取 |

---

## 4. 数据模型变更

### 4.1 ProjectConfig 新增字段

```csharp
public class ProjectConfig
{
    public string? CurrentDirectory { get; set; }
    public string? ProjectName { get; set; }        // 新增：跨设备唯一标识
    public List<string> RecentDirectories { get; set; } = new();
}
```

### 4.2 media_files 表新增列

```sql
ALTER TABLE media_files ADD COLUMN project_name TEXT;
```

`project_path` 保留不变——它仍然是本地文件路径的真实记录。新增的 `project_name` 用于：
- 跨设备匹配项目
- 构建 OSS objectKey（`{ProjectName}/{relative_path}`）
- 查询时的项目筛选（可选，优先用 `project_name`）

### 4.3 OSS objectKey 构建规则

保持不变：`{PathPrefix}/{relative_path}`

`ProjectName` 仅作为跨设备标识和 `media_files` 表中的分组字段，不影响 OSS 路径结构。

---

## 5. 技术约束

| 项 | 约束 |
|---|---|
| 不动 OssConfig | `OssConfig` 所有字段不动。`PathPrefix` 仍然是 OSS 对象 key 的唯一前缀来源。`ProjectName` 是纯本地标识，不与 OSS 路径耦合 |
| 兼容旧数据 | 旧 `media_files` 行的 `project_name` 为 NULL → 扫描时自动回填（取 `ProjectConfig.ProjectName`） |
| 不引入新 NuGet 包 | ListObjects 使用阿里云 SDK 已有 `OssClient.ListObjects` |

---

## 6. 不做清单

| 内容 | 理由 |
|---|---|
| ProjectName 重命名 + 云端路径迁移 | 复杂度高，涉及 OSS 端批量 move/rename 对象，v0.12 不做 |
| 多项目管理（切换 ProjectName 自动切换目录） | 当前「最近目录」下拉已够用，多项目显式管理属于远期需求 |
| OSS 文件夹级增量对比（diff 算法） | v0.12 做简单全量 ListObjects 对比即可 |
| 自动定时同步 | 手动触发即可，不需要后台轮询 |
| 云端文件删除后本地自动感知 | 需要 OSS 事件通知或定时扫描，超出本次范围 |
| `media.db` 的导出/导入 | 改为通过 ProjectName + ListObjects 重建索引，不再需要导出 media.db |
