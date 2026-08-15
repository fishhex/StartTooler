# D04 — 移动端 LAN 同步（原生 App）

> 关联文档：[D02 — 跨设备云端同步](02-cross-device-sync.md)（互补链路）、`doc/0.10/07-upload-server-lan.md`（本需求的协议载体）
> 关联代码：`Services/UploadServerService.cs`（v0.10 已实现，本次仅扩展）

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.1（需求稿）** |
| 目标用户 | 多设备的星助用户，尤其是户外拍摄后需要即时把手机照片传到 PC 入库 |
| 文档状态 | **需求 — 待评审** |
| 关联模块 | UploadServer (v0.10)、ProjectConfig、MediaRepository |
| 新增端 | iOS App（Swift）、Android App（Kotlin）— 独立仓库（位置待定） |

---

## 1. 需求总览

### 1.1 背景

| 现状 | 痛点 |
|---|---|
| v0.10 已有 `UploadServerService`：PC 端起 HTTP 服务，移动端浏览器扫码上传 | 移动端只能用浏览器，体验差：不能批量从相册选、不能保留历史、没有专属 UI |
| v0.12 D02 跨设备同步：依赖 OSS 跨广域网 | 拍摄现场没广域网（地下室、野外），OSS 用不了 |
| PC 端 Gallery 展示需要 `media.db` 索引 | 即使手动复制文件到 PC 目录，Gallery 也看不到（未入索引） |

核心矛盾：

```
拍摄现场想要：
  手机拍完 → 直接推到 PC → 立刻在 PC Gallery 看到

现在的阻碍：
  - 浏览器上传只能单次，体验差
  - 没有"实时入索引"流程
  - 跨广域网 OSS 在户外用不上
```

### 1.2 核心价值

| 现在 | 将来 |
|---|---|
| 户外拍完回来还要用数据线 / AirDrop 传文件到 PC | 手机原生 App 一键推送，自带 UDP 自动发现 |
| 传到 PC 还要手动触发扫描入库 | 上传完成后自动写入 `media_files`，Gallery 立刻可见 |
| 多设备协同：PC + Web 浏览器 | PC + iOS App + Android App 三端协同 |

### 1.3 一句话概括

**新增 iOS / Android 原生 App，复用 v0.10 的 `UploadServerService`，通过 UDP 广播发现局域网 PC，现场拍摄的本地文件可一键推送到 PC 并自动入索引。**

---

## 2. 用户场景

### 场景一：深空拍摄现场

> 用户在郊外拍摄，手机拍了 20 张 RAW。用户想立刻看到照片在 PC 库里：
>
> 1. 手机打开「星助 App」 → 自动扫描到 PC（"Hex-MacBook"）
> 2. 输入 6 位 token（PC 端显示在 UploadServer 面板）
> 3. App 进入主页：当前项目 = `deepsky-2025`
> 4. 点 [选择照片] → 选 20 张 RAW
> 5. 点 [上传] → 进度条 + 成功提示
> 6. PC 端 Gallery 看到 20 张新文件（已写入 `media_files`）

### 场景二：家里 PC + 平板 App

> 用户家里有 PC（有星助），现在想用 iPad 推文件：
>
> 1. iPad 与 PC 同 WiFi
> 2. iPad 打开 App → 自动扫描到 PC
> 3. 选项目 `moon-2025`（不是当前项目）→切换
> 4. 选 5 张截图 → 上传
> 5. PC 端 `moon-2025` 目录里看到新文件

### 场景三：多 PC 场景

> 用户家里有 2 台 PC 同时开着星助（Mac + Windows）：
>
> 1. App 启动扫描 → 列出 2 台设备
> 2. 用户选 "Hex-MacBook"
> 3. 持久化：以后默认连 Mac，除非用户手动切
> 4. 切换 PC：App 设置页"切换设备" → 重新扫描

---

## 3. 功能需求

### 3.1 协议契约（PC-as-server）

#### 3.1.1 UDP 广播发现（端口 9876）

PC 端 `UploadServerService` 启动后，每 2 秒向 `255.255.255.255:9876` 广播 JSON：

```json
{
  "service": "starttooler",
  "version": "0.12",
  "name": "Hex-MacBook",        // PC 机器名（Environment.MachineName）
  "port": 8765,                  // HTTP 上传服务端口
  "token": "123456",             // 6 位数字 token
  "current_project": "deepsky-2025"  // 当前激活项目（ProjectConfig.CurrentDirectory 对应 ProjectName，没有则空）
}
```

App 端持续监听 5 秒（或前台常驻），收到后列表展示。

**字段约定**：
- `service` 必须为 `"starttooler"`（App 端过滤其他局域网噪声）
- `version` 至少 `0.12`
- `current_project` 为空时，App 端显示"PC 当前未打开任何项目"

#### 3.1.2 HTTP API（端口 8765）

| 方法 | 路径 | 鉴权 | 说明 |
|---|---|---|---|
| GET | `/api/v1/health` | 否 | 健康检查 |
| GET | `/api/v1/projects` | 是 | 列出 `RecentDirectories` + 当前项目名 |
| POST | `/api/v1/projects/{name}/upload` | 是 | 上传文件到指定项目 |
| GET | `/upload` | 否 | H5 上传页（v0.10 已存在） |
| POST | `/upload` | 否 | H5 上传（v0.10 已存在） |

**Token 鉴权**：所有 `是` 端点必须带 `?token=XXXXXX` query 参数（或 `X-Token` header）。

**错误码**：

| 状态码 | 含义 |
|---|---|
| 200 | 成功 |
| 401 | token 错误或缺失 |
| 404 | 项目不存在 |
| 413 | 单文件超过大小限制（默认 500MB） |
| 500 | 服务端异常 |

### 3.2 项目列表（`RecentDirectories` 视角）

**语义**：D04 中的"项目"指 PC 端 `ProjectConfig.RecentDirectories` 列表中的一项，每项对应一个本地目录。

**HTTP 响应**：

```json
GET /api/v1/projects?token=XXXXXX
{
  "items": [
    {
      "name": "deepsky-2025",            // RecentDirectories 索引 / or 目录 basename
      "path": "/Users/hex/Astro/deepsky", // 绝对路径
      "project_name": "deepsky-2025",     // ProjectConfig.ProjectName（可空）
      "file_count": 234,                  // media_files WHERE project_path = path
      "size_mb": 1234,                    // 累计文件大小
      "is_current": true                  // path == ProjectConfig.CurrentDirectory
    }
  ]
}
```

**项目不存在处理**：
- `RecentDirectories` 里的目录可能已被用户删除 → PC 端先 `Directory.Exists` 过滤，不返回无效项
- 没有任何项目 → 返回 `{ "items": [] }`，App 端提示"PC 上还未添加任何项目"

### 3.3 Token 鉴权

| 项 | 说明 |
|---|---|
| 长度 | 6 位数字（000000–999999） |
| 生成时机 | `UploadServerService.StartAsync` 时随机生成 |
| 持久化 | 不持久化（每次启动重生成） |
| 手动重生 | UploadServerViewModel 提供"重置 token"按钮（立即生效，新广播包带新 token） |
| 鉴权失败 | 返回 401，App 端清理持久化 token，引导重新输入 |

**Token 显示位置**：沿用 v0.10 的 UploadServerViewModel 面板，已包含 QR 码生成。扩展 QR 码内容：当前 `http://<lan-ip>:<port>/upload?t=<token>`。

### 3.4 上传流程（POST /api/v1/projects/{name}/upload）

#### 3.4.1 请求

```
POST /api/v1/projects/deepsky-2025/upload?token=123456
Content-Type: multipart/form-data; boundary=XXX

--XXX
Content-Disposition: form-data; name="file"; filename="DSC001.jpg"
Content-Type: image/jpeg

<binary>
--XXX
Content-Disposition: form-data; name="file"; filename="DSC002.jpg"
Content-Type: image/jpeg

<binary>
--XXX--
```

多文件并行请求（multipart 内多个 `file` 字段）。

#### 3.4.2 PC 端处理

复用 v0.10 `UploadServerService.ParseMultipartFilesByBytes` + 落盘逻辑：

1. 校验 token → 401 if 错
2. 校验 `{name}` 对应路径存在 → 404 if 不存在
3. 解析 multipart，得到 `List<ParsedFile>`
4. 循环处理每个文件：
   - 扩展名白名单过滤（沿用 v0.10）
   - 单文件大小限制（>500MB → 跳过，且累计到 failed）
   - 落盘到 `{project_path}/{yyyy-MM-dd}/{filename}`（重名追加 `_1` 等）
   - 累计成功数
5. **新增**：落盘完成后，触发该目录的增量扫描（写入 `media_files`）
   - 复用 `MediaRepository.ScanDirectoryAsync` 增量逻辑
   - **不阻塞上传响应**：用 `Task.Run` 异步触发扫描
   - 失败仅记录日志，不影响上传成功状态
6. 返回：

```json
{
  "success": true,
  "count": 20,
  "files": [
    { "name": "DSC001.jpg", "path": "/Users/hex/Astro/deepsky/2026-08-15/DSC001.jpg" },
    ...
  ],
  "failed": []
}
```

#### 3.4.3 索引更新策略

**选定方案 A**：上传完成后，**异步触发该目录的增量扫描**。

理由：
- 复用 v0.10 + 现有 `MediaRepository.ScanDirectoryAsync` 增量逻辑（已有 hash 检测）
- 不引入新代码路径
- 上传响应不会被扫描阻塞（扫描可能耗时）
- 失败仅记日志，Gallery 侧靠 MediaFileRepository 的刷新触发同步

### 3.5 App 端交互

#### 3.5.1 视图结构

```
┌────────────────────────────────────────────────┐
│ App 视图                                       │
│                                                  │
│  1. 启动页（SplashPage）                          │
│     └─ 短暂显示 logo → 跳转连接页               │
│                                                  │
│  2. 连接页（ConnectPage）★ 首次启动 / 重连       │
│     ├─ UDP 扫描中...                             │
│     ├─ 设备列表（按信号强度排序）                  │
│     │   ○ Hex-MacBook (192.168.1.10) 3 个项目   │
│     │   ○ Hex-Win11  (192.168.1.20) 1 个项目    │
│     └─ 点选 → 进入 Token 输入页                  │
│                                                  │
│  3. Token 输入页（TokenPage）                     │
│     ├─ 6 位数字输入框                             │
│     ├─ 验证 → 成功 → 进入主页                    │
│     └─ 失败 → 提示"token 错误，请重试"           │
│                                                  │
│  4. 主页（HomePage）                              │
│     ├─ 顶部状态栏                                  │
│     │   PC: Hex-MacBook                          │
│     │   当前项目: deepsky-2025 [切换项目]        │
│     ├─ [选择照片] 按钮 → 系统照片选择器           │
│     ├─ 已选文件列表                                │
│     └─ [上传] 按钮 → 进度条 + 成功提示           │
│                                                  │
│  5. 历史页（HistoryPage）可选                     │
│     └─ 上传历史记录（本地持久化）                  │
│                                                  │
│  6. 设置页（SettingsPage）                        │
│     ├─ 切换设备（重新扫描）                        │
│     ├─ 清除本地缓存                                │
│     └─ 关于                                       │
└────────────────────────────────────────────────┘
```

#### 3.5.2 关键交互

**首次启动（无持久化）**：
1. 进连接页 → UDP 扫描 → 选设备 → 进 Token 输入 → 验证 → 主页
2. 持久化：`<pc_ip>:<port>:<token>` → 下次自动

**后续启动（有持久化）**：
1. 启动页 → 主页
2. 后台线程：UDP 扫描 → 如果持久化的设备还在 → 不动；不在 → 标记，最后提醒用户
3. 主页加载：用持久化 token 调 `/api/v1/health?token=XXX` 验证
   - 成功 → 拉项目列表，正常显示
   - 失败 → 跳回连接页

**切换项目**：
1. 主页 [切换项目] → 弹窗，从 `/api/v1/projects` 列表里选
2. 默认勾选 `is_current=true` 的项目
3. 选完 → 主页刷新顶部显示

**上传**：
1. [选择照片] → 系统选择器（iOS `PHPickerViewController` / Android `ACTION_PICK` 或 MediaStore）
2. 多选 → 已选列表
3. [上传] → 调 `POST /api/v1/projects/{name}/upload`
4. 进度：使用 `URLSession.upload(for:fromFile:)` / `OkHttp` 的 progress
5. 成功 → Toast "已上传 20 个文件" + 列表清空
6. 失败 → 列表保留，允许重试

#### 3.5.3 持久化

| 平台 | 存储 |
|---|---|
| iOS | `UserDefaults` 或 Keychain（推荐 Keychain，token 是凭证） |
| Android | `EncryptedSharedPreferences` 或 DataStore |

存：
- `pc_ip`
- `pc_port`
- `pc_token`
- `last_current_project_name`

---

## 4. PC 端扩展（在 UploadServerService 上新增端点）

### 4.1 新增 HTTP 端点（伪代码）

```csharp
// UploadServerService.cs 扩展

private async Task HandleRequestAsync(HttpListenerContext ctx) {
    var req = ctx.Request;
    var resp = ctx.Response;

    // 路径分发
    var path = req.Url?.AbsolutePath ?? "";
    var method = req.HttpMethod;

    // 白名单：/upload, /api/v1/health
    if (path == "/api/v1/health" && method == "GET") {
        await HandleHealthAsync(resp);
        return;
    }

    // 受保护：需要 token
    if (!ValidateToken(req)) {
        await WriteJsonAsync(resp, 401, new { error = "invalid token" });
        return;
    }

    if (path == "/api/v1/projects" && method == "GET") {
        await HandleListProjectsAsync(resp);
        return;
    }

    // /api/v1/projects/{name}/upload
    if (path.StartsWith("/api/v1/projects/") && path.EndsWith("/upload") && method == "POST") {
        var name = ExtractProjectName(path);
        await HandleUploadToProjectAsync(name, req, resp);
        return;
    }

    // 现有 /upload 逻辑（H5 上传页）不动
    if (path == "/upload") { await HandleUploadPageAsync(req, resp); return; }
    if (path == "/upload" && method == "POST") { await HandleUploadPostAsync(req, resp); return; }

    // 兜底
    await WriteJsonAsync(resp, 404, new { error = "not found" });
}
```

### 4.2 新增 Token 管理

```csharp
public class UploadServerService {
    private string _currentToken = GenerateToken();  // 6 位数字

    public string CurrentToken => _currentToken;

    public void RegenerateToken() {
        _currentToken = GenerateToken();
        // 触发新的 UDP 广播（下一拍就用新 token）
    }

    private static string GenerateToken() {
        return Random.Shared.Next(0, 1000000).ToString("D6");
    }

    private bool ValidateToken(HttpListenerRequest req) {
        // 优先 query 参数 ?token=
        var token = req.QueryString["token"];
        // 备选 header X-Token
        if (string.IsNullOrEmpty(token)) token = req.Headers["X-Token"];
        return !string.IsNullOrEmpty(token) && token == _currentToken;
    }
}
```

### 4.3 新增 UDP 广播（端口 9876）

```csharp
public class UploadServerService {
    private UdpClient? _udpBroadcast;
    private CancellationTokenSource? _udpCts;

    private async Task StartUdpBroadcastAsync(CancellationToken ct) {
        _udpBroadcast = new UdpClient();
        _udpBroadcast.EnableBroadcast = true;
        _udpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try {
            var endpoint = new IPEndPoint(IPAddress.Broadcast, 9876);
            while (!_udpCts.IsCancellationRequested) {
                var currentProjectName = GetCurrentProjectName();  // 读 ProjectConfig
                var payload = new {
                    service = "starttooler",
                    version = "0.12",
                    name = Environment.MachineName,
                    port = _port,
                    token = _currentToken,
                    current_project = currentProjectName ?? "",
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                await _udpBroadcast.SendAsync(bytes, endpoint);
                await Task.Delay(2000, _udpCts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) {
            Debug.WriteLine($"[UDP] Broadcast error: {ex.Message}");
        }
    }

    // 在 StartAsync 内启动；StopAsync 时关闭
}
```

### 4.4 UploadServerViewModel 改动

```csharp
public partial class UploadServerViewModel : ObservableObject {
    [ObservableProperty] private string currentToken = "";  // 启动后填充

    [RelayCommand]
    private void RegenerateToken() {
        _server.RegenerateToken();
        CurrentToken = _server.CurrentToken;
        StatusMessage = $"Token 已重置：{CurrentToken}";
    }
}
```

XAML：UploadServerView 面板增加 Token 显示 + 重置按钮。

### 4.5 落盘后异步扫描

```csharp
private async Task HandleUploadToProjectAsync(string projectName, HttpListenerRequest req, HttpListenerResponse resp) {
    // 1. 校验 projectName 对应路径
    var projectPath = ResolveProjectPath(projectName);
    if (projectPath == null) {
        await WriteJsonAsync(resp, 404, new { error = "project not found" });
        return;
    }

    // 2. 解析 multipart（复用）
    var files = await ParseMultipartFilesByBytesAsync(...);

    // 3. 落盘（复用，受 projectPath 控制）
    var saved = await SaveFilesAsync(projectPath, files);

    // 4. ★ 异步触发扫描（不阻塞响应）
    _ = Task.Run(async () => {
        try {
            await _mediaRepo.ScanDirectoryAsync(projectPath, CancellationToken.None);
        } catch (Exception ex) {
            Debug.WriteLine($"[UploadServer] Scan after upload failed: {ex.Message}");
        }
    });

    // 5. 返回
    await WriteJsonAsync(resp, 200, new { success = true, count = saved.Count, files = saved });
}
```

`ScanDirectoryAsync` 已是现成接口，需要确认传入的 `project_path` 恰好是 `ProjectConfig.CurrentDirectory` 或 `RecentDirectories` 里的某项。

---

## 5. iOS App 端需求

### 5.1 技术栈

| 项 | 选型 |
|---|---|
| 语言 | Swift 5.9+ |
| UI 框架 | SwiftUI（iOS 16+） |
| 网络 | `URLSession`（HTTP + Upload progress） |
| UDP | `Network.framework` (`NWConnection` UDP 组播) |
| 持久化 | Keychain（token）+ `UserDefaults`（其他配置） |
| 项目结构 | Swift Package + Xcode project |

### 5.2 关键技术点

- **UDP 监听**：`NWListener` 用 `.udp` 协议绑定 `:9876`，前台持续监听
- **照片选择**：`PHPickerViewController`（无需相册权限，只返回选中的）
- **多文件上传**：`URLSession.upload(for:fromFile:)` 不支持 multipart，需手写 body 或用 `MultipartFormData` 库
- **后台约束**：iOS 后台无法持续 UDP 监听；App 进入后台后停止广播接收，回到前台重新启动扫描

### 5.3 系统集成

- **Info.plist**：
  - `NSLocalNetworkUsageDescription`（局域网发现权限）
  - `NSBonjourServices`（如使用 Bonjour）
  - `NSPhotoLibraryUsageDescription`（如果需要全相册权限——PHPicker 不需要）
- **Network.framework**：iOS 14+ 起本地网络权限弹窗（首次扫描时触发）

---

## 6. Android App 端需求

### 6.1 技术栈

| 项 | 选型 |
|---|---|
| 语言 | Kotlin 1.9+ |
| UI 框架 | Jetpack Compose（Material 3） |
| 网络 | OkHttp 4 + Retrofit |
| UDP | `DatagramSocket`（前台 Service 或直接 Activity 内） |
| 持久化 | DataStore + EncryptedSharedPreferences |
| 项目结构 | Gradle module |

### 6.2 关键技术点

- **UDP 监听**：`DatagramSocket(9876)` 监听广播包
- **多文件上传**：OkHttp `MultipartBody`，配合 `RequestBody` 包装 File
- **照片选择**：`ActivityResultContracts.PickMultipleVisualMedia`（Photo Picker，API 33+）
- **后台约束**：Android 8+ 后台启动 Service 收广播受限；App 进入后台后停止 UDP，回到前台重新启动

### 6.3 系统集成

- **AndroidManifest.xml**：
  - `INTERNET` 权限
  - `ACCESS_NETWORK_STATE`
  - `READ_MEDIA_IMAGES` / `READ_MEDIA_VIDEO`（API 33+）
  - `usesCleartextTraffic="true"`（HTTP 非 HTTPS 必需，POC 阶段）
- **网络安全配置**：`network_security_config.xml` 允许 `192.168.x.x` 等私有 IP 段 cleartext

---

## 7. 边界情况

| 场景 | 处理 |
|---|---|
| PC 端 UploadServer 未启动 | App 扫描找不到 → 提示"未发现 PC，请确认 PC 端星助已运行" |
| Token 错误 | 401 → App 清理持久化 token，跳转 Token 输入页 |
| 项目路径被用户删除 | PC 端 `/api/v1/projects` 过滤无效目录；App 端拿到列表后空数组提示 |
| 当前项目为空 | App 主页显示"PC 当前未打开任何项目，请先在 PC 端打开项目" |
| 上传中途网络断开 | OkHttp call 超时 → 提示"上传失败"，已上传文件已落盘 |
| 上传中途 PC 端关闭 | App 端断网错误 → 提示"PC 已离线"，跳转扫描页 |
| 同时多台 PC 广播 | App 列表展示，用户点选 |
| App 后台 → 前台 | 重新启动 UDP 扫描，提示"已重新连接" |
| 单文件 > 500MB | PC 端 413 响应，App 提示"文件过大" |
| 文件扩展名不在白名单 | PC 端跳过，记录到 `failed[]`，App 端弹窗展示失败列表 |
| 端口冲突 | PC 端 UploadServer 启动失败时已有提示，App 端扫描不到 |
| 不同子网（如访客 WiFi） | UDP 广播无法跨网段 → App 端兜底"手动输入 IP"按钮 |
| 路由器 AP 隔离 | 同 WiFi 不可达 → 同上 |

---

## 8. 不做清单

| 内容 | 理由 |
|---|---|
| HTTPS / 自签证书 | 移动端信任自签证书体验差，局域网风险可控（仅家庭 WiFi） |
| 用户名 / 密码鉴权 | 6 位 token 满足家庭场景，复杂度高 |
| 双向同步（PC 端推 → App 端） | 范围超出本次需求 |
| AI 自动打标 | 需求明确"仅写入本地 + 调整索引"，不做 AI 触发 |
| 触发 OSS 上传 | 同上 |
| 断点续传 / 分片上传 | MVP 阶段单次上传够用 |
| App 端查看 PC 端 Gallery | 超出本次需求 |
| iOS / Android 仓库位置 | 暂不处理，独立仓库 vs submodule 后续决定 |
| 后台持续 UDP 监听 | iOS / Android 系统限制，待后续 PoC 验证 |
| `media.db` 同步 | 与 D02 互补：D02 负责 media.db 重建，D04 负责文件入库 |

---

## 9. 与现有系统的关系

### 9.1 与 v0.10 UploadServerService

- **复用**：HTTP listener、multipart 解析、文件落盘、QR 码生成
- **不破坏**：H5 上传页 `/upload` 仍可用
- **新增**：API 端点 `/api/v1/*`、UDP 广播、Token 机制、落盘后扫描

### 9.2 与 D02 跨设备云端同步

- **互补**：D02 走广域网 OSS，D04 走局域网
- **数据模型复用**：D04 上传时 PC 端写 `media_files.project_name`（D02 字段），后续 D02"从云端恢复"能识别
- **不冲突**：D02 不动 D04 的代码，D04 不动 D02 的代码

### 9.3 与 ProjectConfig

- `RecentDirectories`：HTTP `/api/v1/projects` 读取
- `CurrentDirectory`：HTTP 响应中 `is_current=true` 标记
- `ProjectName`：HTTP 响应中 `project_name` 字段（跨设备标识）

### 9.4 与 MediaRepository

- **复用**：`ScanDirectoryAsync` 在落盘后被调用
- **不破坏**：所有现有扫描流程不变

### 9.5 不引入新 NuGet 包

PC 端用 `System.Net.HttpListener`（已用）+ `System.Net.Sockets.UdpClient`（BCL 自带）+ `System.Text.Json`（已用）。

App 端 iOS/Android 是独立仓库，独立选型。

---

## 10. 实施步骤（建议）

| 步骤 | 内容 | 影响范围 |
|------|------|------|
| 1 | UploadServerService 新增 Token 管理 + UDP 广播 | `UploadServerService.cs` |
| 2 | UploadServerViewModel 显示 Token + 重置按钮 | `UploadServerViewModel.cs` + AXAML |
| 3 | UploadServerService 新增 HTTP API 端点 | `UploadServerService.cs` |
| 4 | 落盘后异步扫描集成 | `UploadServerService.cs` + `MediaRepository` |
| 5 | iOS App：连接 + Token 验证 + 项目列表 | iOS 仓库 |
| 6 | iOS App：照片选择 + 上传 | iOS 仓库 |
| 7 | Android App：同上 | Android 仓库 |
| 8 | 端到端测试：实机扫码 → 上传 → Gallery 可见 | — |

---

## 11. 待澄清 / 后续讨论

| # | 问题 | 备注 |
|---|------|------|
| 1 | 移动端 App 仓库位置（独立仓库 / submodule / monorepo） | 暂不处理 |
| 2 | iOS / Android 最低版本（iOS 16+ / Android 10+?） | 待定 |
| 3 | App 端 UI 设计稿 | 待设计 |
| 4 | "切换项目"是否允许 App 端新建项目 | 当前仅在 PC 端管理 |
| 5 | 单文件大小上限是否可配置 | 当前固定 500MB |
| 6 | UDP 广播抓不到时的"手动输入 IP"兜底 UI | 待定 |
| 7 | App 端是否要本地缓存项目列表（断网时显示） | 待定 |
| 8 | App 端是否要显示 PC 端文件总数 / 占用空间 | 当前协议已带 `file_count` / `size_mb` |
| 9 | 私有 IP 段的 NetworkSecurityConfig 白名单范围 | 192.168.x.x / 10.x.x.x / 172.16-31.x.x |
| 10 | App 端历史记录保留多久 | 默认 30 天 |
