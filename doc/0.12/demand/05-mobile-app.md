# D05 · 移动端 App 业务需求

**状态**：讨论稿
**关联**：[D04 PC 作为 server 架构](04-mobile-lan-sync.md)
**前置决策**：[decisions/0001-app-repo-location.md](../decisions/0001-app-repo-location.md)

## 一、背景

[D04 移动端 LAN 同步](04-mobile-lan-sync.md) 决定了主架构：

- PC 端作为 HTTP 服务
- 移动端 App 通过 LAN 连接 PC 端
- 移动端 App 上传照片

D04 描述了**协议**和**与 PC 端协作**。D05 拆出**App 端独立的需求**：

- App 端 UX 全景
- 不依赖 PC 端的边界
- 错误 / 异常 / 失败如何处理
- App 端的"业务规则"（项目选择 / 持久化 / 多 PC）

## 二、用户场景

### 2.1 主用户：天文摄影爱好者

- 出门拍完照片 → 回家上传到 PC 端
- 在不同屋子（工作室 / 客厅）有不同 PC
- 一个 App 同时管理多个 PC

### 2.2 用户需求

| 需求 | 优先级 |
|---|---|
| 不需要账号 / 登录 | P0 |
| 自动发现 PC | P0 |
| 选 1-50 张照片批量上传 | P0 |
| 上传进度可见 | P0 |
| 失败明明白白 | P0 |
| Token 自动重连 | P0 |
| 手动输入 IP（兜底） | P0 |
| 切换不同 PC | P1 |
| 跨 WiFi 提示 | P1 |
| 离线缓存项目列表 | P1 |
| 错误国际化 | P2 |
| 缩略图 | P3 |

## 三、App 端五大模块

### 3.1 Discovery（发现）

负责找到 PC 端：

- UDP 监听 9876 端口
- 5 秒扫描窗口
- 解析广播 JSON
- 维护"已发现 PC"列表

详见 [spec/05-mobile-app.md §4.1](../spec/05-mobile-app.md#41-discovery)。

### 3.2 Connection（连接）

负责 Token 验证：

- 显示 Token 输入框
- 试探 /api/v1/health
- 处理 401 / 网络失败
- 持久化 IP + Token

详见 [spec/05-mobile-app.md §4.2](../spec/05-mobile-app.md#42-connection)。

### 3.3 Project（项目）

负责项目列表：

- GET /api/v1/projects
- 展示当前项目 + 其他项目
- 切换项目
- 标记 isCurrent

详见 [spec/05-mobile-app.md §4.3](../spec/05-mobile-app.md#43-project)。

### 3.4 Upload（上传）

负责上传照片：

- 选照片（系统 Photo Picker）
- 选项目
- 批量上传
- 进度展示
- 失败详情

详见 [spec/05-mobile-app.md §4.4](../spec/05-mobile-app.md#44-upload)。

### 3.5 Persistence（持久化）

负责保存客户端状态：

- Keychain / EncryptedSharedPreferences
- 存 IP / Token / PC 列表
- 失效清理

详见 [API-05-app-persistence.md](../../knowledge-base/API-05-app-persistence.md) + [spec/05-mobile-app.md §4.5](../spec/05-mobile-app.md#45-persistence)。

## 四、App 端状态机

```
         ┌── 未连接 ─── scan ──→ 扫描中
         │    │                      │
         │    │                  found
         │    │                      │
         │    ↑                      ↓
         │  失败 / 401        ┌──→ 已连接 ───→ 主页
         │    │              │      │
         │    │              │   401
         │    └──────────────┘      │
         │                          ↓
         └────────────────── Token 验证页
                重新 Token 输入
```

### 4.1 状态定义

| 状态 | 数据 | UI |
|---|---|---|
| 未连接 | 无 | 跳连接页 |
| 扫描中 | discovered[] | 加载动画 |
| 连接成功 | ip/token | 跳主页 |
| Token 错 | ip | 跳验证页 |
| 网络失败 | last_ip | 错误页 + Retry |

### 4.2 状态转移触发

| 转移 | 触发 |
|---|---|
| 未连接 → 扫描中 | App 启动 |
| 扫描中 → 已连接 | /health 成功 |
| 扫描中 → 未连接 | 5 秒无响应 + 用户手动 |
| 已连接 → Token 错 | 401 响应 |
| Token 错 → 已连接 | Token 重新验证成功 |
| 任何 → 失败 | 网络断开 / PC 端服务异常 |

## 五、App 端存储

### 5.1 持久化数据

| 字段 | 平台 | 用途 |
|---|---|---|
| last_ip | iOS UserDefaults / Android EncryptedSP | 直连 IP |
| last_token | iOS Keychain / Android EncryptedSP | 鉴权 |
| last_name | 公开存储 | UI 显示 |
| last_seen_at | 公开存储 | 时间戳 |
| discovered[] | 公开存储 | 本会话 PC 列表 |

详见 [API-05-app-persistence.md](../../knowledge-base/API-05-app-persistence.md)。

### 5.2 何时清

| 场景 | 反应 |
|---|---|
| 401 响应 | 清 Token，保留 IP |
| 用户主动"切换 PC" | 清 IP + Token |
| 30 天未连接 | 提示清 |
| 卸载 / 清 App 数据 | 系统自动清 |

## 六、错误处理

### 6.1 三类错误

| 类别 | 例子 | App 反应 |
|---|---|---|
| 网络层 | 断网 / 超时 | Retry 按钮 |
| HTTP 状态 | 401 / 404 / 500 | Toast / 跳页 |
| 业务失败 | failed[] | 跳过该文件 |

### 6.2 i18n 映射

详见 [API-06-error-i18n.md](../../knowledge-base/API-06-error-i18n.md)。

### 6.3 错误级别

| 级别 | UI |
|---|---|
| INFO | Toast |
| WARN | 顶部条幅 |
| ERROR | 全屏错误页 |

## 七、App 端能力边界

### 7.1 用户视角

App 端**只做**一件事：**把手机拍的照片上传到 PC 端**。

不做的：

- ❌ 不独立的媒体管理器（无单独相册）
- ❌ 不修图 / 标注 / 评分
- ❌ 不浏览 PC 端照片
- ❌ 不跟拍 / 视频上传
- ❌ 不账号 / 不云同步
- ❌ 不跨网络（公网）

### 7.2 技术视角

| 维度 | 边界 |
|---|---|
| 协议 | HTTP + UDP（PC 端定） |
| 鉴权 | 6 位数字 Token（PC 端定） |
| 数据 | 不入 App 端数据库 |
| 上传并发 | 单连接（多文件 multipart） |
| 文件大小 | 500MB 单文件（PC 端定） |
| 扩展名 | 12 种（PC 端定） |
| 跨多 PC | 是（App 端持久化多 PC） |
| 跨设备 App | 一机一 App，不跨同步 |

## 八、UX 原则

1. **零配置**：不需账号 / 注册
2. **零等待**：所有操作 < 2 秒响应
3. **零失败**：失败明确 + 下一步按钮
4. **零权限**：相册走系统选择器
5. **零后台**：不需常驻

## 九、版本与发布

### 9.1 版本号

| 维度 | 编号 |
|---|---|
| App 端 | semver（如 1.0.0） |
| 协议 | PC 端 `version` 字段（"0.12"） |
| 兼容 | App 端看到 `version` 不识别 → 提示升级 PC 端 |

### 9.2 发布渠道

| 平台 | 渠道 |
|---|---|
| iOS | App Store / TestFlight |
| Android | Google Play / Sideload |

### 9.3 强制更新

如果 PC 端协议 major 变更 → App 端最低版本 +1。

## 十、跨设备身份

App 端**不引入**用户账号、用户 ID。

项目识别靠 `projectName`：

- 不同 PC 端同一 `projectName` → 视为同一项目
- App 端上传时**不带**项目名（PC 端用 URL path）
- `projectName` 用于 App 端**跨设备显示**同一项目

详见 [KB-04-media-library.md §12](../../knowledge-base/04-media-library.md#)。

## 十一、未决定 / 待澄清

| # | 问题 | 决策 |
|---|---|---|
| 1 | iOS / Android 最低版本 | 暂定 iOS 16+ / Android 10+ |
| 2 | App 端是否允许新建项目 | ❌ 当前不允许（PC 端管理） |
| 3 | 手动输入 IP 兜底 UI 位置 | 连接页底部 |
| 4 | 多 PC 选择持久化策略 | 暂只存最近一台 |
| 5 | 上传历史保留 | 30 天 |
| 6 | 离线缓存项目列表 | 暂不实现 |
| 7 | 端到端加密 | 暂不实现 |
| 8 | 上传失败重试 | 手动按钮（暂不自动） |
| 9 | 缩略图 | 暂不实现 |
| 10 | 拍照直传 | 暂不实现 |

## 十二、关键日期

| 事件 | 日期 |
|---|---|
| 需求文档定稿 | 2026-08-15 |
| Spec 文档 | 2026-08-15 |
| 仓库创建 | 2026-08-22 |
| iOS MVP | 2026-09-30 |
| Android MVP | 2026-10-15 |
| 公测 | 2026-11-15 |
