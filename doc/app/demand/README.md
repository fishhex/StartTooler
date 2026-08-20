# 移动端 App 需求文档（与 PC v0.12 对齐）

> 本目录用于整理**移动端 App 端**对 PC 端 v0.12 已实现能力的对接需求。**所有需求均严格对齐 PC v0.12 当前实现**，不存在前瞻设计。

## 一、本目录范围

| 项 | 说明 |
|---|---|
| **包含** | 与 PC v0.12 UDP 广播 + HTTP 业务对接相关的 App 端需求 |
| **不包含** | 移动端独立的产品需求（账号体系 / AI / 修图 / 跨广域网等） |
| **不包含** | PC 端协议扩展（X25519 / mDNS / nonce）——PC 端 v0.12 未实现 |
| **依据** | [UploadServerService.cs](file:///Users/hex/code/StartTooler/StartTooler/Services/UploadServerService.cs) v0.12 实现 |

## 二、PC v0.12 已实现的能力（移动端对接清单）

| 能力 | PC 端实现 | 移动端对接需求 |
|---|---|---|
| **UDP 自动发现** | 每 2 s 向 `255.255.255.255:9876` 广播明文 JSON | [D01](D01-udp-discovery.md) |
| **健康检查 HTTP** | `GET /api/v1/health`（无需鉴权） | [D02](D02-http-upload.md) §3.1.1 |
| **项目列表 HTTP** | `GET /api/v1/projects`（需 token） | [D02](D02-http-upload.md) §3.1.2 |
| **文件上传 HTTP** | `POST /api/v1/projects/{name}/upload`（需 token，multipart）| [D02](D02-http-upload.md) §3.1.3 |
| **6 位数字 Token** | 启动生成 + 用户手动 Regenerate | [D03](D03-token-auth.md) |
| **离线 / 在线判定** | 6 s 未收广播 = 离线（依赖 PC 端 2 s 周期）| [D04](D04-offline-reconnect.md) |
| **iOS / macOS / Android 权限适配** | PC 端无感知；App 端必须配 | [D05](D05-platform-permissions.md) |
| **错误码处理** | 200/400/401/404/405/500 + `failed[]` | [D06](D06-error-handling.md) |

## 三、需求文档列表

| # | 文件 | 主题 | 优先级 |
|---|---|---|---|
| D01 | [udp-discovery.md](D01-udp-discovery.md) | UDP 局域网发现（端口 9876、过滤、去重、手动输入兜底） | P0 |
| D02 | [http-upload.md](D02-http-upload.md) | HTTP 3 个接口（health / projects / upload）+ 上传约束 | P0 |
| D03 | [token-auth.md](D03-token-auth.md) | Token 鉴权状态机（广播权威 + 持久化 + 401 重试） | P0 |
| D04 | [offline-reconnect.md](D04-offline-reconnect.md) | 在线判定 + 后台回前台 + 网络事件 | P0 |
| D05 | [platform-permissions.md](D05-platform-permissions.md) | iOS / macOS Local Network + ATS；Android Cleartext | P0 |
| D06 | [error-handling.md](D06-error-handling.md) | 网络层 / HTTP 状态 / 业务失败三层错误 + i18n | P0 |

## 四、配套文档（项目内既有知识库）

| 文档 | 用途 |
|---|---|
| [`../README.md`](../README.md) | doc/app 目录总览（协议 / 自测 / 现状） |
| [`../01-lan-discovery-overview.md`](../01-lan-discovery-overview.md) | 局域网发现总览（含流程图 + 时序） |
| [`../02-pc-udp-protocol.md`](../02-pc-udp-protocol.md) | PC UDP 协议 v0.12 权威（字段 / 时序 / 安全） |
| [`../03-mobile-checklist.md`](../03-mobile-checklist.md) | 移动端实现 CheckList（自测 / iOS/macOS/Android 注意点） |
| [`../../knowledge-base/API-01-http-routes.md`](../../knowledge-base/API-01-http-routes.md) | HTTP 路由定义（KB 权威） |
| [`../../knowledge-base/API-03-udp-broadcast.md`](../../knowledge-base/API-03-udp-broadcast.md) | UDP 广播协议 KB（权威） |
| [`../../knowledge-base/API-06-error-i18n.md`](../../knowledge-base/API-06-error-i18n.md) | 错误国际化文案（KB） |

## 五、文档维护

- 本目录下文档为**移动端 App 需求稿**，任何 PC v0.12 代码改动（端口、字段、错误码、上传限制、Token 机制）需同步更新对应需求稿。
- 协议版本号跟随 PC 端 service 版本（当前 v0.12）。

### 变更记录

| 日期 | 版本 | 变更人 | 内容 |
|---|---|---|---|
| 2026-08-20 | v0.12（首次建档） | - | 创建 doc/app/demand 目录，按 PC v0.12 已实现能力拆 6 个需求稿：UDP 发现 / HTTP 上传 / Token 鉴权 / 离线重连 / 平台权限 / 错误处理 |