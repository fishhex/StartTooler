# API-04 · QR 协议（v0.14）

PC 端 QR 内容规范与 App 端扫码解析规则。**v0.13 起，QR 是 PC ↔ App 唯一发现入口**。

> **v0.14 变更**：PC 端 secret 默认持久化到 `config.db.upload_secret`；扫码一次后 PC 重启 App 端无需再扫（详见 §五）。
>
> 配套：[05-mobile-app.md](05-mobile-app.md) §四「扫码建连」/ [API-01-http-routes.md](API-01-http-routes.md) §五「Secret 鉴权」

## 一、QR 内容

```
http://{ip}:{port}/upload?k={secret}
```

完整示例：

```
http://192.168.1.10:8765/upload?k=7f3a9b2c8e1d4f6ab2c8e1d4f6ab2c8e
```

### 1.1 字段

| 部分 | 取值 | 校验 |
|---|---|---|
| `scheme` | `http` | 必须（`https` 视为浏览器兜底） |
| `host` | PC LAN IP | IPv4 格式 |
| `port` | URL 端口（默认 80） | 1-65535 |
| `path` | `/upload` | 必须 |
| `?k=` | 32 字符 hex（16 字节）| 必须，`\A[a-f0-9]{32}\z` |

### 1.2 长度估算

| 部分 | 字节 |
|---|---|
| `http://` | 7 |
| IP `192.168.1.10` | 12 |
| `:8765` | 6 |
| `/upload?k=` | 10 |
| secret | 32 |
| **合计** | **~67 字符** |

QR 编码后 ~ 25×25 像素，正常扫码无问题。

### 1.3 公网 relay 模式 QR

公网 relay 模式 QR **不含** `?k=`（保留 v0.12 行为）：

```
http://relay.example.com:8766/upload
```

App 端扫码视为"二维码无效"，需在同 LAN 扫码。

---

## 二、PC 端 QR 生成

### 2.1 触发时机

| 触发 | 说明 |
|---|---|
| HTTP 服务启动（**v0.14**） | 若 `config.db.upload_secret` 存在 → 复用；否则 → 生成 + 写入 |
| IP 切换（多 IP UI） | QR host 字段刷新（secret 不变） |
| 重置密钥 | 立即重生成 secret + 写回 `config.db.upload_secret` + QR 刷新 |
| 端口变化 | 启动后立即生成（复用 secret） |

### 2.2 核心代码位置

- QR 内容生成：[UploadServerViewModel.BuildDisplayUrl()](../../StartTooler/ViewModels/UploadServerViewModel.cs)
- secret 生成：`UploadServerService.GenerateSecret()`
- QR 渲染：[UploadServerView.axaml](../../StartTooler/Views/UploadServerView.axaml) L268-L339

---

## 三、App 端扫码解析

### 3.1 解析流程

```
扫码结果 URL
   ↓
[是 http(s)://?]
   ├─ 是 → 进入字段提取
   └─ 否 → 提示"二维码无效"

字段提取
   ├─ host 非空 + IPv4 格式
   ├─ port 1-65535
   ├─ path == /upload
   ├─ query["k"] 是 32 字符 hex
   └─ 全部通过 → 进入 health 验证

任一校验失败 → 提示"二维码无效"
```

### 3.2 核心代码模板

#### iOS Swift

```swift
func parseQr(_ url: URL) -> Space? {
    guard url.scheme == "http" || url.scheme == "https" else {
        return nil
    }
    guard url.path == "/upload" else {
        return nil
    }
    guard let host = url.host, isIPv4(host) else {
        return nil
    }
    guard let port = url.port, (1...65535).contains(port) else {
        return nil
    }
    guard let k = url.queryParameter("k"),
          k.range(of: "^[a-f0-9]{32}$", options: .regularExpression) != nil else {
        return nil
    }
    return Space(ip: host, port: port, secret: k, name: nil)
}
```

#### Android Kotlin

```kotlin
fun parseQr(url: Uri): Space? {
    if (url.scheme !in listOf("http", "https")) return null
    if (url.path != "/upload") return null
    val host = url.host ?: return null
    if (!isIPv4(host)) return null
    val port = url.port.takeIf { it in 1..65535 } ?: return null
    val k = url.getQueryParameter("k") ?: return null
    if (!k.matches(Regex("^[a-f0-9]{32}$"))) return null
    return Space(ip = host, port = port, secret = k, name = null)
}
```

### 3.3 解析后行为

| 校验 | 行为 |
|---|---|
| 全通过 | 写入 `{ip, port, secret}` → GET `/api/v1/health` → 取 `name` 作为空间名 → 持久化 |
| 任一失败 | 提示"二维码无效" → 回空间列表 |

---

## 四、URL 解析规则

### 4.1 字段优先级

| 字段 | 提取位置 | 校验函数 |
|---|---|---|
| `ip` | URL `host` | IPv4 正则 |
| `port` | URL `port` | 1-65535 |
| `secret` | URL `query["k"]` 或 HTTP `X-Key` header | 32 hex |

### 4.2 兼容旧字段

| 旧字段 | 处理 |
|---|---|
| `?token=` | 忽略（向后保护） |
| `?t=` | 忽略 |
| `X-Token` header | 忽略 |

> v0.13 起所有 v0.12 token 字段均失效；App 端不要解析，仅忽略。

### 4.3 非法字段示例

| URL | 失败原因 |
|---|---|
| `starttooler://pair?ip=...&port=...&k=...` | 协议不是 http |
| `http://192.168.1.10:8765/admin?k=xxx` | path 不是 /upload |
| `http://192.168.1.10:8765/upload` | 缺 `?k=` |
| `http://192.168.1.10:8765/upload?k=short` | secret 长度错 |
| `http://192.168.1.10:8765/upload?k=UPPER...` | 非小写 hex |
| `http://[fe80::1]:8765/upload?k=xxx` | IPv6 不支持 |
| `http://example.com:8765/upload?k=xxx` | 公网域名（需同 LAN） |

---

## 五、Secret 协议

### 5.1 生成

| 项 | 值 |
|---|---|
| 长度 | 16 字节 = 32 字符 hex |
| 生成 | 首次启动 `RandomNumberGenerator.Fill(16)` |
| 范围 | `0-9a-f` |
| 持久化（**v0.14**） | **默认持久化到 `config.db.upload_secret`** |
| 重置 | UI 点"重置密钥"立即重生成 + 写回 `config.db` |

### 5.2 校验

| 维度 | 规则 |
|---|---|
| 大小写 | **仅接受小写**（PC 端 hex 输出 ToLowerInvariant） |
| 长度 | 严格 32 字符 |
| 字符集 | `^[a-f0-9]{32}$` |
| 错误码 | 401 `{ "error": "invalid secret" }` |

### 5.3 权威源（v0.14 更新）

> **secret 的权威源是 PC 端 `config.db.upload_secret`**。
>
> - App 端**不**从 health 响应里取 secret（仅作回包校验）
> - PC 端**持久化** secret（v0.14 起）
> - 启动时复用持久化值；用户点「重置密钥」才变化
> - 重置后 → 旧 secret 立即 401 → App 端必须重新扫码

---

## 六、QR 容量与渲染

| QR 版本 | 容量（字母数字） | 实际像素 |
|---|---|---|
| Version 3 | 42 字符 | 29×29 |
| Version 4 | 56 字符 | 33×33 |
| Version 5 | 76 字符 | 37×37 |

我们的 URL（~67 字符）→ Version 4-5 QR → 33-37 像素 → 屏幕可正常扫码。

PC 端渲染：[QRCoder](https://github.com/codebude/QRCoder) 或系统内置组件。

---

## 七、兜底与边界

### 7.1 扫码兜底

| 扫码结果 | 反应 |
|---|---|
| `http://.../upload?k=xxx` | 解析 → 进 health |
| `https://...` | 浏览器 H5 上传 |
| 其它 | 提示"二维码无效" |

### 7.2 同 PC 重复扫码

| 状态 | 反应 |
|---|---|
| 同 `name` 同 `ip` | 静默覆盖（PC 重启场景） |
| 同 `name` 不同 `ip` | 视为同一 PC，覆盖 IP |
| 不同 `name` | 新增空间 |

### 7.3 失效 QR（v0.14 更新）

| 原因 | 反应 |
|---|---|
| PC 端重启 | **v0.14**：secret 复用，App 旧 secret 仍然有效 → 无需重扫 |
| PC 端重置密钥 | secret 已变，扫码后 health 200 但 secret 不匹配 → 401 |
| PC 端换 IP | 扫码后 health 失败 → 提示"连不上 PC" |

> v0.14 起，App 端只在「重置密钥」后必须重新扫码；常规重启无需重扫。

### 7.4 公网 relay QR

公网 relay QR 不含 `?k=`。App 端扫码视为"二维码无效"，引导用户同 LAN 扫码。

未来 v0.14 可能扩展 relay 扫码（独立决策）。

---

## 八、安全考虑

| 风险 | 缓解 |
|---|---|
| QR 截图泄漏 secret（**v0.14**） | 32 字符 hex；**仅用户主动「重置密钥」才失效**；泄露窗口拉长到「用户感知到」为止 |
| 摄像头拍屏 QR | 短暂可见窗口 |
| LAN 嗅探 | HTTP 明文（家庭 LAN 风险可控） |
| 客户端伪造 QR | health 验证（service/version/name） |

**不实现的**：

- ❌ QR 加密（明文）
- ❌ exp 过期字段（v0.13 不引入；v0.14 评估）
- ❌ 自定义 schema（v0.14 评估）

---

## 九、变更记录

| 日期 | 版本 | 内容 |
|---|---|---|
| 2026-08-21 | v0.13 | 新增文档；定义 QR 唯一发现 + 32 字符 secret 协议 |
| 2026-08-21 | v0.14 | PC 端 secret 默认持久化到 `config.db.upload_secret`；启动复用；用户「重置密钥」为唯一主动失效途径 |
