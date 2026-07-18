# D08 — 拍摄会话管理

> 关联文档：`02-data-layer.md`（数据层）、`05-gallery-view.md`（Gallery 视图）、`11-ai-tagging.md`（AI 打标）

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.2（细化评审稿）** |
| 目标用户 | 天文摄影爱好者 |
| 文档状态 | **需求 — 已细化** |
| 关联模块 | Gallery (05)、Data Layer (02)、AI Tagging (11) |

---

## 1. 需求总览

### 1.1 背景

当前 StartTooler 用「拍摄日期」来组织照片，形成时间轴。但一次出摊（Shooting Session）通常持续数小时，跨越午夜后会被拆成两个日期。用户无法回答以下问题：

- 「12 月的双子流星雨之夜我拍了多少张？」
- 「用 RedCat 51 + ASI2600MC 那次拍了哪些目标？」
- 「昨晚的视宁度怎么样？适不适合拍行星？」

**本需求将「日期分组」升级为「拍摄会话」——把照片按实际出摊行程组织，支持笔记、环境记录和器材关联。**

### 1.2 核心价值

| 维度 | 现在 | 将来 |
|---|---|---|
| 回顾方式 | 按日历日期线性浏览 | 按出摊会话浏览，附带天气、器材、拍摄历程 |
| 跨午夜 | 一张卡横跨两天 | 自动合并到同一会话 |
| 经验积累 | 无记录 | 每次出摊的天气、参数都是可复盘的数据 |
| 数据维度 | EXIF 埋在每个文件里 | 会话级别汇总——总曝光帧数、器材组合 |

---

## 2. 功能需求

### 2.1 会话的自动创建

**规则**：

- 系统按照片拍摄时间自动聚类，同一会话满足：**相邻照片时间间隔 < 4 小时**。
- 跨越午夜的出摊自动合并（例如 22:00 拍到次日 02:00）。
- 用户可手动合并/拆分会话。

**聚类算法**：按 `shot_at` 排序后一趟扫描，O(n log n) 主要开销在排序（已有索引）。

**触发时机**：每次扫描完成后全量重聚类，不依赖增量。同一数据集结果稳定可复现。

**持久化策略**：聚类完成后自动 `UPSERT` 会话 + 批量 `UPDATE media_files.session_id`，无需用户手动确认。

**边界**：

| 情况 | 处理 |
|---|---|
| 下午拍太阳、晚上拍月亮 | 间隔 > 4h → 自动拆成两个会话 |
| 连续两天出摊但白天没拍 | 间隔可能 18h → 自动拆开 |
| 所有照片无连续间隔 > 4h | 整个项目视作一个会话 |
| 只有 1 张照片 | 也创建单照片会话，保持一致性 |
| 照片 shot_at 为 null | 不参与聚类，session_id 保持 NULL |
| 新导入照片打破 4h 窗口 | 全量重聚类，但手动分配的 session_id 保持不变 |
| 删除照片后 | 会话 start/end 由聚类时写入，下次聚类自动修正 |

### 2.2 会话列表入口

**位置**：Gallery 左栏 TabControl 新增「会话」分组模式，与「时间」「标签」同级。

```
TabControl
├── 时间
├── 标签
└── 会话 ★
```

**排序**：按 `start_time` 倒序（最近出摊在最上面）。

**快捷时间刷选**：会话视图下也生效，按会话 `start_time` 过滤。

每个会话卡片展示：

| 元素 | 说明 |
|---|---|
| 会话标题 | 自动生成：`{YYYY-MM-DD} 出摊`，用户可重命名 |
| 起止时间 | `22:00 → 次日 02:15`，总时长 `4h15m` |
| 照片/视频数量 | 「42 张照片 · 2 段视频」 |
| 主要目标 | 该会话内频次最高的 3 个标签（如「银河 · 猎户座大星云」） |
| 天气/器材摘要 | 用户填写后显示，未填不显示 |

### 2.3 会话详情

点击会话 → 右侧图墙展示该会话的全部照片（复用现有网格）。

**会话详情面板**（图墙顶部可折叠 Expander）：

#### 2.3.1 基本信息

| 字段 | 类型 | 来源 |
|---|---|---|
| 标题 | 可编辑文本 | 自动生成，可手动改 |
| 描述/笔记 | 多行文本 | 用户手动输入 |
| 起止时间 | 自动计算 | 照片 EXIF 时间范围 |

#### 2.3.2 环境记录

环境记录精简为 3 个字段，全部可通过 API 自动获取，并支持手动编辑覆盖：

| 字段 | 子项 | 类型 | 自动获取方式 | API |
|---|---|---|---|---|
| **地点** | 地名 | 文本 | EXIF GPS → 逆地理编码 | 高德 / Nominatim |
| **天气** | 风向 | 文本（N/NE/E/SE/S/SW/W/NW） | 经纬度 + 日期 | Open-Meteo |
| | 风级 | 蒲福风级 0-12 | 同上 | Open-Meteo |
| | 云量 | 文本（晴/少云/多云/阴） | 同上 | Open-Meteo |
| **光害** | Bortle 等级 | 下拉 1-9 | 经纬度 | lightpollutionmap.info |

**API 详情**：

- **高德逆地理编码**（地点）：`https://restapi.amap.com/v3/geocode/regeo?key=KEY&location=lng,lat`，免费 5000 次/天，需在设置页配置 API Key
- **Open-Meteo Archive**（天气）：`https://archive-api.open-meteo.com/v1/archive?latitude=...&longitude=...&start_date=...&end_date=...&hourly=wind_direction_10m,wind_speed_10m,cloud_cover`，免费，无需 Key，支持回溯至 1940 年
- **lightpollutionmap.info**（光害）：`https://www.lightpollutionmap.info/QueryRaster/?qk=brd_wa_2015&ql=wa_2015&qt=point&qd=lng,lat`，免费，无需 Key

**自动获取逻辑**：进入会话详情时，取该会话内第一张有 GPS 坐标的照片 → 查天气/光害 → 填充字段。无 GPS → 字段留空，用户手动输入。

**温度不单独列出**：天文摄影中体感温度 ≠ 传感器温度，不是核心复盘指标，由笔记自由描述。

#### 2.3.3 器材关联（Phase 2，见 §6）

> 拍摄会话可关联器材组合（需 D13 设备管理模块）。此字段在 D13 未实现时不显示。

| 字段 | 说明 |
|---|---|
| 器材组合 | 从器材库选择一个组合（望远镜 + 相机 + 赤道仪 + 滤镜） |

### 2.4 会话操作

| 操作 | 行为 |
|---|---|
| 重命名 | 双击标题编辑 |
| 合并 | 多选两个会话 →「合并」，合并后取最早开始时间和最晚结束时间 |
| 拆分 | 在会话详情面板中点击「拆分」，弹出对话框选择一张照片作为分割点 |
| 删除会话 | 仅删除会话元数据，照片不动 |
| 导出会话报告 | 生成文本/Markdown 报告（含天气、器材、拍摄目标、照片缩略图） |

**拆分交互**：在会话详情面板中增加「拆分」按钮 → 弹出对话框，显示该会话内照片的时间序列 → 用户选择一张照片作为分割点 → 以该照片为界，之前的归旧会话，之后的归新会话。

### 2.5 照片与会话的关系

- 一张照片**只属于一个会话**
- 新导入的照片自动分配到最近的会话（按时间间距规则）
- 手动把照片从一个会话拖到另一个会话 → 更新归属
- 删除会话 → 照片变为「未归类」（仍出现在时间轴，会话字段为空）
- 手动分配的 session_id 在自动聚类时不会被覆盖

---

## 3. 数据需求

### 3.1 新表 `sessions`

```sql
CREATE TABLE sessions (
    id          TEXT PRIMARY KEY,          -- GUID
    project_id  TEXT NOT NULL,             -- 所属项目路径
    title       TEXT NOT NULL,             -- 会话标题（自动生成/手动修改）
    description TEXT DEFAULT '',           -- 笔记
    start_time  TEXT NOT NULL,             -- ISO 8601
    end_time    TEXT NOT NULL,             -- ISO 8601
    location    TEXT DEFAULT '',           -- 拍摄地点
    wind_dir    TEXT DEFAULT '',           -- 风向（N/NE/E/SE/S/SW/W/NW）
    wind_level  INTEGER DEFAULT 0,         -- 蒲福风级 0-12
    cloud_cover TEXT DEFAULT '',           -- 云量（晴/少云/多云/阴）
    bortle      INTEGER,                   -- 1-9，光害等级
    rig_id      TEXT,                      -- 器材组合 ID（关联 D13 设备管理）
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    FOREIGN KEY (project_id) REFERENCES ... -- 逻辑外键
);
```

> 变更说明（v0.2）：移除了原 v0.1 中的 seeing / transparency / temperature / wind / moon_phase 字段，改为 location / wind_dir / wind_level / cloud_cover / bortle 五个字段。

### 3.2 修改 `media_files` 表

```sql
ALTER TABLE media_files ADD COLUMN session_id TEXT;
CREATE INDEX idx_media_files_session ON media_files(session_id);
```

`session_id` 为 `NULL` 时表示未归类到任何会话。

### 3.3 新增 Repository 方法

```csharp
// ISessionRepository (新增接口)
Task<IReadOnlyList<Session>> GetByProjectAsync(string projectPath, CancellationToken ct);
Task<Session?> GetByIdAsync(string sessionId, CancellationToken ct);
Task UpsertAsync(Session session, CancellationToken ct);
Task DeleteAsync(string sessionId, CancellationToken ct);

// IMediaRepository (追加)
Task<IReadOnlyList<MediaFile>> GetBySessionAsync(string sessionId, CancellationToken ct);
Task AssignToSessionAsync(IReadOnlyList<string> mediaIds, string sessionId, CancellationToken ct);
Task UnassignFromSessionAsync(IReadOnlyList<string> mediaIds, CancellationToken ct);
```

---

## 4. UI 交互

### 4.1 会话列表

```
┌──────────────────────────┐
│ 🔭 拍摄会话          (3) │
├──────────────────────────┤
│ ┌──────────────────────┐ │
│ │ 12月14日 双子流星雨   │ │  ← 用户重命名过的标题
│ │ 22:00 → 次日 04:30   │ │
│ │ 6h30m · 156 张       │ │
│ │ 📍 浙江安吉天荒坪     │ │
│ │ 🌬️ 西北风 3级 · ☁️ 晴 │ │
│ │ 🌃 Bortle 4          │ │
│ └──────────────────────┘ │
│ ┌──────────────────────┐ │
│ │ 12月07日 冬季银河     │ │
│ │ 20:15 → 23:00       │ │
│ │ 2h45m · 67 张        │ │
│ │ 📍 浙江临安牵牛岗     │ │
│ └──────────────────────┘ │
│ ┌──────────────────────┐ │
│ │ 11月30日 出摊         │ │  ← 未填环境信息
│ │ 01:00 → 03:30       │ │
│ │ 2h30m · 23 张        │ │
│ └──────────────────────┘ │
└──────────────────────────┘
```

### 4.2 会话详情面板

在选中会话后，图墙顶部显示可折叠 Expander：

```
┌──────────────────────────────────────────────┐
│ 📋 双子流星雨之夜                        [✏️] │
│                                              │
│ 📅 2025-12-14 22:00 → 12-15 04:30  (6h30m) │
│                                              │
│ 🌤️ 环境                                     │
│   📍 地点: 浙江省杭州市临安区天荒坪            │
│   🌬️ 天气: 西北风 3级 · 晴                  │
│   🌃 光害: Bortle 4                         │
│                                              │
│ 📝 笔记                                     │
│ ┌──────────────────────────────────────┐    │
│ │ 凌晨 1 点左右流星活动最密集，峰值    │    │
│ │ 约 120 颗/小时。相机用鱼眼朝东拍    │    │
│ │ 了 3 小时延时，ISO 6400。          │    │
│ └──────────────────────────────────────┘    │
│                                              │
│ 📊 拍摄统计                                 │
│   照片: 156 张 · 视频: 2 段                  │
│   主要目标: 流星(89) · 猎户座(23) · 银河(18) │
└──────────────────────────────────────────────┘
```

### 4.3 创建与编辑

- **自动创建**：照片导入后自动检测时间间隔，生成会话（后台静默完成）
- **手动创建**：会话列表底部「+ 新建会话」，手动指定起止时间 → 选择照片归入
- **编辑**：点击详情面板中的「✏️」进入编辑模式
- **拆分**：在会话详情面板中点击「拆分」按钮 → 选择分割点照片 → 执行拆分

---

## 5. 边界与空态

| 状态 | 表现 |
|---|---|
| 无任何照片 | 会话列表显示空态：「导入照片后将自动生成拍摄会话」 |
| 有照片但全部 session_id=NULL | 显示：「暂未聚类，请刷新以自动生成会话」+ [刷新] 按钮 |
| 只有一次出摊 | 仅一个会话卡片，不显示合并/拆分按钮 |
| 数千张照片跨数年 | 会话列表支持滚动；支持按年份折叠 |
| 照片 EXIF 时间缺失 | 该照片不参与会话自动聚类，标为「时间未知」组 |
| 照片无 GPS 坐标 | 环境字段无法自动获取，用户手动输入 |
| 会话删除后 | 照片回到「未归类」状态，仍按拍摄日期在时间轴可见 |
| 编辑环境字段时项目切换 | 有未保存修改 → 弹窗确认「放弃修改？」 |

---

## 6. 分阶段实施

### Phase 1（本需求范围）

- 会话自动创建（按时间间距聚类）
- 会话列表 + 详情面板
- 环境记录字段（地点、天气、光害）
- 笔记编辑
- 照片与会话的关联、重新分配
- 合并/拆分/删除会话
- 高德逆地理编码配置（设置页）

### Phase 2（依赖 D13 设备管理）

- 会话关联器材组合
- 会话报告含器材信息

---

## 7. 不做清单

| 内容 | 理由 |
|---|---|
| 视宁度/透明度/温度/月相/风力 | 视宁度/透明度需人工判断且无历史 API；温度对复盘价值低；月相可本地计算后再议 |
| 按会话自动打标签 | 会话是组织维度，不是内容维度；内容打标仍由 AI 完成 |
| 会话间的照片比较 | 独立需求，见 D10 照片对比 |
| 出摊路线/GPS 轨迹 | 需要移动端配合，超出桌面应用范围 |
| 拍摄清单（计划拍什么） | 独立需求，可列入 v0.14 拍摄计划器 |
| 多人协作、共享会话 | 当前定位为单用户本地应用 |

---

## 8. 待决策事项

| # | 事项 | 选项 | 决策 |
|---|---|---|---|
| 1 | 自动聚类的时间阈值 | A: 4h / B: 6h / C: 可配置 | **A**，硬编码 4h，后续按需加配置 |
| 2 | 会话标题自动命名规则 | A: `日期 + "出摊"` / B: `日期 + 主要目标名` | **A**（简单不犯错），用户可手动改 |
| 3 | 环境字段全部可选 | A: 全部可选 / B: 部分必填 | **A**，降低录入门槛 |
| 4 | 会话详情面板位置 | A: 图墙顶部可折叠 / B: 右侧固定面板 | **A**，不占用照片网格空间 |

---

## 9. 新增配置

| 配置项 | 存储位置 | 设置 UI | 说明 |
|---|---|---|---|
| 高德 API Key | `AppConfig.AmapApiKey`（`config.db` key `app`） | 设置页「通用」Tab | 可选，不填则地点自动获取退化为手动输入 |

Open-Meteo 和 lightpollutionmap.info 无需 API Key，不需要额外配置。

---

## 10. 实现步骤

| 步骤 | 内容 | 改动文件 |
|---|---|---|
| Step 1 | 数据层：`sessions` 表 + `Session` 模型 + `ISessionRepository`/`SessionRepository`；`media_files` 加 `session_id` 列 | `Models/`、`Data/`、SQL migration |
| Step 2 | 聚类服务：`SessionClusteringService`（排序 → 扫描 → 分组 → 持久化） | `Services/SessionClusteringService.cs` |
| Step 3 | 环境 API 服务：`EnvironmentService`（高德逆地理编码、Open-Meteo 天气、光害查询） | `Services/EnvironmentService.cs` |
| Step 4 | 左栏 UI：`GroupMode` 加 `Session`；TabControl 加第 3 个 TabItem；会话列表 | `Models/Models.cs`、`GalleryView.axaml`、`GalleryViewModel.cs` |
| Step 5 | 详情面板：图墙顶部 Expander + 环境字段 + 笔记 + 拆分/合并/重命名 | `GalleryView.axaml`、`SessionViewModel`（新） |
| Step 6 | 集成：扫描后触发聚类 → 会话列表刷新 → 选中会话 → 环境自动获取 | `GalleryViewModel.cs` |
| Step 7 | 设置页：高德 API Key 输入框 | `AppConfig.cs`、`SettingsView.axaml`、`SettingsViewModel.cs` |