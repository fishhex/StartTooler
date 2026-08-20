# D06 · 错误处理与失败兜底

> **状态**：需求稿（与 PC v0.12 实现对齐）
> **关联**：[`../03-mobile-checklist.md` §二.2 / §六 / §七](../03-mobile-checklist.md#22-http-验证首连)、[`../../knowledge-base/API-06-error-i18n.md`](../../knowledge-base/API-06-error-i18n.md)

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.1（需求稿）** |
| 目标用户 | 所有移动端 App 用户 |
| 文档状态 | **需求 — 待评审** |
| PC 端能力状态 | ✅ 返回标准错误码 200/400/401/404/405/500 + 错误消息 |
| App 端目标 | 三类错误分层处理；用户感知级 vs 技术超时严格区分；兜底链路清晰 |

---

## 1. 需求总览

### 1.1 错误分层

| 类别 | 例子 | App 反应 |
|---|---|---|
| 网络层 | 断网 / 超时 / DNS 失败 | Retry 按钮 |
| HTTP 状态 | 401 / 404 / 405 / 500 | Toast / 跳页 |
| 业务失败 | `failed[]` 列表（扩展名 / 超大）| 跳过该文件 + 失败列表 |

### 1.2 用户感知 vs 技术超时（重要！）

| 时间窗 | 含义 | 出处 |
|---|---|---|
| 3s | 单次 `GET /api/v1/health` 请求级超时 | D01 |
| 5s | 单次 `GET /api/v1/projects` 请求级超时 | D02 |
| 60s | 单次 multipart 上传请求级超时 | D02 |
| 2s | PC 端 UDP 广播周期；新 Token 最迟 2s 反映 | D03 |
| 6s（3 个广播周期）| "未收到广播 → 离线"判定 | D04 |
| 5s | 用户感知级"看不到任何 PC"阈值（**仅 UX 节奏用**） | 本文档 |

> 关键：**5s（UX 节奏）和 6s（离线判定）不重叠**。5s 用于"看不到任何 PC → 引导手动输入"；6s 用于"在线的 PC 突然消失 → 标灰"。

### 1.3 一句话概括

**App 端把错误分网络层 / HTTP 状态 / 业务失败三层；按用户感知 vs 技术超时严格区分时间窗；每个错误有明确文案 + 下一步按钮。**

---

## 2. 用户场景

### 场景一：5s 扫描无设备

> 1. 用户首次启动 App
> 2. 扫描 5 s 仍无设备
> 3. UI 自动显示"未找到 PC 端星助？手动输入 IP / 端口 / Token"

### 场景二：HTTP 401

> 1. 用户输入 token 错误 / PC 端重置了 token
> 2. App 收到 401
> 3. 自动等下一次广播（≤ 2 s） + 用新 token 重试一次
> 4. 仍 401 → 提示"PC 已拒绝当前 Token，请重新发现"
> 5. 跳回连接页

### 场景三：HTTP 404（project not found）

> 1. 用户选了一个项目，但 PC 端项目目录被删了
> 2. App 收到 404
> 3. 提示"项目已失效，请重新选择项目"
> 4. 自动刷新项目列表

### 场景四：上传超大文件

> 1. 用户选了 1 个 600MB 视频
> 2. PC 端 `failed[]` 含 `exceeds 500MB limit`
> 3. App 弹窗"1 个文件过大（500MB 上限），请在 PC 端处理"
> 4. 用户取消或选择继续

### 场景五：上传扩展名不支持

> 1. 用户选了 .jpg + .txt
> 2. PC 端 `failed[]` 含 `unsupported extension .txt`
> 3. App 弹窗"30 个成功，1 个失败（.txt 不支持）"
> 4. 列表展示成功 + 失败两个分区

### 场景六：上传中网络断

> 1. 用户正在上传 30 张照片
> 2. 已传 18 张时 Wi-Fi 断开
> 3. App 端断网错误
> 4. 提示"已上传 18 个文件，剩余 12 个未上传，请检查网络后重试"

---

## 3. 功能需求

### 3.1 网络层错误

| 错误 | App 反应 |
|---|---|
| DNS 失败 | "找不到 PC 端地址" + Retry |
| TCP 连接拒绝 | "PC 端星助服务未启动" + 引导检查 |
| TCP 连接超时（3s health / 5s projects）| "PC 端响应超时" + Retry |
| 上传超时（60s）| "上传超时" + 已上传文件已落盘 + 列表保留 |
| 上传中断（Wi-Fi 切换 / 飞行模式）| "网络中断，已上传 N 个文件" + Retry |

### 3.2 HTTP 状态错误

| 状态 | PC 端响应 | App 反应 |
|---|---|---|
| 200 | 正常 | 按 D02 处理 |
| 400 | `{ "error": "..." }` | Toast 显示错误文案（multipart 格式错）|
| 401 | `{ "error": "invalid token" }` | 触发 D03 状态机（等广播 + 重试） |
| 404 | `{ "error": "project 'xxx' not found" }` | 提示"项目失效" + 刷新项目列表 |
| 405 | `{ "error": "method not allowed" }` | 客户端代码 bug；记日志；用户友好提示"客户端错误，请升级 App" |
| 413 | （PC 端未实现，走 `failed[]`）| 不应触发 |
| 500 | `{ "error": "..." }` | Toast "PC 服务异常" + Retry |

### 3.3 业务失败（`failed[]`）

App 端必须展示 `failed[]` 列表，每项含原因：

| PC 端 reason | App 端文案 |
|---|---|
| `unsupported extension .exe` | "扩展名 .exe 不支持" |
| `exceeds 500MB limit` | "文件过大（500MB 上限）" |
| 其他 reason | 原样显示（PC 端已国际化） |

### 3.4 5s UX 节奏阈值（与 6s 技术超时严格区分）

| 触发 | 反应 |
|---|---|
| 扫描 5s 内首屏仍无设备 | 显示"手动输入"入口（**UX 节奏，不是离线判定**） |
| 已在线的 PC 6s 未收广播 | 标记离线（**技术判定**） |

### 3.5 失败兜底链路

```
网络层错误
    │
    ├─ 健康检查失败 → Retry 按钮（不立即跳页）
    │
    └─ 上传中断 → 显示"已传 N 个"，保留列表，用户决定是否重试

HTTP 状态错误
    │
    ├─ 401 → D03 Token 状态机（自动等广播 + 重试一次）
    │
    ├─ 404 (project not found) → 刷新项目列表
    │
    ├─ 405 → 客户端 bug；记日志；提示升级
    │
    └─ 500 → Toast + Retry

业务失败 (failed[])
    │
    └─ 弹窗"X 个成功，Y 个失败"，详情可展开
```

### 3.6 i18n

详见 [`../../knowledge-base/API-06-error-i18n.md`](../../knowledge-base/API-06-error-i18n.md)。

App 端需要的文案：

| Key | 中文 | 英文 |
|---|---|---|
| `err.network.timeout` | 网络超时 | Network timeout |
| `err.network.refused` | PC 端服务未启动 | PC service not running |
| `err.http.401` | PC 已拒绝当前 Token | PC rejected current token |
| `err.http.404.project` | 项目已失效 | Project no longer exists |
| `err.http.500` | PC 服务异常 | PC service error |
| `err.upload.exceeds_limit` | 文件过大（500MB 上限） | File exceeds 500MB limit |
| `err.upload.unsupported_ext` | 扩展名不支持 | Unsupported file extension |
| `err.upload.network_lost` | 网络中断，已上传 N 个 | Network lost, N files uploaded |
| `ux.no_device_found` | 未找到 PC，请检查 Wi-Fi | No PC found, check Wi-Fi |
| `ux.offline` | PC 已离线 | PC is offline |
| `ux.token_reset` | PC 已重置 Token，请重输 | PC reset token, please re-enter |

### 3.7 日志脱敏

| 字段 | 日志中显示 |
|---|---|
| Token | `12****56` |
| IP | 完整（如 `192.168.1.10`）|
| 端口 | 完整（如 `8765`）|
| 文件名 | 完整（不含路径） |
| 错误响应 body | 完整 |

### 3.8 Crash 上报脱敏

- 禁用 Sentry / Bugsnag 默认行为；确保 token / IP 不出现在 crash report
- 如必须上报，手动 scrubbing

---

## 4. 非功能需求

| 维度 | 要求 |
|---|---|
| 错误响应时间 | ≤ 200 ms（除了真实网络超时） |
| 用户感知阈值 | 5s UX 节奏独立于 6s 离线判定 |
| 错误文案 | 国际化；不暴露技术细节（stack trace、SQL 等） |
| 日志 | token 必脱敏 |

---

## 5. 边界情况

| 场景 | 处理 |
|---|---|
| 持续网络中断 | 用户可手动取消 / 重试；不阻塞 UI |
| PC 端服务崩溃中 | health 失败 → 提示用户检查 PC 端 |
| PC 端返回非 JSON（如 HTML） | 客户端代码 bug；记日志；用户友好提示 |
| PC 端返回空 body | 同上 |
| 上传 multipart 解析失败（PC 端返回 400） | 客户端代码 bug；记日志 |
| 同 PC 多次 401 重试 | 不要无限重试；超 3 次强制跳连接页 |

---

## 6. 不做清单

| 内容 | 理由 |
|---|---|
| 自动重试上传（除 401） | 用户决定；避免重复上传 |
| 离线缓存项目列表 | 超出 v0.12 范围 |
| 端到端加密 | PC 端明文 |
| 复杂错误分类（warning/info） | 用户只关心成功 / 失败 |

---

## 7. 验收标准

### 7.1 网络层

- [ ] 健康检查超时（3s）→ 显示"PC 端响应超时" + Retry
- [ ] 上传超时（60s）→ 显示"上传超时，已传 N 个" + 列表保留
- [ ] 上传中网络断 → 显示"网络中断，已传 N 个" + 列表保留
- [ ] 飞行模式开关 → App 端网络事件正确响应

### 7.2 HTTP 状态

- [ ] 401 → 等下一次广播 + 重试一次 → 成功 / 仍 401 跳连接页
- [ ] 404 (project) → 刷新项目列表 + 提示
- [ ] 500 → Toast + Retry

### 7.3 业务失败

- [ ] 上传 .exe → `failed[]` 含原因，UI 弹窗展示
- [ ] 上传 600MB → `failed[]` 含原因，UI 弹窗展示
- [ ] 上传 1.txt → `failed[]` 含原因，UI 弹窗展示

### 7.4 兜底链路

- [ ] 5s 扫描无设备 → 显示"手动输入"入口
- [ ] 6s 已在线 PC 未广播 → 标灰
- [ ] 两个阈值不重叠（5s UX / 6s 技术）

### 7.5 i18n

- [ ] 所有错误文案有中英文
- [ ] 错误文案不暴露技术细节
- [ ] Token / IP 日志脱敏

---

## 8. 关联文档

- 自测清单：[`../03-mobile-checklist.md` §六 / §七](../03-mobile-checklist.md#六ux-与稳定性)
- HTTP 错误码：[`../02-pc-udp-protocol.md` §3.2](../02-pc-udp-protocol.md#32-鉴权约定来自-validatetoken)
- KB i18n：[`../../knowledge-base/API-06-error-i18n.md`](../../knowledge-base/API-06-error-i18n.md)
- UDP 发现：[D01](D01-udp-discovery.md)
- HTTP 上传：[D02](D02-http-upload.md)
- Token 鉴权：[D03](D03-token-auth.md)
- 离线重连：[D04](D04-offline-reconnect.md)
- 平台权限：[D05](D05-platform-permissions.md)