# 0.13 · v0.13 变更记录（移动端发现简化）

> **生效日期**：2026-08-21
> **变更定位**：移除 UDP 自动发现 + 6 位数字 Token 协议，PC 端仅靠 QR（内含 32 字符 hex secret）作为唯一发现入口。

## 一、核心变更

| 维度 | v0.12 | v0.13 |
|---|---|---|
| 发现 | UDP 广播 255.255.255.255:9876 | **QR 唯一** |
| 鉴权 | 6 位数字 token（持久化在 PC 端）| 32 字符 hex secret（启动重生成） |
| 入口 | UDP 扫描 / 手动输入 / QR | **仅 QR 扫码** |
| 工作空间 | 单一最近 PC | **多空间** |
| iOS Local Network 权限 | 必需 | **不需要** |

## 二、PC 端代码变更清单

详见 [07-upload-server-lan.md](../07-upload-server-lan.md)（待改写） + 实施 Checklist。

### 2.1 删除

- UDP 广播字段与方法（`_udpClient` / `StartUdpBroadcastAsync` / `GetBroadcastEndpoints` / `RecordClientIp` / `UdpBroadcastPayload`）
- `using System.Collections.Concurrent;`
- `using System.Net.Sockets;`

### 2.2 改造

- `CurrentToken` → `CurrentSecret`（32 字符 hex 启动重生成）
- `ValidateToken` → `ValidateSecret`
- `?token=` → `?k=`（health / projects / upload）
- Health 响应 `Token` 字段 → `Secret` 字段
- QR 内容 `?t={token}` → `?k={secret}`

### 2.3 保留

- 多 IP 切换 UI
- H5 浏览器上传（无 secret）
- 公网 relay（QR 不带 secret）
- 文件落盘 / 500MB / 扩展名白名单

## 三、App 端对接

**唯一权威文档**：[`doc/knowledge-base/05-mobile-app.md`](../knowledge-base/05-mobile-app.md)（v0.13 聚合）

| 维度 | 文档 |
|---|---|
| 主文档（聚合）| [05-mobile-app.md](../knowledge-base/05-mobile-app.md) |
| HTTP 路由 | [API-01-http-routes.md](../knowledge-base/API-01-http-routes.md) |
| QR 协议 | [API-04-qr-protocol.md](../knowledge-base/API-04-qr-protocol.md) |
| App 持久化 | [API-05-app-persistence.md](../knowledge-base/API-05-app-persistence.md) |
| 错误 i18n | [API-06-error-i18n.md](../knowledge-base/API-06-error-i18n.md) |
| 三通道对比 | [06-cross-device-sync.md](../knowledge-base/06-cross-device-sync.md) |

## 四、破坏性变更

- v0.13 App 不兼容 v0.12 PC 端（UDP 移除）
- v0.12 App 不兼容 v0.13 PC 端（Token 协议移除）

## 五、文档归档

- 删除 `doc/app/` 整个目录（已无内容）
- 删除 [API-03-udp-broadcast.md](../knowledge-base/) 等 v0.12 文档

## 六、变更记录

| 日期 | 版本 | 内容 |
|---|---|---|
| 2026-08-21 | v0.13 | 移除 UDP 自动发现 + 6 位 Token；引入 QR 唯一发现 + 32 字符 secret + 多工作空间模型 |
