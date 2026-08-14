# 03 · 半成品与设计取舍

只记录**用户能在 UI 上看到但有明显缺口**的功能。这些功能代码层有部分落地，但 UI 暴露不全。

---

## H1 · EXIF 统计视图

### 已落地

- `MediaFile` 实体有 `FocalLength35mm` / `Iso` / `ExposureTime` 三个 EXIF 字段（持久化）
- 代码层有相关统计查询方法（`doc/0.11/spec/19-statistics-dashboard.md` 是规格文档）

### 未落地

- UI **无任何入口**：Settings 没有"统计" Tab，Advanced 页没有"统计" Tab
- Gallery 时间轴、标签聚合**都不按焦距 / ISO / 曝光时长**过滤
- 即便 `MediaRepository.GetByTimeRangeAsync` 等接口能用，UI 没暴露给普通用户的"本月用 50mm 拍的所有 M42"这类查询入口

### 用户体验上的状态

EXIF 数据**默默进了 DB**。如果你想做"按拍摄参数筛选"，得自己跑 SQL 进 Advanced → 数据库 Tab 查。

### 不补的理由

`doc/0.11/spec/19-statistics-dashboard.md` 已是历史档案（v0.11 范围不再实现）；仪表盘属于"锦上添花"而非流程必需。

---

## H2 · Session 手动重聚类

### 已落地

- `SessionClusteringService.ClusterAsync(projectPath, intervalHours, ct)` 方法存在
- 每次扫描完成**自动触发**聚类 [MainWindowViewModel.cs:199-206](../../StartTooler/ViewModels/MainWindowViewModel.cs#L199-L206)
- 用户可在 Settings 通用改 `SessionIntervalHours`（1–24h）

### 未落地

- **没有"重新聚类"按钮**——改完间隔阈值后只能等下次扫描或重启应用
- 用户感受：改了 4h → 2h 后，发现 Session 没变化，但没法手动强制重算

### 用户体验上的状态

间隔阈值调整**不会即时生效**。需要触发扫描或重启。

### 不补的理由

属于"边角用例"，普通用户通常一次性设置好。优化优先级低。

---

## H3 · Diary 精选批量标记

### 已落地

- `MediaFile._isDiaryFeatured` 字段 + `SetDiaryFeaturedAsync` API
- Gallery 内"管理精选"对话框（来自 Diary 内置流程）支持多选 [DiaryViewModel.cs:711-756](../../StartTooler/ViewModels/DiaryViewModel.cs#L711-L756)

### 未落地

- **Gallery 内**没有"批量标记为精选 / 取消精选"操作——只能进 Diary 该 Session 页用对话框
- "按时间自动选 12 张"逻辑 [DiaryViewModel.cs:604-624](../../StartTooler/ViewModels/DiaryViewModel.cs#L604-L624) 只在该 Session**没有**精选时才跑，**用户无法强制重新计算**

### 用户体验上的状态

精选一旦定下来，没有 Gallery 内批量管理路径。要重排得进 Diary。

### 不补的理由

Diary 内的对话框已经覆盖了"挑某几张入精选"的需求；批量操作是次级优化。

---

## 设计取舍记录

代码里看得见的"为什么这样设计"，帮助后续维护者理解非显然决定。

### D1 · 配置页面二选（OSS / AI）被固定为单一供应商

OSS Tab 的供应商是 TextBlock 显示「阿里云 OSS」[SettingsView.axaml:282-290](../../StartTooler/Views/SettingsView.axaml#L282-L290)，不可改。

**为什么**：原 spec 想做多供应商（腾讯云 COS / AWS S3），但代码层只写了 `AliyunOssStorage`。其它云的 SDK 没接，硬暴露 ComboBox 选了不能用 → 干脆不暴露。

### D2 · AI 协议与厂商解耦

Settings AI Tab 有"厂商"和"协议"两栏，下拉分离 [SettingsView.axaml:459-497](../../StartTooler/Views/SettingsView.axaml#L459-L497)。

**为什么**：自部署 LLM 服务通常套 OpenAI 兼容协议或 Anthropic 兼容协议；用户用 Qwen / 豆包厂商可以选 OpenAI 协议私有部署版。

### D3 · Trash「已在云端 / 仅本地」分段

Trash 不是单列，而是按 "IsUploaded" 拆两段 [TrashView.axaml:138-367](../../StartTooler/Views/TrashView.axaml#L138-L367)。

**为什么**：清空策略不同。「已在云端」随便删，「仅本地」删了文件就真没了。给用户一个明显的「这里删了是真的删」的视觉提示（橙色 `!` 徽章在本地点缺失时出现）。

### D4 · 启动恢复弹窗仅一次

进入 Gallery 时若 DB 有 InProgress 任务，弹「恢复 / 稍后」[MainWindowViewModel.cs:243-279](../../StartTooler/ViewModels/MainWindowViewModel.cs#L243-L279)。

`private bool _resumePrompted;` 保证一次启动只弹一次。点「稍后」再次进入 Gallery 不会重复弹。

### D5 · Diary 笔记字数上限 500

笔记 TextBox `MaxLength="500"` [DiaryPage.axaml:521](../../StartTooler/Controls/DiaryPage.axaml#L521)。

**为什么**：天文摄影笔记通常是「那天晚上温度多少 / 看到了什么 / 这次没解决的问题」，几百字内完整。强制短促避免"长篇日记"扩张成内容管理系统（v0.12 spec 想做但没做）。

### D6 · 视频 / SER 不进灯箱

灯箱只显示图片。视频 / SER 双击不进灯箱，Space 走外部播放器 [LightboxWindow.axaml.cs:60-67](../../StartTooler/Views/LightboxWindow.axaml.cs#L60-L67)。

**为什么**：灯箱是图片浏览工具，缩放 / 像素级操作。视频播放属于另一类交互。明确分工。

### D7 · Diary 自动精选策略：评分优先 + 时间均匀采样

[DiaryViewModel.cs:604-624](../../StartTooler/ViewModels/DiaryViewModel.cs#L604-L624)：

1. 先拿所有照片
2. 有评分的取前 N
3. 没评分但还有空位的，按时间均匀采样剩余

**为什么**：避免「12 张全是连拍相似镜头」。均匀采样让照片在时间轴上均匀分布。

### D8 · 状态栏存储占用不分"云 / 本"

`MockStorageText` 直接显示「当前过滤 X / 总 Y」[MainWindowViewModel.cs:94-96](../../StartTooler/ViewModels/MainWindowViewModel.cs#L94-L96)。

**为什么**：用户视角"我本地还有多少在管"比"OSS 上多少 / 本地多少"更有意义。云端的不要占用本地磁盘预算。

### D9 · 通知退出策略

上传 / 扫描 / 打标进度通知——任务结束 2 秒后自动消失 [GalleryViewModel.cs:244-256](../../StartTooler/ViewModels/GalleryViewModel.cs#L244-L256)。

**为什么**：成功状态用户能看到即可，2 秒够眼睛扫一眼。错误不自动消失——必须用户主动关闭。
