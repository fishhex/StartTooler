# 局域网发现总览（PC → 移动 App）

> 本文档**严格对齐 PC 当前实现**（v0.12，[UploadServerService.cs](../../StartTooler/Services/UploadServerService.cs)、KB [`API-03`](file:///Users/hex/code/StartTooler/doc/knowledge-base/API-03-udp-broadcast.md)）。协议现状为**无加密、单向广播**，本文会明确写出来。

## 一、协议现状（一句话）

**PC 端每 2 秒向局域网广播一条明文 JSON（UDP `255.255.255.255:9876`），告诉 LAN 内客户端"我在这里 + HTTP 端口 + 6 位数字 Token"。** App 拿到后用 TCP/HTTP 连 PC 做业务，没有 mDNS、没有非对称加密、没有应用层签名/Nonce。

## 二、发现流程图（与实现一致）

```mermaid
flowchart TD
    A[PC 端 HTTP 服务启动<br/>UploadServerService.StartAsync] --> B[启动 UDP 广播<br/>StartUdpBroadcastAsync]
    B --> C[每 2 秒向 255.255.255.255:9876<br/>发送 JSON 明文]
    C --> D[App 在同 LAN 监听 9876]

    D --> E{验证 service 字段}
    E -->|不等于 starttooler| Z[丢弃]
    E -->|通过| F[解析 name / port / token<br/>记录到设备列表]

    F --> G[App 主动 HTTP GET /api/v1/health<br/>无需 token，验证真实可达]
    G -->|200| H[把当前 PC 加入已配对列表]
    G -->|超时 / 非 200| X[标灰，可手动重试]

    H --> I[App HTTP GET /api/v1/projects?token=xxx<br/>拉取项目列表]
    I --> J[用户选项目<br/>POST /api/v1/projects/{name}/upload multipart]
    J --> K[业务完成]
```

## 三、协议层现状（重要）

| 能力 | 现状 | 风险 |
|------|------|------|
| UDP 广播加密 | **无** | LAN 嗅探可见：`token`（明文 6 位）+ `currentProject`（项目名）+ `name`（PC 名）。不含文件内容，不含文件名 |
| HTTP 业务加密 | **无**（明文 HTTP，端口见 PC 启动配置） | LAN 嗅探可见 query 上的 token、Header 上的 X-Token、URL 路径上的项目名，以及 multipart 上传的文件名与内容 |
| 应用层认证 | 6 位数字 Token（明文广播，每次启动 + 手动重置时变更） | LAN 内重放可连接 |
| 防重放 | **无**（没有 nonce / 时间戳） | 抓包重发广播可持久化伪 PC |
| 防伪造 | **无**（没有消息签名） | 同 LAN 客户端可伪广播 |
| 跨路由器 | **不支持**（UDP 广播不跨子网） | 跨子网需走公网 relay |
| 跨公网 | 走 [cross-device-sync.md](file:///Users/hex/code/StartTooler/doc/knowledge-base/06-cross-device-sync.md) | 与 LAN 发现无关 |

代码层依据（[UploadServerService.cs#L627-685](file:///Users/hex/code/StartTooler/StartTooler/Services/UploadServerService.cs#L627-L685)）：

- `UdpClient.EnableBroadcast = true`
- `IPAddress.Broadcast:9876` 每 `UdpBroadcastIntervalMs = 2000` ms 发送一次
- Payload 序列化用 `JsonOpts` (`CamelCase`)
- Token = `Random.Shared.Next(0, 1_000_000).ToString("D6")` —— 6 位数字
- 启动 / 关闭：`StartAsync` 中 `_udpTask = StartUdpBroadcastAsync(...)`，`Stop()` 中 `_udpCts?.Cancel()` + `UdpClient?.Close()`

## 四、典型调用时序（应用层视角）

```
PC (StartTooler)                App (Mobile)
  | HTTP 服务起来                | 后台持续监听 UDP 9876
  | 启动 UDP 广播                 |
  | ── UDP 255.255.255.255:9876 ──>|
  |    每 2 秒 1 次 JSON 明文      | 收到 → 校验 service == "starttooler"
  |                                | 解析 {name, port, token, currentProject}
  |                                | UI 展示设备列表
  |                                | 用户点击该 PC
  |                                | TCP GET /api/v1/health（无需 token）
  | <── HTTP 200 ─────────────────|
  | (确认 service/version/port)  |
  |                                | TCP GET /api/v1/projects?token=xxx
  | <── HTTP 200 (items[])) ─────|
  |                                | 用户选项目 → POST /api/v1/projects/{name}/upload multipart
  | <── HTTP 200 (成功) ──────────|
```

## 五、为何不用 mDNS / 自定义加密协议

KB [`API-03` §二](file:///Users/hex/code/StartTooler/doc/knowledge-base/API-03-udp-broadcast.md#二为什么用-udp-广播而不是-mdns--bonjour) 给出的取舍：

| 方案 | 优点 | 缺点 |
|------|------|------|
| **UDP 广播**（当前） | 0 配置 / 跨平台 / 1ms 响应 | 不跨路由器 / 不跨 VLAN |
| mDNS / Bonjour | 跨子网（router 支持） | 需要 multicast 配置 / 复杂 |
| 自定义 TCP 发现 | 可靠 | 跨平台实现繁琐 |

家用 LAN 场景下 UDP 广播**足够**。跨子网 / 跨路由器场景由公网 relay 通道兜底。

## 六、与"理想发现"协议的差异（已写文档需知会）

此前给移动端同学准备的"配对码 + X25519 + Nonce + 心跳"协议（[`02-pc-udp-protocol.md`](file:///Users/hex/code/StartTooler/doc/app/02-pc-udp-protocol.md) 同名区段）在**当前 v0.12 PC 实现上不存在**，是前瞻设计。要落地这些能力，需要后端先做：

- PC 端引入 mDNS 服务注册（如 Avahi / Bonjour）
- PC 端生成 + 持久化 X25519 密钥对（首启生成，Token 用临时 6 位码）
- App 端实现 NSLocalNetwork 权限、MulticastLock、NsdManager
- HTTP 改为 HTTPS / DTLS 或加应用层 AEAD

如确需驱动，可单独立 spec，先行 dev 任务，不阻塞 App 主流程对接。

## 七、移动端对接时只看哪几份文档

- 协议字段、时序 → [`02-pc-udp-protocol.md`](file:///Users/hex/code/StartTooler/doc/app/02-pc-udp-protocol.md)（已重写）
- 实现 CheckList / 自测 → [`03-mobile-checklist.md`](file:///Users/hex/code/StartTooler/doc/app/03-mobile-checklist.md)（已重写）
- 行为细节 / 防火墙 / 调试 → KB [`API-03`](file:///Users/hex/code/StartTooler/doc/knowledge-base/API-03-udp-broadcast.md)
