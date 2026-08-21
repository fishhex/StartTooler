# 06 · 跨设备同步

星助的文件传输有**三种通道**，按使用场景区分。本文档解释三种通道的定位、用户什么时候用、它们怎么协同。

> v0.13 起，LAN 通道**移除 UDP 自动发现**，改为 QR 唯一入口（详见 [05-mobile-app.md](05-mobile-app.md)）。

## 一、三个通道一览

| 通道 | 介质 | 典型场景 | 用户必做的配置 |
|---|---|---|---|
| **OSS** | 阿里云对象存储 | 异地备灾 / 远程分享 / 永久存储 | 填 OSS 配置（Bucket + AK） |
| **LAN** | 同 WiFi 局域网 | 拍照时手机直接传电脑 | **扫码 + PC 启服务** |
| **公网 relay** | VPS 中转 | 异地电脑之间互传 | 配 VPS SSH 账号 |

这三种通道**互补**，不是互斥——同一个项目可以同时挂在多个通道。

## 二、OSS 通道

### 定位

最通用、最稳的备份通道。**唯一支持"异地"** 的通道。

### 工作流

```
用户在电脑端：
  1. 设置填好 OSS 配置（Bucket / Region / AK / AS / PathPrefix）
  2. 选中媒体 → 点"上传"
  3. 后台 loader 跑上传到 OSS
  4. 上传完 → media_files.is_uploaded=true, remote_url=OSS URL

用户在外地：
  - 通过 OSS URL 链接直接分享（别人能看缩略图）
  - 在另一个设备上下到本地（手动建项目）
```

### 配置位置

设置 → OSS。配置结构：[OssConfig.cs](../../StartTooler/Services/OssConfig.cs)：

| 字段 | 含义 |
|---|---|
| `Provider` | 提供商（当前只支持 Aliyun） |
| `Region` | 阿里云 Region（如 `oss-cn-hangzhou`） |
| `Bucket` | 桶名 |
| `AccessKeyId` | AK |
| `AccessKeySecret` | AS |
| `PathPrefix` | 桶内前缀（多个项目共享一个桶时区分） |

### 用户在 UI 上能看到什么

- 媒体卡右上角**同步徽章**：
  - 灰色↑：未上传
  - 绿色✓：已上传
  - 红色⚠：云端有但本地缺
- 设置 OSS 页：上传 / 下载 / 释放空间（释放空间：把本地文件删除，只留云端副本）

### 关键限制

- 一次性上传（不支持断点续传）
- 单文件 500MB 软上限（OCI 限制）
- 配置泄露 = 全部文件公开（强烈建议只给 OSS 桶授权 RAM 子账号）

代码：[AliyunOssStorage](../../StartTooler/Services/AliyunOssStorage.cs)。

## 三、LAN 通道（v0.13 重写）

### 定位

**同 WiFi 拍完即传** 的最快通道。手机拍照 → 立刻传到电脑 → 不用 import 不用数据线。

### 工作流（v0.13）

```
PC 端：
  1. 切到"上传与共享" Tab
  2. 点"启动服务"（默认 8765 端口）
  3. 看到 QR + IP + 32 字符 hex 密钥

手机端：
  1. 同 WiFi
  2. 打开 App → 扫 PC 端 QR（**唯一发现入口**）
  3. App 解析 URL → health 验证 → 取 PC name 作为工作空间名
  4. 选项目 + 选照片 → 上传
```

### 不需要做的

- 不用配 OSS
- 不用注册账号
- 不用打开防火墙（应用层处理）
- 不用手动输入 IP / 端口 / Token（QR 一站搞定）
- ❌ 不用 UDP 扫描（v0.13 移除）

### 关键限制

- 必须同 WiFi（QR 锁定 IP，不跨路由器）
- 必须 PC 端开着服务
- 需 32 字符 hex secret（**v0.14**：PC 端持久化到 `config.db.upload_secret`，重启复用；仅「重置密钥」才变化）
- QR 含 `?k=` secret；公网 relay QR 不含 → 扫码视为"二维码无效"

代码：[UploadServerService](../../StartTooler/Services/UploadServerService.cs)（[API-01-http-routes.md](API-01-http-routes.md)）。

### 三种情形

| 情形 | PC 端 UI | 手机端 UI |
|---|---|---|
| PC 端服务主 | 服务跑着 + 显示 QR | 扫 QR → 自动建工作空间 |
| PC 端密钥重置 | 旧 App 端 401 提示 | 清 secret + 提示重新扫码 |
| PC 端换了 WiFi（IP 变）| 黄色提示"网络 IP 已变" | 引导重新扫码 |

> v0.13 起，App 端不再依赖任何自动发现；所有变化靠用户感知 + 重新扫码。

### App 端对接需求

完整文档：[05-mobile-app.md](05-mobile-app.md)（聚合主文档）

| 维度 | 详细 |
|---|---|
| QR 协议 | [API-04-qr-protocol.md](API-04-qr-protocol.md) |
| HTTP 路由 | [API-01-http-routes.md](API-01-http-routes.md) |
| App 持久化 | [API-05-app-persistence.md](API-05-app-persistence.md) |
| 错误 i18n | [API-06-error-i18n.md](API-06-error-i18n.md) |

## 四、公网 relay 通道

### 定位

**异地电脑之间**互传——比如家里电脑和工作室电脑之间同步一个项目。

### 工作流

```
一次配置（每台 PC）：
  1. 设置 → 公网代理 → 填 VPS SSH 账号（host / port / user / password 或 key）
  2. 点"部署" → StartTooler SSH 到 VPS 部署 upload-relay（Go 二进制）
  3. 部署完 → 自动启动服务

日常使用：
  - 公网代理开 → PC 端能上传到 VPS
  - VPS 上的文件被另一台 PC 拉走
```

### 关键限制

- 必须有 VPS（自购 / 借用）
- VPS 上跑 upload-relay（星助自己写的 Go 进程）
- 流量经过 VPS 中转（**VPS 流量单价贵**）
- 走 TCP（不是 HTTP），两端 SSH 通道
- QR 不含 secret（保留 v0.12 行为）

代码：[PublicRelayService](../../StartTooler/Services/PublicRelayService.cs)，配置：[PublicRelayConfig.cs](../../StartTooler/Services/PublicRelayConfig.cs)。

### 用户在 UI 上能看到什么

- "上传与共享" Tab 多了"公网代理"区域
- 状态：未部署 / 部署中 / 运行中 / 异常
- 控制按钮：部署 / 启动 / 停止 / 卸载

### 关键事实

- 配置 "SyncPollIntervalSec"（默认 5s）和 "SyncBatchSize"（默认 5）控制带宽，避免打爆 VPS
- 控制按钮在配置没填时禁用
- VPS 上 run 的 PID 文件在 `~/starttooler/upload-relay.pid`

## 五、三个通道怎么协同

### 场景 A：本地 + 异地备灾

```
LAN：今晚拍完立刻从手机传到电脑
OSS：电脑上的文件定期（手动）上传到 OSS 备灾
  → 家里硬盘炸了，OSS 还在
```

### 场景 B：工作室 + 家里

```
家里电脑：今晚拍完，文件传到 VPS
工作室电脑：明天到工作室，启动下载，从 VPS 拉文件
  → 不需要 OSS，只需要 VPS
```

### 场景 C：跨设备同一项目

```
家里电脑：旧的 `/Users/hex/Astro/m42` 项目
工作室电脑：新建同样的 `/Users/hex/Astro/m42` 项目
两边分别拍照
LAN 上传：工作室拍的照片 → 家里电脑（需 PC 端启服务 + 扫 QR）
OSS 备份：家里电脑上的文件 → OSS
  → 两份文件镜像备份
```

## 六、为什么不用第三方网盘

常见问题：为什么不用 OneDrive / iCloud / Dropbox 这些？

| 维度 | 第三方 | 星助 |
|---|---|---|
| 文件大小 | 5GB 限制 | 无限制 |
| 隐私 | 第三方看得到 | 自有 OSS / 私有 VPS |
| 同步速度 | 走第三方网络 | LAN 直传 / VPS 自定 |
| 地理覆盖 | 全球可用 | 用户自配 |
| 成本 | 月费 | 阿里云 OSS / VPS 单价 |

LAN 通道（0 成本 + 极速）从来不是第三方网盘能比的。

## 七、跨设备身份与项目名

为了让手机、电脑、VPS 之间能识别"同一个项目"，每个项目带上 `project_name` 字段（[ProjectConfig.cs](../../StartTooler/Services/ProjectConfig.cs)）。

`project_name` 是用户在设置里给项目起的名字（"M42 2025-12-13"），`project_path` 是本地绝对路径。两者**不是同一个东西**——同名项目可以在不同电脑的不同路径下。

代码：[ProjectConfig.cs](../../StartTooler/Services/ProjectConfig.cs)。

### 同步时

- 上传文件：`media_files.remote_url` 写云端 URL
- 下载：`project_name` 匹配同名项目
- 不同路径下同名项目 → 同时挂到云端（**重复共享**）

### 局限

- `project_name` 重复 → 视为同一项目（用户需自行确保唯一）
- 重命名 `project_name` → 旧分享链接 404（云端不会改）

## 八、用户在 UI 上看到的选择

### 上传 Tab 总览（v0.13）

```
┌─ 上传与共享 ─────────────────────────┐
│                                       │
│  局域网服务（LAN）                    │
│  ├─ 端口：8765   [启动/停止]           │
│  ├─ QR 码                            │
│  ├─ URL：192.168.1.10:8765/upload?k=… │
│  ├─ 密钥：7f3a9b2c...   [重置]        │
│  └─ 上传历史                          │
│                                       │
│  公网代理（VPS relay）                │
│  ├─ 状态：未部署                      │
│  └─ [配置...]                          │
│                                       │
│  阿里云 OSS                            │
│  ├─ 状态：已配置                       │
│  └─ [上传选中] [下载] [释放空间]      │
└───────────────────────────────────────┘
```

卡片右上角**同步徽章**永远显示，表示当前文件的同步状态（与具体通道无关）。

## 九、故障排查（v0.13）

| 现象 | 可能原因 | 排查 |
|---|---|---|
| LAN 上传 401 | Secret 错 / 已过期 | PC 端 UI 看密钥 / 重新扫 QR |
| LAN 上传不到 | 跨 WiFi | 同一路由器 SSID 下 |
| LAN 上传慢 | 5G 信号弱 | 用 2.4G WiFi |
| LAN 找不到 PC | IP / 端口错 | 重新扫 QR |
| QR 扫不到 / 不识别 | QR 模糊 / 距离远 | 靠近 PC 端屏幕重扫 |
| QR 提示"二维码无效" | QR 不含 `?k=`（公网 relay 模式）| 同 LAN 扫码 / 关公网 relay |
| OSS 上传失败 | AK 错 / 余额不足 | 设置 OSS 页面看错误 |
| 公网 relay 部署失败 | SSH 配置错 | 配置 SSH 检查 |
| 公网 relay 收不到文件 | 防火墙拦截 | VPS 端口 8766 开放 |

## 十、未来扩展

- 跨设备身份（用户系统）→ 多账号同步
- 端到端加密 → 隐私保护
- 双向同步 → 任意设备改标签都同步
- 同步冲突解决 → 解决"两边都改"的合并
- iCloud 同步 App 端工作空间（v0.14 候选）

详见 [03-half-built.md](03-half-built.md)。

## 十一、变更记录

| 日期 | 版本 | 内容 |
|---|---|---|
| 2026-08-21 | v0.13 | 改写：LAN 通道移除 UDP；改为 QR + 32 字符 hex secret |
| 2026-08-21 | v0.14 | PC 端 secret 默认持久化；LAN 通道「扫码一次终身免扫」（仅「重置密钥」失效） |
