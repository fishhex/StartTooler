# 项目知识库

星助（StartTooler）的业务知识库。基于当前代码事实（不依赖 `doc/0.10/`、`doc/0.11/`、`doc/0.12/` 三个已沉淀但不再实现的需求文档）。

## 阅读顺序

| 想了解 | 读这篇 |
|---|---|
| 产品定位 / 用户是谁 / 六个页面在做什么 | [00-product-overview.md](00-product-overview.md) |
| 业务对象字典（媒体、标签、会话、评分、OSS、AI 等） | [01-objects.md](01-objects.md) |
| 一个完整用户场景怎么跑 | [02-scenarios.md](02-scenarios.md) |
| 哪些是半成品 / 为什么没做完 | [03-half-built.md](03-half-built.md) |
| 媒体库的字段、状态、生命周期 | [04-media-library.md](04-media-library.md) |
| 跨设备同步三种通道（OSS / LAN / 公网 relay） | [06-cross-device-sync.md](06-cross-device-sync.md) |

## API 文档

| 谁读 | 读这篇 |
|---|---|
| 写客户端 / 对接 HTTP 服务 / curl 调通 | [API-01-http-routes.md](API-01-http-routes.md) |
| 写 PC 发现 / 局域网 / 移动端发现 PC | [API-03-udp-broadcast.md](API-03-udp-broadcast.md) |
| 备份 / 迁移 / 调试图床 / 解析 user config | [API-04-config-schema.md](API-04-config-schema.md) |

## 边界声明

- 本目录只描述 **用户能在 UI 上看到的行为 + 客户端可调用的 API**，不记录实现细节、命名空间、SQLite 迁移等技术信息
- 与 `doc/0.10/`、`doc/0.11/`、`doc/0.12/` 的关系：那边是历史需求/方案档案，本目录是当前已实现事实，不互相引用
- 文件行号引用基于截至 2026-08-15 仓库状态；后续重构若行号变动，以代码语义为准
