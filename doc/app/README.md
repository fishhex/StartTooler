# 移动端 App 开发文档

本目录用于给**移动端 App** 同学对接 PC 端局域网发现 / 业务通信的协议与实现指引。

> ⚠️ **协议现状**：PC 端 v0.12 实现为**单向 UDP 广播 + HTTP 明文 + 6 位数字 Token**。**没有加密、没有签名、没有 nonce**。本目录文档严格对齐 [StartTooler/Services/UploadServerService.cs](../../StartTooler/Services/UploadServerService.cs)，不存在"前瞻设计"。若需 mDNS / X25519 等能力，需要 PC 端先开发。

## 目录

| 文件 | 说明 | 适用对象 |
|------|------|----------|
| [01-lan-discovery-overview.md](file:///Users/hex/code/StartTooler/doc/app/01-lan-discovery-overview.md) | 局域网发现总览（流程图 + 现状说明） | App、PC 端工程师对齐场景 |
| [02-pc-udp-protocol.md](file:///Users/hex/code/StartTooler/doc/app/02-pc-udp-protocol.md) | PC UDP 发现 + HTTP 业务协议 v0.12（字段、时序、安全边界） | App 端工程师实现主参考 |
| [03-mobile-checklist.md](file:///Users/hex/code/StartTooler/doc/app/03-mobile-checklist.md) | 移动端实现 CheckList（iOS / Android 关键注意点 + 自测） | App 端工程师开发与自测 |

## 配套文档（项目内既有知识库）

- `doc/knowledge-base/05-mobile-app.md`：移动 App 知识库总览
- `doc/knowledge-base/API-03-udp-broadcast.md`：UDP 广播协议 KB（权威来源，本目录文档与之严格对齐）
- `doc/knowledge-base/API-01-http-routes.md`：HTTP 路由定义
- `doc/0.12/spec/04-mobile-lan-sync.md`：移动端局域网同步规格说明
- `doc/0.12/spec/05-mobile-app.md`：移动端 App 规格说明

## 文档维护

- 本目录下文档为**对移动端对外公开**版本，必须与 PC 端代码一致——任何代码改动（端口、字段、错误码、上传限制等）需同步更新 `02-pc-udp-protocol.md` 与 `03-mobile-checklist.md`。
- 协议版本号跟随 PC 端 service 版本（当前 v0.12）。

### 变更记录

| 日期 | 版本 | 变更人 | 内容 |
|------|------|--------|------|
| 2026-08-19 | v0.12（重写） | - | 文档完全对齐 v0.12 PC 实现；删除原"前瞻"配对码 / X25519 / nonce 协议（v0.12 不存在），明确当前为明文协议 |
| 2026-08-19 | v0.12（同源 token 修订） | - | CheckList §2.3 加 Token 状态机；协议 §2.3 / §3.2 / §4 / §5.2 同步补"广播 token 唯一权威 + health 同源 + 401 重试 + health 明文风险" |
| 2026-08-19 | v0.12（与实现一致核查 + 修复） | - | 一致性巡检后修：(1) 概述流程图 / 时序图移除 `/api/v1/health` 多余 token 参数；(2) 加密风险表拆 UDP 广播 / HTTP 业务两行，明确广播不含文件名；(3) 协议 projects DTO 加 `projectName` nullable / `sizeMb` 截断精度 / `fileCount` 可能为 0 三条注意；(4) CheckList 新增 §2.2 时间窗总结表分层说明 3s/5s/60s/2s/6s/5s 各处含义；(5) CheckList §四 拆 4.1 iOS / 4.2 macOS / 4.3 通用 ATS 三段，补 macOS Local Network 权限说明 |
| 2026-08-20 | v0.12（代码行号 + 权威来源对齐） | - | (1) §5.2 Python 示例明确 `token` 应从广播 payload 取（不要从 health 响应里拿），与 §2.3 权威源原则一致；(2) §3.4 500MB 行号从 `L508-L514` 精确化为 `L510-L514`；(3) §2.3 / §3.2 引用 CheckList §2.3 行号从 `L30-L46` 修正为 `L41-L58` |
