# D00 — PC 端需求（v0.14，聚合文档）

> **本文件是 PC 端的聚合需求文档**，覆盖：上传与共享 Tab 的 HTTP 服务 / QR / secret / config.db 持久化 / 公网 relay / UI。
>
> 配套：
> - App 端需求：[`../../knowledge-base/05-mobile-app.md`](../../knowledge-base/05-mobile-app.md)
> - 协议 KB：[`../../knowledge-base/API-01-http-routes.md`](../../knowledge-base/API-01-http-routes.md) / [`../../knowledge-base/API-04-qr-protocol.md`](../../knowledge-base/API-04-qr-protocol.md) / [`../../knowledge-base/API-04-config-schema.md`](../../knowledge-base/API-04-config-schema.md)
> - 跨设备同步：[`06-cross-device-sync.md`](../../knowledge-base/06-cross-device-sync.md)

## 〇、版本基线

| 版本 | 关键节点 |
|---|---|
| v0.10 | 首版上传服务（[`07-upload-server-lan.md`](../0.10/07-upload-server-lan.md)）：H5 `/upload` GET/POST |
| v0.11 | UI 改版（spec/17）：双卡片布局 / 多 IP 切换 / 端口冲突建议 |
| v0.12 | API 化：3 个 `/api/v1/*` 端点 + 6 位数字 Token + UDP 广播 |
| v0.13 | 移除 UDP / 6 位 Token；引入 QR 唯一发现 + 32 字符 hex secret + 多工作空间（App 端） |
| **v0.14** | **PC 端 secret 默认持久化到 `config.db.upload_secret`**；用户「重置密钥」为唯一主动失效途径 |

> v0.14 主变更：**扫码一次后，PC 重启 App 端无需再扫**。代价：QR 截图泄露的窗口期拉长到「用户主动重置」为止。

---

## 一、产品定位

**PC 端 = 主战场，移动端是配套快传工具**。

| 类比 | 关系 |
|---|---|
| AirDrop | iOS ↔ iOS |
| 投屏软件 | mobile ↔ PC |
| **星助 PC 端** | **PC 主战场 + mobile 配套** |

**核心场景**：拍摄现场用手机拍 → 回家打开 PC → App 扫 PC 端 QR → 一键推送到 PC → PC 端入库 + AI 打标 + 自动备份 OSS。

---

## 二、上传与共享 Tab（v0.14 完整版）

### 2.1 入口与状态

PC 端侧栏「上传与共享」Tab（[`UploadServerView.axaml`](../../../StartTooler/Views/UploadServerView.axaml)），含两块：

| 区块 | 内容 |
|---|---|
| **左：LAN 上传** | 端口 / URL / 启动-停止 / 上传历史 / 多 IP 切换 / 复制链接 |
| **右：扫码上传** | QR 码 / 当前 secret / 「重置密钥」按钮 / IP 变化提示 |

状态机：

```
       ┌──────────┐  启动   ┌──────────┐
       │  未启动   │ ──────→ │  运行中   │
       └──────────┘         └──────────┘
            ▲                     │
            │ 停止 / 端口冲突      │ 重置密钥 / IP 变化
            └─────────────────────┘
```

### 2.2 启动服务

```
1. 用户点「启动服务」
   ↓
2. UploadServerService 启动 HTTP Listener
   ↓
3. 绑定端口（8765 默认；端口占用时弹冲突建议 → 用户点「用建议端口」或换端口）
   ↓
4. [v0.14] 读 config.db.upload_secret
   ├─ 存在 → 复用
   └─ 不存在 → 生成 32 字符 hex → 写回 config.db.upload_secret
   ↓
5. 生成 QR 码 + DisplayUploadUrl
   ↓
6. 展示给用户
```

### 2.3 IP 变化

监听 [`NetworkChange.NetworkAddressChanged`](https://learn.microsoft.com/dotnet/api/system.net.networkinformation.networkchange.networkaddresschanged)：

- 自动刷新 QR 码的 `host` 字段（secret 不变）
- 顶部黄色提示条「网络 IP 已变，请重新扫码」
- App 端旧空间 health 失败 → 提示「连不上 PC」→ 引导重扫

### 2.4 端口变化

用户在「PORT」面板改端口 → 触发服务重启 → 复用 secret → QR 刷新。

### 2.5 重置密钥（v0.14 唯一主动失效途径）

```
用户在「扫码上传」卡片点「重置密钥」
   ↓
1. 生成新 32 字符 hex secret
   ↓
2. 写回 config.db.upload_secret（旧值覆盖）
   ↓
3. 触发 OnSecretChanged 事件
   ↓
4. UploadServerViewModel 刷新 QR + 当前 secret 显示
   ↓
5. 所有已扫码的 App 端 → 下次 projects/upload 请求 → 401
   → App 端弹「PC 端密钥已重置，请重新扫码」
```

> v0.13 的「启动重生成」机制在 v0.14 移除。重启 = 复用持久化值 = 无感。

---

## 三、HTTP 服务（PC 端是 server）

### 3.1 路由总表

| # | 路径 | 方法 | 鉴权 | 用途 | 服务端 |
|---|---|---|---|---|---|
| 1 | `/upload` | GET | ❌ | H5 上传页面 | [`UploadServerService.cs`](../../../StartTooler/Services/UploadServerService.cs) |
| 2 | `/upload` | POST | ❌ | H5 上传（multipart） | 同上 |
| 3 | `/api/v1/health` | GET | ❌ | 健康检查 + 取 `name` | 同上 |
| 4 | `/api/v1/projects` | GET | ✅ `?k=` | 项目列表 | 同上 |
| 5 | `/api/v1/projects/{name}/upload` | POST | ✅ `?k=` | 上传 | 同上 |

详细 DTO / 错误码：[`../../knowledge-base/API-01-http-routes.md`](../../knowledge-base/API-01-http-routes.md)。

### 3.2 Health 响应

```json
{
  "ok": true,
  "service": "starttooler",
  "version": "0.14",
  "name": "鱼鱼的 MacBook",
  "port": 8765,
  "secret": "7f3a9b2c8e1d4f6a...",
  "currentProject": "m42-2025-12-13"
}
```

| 字段 | 来源 |
|---|---|
| `name` | `Environment.MachineName`（v0.13 起作为 App 工作空间主键） |
| `version` | `StartTooler.csproj` 的 `<Version>` |
| `port` | UploadServerService.Port |
| `secret` | config.db.upload_secret（v0.14 起持久化） |
| `currentProject` | ProjectConfig.CurrentProjectName |

### 3.3 项目列表（`/api/v1/projects`）

App 端用 `?k={secret}` 拉取：

```json
{
  "projects": [
    { "name": "m42-2025-12-13", "path": "C:/Astro/m42-2025-12-13", "isCurrent": true },
    { "name": "ngc7000",        "path": "C:/Astro/ngc7000",        "isCurrent": false }
  ]
}
```

| 字段 | 来源 |
|---|---|
| `name` | `ProjectConfig.Name` |
| `path` | `ProjectConfig.Path` |
| `isCurrent` | `name == ProjectConfig.CurrentProjectName` |

### 3.4 上传（`/api/v1/projects/{name}/upload`）

```
multipart/form-data; ?k={secret}
   ↓
1. 校验 ?k = 当前 secret → 401 invalid secret
   ↓
2. 校验项目存在 → 404 project not found
   ↓
3. 遍历文件 → 校验扩展名白名单 + 500MB 上限
   ↓
4. 落盘到 {project_root}/{yyyy-MM-dd}/{filename}
   （重名 _1 / _2 / ...）
   ↓
5. 异步触发 ScanDirectoryAsync → media_files 新行
   ↓
6. 返回 { success, count, files[], failed[] }
```

详细 DTO：[`../../knowledge-base/API-01-http-routes.md`](../../knowledge-base/API-01-http-routes.md) §四。

### 3.5 扩展名白名单

```
.jpg .jpeg .png .raw .avi .mp4 .mov .mkv .webm .m4v .mpg .mpeg
```

上传时不在白名单的文件 → 进 `failed[]`（不走落盘逻辑）。

---

## 四、Secret 协议（v0.14 关键）

### 4.1 生成

| 项 | 值 |
|---|---|
| 长度 | 16 字节 = 32 字符 hex |
| 生成 | 首次启动 `RandomNumberGenerator.Fill(16)` |
| 范围 | `0-9a-f`（ToLowerInvariant） |
| 持久化（**v0.14**） | **默认持久化到 `config.db.upload_secret`** |
| 重置 | UI「重置密钥」按钮 → 重生成 + 写回 |

### 4.2 校验

| 维度 | 规则 |
|---|---|
| 大小写 | **仅接受小写** |
| 长度 | 严格 32 字符 |
| 字符集 | `^[a-f0-9]{32}$` |
| 错误码 | 401 `{ "error": "invalid secret" }` |

### 4.3 权威源（v0.14）

> **secret 的权威源是 PC 端 `config.db.upload_secret`**。
>
> - 启动时读：存在 → 复用；不存在 → 生成 + 写
> - 重置时：生成新值 → 覆盖写回
> - App 端 secret 失效 → 必须用户主动「重置密钥」（或手动清 config.db 字段）

### 4.4 与 v0.13 的对比

| 维度 | v0.13 | v0.14 |
|---|---|---|
| 存储位置 | 内存（`_currentSecret`） | **config.db.upload_secret** |
| 启动行为 | 重新生成 | 复用持久化值 |
| App 401 频率 | 每次 PC 重启 | 仅「重置密钥」时 |
| 用户体验 | 每次重启都要重扫 | **扫码一次终身免扫** |

---

## 五、QR 码生成

### 5.1 内容

```
http://{ip}:{port}/upload?k={32-hex-secret}
```

例：`http://192.168.1.10:8765/upload?k=7f3a9b2c8e1d4f6ab2c8e1d4f6ab2c8e`

| 部分 | 取值 |
|---|---|
| `host` | 当前选中 IP（多 IP 列表） |
| `port` | UploadServerService.Port |
| `?k=` | 当前 secret（来自 config.db.upload_secret） |

### 5.2 触发刷新

| 触发 | 行为 |
|---|---|
| HTTP 服务启动 | 首次生成 QR |
| IP 切换 | host 字段刷新（secret 不变） |
| 重置密钥 | secret + host 同步刷新 |
| 端口变化 | 重启服务后刷新 |

### 5.3 渲染

- 库：[QRCoder](https://github.com/codebude/QRCoder)（[UploadServerViewModel.BuildDisplayUrl()](file:///Users/hex/code/StartTooler/StartTooler/ViewModels/UploadServerViewModel.cs)）
- 位置：[UploadServerView.axaml](file:///Users/hex/code/StartTooler/StartTooler/Views/UploadServerView.axaml) L268-L339
- 像素：约 33×33（QR Version 4-5）

---

## 六、config.db 持久化（v0.14 新增字段）

### 6.1 表结构

```sql
-- 已有表（v0.10 起）
CREATE TABLE config (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

-- v0.14 新增
INSERT INTO config (key, value) VALUES
  ('upload_secret', '7f3a9b2c8e1d4f6ab2c8e1d4f6ab2c8e');
```

### 6.2 upload_secret 读写

| 操作 | 触发 | 实现位置 |
|---|---|---|
| 启动读 | UploadServerService 构造 / StartServer | 读 `IConfigService.Get("upload_secret")` |
| 不存在则生成 | 启动读返回 null | `RandomNumberGenerator.Fill(16)` + 写回 |
| 重置写 | UI「重置密钥」按钮 | 生成新值 + `IConfigService.Set("upload_secret", newValue)` |

### 6.3 与其他 config 字段的关系

| key | 用途 | 持久化 |
|---|---|---|
| `upload_port` | 默认端口 | ✅（已有） |
| `current_project` | 当前激活项目 | ✅（已有） |
| `upload_secret` | **v0.14 新增** | ✅ |
| `oss_*` | OSS 配置 | ✅ |
| `ai_*` | AI 配置 | ✅ |

> 完整配置 schema：[`../../knowledge-base/API-04-config-schema.md`](../../knowledge-base/API-04-config-schema.md)

### 6.4 跨版本迁移

- **v0.13 → v0.14**：自动。首次 v0.14 启动 → `upload_secret` 不存在 → 生成 + 写。
- **v0.14 → v0.13 降级**：用户主动清 `upload_secret` 行（或删 db 文件），回到每次启动重生成。
- **同一 PC 多用户**：各自读同一 `upload_secret`（无多用户隔离）。

---

## 七、UI 组件拆解

### 7.1 UploadServerView 整体布局

```
┌──────────────── UploadServerView ─────────────────┐
│                                                  │
│  ┌─── LAN 上传 ───┐  ┌─── 扫码上传 ───┐            │
│  │  PORT: 8765    │  │   ┌─────────┐  │            │
│  │  [启动/停止]   │  │   │  QR     │  │            │
│  │  URL: ...      │  │   │  CODE   │  │            │
│  │  [复制]        │  │   └─────────┘  │            │
│  │  上传历史      │  │  secret: 7f3a │            │
│  │  多 IP 切换    │  │  [重置密钥]   │            │
│  └────────────────┘  └────────────────┘            │
│                                                  │
│  ⚠️ 黄色提示条（IP 变化时）                       │
│  ⚠️ 红色错误条（端口冲突时）                      │
└──────────────────────────────────────────────────┘
```

### 7.2 关键控件

| 控件 | ViewModel 属性 | 说明 |
|---|---|---|
| 端口数字 | `Port` | 可编辑；触发 StartServer |
| 启动/停止按钮 | `IsRunning` → `CanStart/CanStop` | 互斥 |
| URL 显示 | `DisplayUploadUrl` | 多 IP 时按 `AddressIndex` 切换 |
| QR 码 | `QrCodeImage` (Bitmap) | 渲染 DisplayUploadUrl |
| 当前 secret | `CurrentSecret` | 「复制」+「重置密钥」 |
| 上传历史 | `UploadHistory` (ObservableCollection) | 不持久化 |
| 多 IP 列表 | `LocalAddresses` | 启动时拉取 |

### 7.3 状态可视化

| 状态 | 视觉 | 触发 |
|---|---|---|
| 启动成功 | 绿色「服务已启动」 | `StatusMessage` 非空 |
| 端口冲突 | 红色「8765 已被占用」+ 建议端口 | `IsPortConflict` |
| IP 变化 | 黄色「网络 IP 已变，请重新扫码」 | `NetworkChange` 事件 |
| 上传成功 | Toast / 通知卡片 | `OnUploadSuccess` |
| 上传失败 | Toast + 文件名 | `OnUploadError` |

---

## 八、上传历史与日志

### 8.1 上传历史（内存）

```csharp
public class UploadHistoryEntry
{
    public string FileName { get; set; }
    public long FileSize { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsSuccess { get; set; }
}
```

| 项 | 行为 |
|---|---|
| 写入时机 | `OnUploadSuccess` / `OnUploadError` 事件 |
| 持久化 | **故意不持久化**（重启清空，避免 LocalAddress / 路径历史信息误导） |
| 上限 | 不限（建议 UI 加 ScrollViewer 分页） |
| Token 日志脱敏 | `12****56` 形式（KB §十六） |

### 8.2 与 App 端上传进度的对应

App 端是「进度条 + 已传 N/M」；PC 端是「上传历史 ScrollViewer」。**两端独立展示**，无实时同步。

---

## 九、与公网 Relay 的关系

### 9.1 双模式并存

| 模式 | 入口 | 发现机制 | 适用场景 |
|---|---|---|---|
| **LAN** | 「上传与共享」Tab → 「扫码上传」 | QR 含 `?k=secret` | 同 WiFi；快速 |
| **公网 Relay** | 「上传与共享」Tab → 「公网中转」 | App 端需配置 relay 地址 | 跨网络；出差 |

详细：[`../../knowledge-base/06-cross-device-sync.md`](../../knowledge-base/06-cross-device-sync.md) §公网 Relay。

### 9.2 公网 Relay QR 不含 secret

```
http://relay.example.com:8766/upload
            ↑ 无 ?k=
```

App 端扫码视为「二维码无效」（v0.13 行为延续）。

### 9.3 PC 端 UI 切换

`UploadServerViewModel.IsPublicMode`：

- `true` → 「扫码上传」QR 块隐藏，提示「已切换至公网中转」
- `false` → 正常显示 QR

切到公网时：**不清** `config.db.upload_secret`（切回 LAN 还能用）。

---

## 十、安全边界

### 10.1 风险与缓解

| 风险 | 现状 | 缓解 |
|---|---|---|
| QR 截图泄漏 secret（v0.14） | 32 字符 hex | **仅用户主动「重置密钥」才失效**；泄露窗口拉长 |
| LAN 嗅探 HTTP | 明文 | 家庭 LAN 风险可控 |
| 摄像头拍屏 QR | 短期可见 | 短暂可见窗口 |
| 客户端伪造 QR | 无 | App 端 health 验证 |
| 重复扫码 | 无害 | 每次扫码写入持久化 |

### 10.2 不实现

- ❌ TLS / HTTPS（家用 LAN 风险可控）
- ❌ ECDH / 应用层加密
- ❌ 签名 / nonce
- ❌ secret 自动轮换（v0.14 仍仅「重置密钥」手动触发）

### 10.3 多设备 secret 共享

若用户把 QR 截图发给另一台设备：
- 两台 App 的 secret **相同**（都来自同一 PC config.db.upload_secret）
- 用户在任一 App 重扫同一 PC 的新 QR → secret 不变（仍是 `config.db.upload_secret`）
- 「重置密钥」后两台 App 同时 401

---

## 十一、与 App 端能力的对应

| App 端需求 | PC 端实现 | 文档位置 |
|---|---|---|
| HTTP 路由（3 个 API） | UploadServerService | §三 |
| 32 字符 hex secret（v0.14 持久化） | config.db.upload_secret | §四、§六 |
| Health 响应 `name` 字段 | `Environment.MachineName` | §3.2 |
| 500MB 单文件 | upload.html / UploadServerService | §3.4 |
| 扩展名白名单 | `AllowedExtensions` 常量 | §3.5 |
| 重名 `_1/_2` 策略 | `name_1.ext` 递增 | §3.4 |
| H5 浏览器上传（无 secret） | `/upload` GET/POST | §3.1 |
| 公网 relay（QR 不带 secret） | PublicRelayService | §九 |
| 多 IP 切换 | `LocalAddresses` + `AddressIndex` | §七 |
| 「重置密钥」按钮 | `ResetSecretCommand` | §2.5、§四 |

App 端完整对接需求：[`../../knowledge-base/05-mobile-app.md`](../../knowledge-base/05-mobile-app.md) §十三。

---

## 十二、边界情况

| 场景 | 行为 |
|---|---|
| 端口被占 | 弹冲突建议（v0.11 spec/17） |
| IP 变化 | 黄色提示 + QR 刷新 + App 端需重扫 |
| secret 重置时正在上传 | 当前请求可能 401；App 端需重新扫码 |
| 多 PC 同名 | App 端按 `name+ip` 去重（KB §十四） |
| 配置文件被外部删除 | 启动时 `upload_secret` 为空 → 自动生成新值 |
| 配置文件被外部篡改 | 启动时按 hex 32 校验；非法 → 重生成 |
| 多用户共用同一 PC | 读同一 secret；无隔离 |
| 跨子网 | 走公网 relay（不在本服务范围） |

---

## 十三、非功能需求

| 维度 | 要求 |
|---|---|
| HTTP 启动（PC） | ≤ 1s |
| QR 生成 | ≤ 200ms |
| Secret 生成（首次） | 启动时 ≤ 50ms |
| QR 刷新（IP/重置） | ≤ 100ms |
| 上传并发 | 1 串行（按请求顺序） |
| 单批上限 | 50 张 |
| 单文件上限 | 500MB |
| Token 日志脱敏 | `12****56` 形式 |

---

## 十四、不做清单

| 内容 | 理由 |
|---|---|
| UDP 自动发现 | **v0.15 已彻底删除代码**（_udpClient / StartUdpBroadcastAsync / UdpBroadcastPayload） |
| 6 位数字 Token | 改为 32 字符 secret |
| mDNS / Bonjour | 复杂、跨平台难 |
| 加密 / 签名 / nonce | 家庭 LAN 风险可控 |
| 后台推送通知 | PC 端未落地 |
| 断点续传 / 分片 | MVP 不需要 |
| 自动重试上传 | 用户决定 |
| 跨子网（不走 relay） | 不可达 |
| 后台持续监听 | 系统限制 |
| 自定义 schema | v0.14 不引入 |
| 「记住密钥」开关 UI | **v0.14 决定**：全局持久化，不暴露开关 |
| secret 自动轮换 | **v0.14 不引入**；仅「重置密钥」手动触发 |
| 上传历史持久化 | 故意不持久化（v0.11 决定） |

---

## 十五、验收标准

### 15.1 PC 端

- [ ] 启动服务 → 200ms 内展示 QR
- [ ] `config.db.upload_secret` 不存在 → 启动时生成 + 写回
- [ ] `config.db.upload_secret` 存在 → 启动时复用
- [ ] 点「重置密钥」→ 新 secret 写回 db + QR 刷新 + 通知
- [ ] IP 变化 → QR host 字段刷新 + 黄色提示条
- [ ] 端口冲突 → 红色错误条 + 建议端口列表
- [ ] 多 IP 列表非空 → 显示区块，支持 `AddressIndex` 切换
- [ ] 上传历史 ScrollViewer 在上传后追加新条目
- [ ] secret 在日志中脱敏（`12****56`）
- [ ] 重启 PC 服务 → secret 复用（v0.14 关键验收）

### 15.2 跨端

- [ ] App 端扫码后 PC 重启 → App 端无需重扫（**v0.14 新验收**）
- [ ] PC 点「重置密钥」→ App 端 projects 401 → 弹「PC 端密钥已重置」
- [ ] PC 端 IP 变化 → App 端 health 失败 → 引导重扫
- [ ] PC 端端口变化 → App 端连不上 → 引导重扫
- [ ] QR 截图发给另一设备 → 两台 App 同时可用同一 secret

---

## 十六、版本兼容

| PC 版本 | 兼容 App 版本 |
|---|---|
| v0.14 | v0.13+（App 不感知 secret 来源） |
| v0.13 | v0.13 |
| v0.12 | v0.12（UDP 协议） |

**升级路径**：

- v0.13 → v0.14：自动。首次启动生成 `upload_secret`；用户无感。
- v0.14 → v0.13 降级：用户需手动清 `upload_secret`（或删 db 文件）。
- v0.14 PC + v0.13 App：兼容（App 仅按 `?k=` 调用，不查持久化）。
- v0.13 PC + v0.14 App：**降级体验**——v0.13 PC 启动重生成 secret → App 端 401 → 每次重启都要重扫。

---

## 十七、相关文档

| 文档 | 用途 |
|---|---|
| [`../../knowledge-base/05-mobile-app.md`](../../knowledge-base/05-mobile-app.md) | App 端聚合需求 |
| [`../../knowledge-base/API-01-http-routes.md`](../../knowledge-base/API-01-http-routes.md) | HTTP 路由权威（5 端点 / DTO / 错误码） |
| [`../../knowledge-base/API-04-qr-protocol.md`](../../knowledge-base/API-04-qr-protocol.md) | QR 内容 + secret 协议 |
| [`../../knowledge-base/API-04-config-schema.md`](../../knowledge-base/API-04-config-schema.md) | config.db 字段总表 |
| [`../../knowledge-base/API-05-app-persistence.md`](../../knowledge-base/API-05-app-persistence.md) | App 端持久化 |
| [`../../knowledge-base/06-cross-device-sync.md`](../../knowledge-base/06-cross-device-sync.md) | 三通道对比（LAN / OSS / relay） |
| [`../0.10/07-upload-server-lan.md`](../0.10/07-upload-server-lan.md) | v0.10 上传服务原始需求 |
| [`../../0.11/spec/17-upload-server-ui-redesign.md`](../../0.11/spec/17-upload-server-ui-redesign.md) | v0.11 UI 改版 spec |

---

## 十八、变更记录

| 日期 | 版本 | 内容 |
|---|---|---|
| 2026-08-21 | v0.10 | 首版 HTTP 上传服务（H5 `/upload`） |
| 2026-08-21 | v0.11 | UI 改版（双卡片 / 多 IP / 端口冲突建议） |
| 2026-08-21 | v0.12 | API 化（3 个 `/api/v1/*` + 6 位 Token + UDP 广播） |
| 2026-08-21 | v0.13 | 移除 UDP / 6 位 Token；QR 唯一发现 + 32 字符 secret |
| 2026-08-21 | **v0.14** | **PC 端 secret 默认持久化到 `config.db.upload_secret`**；启动复用；「重置密钥」为唯一主动失效途径 |
| 2026-08-21 | **v0.15** | 彻底删除 UDP 广播代码（_udpClient / StartUdpBroadcastAsync / GetBroadcastEndpoints / RecordClientIp / PruneExpiredClients / UdpBroadcastPayload）；PC 端不再监听 9876 |
