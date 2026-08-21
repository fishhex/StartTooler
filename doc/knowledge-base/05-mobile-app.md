# 05 · 移动端 App（v0.13，聚合文档）

> **本文件是移动端 App 端对接 PC 端的**唯一权威需求文档**，覆盖：发现 / 协议 / 工作空间 / 错误 / 持久化 / 平台权限**。
> 配套：协议 KB 见 [API-01-http-routes.md](API-01-http-routes.md) + [API-04-qr-protocol.md](API-04-qr-protocol.md)。

## 〇、变更摘要（v0.12 → v0.13）

| 维度 | v0.12 | v0.13 |
|---|---|---|
| 发现 | UDP 广播 255.255.255.255:9876 | **QR 唯一**（`http://{ip}:{port}/upload?k={secret}`） |
| 鉴权 | 6 位数字 token（持久化在 PC 端）| 32 字符 hex secret（启动重生成，**不持久化**） |
| 入口 | UDP 扫描 / 手动输入 / QR | **仅 QR 扫码** |
| 工作空间 | 单一最近 PC | **多空间**（name 为 key，可保存多 PC） |
| 命名来源 | UDP 广播里的 `name` | **Health 响应里的 `name`** |
| 协议版本 | 0.12 | 0.13 |
| iOS Local Network 权限 | 必需（UDP 监听） | **不需要** |

---

## 一、产品定位

**星助 App = PC 端配套的"无线快传"工具**。

不是独立 App。**没有 PC 端 = App 不能用**。

| 类比 | 关系 |
|---|---|
| AirDrop | iOS ↔ iOS |
| **星助 App** | **mobile ↔ PC** |
| KDE Connect | mobile ↔ PC |

**核心场景**：晚上拍完一组照片 → 回家打开 PC → App 扫 PC 端 QR → 一键传到 PC。

---

## 二、用户流程（v0.13 完整版）

### 2.1 首次添加工作空间

```
1. PC 端打开"上传与共享" Tab → 点"启动服务"
   ↓ PC 端立即生成 32 字符 hex secret + QR（http://{ip}:{port}/upload?k={secret}）
2. App 首屏「+ 添加空间」→ 相机扫码页
   ↓ 扫描 QR
3. App 解析 host / port / k=secret
   ↓
4. GET http://{ip}:{port}/api/v1/health（3s 超时，无鉴权）
   ↓ 200
5. 拿到 {name, version, port, currentProject}
   ↓
6. App 用 name 作为工作空间名持久化：
   {name, ip, port, secret, last_seen_at}
   ↓
7. 进主页 → 拉项目列表 → 选照片上传
```

### 2.2 上传照片

```
1. App 主页显示项目列表（is_current=true 优先）
2. 用户选项目
3. 系统 Photo Picker → 选 1-50 张
4. POST /api/v1/projects/{name}/upload?k={secret}
5. 进度条 + 已传 N/M
6. 响应 success/failed → Toast 列表
```

### 2.3 后续重连（持久化直连）

```
App 启动
   ↓
[读持久化 current_space]
   ├─ 有 → GET /api/v1/health（3s 超时）
   │     ├─ 200 → 进主页
   │     └─ 失败 → 跳空间列表（保留空间）
   └─ 无 → 空间列表
```

### 2.4 多 PC 切换

```
空间列表
   ├─ 点 [+] → 扫码 → 新空间
   ├─ 点已有空间 → 切到该空间 → 进主页
   └─ 长按 → 删空间
```

---

## 三、工作空间模型

### 3.1 概念

| 概念 | 定义 |
|---|---|
| **工作空间** | App 端一个连接目标 = 一台 PC |
| 工作空间名 | **PC 端 health 响应的 `name`**（机器名，唯一稳定） |
| 当前空间 | App 端 UI 顶部展示 |

### 3.2 持久化结构

```json
{
  "spaces": [
    {
      "name": "鱼鱼的 MacBook",
      "ip": "192.168.1.10",
      "port": 8765,
      "secret": "7f3a9b2c8e1d4f6a...",
      "last_seen_at": "2026-08-21T14:32:00",
      "current_project": "m42-2025-12-13"
    }
  ],
  "current_space_name": "鱼鱼的 MacBook"
}
```

| 字段 | 存储 |
|---|---|
| `name` | Keychain / EncryptedSharedPreferences（**主键**）|
| `ip` / `port` / `secret` | 加密 |
| `last_seen_at` / `current_project` | 加密 |

| 平台 | 存储 |
|---|---|
| iOS | Keychain（`kSecAttrAccessibleAfterFirstUnlock`） |
| Android | EncryptedSharedPreferences |
| macOS | Keychain |

### 3.3 主键策略

- **同 `name` 二次扫码**：覆盖 ip/port/secret（同一 PC 重启）
- **不同 `name` 扫码**：新增空间
- **重启 App 不持久化 `current_space_name`？**：持久化最近一次空间

### 3.4 清理策略

| 场景 | 行为 |
|---|---|
| 30 天未连接 | 启动时提示"上次连接的 PC 超过 30 天未连接，是否清理？" |
| 用户主动删除 | 立即清 |
| 401 持续 3 次 | 提示"密钥已过期，请重新扫码"（不自动删除） |

---

## 四、扫码建连（QR 唯一入口）

### 4.1 QR 内容

```
http://{ip}:{port}/upload?k={32-hex}
```

例：`http://192.168.1.10:8765/upload?k=7f3a9b2c8e1d4f6a...`

| 部分 | 取值 | 校验 |
|---|---|---|
| `host` | PC LAN IP | IPv4 格式 |
| `port` | URL 端口 | 1-65535 |
| `path` | `/upload` | 必须 |
| `?k=` | 32 字符 hex（16 字节）| 必须，`\A[a-f0-9]{32}\z` |

> 公网 relay 模式 QR **不含** `?k=`（保留 v0.12 行为）；扫码视为"二维码无效"。

### 4.2 URL 解析

```
扫码结果 URL
   ↓
[是 http(s)://?]
   ├─ host 合法（IPv4）
   ├─ port 1-65535
   ├─ path == /upload
   ├─ k == 32 字符 hex
   └─ 全部通过 → 进入 health 验证
```

| 校验失败 | 反应 |
|---|---|
| host 非 IPv4 | 提示"二维码无效" |
| port 越界 | 提示"二维码无效" |
| path 不是 `/upload` | 提示"二维码无效" |
| `?k` 缺失或不是 32 hex | 提示"二维码无效" |

### 4.3 兜底分支

| 扫码结果 | 反应 |
|---|---|
| `http://.../upload?k=...` | 兼容模式 → 解析 → 进 health |
| `https://...` | 浏览器 H5 上传 |
| 其它协议 | 提示"二维码无效" |

> v0.13 不引入 `starttooler://` 自定义 schema（PC 端零改动）。未来 v0.14 再扩展。

### 4.4 入口

| 平台 | 相机 |
|---|---|
| iOS | `AVCaptureSession` + `AVCaptureMetadataOutput` |
| Android | CameraX + ML Kit Barcode |
| macOS | `AVCaptureSession` |

相机权限：

| 平台 | 配置 |
|---|---|
| iOS | `NSCameraUsageDescription` |
| Android | `<uses-permission android:name="android.permission.CAMERA" />` |

---

## 五、HTTP 协议

### 5.1 路由总表

| # | 路径 | 方法 | 鉴权 | 用途 |
|---|---|---|---|---|
| 1 | `/upload` | GET | ❌ | H5 上传页面 |
| 2 | `/upload` | POST | ❌ | H5 上传（multipart） |
| 3 | `/api/v1/health` | GET | ❌ | 健康检查 + 取 `name` |
| 4 | `/api/v1/projects` | GET | ✅ `?k=` | 项目列表 |
| 5 | `/api/v1/projects/{name}/upload` | POST | ✅ `?k=` | 上传 |

详细 DTO / 错误码：见 [API-01-http-routes.md](API-01-http-routes.md)。

### 5.2 Secret 鉴权

| 项 | 值 |
|---|---|
| 字段 | query `?k=` 或 header `X-Key` |
| 长度 | 32 字符 hex |
| 校验顺序 | query 优先，再 header |
| 错误码 | 401 `{ "error": "invalid secret" }` |
| 持久化 | PC 端不持久化；App 端持久化 |

### 5.3 Health 响应（App 端首用）

```json
{
  "ok": true,
  "service": "starttooler",
  "version": "0.13",
  "name": "鱼鱼的 MacBook",
  "port": 8765,
  "secret": "...",
  "currentProject": "m42-2025-12-13"
}
```

| 字段 | App 端处理 |
|---|---|
| `name` | **作为工作空间名持久化**（唯一标识） |
| `version` | 不低于 `"0.13"` 视为兼容 |
| `port` | 与 URL 一致 → 通过；不一致 → 提示"二维码无效" |
| `secret` | **仅作回包校验**，不从此取值 |
| `currentProject` | 主页默认选中 |

---

## 六、网络变化处理（v0.13 关键规则）

**核心**：v0.13 不引入运行时自动发现。所有变化靠**用户感知 + 重新扫码**。

### 6.1 变化矩阵

| 变化 | PC 端 | App 端 |
|---|---|---|
| **IP 变** | 监听 `NetworkChange.NetworkAddressChanged` → 黄色提示"网络 IP 已变，请重新扫码" | health 失败 → 提示"连不上 PC" → 引导重扫 |
| **Port 变** | UI 改端口触发服务重启 | 同上 |
| **Secret 变** | 启动 / 重置立即生效 | health 200 + projects 401 → 提示"密钥已过期，请重新扫码" |

### 6.2 不做的事

- ❌ App 端轮询 / 主动探测
- ❌ App 端订阅网络事件（PC 端做即可）
- ❌ mDNS / Bonjour
- ❌ PC 端 push / SSE

### 6.3 App 端启动逻辑

```
读持久化 current_space
   ↓
GET /api/v1/health（3s 超时）
   ├─ 200 → 进主页（IP 仍可用）
   ├─ 网络失败 → 提示"连不上 PC，工作空间保留"
   └─ 401 → 提示"密钥已过期，请重新扫码"
```

---

## 七、错误处理

### 7.1 HTTP 错误码

| 状态 | 反应 |
|---|---|
| 200 | 正常 |
| 400 | Toast"请求格式错" |
| 401 | 清 secret + 跳空间列表 + 提示"密钥已过期" |
| 404 | 项目失效 → 刷新项目列表 |
| 405 | Toast"PC 不支持此操作" |
| 500 | Toast + Retry |

### 7.2 网络层错误

| 场景 | 提示 |
|---|---|
| DNS 失败 | "域名解析失败" |
| TCP 拒绝 | "连不上 PC" |
| 超时 3s（health） | "PC 不可达" |
| 超时 60s（upload） | "上传超时，可重试" |

### 7.3 上传业务失败（`failed[]`）

```json
{
  "success": true,
  "count": 2,
  "files": [...],
  "failed": [
    { "name": "notes.txt", "reason": "unsupported extension .txt" }
  ]
}
```

| 原因 | 中文提示 |
|---|---|
| `unsupported extension X` | 不支持的文件类型：X |
| `exceeds 500MB limit` | 超过 500MB 上限 |
| 其它 | 上传失败：{reason} |

UI 展示：

```
✓ 已上传 5 张
✗ 失败 2 张
  - IMG_001.txt：不支持的文件类型 .txt
  - IMG_002.bmp：超过 500MB 上限
```

### 7.4 i18n key 规范

```
error.unauthorized = "密钥已过期，请重新扫码"
error.network = "网络连接失败"
error.timeout = "请求超时"
error.qr_invalid = "二维码无效"
error.pc_unreachable = "连不上 PC，请检查同 Wi-Fi"
error.qr_secret_expired = "密钥已过期，请重新扫码"
error.unknown = "未知错误"
```

iOS `Localizable.strings` / Android `strings.xml` 双向宽容（未识别 key → 兜底）。

---

## 八、状态机

```
┌──────────┐  首次启动  ┌──────────┐
│  未添加空间 │ ────────→ │  待扫码   │
└──────────┘           └──────────┘
     ▲                       │ 扫到 QR
     │                       ▼
     │                 ┌──────────┐
     │                 │  验证中   │
     │                 └──────────┘
     │                       │
     │        ┌──────────────┼──────────────�
     │        ▼              ▼              ▼
     │  ┌──────────┐  �──────────┐  ┌──────────┐
     │  │  已连接   │  │ 扫码失败  │  │ 连接失败 │
     │  └──────────┘  └──────────┘  └──────────┘
     │        │                       │
     │        │ 退出 / 重启           │ 重试
     │        ▼                       │
     │  ┌──────────┐                  │
     └──│  空间列表 │←─────────────────┘
        └──────────┘
```

| 状态 | UI |
|---|---|
| 未添加空间 | 空间列表 + [+] |
| 待扫码 | 相机扫码页 |
| 验证中 | Loading |
| 已连接 | 主页 |
| 扫码失败 | Toast"二维码无效" + 回空间列表 |
| 连接失败 | Toast"连不上 PC" + 回空间列表 |

---

## 九、空间列表 UI

```
┌─ 工作空间 ─────────────────┐
│  ◉ 鱼鱼的 MacBook          │
│    192.168.1.10 · 刚刚连接  │
│  ◯ 工作室 Win11            │
│    192.168.1.20 · 昨天连接  │
│                            │
│  + 添加空间                 │
└────────────────────────────┘
```

| 元素 | 行为 |
|---|---|
| 当前空间 | 顶部 ◉ |
| 切换 | 点列表项 → 进主页 |
| 删除 | 长按 / 左滑 → 弹确认 → 删 |
| 添加 | [+] → 相机扫码页 |

---

## 十、主页 UI

```
┌────────────────────────────┐
│ < 鱼鱼的 MacBook | 切换空间 │ ← 顶栏：当前空间名
├────────────────────────────┤
│ 当前项目                    │
│ ┌─────────────────────┐   │
│ │ M42 2025-12-13      │   │
│ │ 234 张 · 4GB        │   │
│ └─────────────────────┘   │
│                            │
│ 其他项目                    │
│ • NGC7000（12 张）         │
│ • M51（45 张）             │
│                            │
│ [切换项目]                 │
│ [+ 上传文件]               │
└────────────────────────────┘
```

---

## 十一、上传流程

```
主页 → 选项目 → 系统 Photo Picker
   ↓
客户端预过滤：扩展名白名单
   ↓
POST /api/v1/projects/{name}/upload?k={secret}
   ↓
[进度条 + "已传 N/M"]
   ↓
解析响应
   ├─ success=true → 列表展示成功 + failed[]
   └─ 401 → 引导重新扫码
```

| 项 | 值 |
|---|---|
| 上传并发 | 1 串行（按顺序） |
| 单批上限 | 50 张 |
| 超时 | 60 秒 / 请求 |
| 重试 | 失败分桶，用户决定 |
| 客户端预过滤 | `.jpg .jpeg .png .raw .avi .mp4 .mov .mkv .webm .m4v .mpg .mpeg` |

---

## 十二、平台权限（v0.13 大幅简化）

### 12.1 iOS

| 项 | 配置 |
|---|---|
| 相机 | `NSCameraUsageDescription`（必填） |
| **本地网络** | � **不需要**（v0.13 移除 UDP） |
| ATS | HTTP 例外（LAN 明文） |

### 12.2 macOS

| 项 | 配置 |
|---|---|
| 相机 | `NSCameraUsageDescription`（必填） |
| **本地网络** | ❌ **不需要** |
| App Sandbox | 允许网络客户端 |

### 12.3 Android

| 项 | 配置 |
|---|---|
| 相机 | `<uses-permission android:name="android.permission.CAMERA" />` |
| INTERNET | `<uses-permission android:name="android.permission.INTERNET" />` |
| **ACCESS_NETWORK_STATE** | ❌ **不需要**（无网络监听） |
| **ACCESS_WIFI_STATE** | ❌ **不需要** |
| 网络安全配置 | `network_security_config.xml` 放行 LAN 明文 |

### 12.4 不需要做的事

- ❌ iOS `NSLocalNetworkUsageDescription`
- ❌ iOS Local Network 真机调试权限
- ❌ Android 后台白名单提示
- � 任何本地网络发现权限

---

## 十三、与 PC 端能力的对应

| PC 端能力 | App 端需求 |
|---|---|
| HTTP 路由（3 个 API） | §五 |
| 32 字符 hex secret | §5.2 |
| Health 响应 `name` 字段 | §3、§5.3 |
| 500MB 单文件 | §7.3 |
| 扩展名白名单 | §十一 |
| 重名 `_1/_2` 策略 | §十一（响应 `files[].name` 为准） |
| H5 浏览器上传（无 secret） | §4.3 兜底 |
| 公网 relay（QR 不带 secret） | §4.2 提示"二维码无效" |
| 多 IP 切换（PC 端 UI） | **App 端不感知**（QR 锁定当前 IP） |

---

## 十四、边界情况

| 场景 | 行为 |
|---|---|
| 两台 PC 同名 | 按 `name+ip` 去重，仍允许多 PC |
| PC 端改名 | `name` 变 → App 视为新空间 |
| PC 端 `name` 含特殊字符 | 持久化转义 |
| 用户在 App 内手动改名 | 不支持（直接展示 PC 端 `name`） |
| 30 天未用 | 启动时提示清理 |
| 多空间都 401 | 逐个提示，不批量 |
| 扫到非标准 URL | 提示"二维码无效" |
| 同 `name` 同 `ip` 二次扫码 | 静默覆盖（重启场景） |
| 同 `name` 不同 `ip` 二次扫码 | 视为同一 PC（覆盖 IP） |
| 不同 `name` 二次扫码 | 新增空间（多 PC） |

---

## 十五、安全边界

| 风险 | 现状 | 缓解 |
|---|---|---|
| QR 截图泄露 secret | 32 字符 hex | PC 重启失效；用户可重置 |
| LAN 嗅探 HTTP | 明文 | 家庭 LAN 风险可控 |
| LAN 嗅探 QR | 摄像头拍屏 | 短暂可见窗口 |
| 客户端伪造 QR | 无 | App 端 health 验证 |
| 重复扫码 | 不会 | 每次扫码写入持久化 |

**不实现的**：

- ❌ TLS / HTTPS（家用 LAN 风险可控）
- ❌ ECDH / 应用层加密
- ❌ 签名 / nonce
- ❌ 跨公网（走 relay）

---

## 十六、非功能需求

| 维度 | 要求 |
|---|---|
| QR 生成（PC） | ≤ 200ms |
| HTTP 启动（PC） | ≤ 1s |
| Secret 生成 | 启动时立即 |
| App 扫码到进主页 | ≤ 3s（含 health 验证） |
| 持久化加密 | iOS Keychain / Android EncryptedSP |
| Token 日志脱敏 | `12****56` 形式 |

---

## 十七、不做清单

| 内容 | 理由 |
|---|---|
| UDP 自动发现 | v0.13 移除 |
| 6 位数字 Token | 改为 32 字符 secret |
| mDNS / Bonjour | 复杂、跨平台难 |
| 加密 / 签名 / nonce | 家庭 LAN 风险可控 |
| 后台推送通知 | PC 端未落地 |
| 断点续传 / 分片 | MVP 不需要 |
| 自动重试上传 | 用户决定 |
| 跨子网（不走 relay） | 不可达 |
| 后台持续监听 | 系统限制 |
| 自定义 schema | v0.14 再扩展 |

---

## 十八、验收标准

### 18.1 App 端

- [ ] 首次启动 → 显示「+ 添加空间」
- [ ] 扫码 → 1-2s 内进主页
- [ ] 二次启动 → 读持久化 → 直接进主页
- [ ] 持久化 secret 失效 → 自动清 + 引导重扫
- [ ] 切换 PC → 提示"切换 PC?"
- [ ] 30 天未用 → 提示清理
- [ ] health 失败 → 提示"连不上 PC"
- [ ] 401 → 引导重新扫码
- [ ] 失败 `failed[]` 列表展示
- [ ] 进度条 + "已传 N/M"
- [ ] 客户端预过滤白名单
- [ ] 主页顶栏显示 PC 端 `name`
- [ ] 删除空间 → 同步清持久化

### 18.2 跨端

- [ ] QR 字段缺失 / 非法 → 提示"二维码无效"
- [ ] QR 解析后写入持久化，再启 App → 自动续连
- [ ] PC 重启换 secret → App 端 secret 失效 → 引导重扫
- [ ] 多 PC 切换 → App 端正确切换
- [ ] PC 重启换 IP → 重新扫码 → 续连

---

## 十九、版本兼容

| App 版本 | 兼容 PC 端版本 |
|---|---|
| v0.13 | 0.13+（`version` 字段校验） |
| v0.12 | 0.12（UDP 协议；不兼容 v0.13） |

**破坏性变更**：v0.13 App 不兼容 v0.12 PC 端；v0.12 App 不兼容 v0.13 PC 端。

---

## 二十、相关文档

| 文档 | 用途 |
|---|---|
| [API-01-http-routes.md](API-01-http-routes.md) | HTTP 路由权威（5 端点 / DTO / 错误码） |
| [API-04-qr-protocol.md](API-04-qr-protocol.md) | QR 内容 + secret 协议 |
| [API-05-app-persistence.md](API-05-app-persistence.md) | 持久化（Keychain / EncryptedSP） |
| [API-06-error-i18n.md](API-06-error-i18n.md) | 错误码 i18n 映射 |
| [06-cross-device-sync.md](06-cross-device-sync.md) | 三通道对比（LAN / OSS / relay） |

---

## 二十一、变更记录

| 日期 | 版本 | 内容 |
|---|---|---|
| 2026-08-21 | v0.13（聚合） | 移除 UDP / 6 位 Token；引入 QR 唯一发现 + 32 字符 secret + 工作空间模型 |
