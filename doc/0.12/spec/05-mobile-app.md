# S05 · 移动端 App 技术规格

**状态**：技术实施规格
**关联**：[demand/05-mobile-app.md](../demand/05-mobile-app.md)
**前置决策**：[decisions/0001-app-repo-location.md](../decisions/0001-app-repo-location.md)

## 一、范围与限制

本文档定义**移动端 App 独立仓库**（[decisions/0001](../decisions/0001-app-repo-location.md)）的技术实施规格。

- 不写 PC 端代码（PC 端实施见 [spec/04-mobile-lan-sync.md](04-mobile-lan-sync.md)）
- 不写协议层（PC 端定，详见 [KB-API-01](../../knowledge-base/API-01-http-routes.md) + [KB-API-03](../../knowledge-base/API-03-udp-broadcast.md)）
- 只写 App 端**技术选型 / 架构 / 状态机 / 实施分解**

## 二、技术栈选型

### 2.1 iOS

| 维度 | 选型 | 理由 |
|---|---|---|
| 语言 | Swift 5.10+ | iOS 16+ 最低 |
| UI 框架 | SwiftUI | iOS 16+ 稳定 |
| 网络 | URLSession | Apple 原生 |
| 持久化 | Keychain + UserDefaults | 敏感 / 公开 分层 |
| UDP | NWConnection (Network.framework) | iOS 12+ |
| JSON | Codable | Apple 原生 |
| 异步 | async / await | Swift 5.5+ |
| 测试 | XCTest + Swift Testing | iOS 16+ |

### 2.2 Android

| 维度 | 选型 | 理由 |
|---|---|---|
| 语言 | Kotlin 1.9+ | Android 10+ 最低 |
| UI 框架 | Jetpack Compose | 现代推荐 |
| 网络 | OkHttp 4.x | 业界标准 |
| 持久化 | EncryptedSharedPreferences | AndroidX Security |
| UDP | java.net.DatagramSocket | Java 标准 |
| JSON | Kotlinx.Serialization | Kotlin 官方 |
| 异步 | Coroutines | Kotlin 官方 |
| 测试 | JUnit + Compose Test | 标准 |

### 2.3 跨平台？

**不采用** Flutter / React Native / KMP。理由：

- 性能：上传 500MB 照片不能用 webview 桥接
- 体积：原生 App < 5MB，跨端 30-50MB
- 团队：原生开发者更易招
- 复用：业务一致但技术栈不同；共享文档 / 不共享代码

## 三、目录结构

### 3.1 iOS

```
ios/
├── StartTooler.xcworkspace
├── StartTooler/
│   ├── App/
│   │   ├── StartToolerApp.swift           # @main
│   │   └── AppDelegates.swift
│   ├── Features/
│   │   ├── Discovery/
│   │   │   ├── DiscoveryService.swift     # UDP 监听
│   │   │   ├── PC.swift                   # 数据模型
│   │   │   └── DiscoveryView.swift
│   │   ├── Connection/
│   │   │   ├── ConnectionService.swift    # HTTP /api/v1/health
│   │   │   ├── ConnectionView.swift
│   │   │   └── TokenInputView.swift
│   │   ├── Project/
│   │   │   ├── ProjectService.swift       # HTTP /api/v1/projects
│   │   │   ├── ProjectListView.swift
│   │   │   └── Project.swift
│   │   ├── Upload/
│   │   │   ├── UploadService.swift        # multipart 上传
│   │   │   ├── UploadView.swift
│   │   │   ├── PhotoPicker.swift          # PHPicker
│   │   │   └── UploadProgress.swift
│   │   └── Persistence/
│   │       ├── KeychainStorage.swift
│   │       ├── UserDefaultsStorage.swift
│   │       └── Preferences.swift
│   ├── Core/
│   │   ├── Network/
│   │   │   ├── HTTPClient.swift           # URLSession 封装
│   │   │   ├── ErrorMapper.swift          # 错误 → i18n
│   │   │   └── RetryPolicy.swift
│   │   ├── Storage/
│   │   │   └── ...
│   │   └── Models/
│   │       └── ProjectDTO.swift
│   └── Resources/
│       ├── Localizable.xcstrings
│       └── Assets.xcassets
└── StartToolerTests/
    └── ...
```

### 3.2 Android

```
android/
├── app/
│   ├── src/main/kotlin/com/starttooler/app/
│   │   ├── MainActivity.kt
│   │   ├── features/
│   │   │   ├── discovery/
│   │   │   │   ├── DiscoveryService.kt
│   │   │   │   ├── PC.kt
│   │   │   │   └── DiscoveryView.kt
│   │   │   ├── connection/
│   │   │   │   ├── ConnectionService.kt
│   │   │   │   ├── ConnectionView.kt
│   │   │   │   └── TokenInputView.kt
│   │   │   ├── project/
│   │   │   │   ├── ProjectService.kt
│   │   │   │   ├── ProjectListView.kt
│   │   │   │   └── Project.kt
│   │   │   ├── upload/
│   │   │   │   ├── UploadService.kt
│   │   │   │   ├── UploadView.kt
│   │   │   │   ├── PhotoPicker.kt
│   │   │   │   └── UploadProgress.kt
│   │   │   └── persistence/
│   │   │       ├── EncryptedPreferences.kt
│   │   │       └── Preferences.kt
│   │   ├── core/
│   │   │   ├── network/
│   │   │   │   ├── HTTPClient.kt
│   │   │   │   ├── ErrorMapper.kt
│   │   │   │   └── RetryPolicy.kt
│   │   │   └── models/
│   │   │       └── ProjectDTO.kt
│   │   └── App.kt
│   ├── src/main/res/
│   │   ├── values/strings.xml             # i18n
│   │   ├── values-en/strings.xml
│   │   └── ...
│   └── src/test/
│       └── ...
└── build.gradle.kts
```

## 四、模块详细规格

### 4.1 Discovery

**职责**：通过 UDP 广播发现局域网内的 PC 端。

**iOS 实现**：

```swift
import Network

final class DiscoveryService {
    private var connection: NWConnection?
    private var listener: NWListener?
    private var onPCDiscovered: (PC) -> Void
    
    func start() throws {
        // 创建 UDP listener 监听 9876 端口
        let parameters = NWParameters.udp
        parameters.allowLocalEndpointReuse = true
        listener = try NWListener(using: parameters, on: 9876)
        listener?.newConnectionHandler = { [weak self] connection in
            self?.handleConnection(connection)
        }
        listener?.start(queue: .global())
    }
    
    private func handleConnection(_ connection: NWConnection) {
        connection.receiveMessage { [weak self] data, _, _, _ in
            guard let data = data,
                  let json = try? JSONDecoder().decode(UDPAnnounce.self, from: data)
            else { return }
            self?.onPCDiscovered(PC(from: json))
        }
    }
    
    func stop() {
        listener?.cancel()
    }
}
```

**Android 实现**：

```kotlin
class DiscoveryService {
    private var socket: DatagramSocket? = null
    private val scope = CoroutineScope(Dispatchers.IO)
    
    fun start() {
        scope.launch {
            socket = DatagramSocket(9876).apply {
                broadcast = true
            }
            val buffer = ByteArray(4096)
            while (isActive) {
                val packet = DatagramPacket(buffer, buffer.size)
                socket?.receive(packet)
                val json = Json.parseToJsonElement(
                    String(packet.data, packet.offset, packet.length)
                )
                val pc = PC.fromJson(json)
                _pcFlow.emit(pc)
            }
        }
    }
    
    fun stop() {
        scope.cancel()
        socket?.close()
    }
}
```

**关键不变量**：

- 5 秒扫描结束 → 跳到验证页或主页
- 收到 PC 广播 → 累积到 discovered[]
- 重复 PC（同名同 IP）→ 覆盖，不重复添加

### 4.2 Connection

**职责**：Token 验证 + 持久化 IP/Token。

**iOS 实现**：

```swift
final class ConnectionService {
    private let httpClient: HTTPClient
    private let preferences: Preferences
    
    func connect(ip: String, token: String) async throws -> PC {
        let url = URL(string: "http://\(ip)/api/v1/health")!
        let response = try await httpClient.get(url, token: token)
        let pc = PC(from: response)
        try preferences.save(ip: ip, name: pc.name, token: token)
        return pc
    }
    
    func tryReconnect() async throws -> Bool {
        guard let (ip, token) = preferences.load() else { return false }
        return try await connect(ip: ip, token: token)
        .map { _ in true }
        .recover { error in
            if case .invalidToken = error { return false }
            throw error
        }
    }
}
```

**Android 实现**：类似，差异在用 `Result` / `Either` 处理。

**关键不变量**：

- 401 → 抛 `invalidToken` 错误
- 404 → 抛 `notFound` 错误
- 网络失败 → 抛 `networkError` 错误
- 任何错误 → Token 不保存

### 4.3 Project

**职责**：拉取项目列表。

**iOS 实现**：

```swift
final class ProjectService {
    func listProjects(token: String) async throws -> [Project] {
        let url = URL(string: "http://\(ip)/api/v1/projects?token=\(token)")!
        let response = try await httpClient.get(url, token: token)
        return response.items.map(Project.init)
    }
}
```

**DTO**（iOS Swift 端）：

```swift
struct ProjectListResponse: Codable {
    let items: [ProjectListItem]
}

struct ProjectListItem: Codable {
    let name: String
    let path: String
    let projectName: String?
    let fileCount: Int64
    let sizeMb: Int64
    let isCurrent: Bool
}
```

**关键不变量**：

- 项目按 `isCurrent` 分组（当前项目置顶）
- 项目加载失败 → 保留旧列表 + 提示刷新
- 切换项目 → 不影响已选文件

### 4.4 Upload

**职责**：批量上传照片到指定项目。

**iOS 实现**：

```swift
final class UploadService {
    func upload(
        projectName: String,
        files: [URL],
        token: String,
        progress: @escaping (Int, Int) -> Void
    ) async throws -> UploadResult {
        let boundary = "Boundary-\(UUID().uuidString)"
        var request = URLRequest(url: ...)
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", 
                         forHTTPHeaderField: "Content-Type")
        
        var body = Data()
        for file in files {
            body.append("--\(boundary)\r\n")
            body.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(file.lastPathComponent)\"\r\n")
            body.append("Content-Type: \(mimeType(for: file))\r\n\r\n")
            body.append(try Data(contentsOf: file))
            body.append("\r\n")
        }
        body.append("--\(boundary)--\r\n")
        request.httpBody = body
        request.setValue("\(body.count)", forHTTPHeaderField: "Content-Length")
        
        let (data, _) = try await URLSession.shared.upload(for: request, from: body)
        let response = try JSONDecoder().decode(UploadResponse.self, from: data)
        progress(files.count, files.count)
        return response
    }
}
```

**Android 实现**（使用 OkHttp）：

```kotlin
class UploadService {
    suspend fun upload(
        projectName: String,
        files: List<Uri>,
        token: String,
        onProgress: (Int, Int) -> Unit
    ): UploadResponse {
        val request = MultipartBody.Builder()
            .setType(MultipartBody.FORM)
        files.forEach { uri ->
            val bytes = context.contentResolver.openInputStream(uri)?.readBytes()
            request.addFormDataPart(
                "file",
                uri.lastPathSegment ?: "file",
                bytes!!.toRequestBody()
            )
        }
        val httpRequest = Request.Builder()
            .url("http://$ip/api/v1/projects/$projectName/upload?token=$token")
            .post(request.build())
            .build()
        val response = okHttpClient.newCall(httpRequest).execute()
        return Json.decodeFromString(response.body!!.string())
    }
}
```

**关键不变量**：

- 单文件 > 500MB → 客户端预校验（减少无效请求）
- 单次上传 ≤ 50 张 → 客户端预校验
- 并发上传 = 1（避免抢 PC 端资源）
- 失败/取消 → 不重试

### 4.5 Persistence

详见 [API-05-app-persistence.md](../../knowledge-base/API-05-app-persistence.md)。

**iOS Keychain**：

```swift
final class KeychainStorage {
    private let service = "com.starttooler.app"
    
    func saveToken(_ token: String) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: "pc.last.token",
            kSecValueData as String: token.data(using: .utf8)!,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock,
            kSecAttrSynchronizable as String: false
        ]
        SecItemDelete(query as CFDictionary)
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeychainError.failed(status) }
    }
    
    func loadToken() throws -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: "pc.last.token",
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess else { return nil }
        return String(data: result as! Data, encoding: .utf8)
    }
}
```

**Android EncryptedSharedPreferences**：

```kotlin
class EncryptedPreferences(context: Context) {
    private val prefs by lazy {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        EncryptedSharedPreferences.create(
            context,
            "pc_prefs",
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    }
    
    fun saveToken(token: String) {
        prefs.edit().putString("pc.last.token", token).apply()
    }
    
    fun loadToken(): String? = prefs.getString("pc.last.token", null)
    
    fun clearToken() {
        prefs.edit().remove("pc.last.token").apply()
    }
}
```

## 五、HTTP 客户端

### 5.1 iOS URLSession 封装

```swift
final class HTTPClient {
    private let session: URLSession
    
    init() {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 60
        self.session = URLSession(configuration: config)
    }
    
    func get<T: Codable>(
        _ url: URL,
        token: String? = nil
    ) async throws -> T {
        var request = URLRequest(url: url)
        if let token = token {
            request.setValue(token, forHTTPHeaderField: "X-Token")
        }
        let (data, response) = try await session.data(for: request)
        try mapResponse(response: response, data: data)
        return try JSONDecoder().decode(T.self, from: data)
    }
    
    private func mapResponse(response: URLResponse, data: Data) throws {
        guard let http = response as? HTTPURLResponse else {
            throw HTTPError.invalidResponse
        }
        switch http.statusCode {
        case 200...299: return
        case 401: throw HTTPError.invalidToken
        case 404: throw HTTPError.notFound
        case 405: throw HTTPError.methodNotAllowed
        default: throw HTTPError.serverError(http.statusCode)
        }
    }
}
```

### 5.2 Android OkHttp 封装

```kotlin
class HTTPClient {
    private val client = OkHttpClient.Builder()
        .connectTimeout(30, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .build()
    
    suspend fun <T> get(
        url: String,
        token: String? = null,
        decode: (String) -> T
    ): T {
        val request = Request.Builder()
            .url(url)
            .apply { token?.let { addHeader("X-Token", it) } }
            .build()
        val response = client.newCall(request).execute()
        return when (response.code) {
            in 200..299 -> decode(response.body!!.string())
            401 -> throw HTTPError.InvalidToken
            404 -> throw HTTPError.NotFound
            405 -> throw HTTPError.MethodNotAllowed
            else -> throw HTTPError.ServerError(response.code)
        }
    }
}
```

## 六、错误映射

详见 [API-06-error-i18n.md](../../knowledge-base/API-06-error-i18n.md)。

iOS：

```swift
enum AppError: LocalizedError {
    case invalidToken
    case notFound
    case methodNotAllowed
    case serverError(Int)
    case networkError(Error)
    case timeout
    case noPC
    
    var errorDescription: String? {
        switch self {
        case .invalidToken: return NSLocalizedString("error.invalid_token", comment: "")
        case .notFound: return NSLocalizedString("error.not_found", comment: "")
        // ...
        }
    }
}
```

Android：

```kotlin
sealed class AppError(val message: String) {
    object InvalidToken : AppError("invalid_token")
    object NotFound : AppError("not_found")
    data class ServerError(val code: Int) : AppError("server_error_$code")
    data class NetworkError(val cause: Throwable) : AppError("network_error")
    object Timeout : AppError("timeout")
    object NoPC : AppError("no_pc")
}
```

## 七、并发模型

### 7.1 iOS

- SwiftUI Views = `@MainActor`
- Services = `actor` 或 `class` + `@MainActor`
- Network = `async` 函数
- 持久化 = `actor` 串行访问

### 7.2 Android

- Compose UI = `viewModelScope.launch`、`StateFlow`
- Services = `class` with `Dispatchers.IO`
- Network = `suspend` 函数
- Persistence = `class` 串行读

## 八、测试计划

### 8.1 iOS

| 层级 | 测试 |
|---|---|
| 单元 | XCTest 各 Service |
| 集成 | URLSession protocol mock |
| UI | XCUITest 关键路径 |
| 真机 | iPhone 14 / iPad mini 6 |

### 8.2 Android

| 层级 | 测试 |
|---|---|
| 单元 | JUnit 各 Service |
| 集成 | OkHttp Interceptor mock |
| UI | Compose Test |
| 真机 | Pixel 6 / 三星 S23 |

### 8.3 端到端

- 真机 + PC 端服务同时跑
- 跑完整流程：发现 → 连接 → 上传 5 张图 → 验证 PC 端入库
- 覆盖 4 种 token 状态：成功 / 失效 / 重置 / 错

## 九、CI/CD

### 9.1 iOS

```yaml
name: iOS Build
on: [push, pull_request]
jobs:
  build:
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v4
      - name: Build
        run: xcodebuild -workspace ios/StartTooler.xcworkspace -scheme StartTooler -configuration Debug
      - name: Test
        run: xcodebuild test -workspace ios/StartTooler.xcworkspace -scheme StartTooler
```

### 9.2 Android

```yaml
name: Android Build
on: [push, pull_request]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-java@v4
        with: { java-version: 17 }
      - name: Build
        run: cd android && ./gradlew assembleDebug
      - name: Test
        run: cd android && ./gradlew test
```

## 十、依赖

### 10.1 iOS

- Apple 原生 API（SwiftUI / URLSession / Network / Keychain）
- 0 个第三方依赖（MVP）

### 10.2 Android

- `androidx.security:security-crypto:1.1.0-alpha06`
- `com.squareup.okhttp3:okhttp:4.12.0`
- `org.jetbrains.kotlinx:kotlinx-serialization-json:1.6.0`
- `androidx.compose:compose-bom:2024.02.00`
- `androidx.lifecycle:lifecycle-viewmodel-compose:2.7.0`
- 0 个额外（其余 androidx.compose BOM）

## 十一、版本号

| 维度 | 编号 |
|---|---|
| App 端 | semver（1.0.0） |
| 协议 | PC 端 `version` 字段 |
| 兼容 | App 端识别 `version` 不符 → 提示 |

App 端 1.0.0 对应 PC 端 v0.12。

## 十二、风险

| 风险 | 缓解 |
|---|---|
| iOS 16+ 限制了 NWConnection 的局域网权限 | 加 Info.plist NSLocalNetworkUsageDescription |
| Android 13+ POST_NOTIFICATIONS 权限 | 强提示 |
| OkHttp 阻塞主线程 | 强 Dispatchers.IO |
| 500MB 上传内存压力 | 流式 multipart |
| Swift 6 strict concurrency | 暂不启用 |
| 跨设备 App 端同步状态 | 不做（一机一 App） |

## 十三、里程碑

| 里程碑 | 交付 |
|---|---|
| M1 仓库 + CI | repo setup + workflows |
| M2 iOS MVP | iOS 单端：发现 / 连接 / 上传 / 持久化 |
| M3 Android MVP | Android 同步 |
| M4 端到端测试 | 真机 + PC 端 |
| M5 公测 | TestFlight + Play Console |
| M6 正式发布 | App Store + Google Play |

详见 [decisions/0001-app-repo-location.md §关键日期](../decisions/0001-app-repo-location.md)。
