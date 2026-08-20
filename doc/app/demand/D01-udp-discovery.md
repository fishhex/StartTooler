# D01 · 移动端自动发现局域网 PC 端（UDP 广播）

> **状态**：需求稿（与 PC v0.12 实现对齐）
> **关联**：[`../02-pc-udp-protocol.md`](../02-pc-udp-protocol.md)（协议细节）、[`../03-mobile-checklist.md` §2.1](../03-mobile-checklist.md#21-udp-发现)（自测）、[`../../knowledge-base/API-03-udp-broadcast.md`](../../knowledge-base/API-03-udp-broadcast.md)（KB 权威）
> **PC 端代码**：`StartTooler/Services/UploadServerService.cs#L42-L43, L627-L685`

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.1（需求稿）** |
| 目标用户 | 在户外/家里拍摄后想把手机照片推到 PC 星助入库的用户 |
| 文档状态 | **需求 — 待评审** |
| PC 端能力状态 | ✅ 已实现（v0.12，[UploadServerService.StartUdpBroadcastAsync](../../StartTooler/Services/UploadServerService.cs#L627-L685)） |
| App 端目标 | 监听 9876 端口 → 过滤 → 展示 → 引导进入 HTTP 验证流程 |

---

## 1. 需求总览

### 1.1 背景

| 现状 | 痛点 |
|---|---|
| PC 端 v0.12 已实现 UDP 广播（每 2 s 明文 JSON） | App 端必须实现同等监听能力才能"零配置"发现 PC |
| 现有移动端只有 H5 浏览器扫码 | 用户必须能看见二维码才能连——离开 PC 身边就断链 |
| 多设备用户（手机 + 平板 + Mac + Win） | App 应能同时列出所有 PC 供选 |

### 1.2 核心价值

- **零配置**：不需账号 / 不需扫二维码 / 不需手填 IP
- **近场友好**：手机与 PC 在同一 LAN 自动出现
- **可视化**：UI 立即展示"发现了哪些 PC"

### 1.3 一句话概括

**移动端 App 在前台监听 9876 端口，过滤出 `service=="starttooler"` 的广播，把 PC 机器名 / IP / 端口 / 当前项目展示给用户，点击即进入 HTTP 验证流程。**

---

## 2. 用户场景

### 场景一：第一次回家拍完上传

> 1. 打开 App → 自动出现"扫描中..."
> 2. 1-2 s 后出现"鱼鱼的 MacBook · 当前项目 m42-2025-12-13"
> 3. 点选 → 进入 Token 输入页 → 输入 6 位 token → 主页
> 4. 后续启动：直接进主页（token 持久化）

### 场景二：家里多台 PC

> 1. 打开 App → 列表出现 2-3 台设备（Mac 工作室机 + Win NUC + 阳台 Mini）
> 2. 用户按机器名 + 当前项目判断选哪一台
> 3. 选错可点"切换 PC"重选

### 场景三：路由器 AP 隔离（兜底）

> 1. App 扫描 5 s 没有任何设备
> 2. 自动显示"未找到设备？手动输入 IP / 端口 / Token"
> 3. 用户手动填入 → 进入 Token 验证流程

---

## 3. 功能需求

### 3.1 监听目标（与 PC 协议对齐）

| 项 | 值 | 来源 |
|---|---|---|
| 协议 | UDP（IPv4） | [UploadServerService.cs#L42](file:///Users/hex/code/StartTooler/StartTooler/Services/UploadServerService.cs#L42-L43) |
| 端口 | **9876** | `UdpBroadcastPort = 9876` |
| 目标地址 | `255.255.255.255`（PC 端发） | `IPAddress.Broadcast` |
| 广播周期 | 2000 ms | `UdpBroadcastIntervalMs = 2000` |
| Payload 编码 | UTF-8 JSON | `JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts)` |
| 字段命名 | camelCase | `JsonNamingPolicy.CamelCase` |

### 3.2 监听生命周期

| 阶段 | App 端行为 |
|---|---|
| App 启动 | 立即开 UDP 监听（前台） |
| App 前台 | 持续监听（推荐永久 loop） |
| App 后台 | 系统会暂停监听（iOS / Android Doze）；不依赖后台监听 |
| App 回到前台 | 重置离线计时；不要让用户看到错误状态 |
| App 退出 | 关闭 socket |

### 3.3 必做：广播过滤

- [ ] **必须**校验 `payload["service"] == "starttooler"`，否则丢弃（避免其他广播干扰，如 SSDP / NetBIOS）
- [ ] **必须**校验 `payload["version"]` 至少为 `"0.12"`，不识别时显示提示（不是 PC 端兼容性问题，是协议变更警告）

### 3.4 必做：解析字段

| 字段 | 用途 |
|---|---|
| `name` | 列表展示（机器名，可能重复） |
| `port` | HTTP 请求 base url 端口 |
| `token` | HTTP 鉴权（**唯一权威来源**） |
| `currentProject` | 默认项目展示（**可能为空串 `""`，不是 null**） |
| 源 IP（socket `addr[0]`） | HTTP 直连用 |

### 3.5 必做：去重

| Key | 说明 |
|---|---|
| `name + port + ip` 三元组 | 同 LAN 多台 PC 可能同名；用三元组去重 |
| IP 视为浮动 | 切 Wi-Fi / 重启后 IP 会变，但 `name + port` 不变 |

### 3.6 必做：PC 列表展示

UI 至少包含：

- 机器名（`name`）
- 当前项目（`currentProject`，空时显示"PC 未选择项目"）
- IP（用 socket 收到的 `addr[0]`，仅供展示）
- 端口（与 IP 拼成"连接目标"，可手动覆盖为手动输入模式）
- Token（**不在 UI 明文展示给最终用户**——但开发者调试模式可看）

### 3.7 必做：手动输入兜底

- [ ] 扫描 5 s 无任何设备 → 显示"手动输入 IP / 端口 / Token"入口
- [ ] 手动输入后跳过 UDP 监听阶段，直接进入 HTTP 验证
- [ ] 手动输入的设备同样走 Token 持久化与后续广播自动覆盖流程

---

## 4. 非功能需求

| 维度 | 要求 |
|---|---|
| 启动到首条广播显示 | ≤ 3 s（PC 端每 2 s 一次，App 最多等 1 个周期） |
| 内存 | 监听 socket + 列表 < 5 MB |
| CPU | 主线程不阻塞；socket I/O 走后台线程 / IO Dispatcher |
| 电量 | 前台监听 1 小时耗电 ≤ 5% |

---

## 5. 平台要求（摘要，详细见 D05）

| 平台 | 关键 |
|---|---|
| iOS | `Info.plist` 必须含 `NSLocalNetworkUsageDescription`；首次扫描触发本地网络权限弹窗 |
| macOS | 同 iOS；macOS 13+ 对未沙盒二进制同样会拦截 UDP |
| Android | `DatagramSocket` 默认即可接收广播；**不需要** MulticastLock（这是组播的需求，UDP 广播不需要） |
| Android 9+ | 默认禁明文 HTTP，需 `network_security_config.xml` 放行 |

---

## 6. 边界情况

| 场景 | 处理 |
|---|---|
| 收到包 `service` 不匹配 | 丢弃，不入列表 |
| `currentProject` 为空串 `""` | UI 显示"PC 未选择项目"，不是 null |
| `name` 重复（同 LAN 多台 PC） | 按 `name+port+ip` 三元组合并；UI 用 IP 区分 |
| iOS 拒绝本地网络权限 | 引导到「设置 → 隐私 → 本地网络」开启；同时启用手动输入入口 |
| Android 厂商后台限制 | 提示用户加白名单（小米/华为/OPPO） |
| 路由器 AP 隔离 / 跨子网 | UDP 收不到 → 走手动输入兜底 |
| PC 端 HTTP 服务未启动 | UDP 不会发 → 列表为空 → 走手动输入兜底 |
| 6 s 内无任何包 | 走手动输入兜底 |

---

## 7. 不做清单

| 内容 | 理由 |
|---|---|
| 后台持续监听 | iOS / Android 系统限制，待后续 PoC 验证 |
| mDNS / Bonjour 双发现 | PC 端 v0.12 未实现，前瞻设计 |
| 蓝牙 / NFC / 二维码扫描发现 | 超出 v0.12 范围 |
| 加密广播 | PC 端 v0.12 明文 |
| 自动选最近 PC | 用户应主动选择 |

---

## 8. 验收标准（与 PC v0.12 对齐）

- [ ] PC 启动 HTTP 服务 → App 1-3 s 内出现设备
- [ ] PC 关闭 HTTP 服务 → App 6 s 内无新广播（注意：6 s 是 D04 的离线判定）
- [ ] 同 LAN 收到 2 台 PC → 列表展示 2 项，按机器名排序
- [ ] `service` 不匹配的广播不进入列表（抓 SSDP / NetBIOS 验证）
- [ ] `currentProject=""` 时显示"PC 未选择项目"
- [ ] iOS 真机调试可抓到 9876 流量（`sudo tcpdump -i en0 -n udp port 9876 -A`）
- [ ] Android 真机可 PCAP 抓包
- [ ] 手动输入入口在 5 s 无设备时自动出现

---

## 9. 关联文档

- 实现细节：[`../02-pc-udp-protocol.md`](../02-pc-udp-protocol.md)
- 自测清单：[`../03-mobile-checklist.md` §2.1](../03-mobile-checklist.md#21-udp-发现)
- 协议权威：[`../../knowledge-base/API-03-udp-broadcast.md`](../../knowledge-base/API-03-udp-broadcast.md)
- 协议变化记录：[`../README.md`](../README.md)