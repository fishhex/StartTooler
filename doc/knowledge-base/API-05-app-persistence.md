# API-05 · App 端持久化（v0.14）

App 端持久化 = 工作空间（多 PC）存储。本文档定义 iOS / Android / macOS 客户端存储结构、加密策略。**清理仅用户主动**（App 端不自动清理）。

> **v0.14 变更**：PC 端 secret 默认持久化，App 端 secret 失效 → 401 几乎不触发；只在用户主动「重置密钥」时才需重新扫码。
>
> 配套：[05-mobile-app.md](05-mobile-app.md) §三「工作空间模型」

## 一、存储内容

### 1.1 工作空间结构

```json
{
  "spaces": [
    {
      "name": "鱼鱼的 MacBook",
      "ip": "192.168.1.10",
      "port": 8765,
      "secret": "7f3a9b2c8e1d4f6ab2c8e1d4f6ab2c8e",
      "last_seen_at": "2026-08-21T14:32:00",
      "current_project": "m42-2025-12-13"
    },
    {
      "name": "工作室 Win11",
      "ip": "192.168.1.20",
      "port": 8765,
      "secret": "...",
      "last_seen_at": "2026-08-20T10:15:00",
      "current_project": null
    }
  ],
  "current_space_name": "鱼鱼的 MacBook"
}
```

### 1.2 字段说明

| 字段 | 类型 | 用途 |
|---|---|---|
| `name` | string | **主键**（PC 端 health 响应的 `name`）|
| `ip` | string | PC LAN IP |
| `port` | int | HTTP 端口 |
| `secret` | string | 32 字符 hex（QR 拿到的）|
| `last_seen_at` | ISO 8601 | 上次成功 health 时间 |
| `current_project` | string | 当前激活项目 basename（可空）|
| `current_space_name` | string | 全局当前空间名 |

### 1.3 安全性

| 字段 | 敏感性 |
|---|---|
| `name` / `ip` / `port` | 公开 |
| `secret` | **敏感**（必须加密） |
| `last_seen_at` / `current_project` | 半公开 |

---

## 二、iOS 端：Keychain + UserDefaults

### 2.1 选型

| 数据 | 存储 |
|---|---|
| `spaces[]` 整体 | UserDefaults（JSON 序列化；含 secret） |
| `current_space_name` | UserDefaults |

> iOS Keychain 单条存敏感字段不适合存整个 JSON 列表。整体存 UserDefaults + 加密（详见 §二.3）。

### 2.2 字段到 UserDefaults 映射

| Key | 类型 | 内容 |
|---|---|---|
| `pc.spaces` | Data (JSON) | 加密后的 spaces 数组 |
| `pc.current_space_name` | string | 当前空间名 |

### 2.3 加密方案（v0.13 简化）

v0.13 简化：不引入 CryptoKit 自加密。**依赖 iOS 设备级加密**（设备锁屏后无法访问）。

```swift
// App 启动时
let spacesData = try JSONEncoder().encode(spaces)
UserDefaults.standard.set(spacesData, forKey: "pc.spaces")
```

> 严格安全场景下应结合 Keychain + AES。本版本简化接受此权衡（用户设备锁屏即保护）。

### 2.4 关键设计：何时清

| 场景 | 行为 |
|---|---|
| 用户主动"删除空间"按钮 | 删 `pc.spaces` 里对应项 |
| 401 响应持续 3 次 | 提示"密钥已过期"，**不**自动删 |
| App 卸载 | 系统自动清 |
| 用户重建 App | 系统自动清 |

> **v0.13 原则**：App 端**不**按时间（如 30 天未用）自动清理。所有清理由用户发起。

---

## 三、Android 端：EncryptedSharedPreferences

### 3.1 选型 EncryptedSharedPreferences

AndroidX Security 提供的 `EncryptedSharedPreferences`，存**加密**键值对。

AndroidKeyStore 保护 keys + AES 加密 values。

### 3.2 字段到 EncryptedSharedPreferences 映射

| Key | 内容 |
|---|---|
| `pc.spaces` | 加密 JSON（spaces 数组）|
| `pc.current_space_name` | 加密 string |

### 3.3 配置

```kotlin
val masterKey = MasterKey.Builder(context)
    .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
    .build()

val prefs = EncryptedSharedPreferences.create(
    context,
    "pc_prefs",
    masterKey,
    EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
    EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
)
```

### 3.4 关键设计：何时清

| 场景 | 行为 |
|---|---|
| 用户主动"删除空间"按钮 | 删 `pc.spaces` 里对应项 |
| 401 响应持续 3 次 | 提示"密钥已过期"，**不**自动删 |
| App 卸载 | 系统自动清 |

> **v0.13 原则**：App 端**不**按时间（如 30 天未用）自动清理。所有清理由用户发起。

### 3.5 备份建议

EncryptedSharedPreferences 默认**不参与 Auto Backup**。建议关闭 backup：

```xml
<application
    android:allowBackup="false"
    android:fullBackupContent="false">
```

---

## 四、macOS 端：Keychain（与 iOS 一致）

```swift
// macOS 同 iOS，使用 Keychain
let query: [String: Any] = [
    kSecClass as String: kSecClassGenericPassword,
    kSecAttrService as String: "com.starttooler.app",
    kSecAttrAccount as String: "pc.spaces",
    kSecValueData as String: data,
    kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock
]
```

---

## 五、字段生命周期

### 5.1 写入时机

| 字段 | 写入时机 |
|---|---|
| `spaces[name].name` | health 200 + version 兼容 |
| `spaces[name].ip` / `port` / `secret` | QR 解析通过 + health 200 |
| `spaces[name].last_seen_at` | 每次 GET /api/v1/health 成功 |
| `spaces[name].current_project` | health 响应的 `currentProject` |
| `current_space_name` | 用户切换空间时 |

### 5.2 主键策略

| 触发 | 行为 |
|---|---|
| 扫码解析通过 + 同 `name` | 覆盖 ip/port/secret/last_seen_at（同一 PC 重启场景）|
| 扫码解析通过 + 不同 `name` | 新增空间项（多 PC） |
| `name` 变化（PC 端改名）| 视为新空间；旧项保留直到用户删 |

### 5.3 失效处理（v0.14 更新）

| 场景 | 行为 |
|---|---|
| PC 重启（**v0.14**） | secret 复用 → App 旧 secret 仍然有效 → 无需重扫 |
| PC 点「重置密钥」 | secret 已变 → health 200 → projects 401 → 提示"PC 端密钥已重置" |
| PC 换 IP | health 失败 → 提示"连不上 PC" |
| PC 关服务 | health 失败 → 同上 |
| PC 端改名 | `name` 变 → 视为新空间 |

---

## 六、清理策略（仅用户主动）

> **v0.13 原则**：App 端**不**主动清理持久化空间。所有清理由用户发起。

| 场景 | 行为 |
|---|---|
| 用户主动"删除空间" | 弹"确认" → 立即清该项 |
| 401 持续 3 次 | 提示"密钥已过期"（**不**自动删） |
| App 卸载 / 清数据 | OS 自动清 |

❌ 不做的事：

- 不按时间（如 30 天未用）自动清理
- 不按 401 次数自动清理
- 不按 PC 端状态自动清理

```swift
// 用户主动删除空间（弹确认）
func deleteSpace(_ space: Space) {
    let alert = UIAlertController(
        title: "删除空间",
        message: "确定要删除 \"\(space.name)\" 吗？",
        preferredStyle: .alert
    )
    alert.addAction(UIAlertAction(title: "取消", style: .cancel))
    alert.addAction(UIAlertAction(title: "删除", style: .destructive) { _ in
        self.persistence.remove(space.name)
        self.spaces.removeAll { $0.name == space.name }
    })
    present(alert, animated: true)
}
```

---

## 七、跨平台一致性

### 7.1 字段命名

| 字段 | iOS | Android | macOS |
|---|---|---|---|
| `pc.spaces` | ✅ | ✅ | ✅ |
| `pc.current_space_name` | ✅ | ✅ | ✅ |

### 7.2 多端独立

- iOS 工作空间 ≠ Android 工作空间（完全独立，无云同步）
- 用户可在不同设备维护不同的空间列表

---

## 八、与 PC 端持久化的对比

| 维度 | PC 端 `config.db` | App 端 Keychain / EncryptedSharedPreferences |
|---|---|---|
| 存储介质 | SQLite | Keychain / EncryptedSP |
| 加密 | ❌ 无 | ✅ 加密 |
| 持久化内容 | 服务器配置 + `upload_secret` | 工作空间列表（含 App 端复制的 secret） |
| 网络位置 | 同 PC | 同设备 |
| 内容 | PC 配置 + 持久化 secret | 客户端持久化的 ip/port/secret |

**v0.14 两端都持久化 secret**：

- PC 端：写到 `config.db.upload_secret`，重启复用
- App 端：扫码后写入加密存储

> 单一失效途径：用户点 PC 端「重置密钥」。此时 PC 端 secret 更新 + App 端旧 secret → 401 → 引导重扫。

---

## 九、安全最佳实践

### 9.1 Token 绝不进日志

```swift
// ❌ 错
print("Secret: \(secret)")

// ✅ 对
print("[DEBUG] Secret len=\(secret.count)")
// 或脱敏：print("[DEBUG] Secret=****\(secret.suffix(4))")
```

### 9.2 JSON 序列化

iOS `Codable` / Android `Gson` 默认全部序列化字段。**不要**用 `@JsonIgnore` 跳过 secret（保留可序列化）；用**加密存储**防泄漏。

### 9.3 写时加密读时解密

EncryptedSharedPreferences 自动做。UserDefaults 用户需自行加密或接受"设备锁屏保护"。

---

## 十、未来扩展

### 10.1 iCloud 同步（v0.14 候选）

iOS Keychain 可选同步到 iCloud（需 `kSecAttrSynchronizable = true`）。当前 v0.13 **不启用**同步（避免跨设备泄漏 secret）。

### 10.2 多端并用（v0.14 候选）

未来用户可同时在 iOS / Android 维护同一 PC 的空间。当前 v0.13 各端独立。

### 10.3 自定义空间名（v0.14 候选）

允许用户在 App 内为 PC 起别名（区别于 PC 端 `name`）。当前 v0.13 直接展示 PC 端 `name`。

---

## 十一、版本兼容

| App 版本 | 持久化策略 |
|---|---|
| 1.0.0（v0.13） | 多空间 + EncryptedSP / iOS Keychain+UD |
| 1.1.0（v0.14） | 同上 + PC 端 secret 持久化协同 |

字段**新增** = 老 App 不读，无破坏。**字段删除** = 新 App 老 db 读不出，OK。

> **v0.13 → v0.14 升级路径**：App 端持久化结构无变化（仍存 ip/port/secret）；用户升级后首次启动若 health 200 即直接进主页，无迁移动作。降级到 v0.13 App 也兼容（仍按 secret 字段调用）。

---

## 十二、变更记录

| 日期 | 版本 | 内容 |
|---|---|---|
| 2026-08-21 | v0.13 | 改写：多空间列表模型；移除 UDP 5s / 6 位 token 持久化 |
| 2026-08-21 | v0.14 | PC 端 secret 默认持久化；App 端 401 → 引导重扫的触发条件改为「PC 重置密钥」 |
