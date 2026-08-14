# 01 · 业务对象字典

按"用户能感知到的实体"划分。每个对象包含：业务定义 + 用户能看到的形态 + 用户能做的动作 + 文件位置。

---

## Media（媒体）

**业务定义**：一张被导入/扫描进项目目录的物理文件（图片 / 视频 / SER 采集序列）。

**用户看到的形态**：

- Gallery 中的一张 160×120 缩略图瓦片
- 灯箱（Lightbox）中全屏展示的图片
- Trash 中的卡片
- Diary 中某个会话下的照片

**瓦片上能看到的视觉元素** [GalleryView.axaml:368-590](../../StartTooler/Views/GalleryView.axaml#L368-L590)：

- 缩略图（图片直接展示；视频 / SER 显示占位）
- 同步状态徽章（右上，颜色见 [00-product-overview §5.3](00-product-overview.md)）
- 视频 / SER 角标（左上）
- 评分 + 主体标签（底部条）
- 质量标签（hover 时下方 chip）
- 选中态（蓝边框 + 半透明覆盖 + 圆心对勾）

**用户能做的动作**：

| 动作 | 怎么触发 | 业务效果 |
|---|---|---|
| 单击 | 瓦片 | 进入多选模式（已在多选时）或选中（不在多选时） |
| 双击 | 瓦片（图片） | 进灯箱 |
| 右键 | 瓦片 | 弹出上下文菜单（详情需查 code-behind） |
| 拖拽 | 全局 | 把外部文件拖进来导入 |
| 多选 + 删除 | 工具栏 | 移入回收站 |

---

## Project（项目）

**业务定义**：用户选择的根目录，里面装着一段时间内拍的所有照片。

**用户看到的形态**：

- Settings 通用 Tab「项目目录」字段 [SettingsView.axaml:51-115](../../StartTooler/Views/SettingsView.axaml#L51-L115)，可选「浏览…」或从最近列表选
- 窗口标题栏的「星助 — 媒体」等前缀

**用户能做的动作**：

- 选一个新目录（在 Settings 通用里点 SelectButton → 浏览）
- 浏览历史目录（点按钮看到「最近使用」悬浮菜单）
- 清空最近历史（菜单底部「清空最近」）

**没换目录但项目里加文件时**——点 Gallery 工具栏右侧的「刷新」重新扫描 [MainWindow.axaml:143-162](../../StartTooler/Views/MainWindow.axaml#L143-L162)。

---

## Session（拍摄会话）

**业务定义**：一次完整拍摄活动的照片集合。按相邻两张照片的最大间隔分组，间隔可在 Settings 通用里设（1–24 小时，默认 4 小时）[SettingsView.axaml:232-251](../../StartTooler/Views/SettingsView.axaml#L232-L251)。

**用户看到的形态**：

- 日记页 [DiaryView.axaml](../../StartTooler/Views/DiaryView.axaml) 的一页
- 大日期 + 星期 + 农历 + 标题 + 时段 + 天气胶囊 + 地点胶囊 + 精选照片 + 笔记 + 常用标签
- 时间轴底部的小圆点（一个会话一个点）

**用户能做的动作**：

- 在 Sessions 间翻页（`<` `>` 按钮 + 时间轴点点击）[DiaryViewModel.cs:194-215](../../StartTooler/ViewModels/DiaryViewModel.cs#L194-L215)
- 编辑标题、地点、时段（点击对应字段 → 弹输入框 → 失焦保存）
- 改天气（点天气胶囊 → 弹 7 选项图标选择器 → 选完即存）[DiaryViewModel.cs:672-707](../../StartTooler/ViewModels/DiaryViewModel.cs#L672-L707)
- 写拍摄笔记（TextBox 失焦自动保存，最多 500 字）
- 管理精选照片（点右上「管理精选」→ 多选对话框 → 保存）[DiaryViewModel.cs:711-756](../../StartTooler/ViewModels/DiaryViewModel.cs#L711-L756)
- 单张移除精选（hover 显示 × 按钮）
- 刷新环境数据（用首张照片 GPS 重查）
- 删除整个会话（将所有照片的 session_id 清空）

---

## Tag（标签）—— 主体标签 / 质量标签

### 主体标签

- 业务定义：天文对象名（"昴星团"、"M42"、"仙女座大星系"…）。约 18 个常驻分类
- 可见位置：Gallery 瓦片底部条
- 编辑入口：Gallery 多选 → 工具栏「编辑标签」→ 三选对话框（替换 / 追加 / 删除）
- 在 Gallery 左栏「标签」 Tab 按主体标签聚类浏览

### 质量标签

- 业务定义：拍摄瑕疵（"拉直"、"过曝"、"脱焦"、"拉丝"…）
- 可见位置：仅 hover / 键盘焦点时显示
- 编辑入口：同上 → 同一对话框
- 与主体标签的视觉区别：配色不同（主体浅色背景 + 主文字色；质量 hover chip 单独一片）

---

## Score（评分）

**业务定义**：1–5 星的整数评分（部分用了 0–10 细化）。

**用户看到的形态**：Gallery 瓦片底部条靠左的小 chip，颜色由分值映射（红 / 黄 / 蓝）。

**用户能做的动作**：

- AI 自动打分（取决于厂商模型是否输出）
- 在灯箱里手动打分（数字键 1–5，目前是 Gallery 右键菜单走，具体接口待 code-behind 核实）

**排序影响**：工具栏右上「排序」可切「按评分降序」[GalleryViewModel.cs:404-408](../../StartTooler/ViewModels/GalleryViewModel.cs#L404-L408)。

---

## OSS 备份配置

**业务定义**：阿里云 OSS 凭证 + Bucket 信息，用于将照片上传到云。

**用户能做的动作**——在 Settings「OSS 配置」 Tab [SettingsView.axaml:279-441](../../StartTooler/Views/SettingsView.axaml#L279-L441)：

| 字段 | 说明 |
|---|---|
| 供应商 | 固定「阿里云 OSS」（TextBlock 显示，不可改） |
| 区域 (endpoint) | 例：cn-hangzhou |
| 桶名 (bucket) | 例：my-photos |
| AccessKeyId / Secret | 两个 TextBox，Secret 加眼睛按钮切换明文 |
| 路径前缀 | 例：astrophotos/，失焦自动补末尾 `/` |
| 连接测试 | 按钮，验证凭证有效 |

**首次上传触发**：如果还没配，用户在 Gallery 点批量上传会弹「OSS 未配置」对话框，按钮「去设置」直接跳到这一 Tab [MainWindowViewModel.cs:486-503](../../StartTooler/ViewModels/MainWindowViewModel.cs#L486-L503)。

---

## AI 配置

**业务定义**：用于 AI 打标的 LLM 厂商与凭证。

**用户能做的动作**——在 Settings「AI」 Tab [SettingsView.axaml:444-625](../../StartTooler/Views/SettingsView.axaml#L444-L625)：

| 字段 | 说明 |
|---|---|
| 厂商 | 下拉（豆包 / Qwen / GPT / Claude / Gemini …共 7 家），切换自动填默认 Base URL 和推荐模型 |
| 协议 | 下拉，与厂商解耦——私有化部署可手选 OpenAI/Anthropic 兼容协议 |
| API Key | TextBox + 眼睛切换 |
| Base URL | TextBox，可改 |
| 模型 | 可编辑下拉——既可从推荐列表选，也可手动输入任意名（厂商出新品无需更新 UI） |
| 测试 prompt | TextBox，「测试连接」时发给 AI 的提示词 |
| 连接测试 | 按钮，验证连通 |

**调用时机**：用户多选照片 → 工具栏「开始 AI」→ 调厂商端点 → 把返回的标签回写到 DB。

---

## Trash（回收站）

**业务定义**：被删除的媒体的暂存区，两类：

- **已在云端**——已上传到 OSS，删本地不丢文件
- **仅本地**——还没传云，删了就真没了

**用户看到的形态**——[TrashView.axaml:113-369](../../StartTooler/Views/TrashView.axaml#L113-L369)：

- 顶部容量统计 + 「清空垃圾筒」按钮
- 「已在云端 (N)」一段——蓝色顶条 + 云端徽章，本地缺失时左下橙色 `!` 徽章
- 「仅本地 (M)」一段——灰色顶条

**用户能做的动作**：

- 单击卡片——进多选模式（带圆心对勾）
- 多选「全选 / 取消全选 / 批量恢复 / 批量清理」 [TrashView.axaml:85-96](../../StartTooler/Views/TrashView.axaml#L85-L96)
- 单张右键上下文菜单（待 code-behind 核实）
- 单张「恢复」——成功弹 Toast「已恢复 xxx」+ 「跳转」按钮，直接回 Gallery 跳到该照片所在日期 [TrashView.axaml:374-396](../../StartTooler/Views/TrashView.axaml#L374-L396)
- 「清空垃圾筒」——全删（仅本地会真删）
- 「不再询问」30 天内不重复弹清空确认（[MainWindowViewModel.cs:242-279](../../StartTooler/ViewModels/MainWindowViewModel.cs#L242-L279)）

---

## Upload Job（上传任务）

**业务定义**：一次未完成的上传记录（断网 / 退出中断后下次启动时会显示在「高级」页）。

**用户看到的形态**——[AdvancedView.axaml:84-135](../../StartTooler/Views/AdvancedView.axaml#L84-L135)：

高级页 → 上传任务管理 Tab → 一行卡片：

```
文件相对路径                [恢复] [删除]
文件大小 · 分片大小 · 已传 N · 更新于 yyyy-MM-dd HH:mm:ss
```

**用户能做的动作**：

- 「刷新」——重读当前项目未完成任务
- 「全部恢复」——批量重启
- 「清空全部」——清记录（本地文件不受影响，下次再传从头）
- 单条「恢复 / 删除」

**启动时自动弹窗**：进入 Gallery 时如果 DB 有 InProgress 任务，会弹「上次没传完，恢复 / 稍后」[MainWindowViewModel.cs:243-279](../../StartTooler/ViewModels/MainWindowViewModel.cs#L243-L279)。

---

## Public Relay（公网代理）

**业务定义**：通过 VPS 把手机扫码的内网访问扩展到公网，需要一台自己的 VPS（阿里云 / 腾讯云…）。

**用户能做的动作**——[UploadServerView.axaml:319-727](../../StartTooler/Views/UploadServerView.axaml#L319-L727)：

**第一步：VPS 连接**

| 字段 | 说明 |
|---|---|
| 认证方式 | 密码 / SSH Key |
| SSH Host | 例：47.111.138.46 |
| SSH Port | NumericUpDown，默认 22 |
| User | SSH 用户名 |
| 凭据 | 密码 TextBox 或 Key 路径 + 浏览 |
| Remote Path | 例：~/starttooler |
| VPS 架构 | 自动 / amd64 / arm64 |

保存后右侧展开第二步。

**第二步：服务配置 + 控制**

- HTTP Port + TCP Port（NumericUpDown）
- 公网 Host（留空用 SSH Host）
- 「部署 / 启动 / 停止」按钮
- 实时状态指示灯 + 文字
- 「待下载 N 个」—— Go relay 推到 VPS 的文件数
- 「错误」提示区
- 「运行日志」只读 TextBox（80–200 高，可滚动）

应用退出时自动 kill VPS 上的 nohup 进程（[MainWindowViewModel.cs:213-225](../../StartTooler/ViewModels/MainWindowViewModel.cs#L213-L225)，5 秒超时）。

---

## Lightbox（灯箱）

**业务定义**：图片全屏预览窗口（独立顶级窗口，非内容区路由）。

**用户能做的动作**：双击 Gallery 瓦片触发 [GalleryView.axaml:355](../../StartTooler/Views/GalleryView.axaml#L355)。

**键盘快捷键** [LightboxWindow.axaml.cs:46-89](../../StartTooler/Views/LightboxWindow.axaml.cs#L46-L89)：

| 键 | 行为 | 适用 |
|---|---|---|
| ← / → | 翻页 | 全部 |
| + / = | 放大 | 图片 |
| - | 缩小 | 图片 |
| 0 | 重置缩放 | 图片 |
| Esc | 关闭窗口 | 全部 |
| F | 全屏切换 | 全部 |
| Space | 打开外部播放器 | 视频 / SER |

滚轮也支持缩放（图片模式）。

---

## Scan（扫描）

**业务定义**：把项目目录下的媒体文件入库的过程。

**用户能做的动作**：

- 点 Gallery 工具栏「刷新」—— 全量扫描 [MainWindow.axaml:143-162](../../StartTooler/Views/MainWindow.axaml#L143-L162)
- 全局拖拽外部文件到窗口—— 落入 `DragDropHandler` 处理（[MainWindow.axaml:516-542](../../StartTooler/Views/MainWindow.axaml#L516-L542) 显示「松开以导入 · N 个文件」）
- 首次启动完成自动跑一次扫描

**进度展示**：右下角 Toast「扫描中 · N/M」，结束后 2 秒自动消失 [GalleryViewModel.cs:340-388](../../StartTooler/ViewModels/GalleryViewModel.cs#L340-L388)。

**触发后续**：扫描完成 → 自动触发 Diary 聚类 [MainWindowViewModel.cs:199-206](../../StartTooler/ViewModels/MainWindowViewModel.cs#L199-L206)。

---

## AI Tagging（AI 打标）

**业务定义**：用配置好的 LLM 厂商，给一批照片补上主体标签。

**用户能做的动作**：Gallery 多选 → 工具栏「开始 AI」→ 后台串行/并发打标（取决于实现）→ 进度条 → 完成后回写 DB。

**取消**：工具栏「取消打标」按钮，AI 打标有独立 CTS，不会被其它操作冲掉。

**失败处理**：失败的瓦片右下角小 `!` 徽章，hover 看 error 文本 [GalleryView.axaml:540-553](../../StartTooler/Views/GalleryView.axaml#L540-L553)。

---

## Onboarding（首次引导）

**业务定义**：第一次启动时帮助用户完成三步上手设置。

**用户看到的形态**——[OnboardingCard.axaml](../../StartTooler/Controls/OnboardingCard.axaml) 居中浮层，三步：

1. 设置项目目录（→ 去设置按钮）
2. 扫描媒体文件
3. 配置云备份（可选）

每步有状态：✅ = 完成；彩色编号 = 未完成。

**用户能做的动作**：点每步右侧按钮跳到对应位置。完成后「全部完成后此卡片自动消失」。

**重置**：未发现明显的「重置引导」入口（待 ConfigService 相关方法核实）。

---

## Theme（主题）

**业务定义**：UI 配色。

**两个选项** [SettingsView.axaml:118-131](../../StartTooler/Views/SettingsView.axaml#L118-L131)：

| 选项 | 用途 |
|---|---|
| Deep Space | 默认；深蓝紫星空感 |
| Red Night Vision | 红黑夜间模式；保护暗适应，避免从望远镜取景回头时屏幕白光刺眼 |

切换 ComboBox 立即应用，配置落 DB。

---

## Notification（通知）

**业务定义**：右下角 Toast 浮层 + 状态栏历史列表。

**三种类型**（详见 [00 §5.4](00-product-overview.md)）：

- 进度类——上传 / 扫描 / AI 打标，2 秒后自动消失
- 简短类——"已恢复 xxx" 等
- 错误类——红色，长期挂直到用户关闭

**历史**：状态栏右下铃铛 → 弹出最近通知列表 [MainWindow.axaml:466-512](../../StartTooler/Views/MainWindow.axaml#L466-L512)，无清理按钮（最多 N 条滚动）。

---

## Capture Sequence（SER 采集序列）

**业务定义**：ZWO ASICAP / SharpCap 等拍摄软件输出的多帧原始数据（`.ser` 文件），每个文件对应一次完整的曝光序列。

**与普通视频 / 图片的区别**：

- 媒体类型过滤单独有「采集序列」一项 [MainWindow.axaml:243-258](../../StartTooler/Views/MainWindow.axaml#L243-L258)
- 瓦片占位用相机 + "SER" [GalleryView.axaml:416-432](../../StartTooler/Views/GalleryView.axaml#L416-L432)
- 双击不进灯箱（按 Space 走外部播放器） [LightboxWindow.axaml.cs:60-67](../../StartTooler/Views/LightboxWindow.axaml.cs#L60-L67)
- 无缩略图（`ThumbnailService` 对 .ser 返回 null，SerReader 留存用于读帧但不渲染）

**用户群体专属**：天文摄影拍摄的中间产物，跟 jpg/png 是并列关系，不属于视频播放场景。

---

## Config（配置导入/导出）

**业务定义**：把 Settings 里的全部 KV 备份到一个文件（含 API Key 等密钥！）。

**用户能做的动作**——[SettingsView.axaml:196-211](../../StartTooler/Views/SettingsView.axaml#L196-L211)：

- 「导出配置…」—— 写到磁盘
- 「导入配置…」—— 从磁盘读

UI 明示「含密钥，请妥善保存备份文件」。

---

## Shortcut（快捷键）

**全局导航** [MainWindow.axaml:36-41](../../StartTooler/Views/MainWindow.axaml#L36-L41)：

| 键 | 行为 |
|---|---|
| `Ctrl+1` | 媒体 |
| `Ctrl+2` | 上传服务 |
| `Ctrl+3` | 垃圾筒 |
| `Ctrl+4` | 设置 |
| `Ctrl+5` | 日记 |
| `Ctrl+6` | 高级 |

**NavRail tooltip**（macOS 自动用 `⌘`，其它平台 `Ctrl`）[MainWindowViewModel.cs:75-81](../../StartTooler/ViewModels/MainWindowViewModel.cs#L75-L81)。

**灯箱内**：见本篇「Lightbox（灯箱）」一节。

**多选模式下**：

| 键 | 行为 |
|---|---|
| `Shift+Click` | 范围选（点击 last clicked index 到当前） |
| 拖拽框选 | 跨瓦片矩形多选 |

---

## Stats（EXIF 统计）—— ⚠ 半成品

**状态**：MediaFile 的 EXIF 三字段（FocalLength35mm / Iso / ExposureTime）已落库；`doc/0.11/spec/19-statistics-dashboard.md` 有仪表盘规划；但 Settings / 高级页无任何入口，Gallery 时间轴也不按焦距/ISO 过滤。

用户当前**不能**在 UI 上看到 EXIF 统计视图。

详见 [03-half-built.md H1](03-half-built.md)。
