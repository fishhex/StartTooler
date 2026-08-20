# D03 · Token 鉴权与状态机

> **状态**：需求稿（与 PC v0.12 实现对齐）
> **关联**：[`../02-pc-udp-protocol.md` §2.3](../02-pc-udp-protocol.md#23-token-来源与权威性与-checklist-23-配对阅读)、[`../03-mobile-checklist.md` §2.3](../03-mobile-checklist.md#23-token-更新策略以-pc-实现为准)
> **PC 端代码**：`StartTooler/Services/UploadServerService.cs#L34-L36`（单字段）、`L173-L186`（生成 + 验证）

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.1（需求稿）** |
| 目标用户 | 所有需要访问 PC 端 HTTP 业务的移动端用户 |
| 文档状态 | **需求 — 待评审** |
| PC 端能力状态 | ✅ 已实现（v0.12，`GenerateToken` / `RegenerateToken` / `ValidateToken`） |
| App 端目标 | 实现完整的 Token 状态机：来源、覆盖、持久化、401 重试 |

---

## 1. 需求总览

### 1.1 背景

PC 端 v0.12 只有一个 Token 实例字段 `_currentToken`：

- **构造时**生成一次
- **StartAsync 时**重新生成一次
- **RegenerateToken()** 手动重置（UploadServerViewModel"重置"按钮）
- UDP 广播每 2 s 重读 `_currentToken`，所以**新 Token 最迟 2 s 内出现在下一次广播中**
- HTTP `/api/v1/health` 响应 body **也带回** `_currentToken`（仅用于回包校验）

→ App 端必须严格按"广播为权威源 + health 响应仅校验"原则设计状态机。

### 1.2 核心价值

- **零感知换 Token**：PC 端重置后 App 端不需用户重新输入
- **明文风险可控**：LAN 嗅探可见，但仅临时凭据
- **可调试**：开发者模式下能看到当前 Token

### 1.3 一句话概括

**Token 的唯一权威来源是 UDP 广播 payload，App 端每次收到广播都无条件覆盖内存值；持久化到 Keychain/EncryptedSP；401 时等下一次广播（≤2 s）+ 用新 Token 重试一次。**

---

## 2. 用户场景

### 场景一：首次启动

> 1. App 启动 → 收不到任何广播（或持久化为空）
> 2. 进连接页 → 选 PC → 进 Token 输入页
> 3. 用户输入 6 位数字 → 调 `health` 验证（仅验证可达，不取 token）
> 4. 用户输入的 token 与广播 token 一致（首次）→ 进主页
> 5. 持久化：`{ip, port, token, name}`

### 场景二：PC 端用户点了"重置 Token"

> 1. PC 端 `RegenerateToken()` → `_currentToken` 变化 + `OnTokenChanged` 事件
> 2. 下一次广播（≤ 2 s）带新 token
> 3. App 收到新广播 → **无条件覆盖**内存 + 持久化的 token
> 4. App 主页无感（如果当前不在请求中）
> 5. App 正在上传中：401 → 等广播 + 重试一次 → 成功

### 场景三：App 进程被回收重启

> 1. App 启动 → 读持久化 token
> 2. 后台线程：UDP 扫描中
> 3. 先用持久化 token 调 `health`（无鉴权，本步只是探活 + 验证）
> 4. 收到新广播 → 用广播 token 覆盖持久化
> 5. 进主页

### 场景四：App 端 token 与广播 token 不一致（理论上不会发生）

> 1. 内存中 token 已被广播覆盖，理论上不会不一致
> 2. 若真的发生：以广播 token 为准，立即覆盖

---

## 3. 功能需求

### 3.1 Token 来源唯一权威：UDP 广播

| 来源 | 用途 | 是否作为主动取值 |
|---|---|---|
| UDP 广播 `payload["token"]` | **权威源** | ✅ |
| `/api/v1/health` 响应 `token` | 仅用于"与权威源回包校验" | ❌ |
| `/api/v1/projects` 401 重试用 | 重试时**必须等下一次广播拿到的新 token** | ❌（不要用 health 取） |
| 用户手动输入 | 仅在首次 / 重置时使用 | ✅ |

### 3.2 Token 字段约束

| 项 | 值 |
|---|---|
| 长度 | 6 位数字 |
| 范围 | 000000 - 999999 |
| 生成 | PC 端启动 + 用户手动 RegenerateToken() |
| 变更反映 | ≤ 2 s（下一拍广播） |
| 持久化（PC 端） | 不持久化（每次启动重生成） |

### 3.3 Token 状态机

```
         收到广播
           │
           ▼
   ┌──────────────────┐
   │  内存中该 PC 的   │
   │  token 被覆盖     │
   └──────────────────┘
           │
   ┌───────┴──────────┐
   │ 持久化值被覆盖     │
   └──────────────────┘
           │
   受保护请求 401
           │
           ▼
   ┌──────────────────┐
   │ 等下一次广播       │  ≤ 2 s
   │ (≤ 2 s)           │
   └──────────────────┘
           │
   ┌───────┴──────────┐
   │ 用新 token 重试    │
   └──────────────────┘
           │
   ┌───────┴──────────┐
   │ 仍 401            │
   └──────────────────┘
           │
           ▼
   提示用户"PC 已拒绝当前 Token，请重新发现"
   跳回连接页
```

### 3.4 持久化策略

| 平台 | 存储 | 字段 |
|---|---|---|
| iOS | Keychain（推荐）| `pc_ip`, `pc_port`, `pc_token`, `pc_name`, `last_current_project`, `last_seen_at` |
| Android | EncryptedSharedPreferences | 同上 |

| 字段 | 持久化 | 原因 |
|---|---|---|
| `pc_ip` | ✅ | App 重启后可直接尝试直连 |
| `pc_port` | ✅ | 同上 |
| `pc_token` | ✅ | 重启后能继续业务 |
| `pc_name` | ✅ | 列表展示用 |
| `last_current_project` | ✅（可选） | 重启后默认选中 |
| `last_seen_at` | ✅ | 用于"30 天未连接提示清理" |

### 3.5 覆盖式更新

- 每次收到新广播，**无条件**用 `payload["token"]` 覆盖内存中的 token
- 不做合并、不做 diff、不做版本比较
- IP 浮动时按 `name + port` 识别同一 PC，仍用广播 token 覆盖

### 3.6 不做主动轮询

- PC 端**没有**"取 token"接口
- App 端**不应**自己造轮询（如 GET /api/v1/token）
- 只能被动等广播

### 3.7 401 重试流程

受保护接口（`/api/v1/projects`、`/api/v1/projects/{name}/upload`）返回 401 时：

1. 等下一次广播（最迟 2 s）
2. 拿到新 token 后**重试一次**当前请求
3. 仍 401 → 提示用户"PC 已拒绝当前 Token，请确认 PC 端 token 与 App 一致"，引导重新发现

### 3.8 健康检查的 token 处理

`GET /api/v1/health`：

- 该接口**本身无鉴权**（不需要 token 就能调）
- 响应 body 里回带 `_currentToken`——**仅作"回包校验"**
- App 端不要用 health 响应里的 token 做主动取值
- 调试模式可展示 health 返回的 token 与内存 token 是否一致（应一致）

### 3.9 Token 显示位置

| 场景 | 显示 |
|---|---|
| Token 输入页 | 6 位数字输入框（iOS `isSecureTextEntry` / Android `password` InputType） |
| 主页 | **不**明文展示 Token（安全考虑） |
| 调试页（开发者模式）| 明文 + "复制"按钮 |
| 设置页"当前连接" | 展示 Token 的首末各 2 位（如 `12****56`） |

### 3.10 清理时机

| 场景 | 反应 |
|---|---|
| 401 响应 | 清 Token，保留 IP（让用户重输 token 而不是重选 PC） |
| 用户主动"切换 PC" | 清 IP + Token + 名字 |
| 30 天未连接 | 提示清（不主动清） |
| 卸载 / 清 App 数据 | 系统自动清 |

---

## 4. 非功能需求

| 维度 | 要求 |
|---|---|
| Token 存储加密 | 强制（Keychain / EncryptedSP） |
| Token 日志脱敏 | 日志中 token 一律 `12****56` |
| Token crash 上报脱敏 | crash report 不含 token |
| 401 重试最多一次 | 不要无限重试 |
| 等广播最长 2 s | 不要等超过 2.5 s（用户体验差） |

---

## 5. 边界情况

| 场景 | 处理 |
|---|---|
| 持久化的 PC 已离线（30 天未连） | 启动时提示"上次连接的 PC 超过 30 天未连接，是否仍尝试？" |
| 持久化的 PC 当前 token 已被 PC 重置 | 用持久化 token 调 projects → 401 → 触发重试 |
| 持久化的 PC 当前 IP 已变 | 按 `name+port` 仍识别同一 PC；用新 IP 重新发现 |
| 持久化的 PC 当前端口已变 | 用户主动扫描；提示"PC 端口已变，请重新发现" |
| 用户手动重置 PC 端 token | App 端最迟 2 s 内自动更新，无需用户介入 |
| App 进程被回收 | 启动后用持久化 token 试探 + 立即覆盖 |
| 同 LAN 收到伪造广播 | 用 `service == "starttooler"` 过滤；伪造者可模拟广播但无法绕过 HTTP 鉴权（无 token） |

---

## 6. 不做清单

| 内容 | 理由 |
|---|---|
| 自造轮询取 token | PC 端无此接口 |
| Token 加密广播 | PC 端 v0.12 明文 |
| 双 Token 机制 | PC 端只有一个 token |
| Token 版本号 | 6 位数字，重置即换 |
| Token 过期时间 | PC 端没有时间机制；用户手动重置 |
| App 端持久化加密 key 自己生成 | 用平台自带（Keychain / EncryptedSP） |

---

## 7. 验收标准

- [ ] 首次启动进入连接页 → 选 PC → 输入 token → 进主页 → token 持久化
- [ ] PC 端 `RegenerateToken()` → App 最迟 2 s 内自动覆盖内存值（不应需用户重输）
- [ ] PC 重启（Token 变更）→ App 重新收到广播后自动用新 Token 继续可用
- [ ] PC 重启同时换了 IP → App 用 `name+port` 仍能识别同一 PC
- [ ] 持久化 token 与最新广播 token 不一致 → App 立即覆盖
- [ ] 401 响应 → 等下一次广播（≤ 2 s）+ 重试一次 → 成功
- [ ] 401 重试仍 401 → 提示用户重新发现
- [ ] App 进程被回收重启 → 用持久化 token 试探 health，无需等下次广播
- [ ] 健康检查响应里 token 与内存 token 应一致（不一致说明实现 bug）
- [ ] 日志 / crash report 中 token 脱敏

---

## 8. 关联文档

- 协议细节：[`../02-pc-udp-protocol.md` §2.3](../02-pc-udp-protocol.md#23-token-来源与权威性与-checklist-23-配对阅读)
- 自测清单：[`../03-mobile-checklist.md` §2.3](../03-mobile-checklist.md#23-token-更新策略以-pc-实现为准)
- HTTP 鉴权接口：[D02 §3.1](D02-http-upload.md#31-必须实现的-3-个-http-接口)
- UDP 发现：[D01](D01-udp-discovery.md)