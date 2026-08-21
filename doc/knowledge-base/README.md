# 项目知识库

星助（StartTooler）的业务知识库。**v0.13 起**基于当前代码事实（不再依赖 `doc/0.10/`、`doc/0.11/`、`doc/0.12/` 三个已沉淀但不再实现的需求文档）。

## 阅读顺序

| 想了解 | 读这篇 |
|---|---|
| 产品定位 / 用户是谁 / 六个页面在做什么 | [00-product-overview.md](00-product-overview.md) |
| 业务对象字典（媒体、标签、会话、评分、OSS、AI 等） | [01-objects.md](01-objects.md) |
| 一个完整用户场景怎么跑 | [02-scenarios.md](02-scenarios.md) |
| 哪些是半成品 / 为什么没做完 | [03-half-built.md](03-half-built.md) |
| 媒体库的字段、状态、生命周期 | [04-media-library.md](04-media-library.md) |
| **移动端 App 业务全景（v0.13 聚合主文档）** | [05-mobile-app.md](05-mobile-app.md) |
| 跨设备同步三种通道（OSS / LAN / 公网 relay） | [06-cross-device-sync.md](06-cross-device-sync.md) |

## API 文档

| 谁读 | 读这篇 |
|---|---|
| 写客户端 / 对接 HTTP 服务 / curl 调通 | [API-01-http-routes.md](API-01-http-routes.md) |
| **扫码建连 / QR 解析 / 32 字符 hex secret** | [API-04-qr-protocol.md](API-04-qr-protocol.md) |
| 备份 / 迁移 / 调试图床 / 解析 user config | [API-04-config-schema.md](API-04-config-schema.md) |
| 移动端持久化（多工作空间 / iOS Keychain / Android EncryptedSP） | [API-05-app-persistence.md](API-05-app-persistence.md) |
| 错误码国际化（HTTP / 业务 / 网络 / QR 解析） | [API-06-error-i18n.md](API-06-error-i18n.md) |

## v0.13 移动端对接索引（速查）

```
App 端对接 PC 端
  │
  ├─ 主文档（聚合） → 05-mobile-app.md
  │   ├─ 〇 变更摘要
  │   ├─ 二 用户流程（添加工作空间 / 上传 / 重连 / 切换）
  │   ├─ 三 工作空间模型（多 PC）
  │   ├─ 四 扫码建连（QR 唯一入口）
  │   ├─ 五 HTTP 协议（5 端点）
  │   ├─ 六 网络变化处理
  │   ├─ 七 错误处理
  │   ├─ 八 状态机
  │   ├─ 九～十 UI（空间列表 / 主页）
  │   ├─ 十一 上传流程
  │   ├─ 十二 平台权限
  │   ├─ 十八 验收标准
  │   └─ 二十 相关文档
  │
  ├─ QR 协议规范 → API-04-qr-protocol.md
  ├─ HTTP 路由 DTO/错误码 → API-01-http-routes.md
  ├─ 持久化加密 → API-05-app-persistence.md
  └─ i18n 文案 → API-06-error-i18n.md
```

## v0.13 关键变更

| 维度 | v0.12 | v0.13 |
|---|---|---|
| 发现 | UDP 广播 | **QR 唯一** |
| 鉴权 | 6 位数字 token | **32 字符 hex secret** |
| 工作空间 | 单一 | **多 PC** |
| iOS Local Network 权限 | 必需 | 不需要 |

详见 [0.13/README.md](../0.13/README.md)。

## 边界声明

- 本目录只描述 **用户能在 UI 上看到的行为 + 客户端可调用的 API**，不记录实现细节、命名空间、SQLite 迁移等技术信息
- 与 `doc/0.10/`、`doc/0.11/`、`doc/0.12/` 的关系：那边是历史需求/方案档案，本目录是当前已实现事实，不互相引用
- 文件行号引用基于截至 2026-08-21 仓库状态；后续重构若行号变动，以代码语义为准
