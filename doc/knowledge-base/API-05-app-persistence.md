# API-05 · App 端持久化

App 端持久化 = 客户端存储。本文档定义 iOS / Android 的客户端存储字段、加密策略、失效处理。

## 一、存储什么

| 数据 | 用途 | 安全性 |
|---|---|---|
| `pc.last_ip` | App 启动直连 PC | 公开 |
| `pc.last_name` | UI 显示 | 公开 |
| `pc.last_token` | 跳过 Token 输入 | **敏感** |
| `pc.last_seen_at` | 时间戳 | 公开 |
| `pc.last_connected_at` | 上次成功连接时间 | 公开 |
| `pc.discovered` | 已知 PC 列表（含 IP/端口/name） | 公开 |

### 字段语义

| 字段 | 类型 | 用途 |
|---|---|---|
| `last_ip` | string | 上次成功连接的 IP（含端口） |
| `last_name` | string | PC 名（鱼鱼的 MacBook） |
| `last_token` | string | 6 位数字 Token |
| `last_seen_at` | long | Unix 毫秒（PC UDP 广播最后收到） |
| `last_connected_at` | long | 上次成功 GET /api/v1/health 的时间 |
| `discovered[]` | array | 本会话内 UDP 抓到的 PC 列表 |

### 字段大小

| 字段 | 平均字节 | 说明 |
|---|---|---|
| `last_ip` | ~20 | "192.168.1.10:8765" |
| `last_token` | 6 | "123456" |
| `discovered[]` | ~500 | 最多 10 个 PC 记录 |

总大小 < 1 KB。

## 二、iOS 端：Keychain

### 2.1 选型 Keychain

iOS 提供 `Keychain Services` API，给 App 存**单条**敏感凭据（SSL 证书、token、密码）。我们用它存 Token。

非敏感数据（IP、PC name）放 **UserDefaults**。混合存储。

### 2.2 字段到 Keychain 映射

| 字段 | Keychain / UserDefaults | 备注 |
|---|---|---|
| `last_token` | Keychain `kSecClassGenericPassword` | 加密存储 |
| `last_ip` | UserDefaults `pc.last.ip` | 公开 |
| `last_name` | UserDefaults `pc.last.name` | 公开 |
| `discovered[]` | UserDefaults `pc.discovered`（JSON） | 本会话缓存 |

### 2.3 Keychain 配置

```swift
// iOS Keychain query
let query: [String: Any] = [
    kSecClass as String: kSecClassGenericPassword,
    kSecAttrService as String: "com.starttooler.app",
    kSecAttrAccount as String: "pc.last.token",
    kSecValueData as String: token.data(using: .utf8)!,
    kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock
]
```

| 属性 | 值 | 含义 |
|---|---|---|
| `kSecClass` | `kSecClassGenericPassword` | 通用密码项 |
| `kSecAttrService` | `"com.starttooler.app"` | 服务名（App 唯一） |
| `kSecAttrAccount` | `"pc.last.token"` | 账户名 |
| `kSecAttrAccessible` | `kSecAttrAccessibleAfterFirstUnlock` | 首次解锁后可用 |

### 2.4 关键设计：何时清 Token

- 用户主动"退出连接"按钮 → 删 `last_token`
- 401 响应 → 删 `last_token`（但保留 IP 方便重连）
- 用户重建 App → 系统自动清
- iCloud 备份 → Keychain **可选不备份**（避免跨设备恢复导致 Token 泄漏）

### 2.5 iCloud 同步建议

默认 Keychain **参与 iCloud 同步**（如果用户开启）。**不推荐**同步我们的 Token：

```swift
// 关闭 Token 的 iCloud 同步
let query: [String: Any] = [
    kSecAttrSynchronizable as String: kCFBooleanFalse!
]
```

## 三、Android 端：EncryptedSharedPreferences

### 3.1 选型 EncryptedSharedPreferences

Android 提供 `EncryptedSharedPreferences`（AndroidX Security），存**加密**键值对。

AndroidKeyStore 保护 keys + AES 加密 values。

### 3.2 字段到 EncryptedSharedPreferences 映射

| 字段 | Key | 备注 |
|---|---|---|
| `last_token` | `pc.last.token` | 加密 |
| `last_ip` | `pc.last.ip` | 加密 |
| `last_name` | `pc.last.name` | 加密 |
| `last_seen_at` | `pc.last.seen_at` | 加密 |
| `last_connected_at` | `pc.last.connected_at` | 加密 |
| `discovered[]` | `pc.discovered`（JSON） | 加密 |

全部字段都加密（实现简单，避免分别挑选）。

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

### 3.4 关键设计：何时清 Token

- 用户主动"退出连接"按钮 → 删 `pc.last.token`
- 401 响应 → 删 `pc.last.token`（保留 IP）
- 用户清 App 数据 → 系统自动清
- 卸载 → 系统自动清

### 3.5 备份建议

EncryptedSharedPreferences 默认**不参与 Auto Backup**。如需备份，需在 `AndroidManifest.xml` 中显式配置：

```xml
<application
    android:allowBackup="false"
    android:fullBackupContent="false">
```

**强烈建议**关闭 backup（不暴露 Token）。

## 四、字段生命周期

### 4.1 写入时机

| 字段 | 写入时机 |
|---|---|
| `last_ip` | 首次 GET /api/v1/health 成功 |
| `last_name` | 同上 |
| `last_token` | 用户在 Token 验证页输入并验证成功 |
| `last_seen_at` | 每次收到 UDP 广播 |
| `last_connected_at` | 每次 GET /api/v1/health 成功 |
| `discovered[]` | 每次 UDP 扫描结束（覆盖，不累积） |

### 4.2 失效处理

| 场景 | 失效字段 | 反应 |
|---|---|---|
| PC 端换 WiFi（IP 变） | `last_ip` 失效 | App 试探 /health 失败 → 跳连接页 |
| PC 端 Token 重置 | `last_token` 失效 | 401 → 清 Token，跳验证页 |
| PC 端关服务 | `last_seen_at` 不更新 | 试探 /health 失败 → 跳连接页（IP 暂保留） |
| 用户手动切换 PC | 全部覆盖 | 写新 IP/Token |
| 用户清 App 数据 | 全部清 | 重走首启流程 |

### 4.3 保留有效期

| 字段 | 保留 |
|---|---|
| `last_ip` | 永久（直到用户切换） |
| `last_token` | 永久（直到 401 或用户切换） |
| `last_seen_at` | 永久（历史信息） |
| `discovered[]` | 仅本会话（App 关闭清） |

## 五、跨平台一致性

### 5.1 字段命名约定

| 字段 | 跨平台 |
|---|---|
| `last_ip` | ✅ iOS / Android 都用 |
| `last_token` | ✅ |
| `last_name` | ✅ |
| `last_seen_at` | ✅ |
| `last_connected_at` | ✅ |
| `discovered[]` | ✅ |

### 5.2 Token 字段冲突

如果用户同时有 iOS 和 Android 两个 App，分别连不同的 PC：

- iOS Token = "123456"（连鱼鱼的 MacBook）
- Android Token = "789012"（连工作室 Win11）

**完全独立**——没有同步。每台 PC 自己的 Token。

## 六、与 PC 端持久化的对比

| 维度 | PC 端 `config.db` | App 端 Keychain/SP |
|---|---|---|
| 存储介质 | SQLite | Keychain / EncryptedSharedPreferences |
| 加密 | ❌ 无 | ✅ 加密 |
| 多字段 | ✅ N | ❌ 单字段为主 |
| 拷贝 | 容易（db 文件） | 难（系统级） |
| 网络位置 | 同 PC | 同设备 |
| 内容 | 完整配置 | 客户端状态 |

**两套独立**，互不依赖：

- PC 端 `config.db` 不存客户端 IP（不知道有谁连过）
- App 端不存 PC 端项目目录（每次动态拉）

## 七、错误处理

### 7.1 Keychain 失败

| 错误 | 原因 | 缓解 |
|---|---|---|
| `errSecAuthFailed` | 设备锁定 | 提示用户解锁 |
| `errSecItemNotFound` | 没记录 | 首次正常 |
| `errSecDuplicateItem` | 重复 | 删除旧的 |
| `errSecParam` | 参数错 | 修复 |

### 7.2 EncryptedSharedPreferences 失败

| 错误 | 原因 | 缓解 |
|---|---|---|
| `KeyStoreException` | 系统问题 | 退到 SharedPreferences |
| `GeneralSecurityException` | 加密失败 | 提示用户清 App 数据 |
| `IOException` | 磁盘 | 提示 |

### 7.3 优雅降级

Keychain 失败 → 退到 UserDefaults（iOS）或 SharedPreferences（Android）。

存明文 token 时设置提醒：

```swift
// iOS: UserDefaults 退到时打印警告
print("[WARN] Token 存储退到 UserDefaults，未加密")
```

```kotlin
// Android: 类似
Log.w("Persistence", "退到 SharedPreferences，未加密")
```

## 八、最佳实践

### 8.1 Token 绝不进日志

```swift
// ❌ 错
print("Token: \(token)")

// ✅ 对
print("[DEBUG] Token len=\(token.count)")
```

### 8.2 序列化时排除 Token

```kotlin
// JSON 序列化时排除 token
@JsonIgnore
private val lastToken: String = ""

// 排除字段（防止 Gson/Moshi 序列化泄漏）
```

### 8.3 写时加密，读时解密

EncryptedSharedPreferences 自动做。Keychain 也要按规范操作。

### 8.4 定期清无主 Token

如果 `last_seen_at` 超过 30 天没更新 → 主动清：

```swift
if let lastSeen = lastSeenAt {
    if Date().timeIntervalSince(lastSeen) > 30 * 86400 {
        clearAll()
    }
}
```

## 九、未来扩展

### 9.1 多台 PC 持久化

当前只存"最近一台"。未来可支持：

```json
{
  "history": [
    {"ip": "192.168.1.10", "name": "鱼鱼的 MacBook", "token": "123456"},
    {"ip": "192.168.1.20", "name": "工作室 Win11", "token": "789012"}
  ]
}
```

### 9.2 Token 加密预协商

未来可用 ECDH 预协商加密 Token（防 LAN 嗅探）：

1. PC 端发公钥
2. App 端用公钥加密 Token
3. PC 端私钥解密

需要 PC 端发起一次性的交换协议。

### 9.3 端到端加密

未来照片可以 E2E 加密后上传。

## 十、API 客户端读取

客户端（iOS / Android）**不读写** PC 端 `config.db`。它**只读**自己 App 内的 Keychain/SP。

```
┌──────────┐                    ┌──────────┐
│  iOS     │  PC 的 IP/Token 在  │  PC 端    │
│  App    │  iOS Keychain 里     │  config.db│
│          │  ←─── 独立 ───→    │  (无客户端)│
└──────────┘                    └──────────┘
```

**两套独立，互不读写**。

## 十一、调试路径

### iOS 模拟器

```
Xcode → Window → Devices and Simulators → 选模拟器 → Download Container
```

解压后：

```
AppData/Library/Preferences/com.starttooler.app.plist
```

### Android 模拟器

```bash
adb shell run-as com.starttooler.app ls /data/data/com.starttooler.app/shared_prefs/
```

或用 `Android Studio → App Inspection → Database`。

## 十二、版本兼容

| App 版本 | 持久化策略 |
|---|---|
| 1.0.0 | 上文所述（Keychain + UserDefaults / EncryptedSharedPreferences） |
| 1.1.0 | 加多 PC 历史 |
| 1.2.0 | 加 Token 重置自动清 |

字段**新增** = 老 App 启动不读，无破坏。**字段删除** = 新 App 老 db 读不出，OK。

详见 [API-04-config-schema.md](API-04-config-schema.md) PC 端持久化对照。
