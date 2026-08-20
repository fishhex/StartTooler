# v0.12 — 移动端 LAN 同步（PC 端实现 spec）

> 对应需求文档 `doc/0.12/demand/04-mobile-lan-sync.md`。
> 关联代码：`Services/UploadServerService.cs`（v0.10）、`ViewModels/UploadServerViewModel.cs`、`Services/UploadServerService.cs`（扩展）、`Data/IMediaRepository.cs`（复用 `ScanDirectoryAsync`）。
> 关联文档：`doc/0.10/07-upload-server-lan.md`（v0.10 现有协议）。
> 关联需求：`doc/0.12/demand/02-cross-device-sync.md`（D02，互补）。

---

## 1. 模块边界

```
┌─────────────────────────────────────────────────────────────┐
│                 UploadServerService（v0.10 + v0.12 扩展）    │
│                                                              │
│  路由表（HandleRequestAsync 顶部 switch）                       │
│  ├─ GET  /upload              H5 上传页（v0.10 保留）         │
│  ├─ POST /upload              H5 上传（v0.10 保留）           │
│  ├─ GET  /api/v1/health       健康检查                    │
│  ├─ GET  /api/v1/projects     项目列表（RecentDirectories）│
│  └─ POST /api/v1/projects/{name}/upload   指定项目上传           │
│                                                              │
│  Token 管理（新增）                                          │
│  ├─ CurrentToken / TryRegenerateToken                       │
│  └─ ValidateToken(req) → bool                              │
│                                                              │
│  UDP 广播（新增，独立协程）                                    │
│  └─ 每 2s 广播 JSON 到 255.255.255.255:9876                  │
│                                                              │
│  IConfigService 注入（新增）                                  │
│  └─ 每次请求动态读 ProjectConfig（不做构造时绑定）             │
└─────────────────────────────────────────────────────────────┘

依赖链：
  UploadServerService
    ├─ IMediaRepository（注入，落盘后异步触发 ScanDirectoryAsync）
    ├─ IConfigService（注入，读取 ProjectConfig）
    └─ HttpListener（已用）+ UdpClient（新增）

  UploadServerViewModel
    ├─ UploadServerService（订阅 CurrentToken 变化）
    ├─ GalleryViewModel（上传事件沿用）
    └─ PublicRelayViewModel（QR URL 切换逻辑保留）
```

> **App 端不在本 spec 范围**——iOS / Android 仓库位置未定，由独立 spec 描述。

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|---|---|---|
| `Services/UploadServerService.cs` | 新增路由分支 + Token + UDP + 注入 `IConfigService` / `IMediaRepository` | **重写** |
| `ViewModels/UploadServerViewModel.cs` | 新增 `CurrentToken` 字段 + `RegenerateTokenCommand` + 启动后绑定 | **修改** |
| `Views/UploadServerView.axaml` | 新增 Token 显示 + 重置按钮 | **修改** |
| `Services/UploadServerHelpers.cs`（可选） | 拆出 `OssObjectInfo` / `UploadFileItem` 等 JSON DTO 集合 | **新增**（可选） |

> **不引入新 NuGet 包**。UDP 用 BCL `System.Net.Sockets.UdpClient`；JSON 用 BCL `System.Text.Json`。

---

## 3. UploadServerService 重写设计

### 3.1 字段变更

```csharp
public class UploadServerService : IDisposable {
    // === v0.10 保留 ===
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private static readonly string[] AllowedExtensions = { /* 不变 */ };
    public int Port { get; private set; }
    public string UploadUrl => $"http://{GetLocalIp()}:{Port}/upload";
    public event Action<string>? OnUploadSuccess;
    public event Action<string>? OnUploadError;

    // === v0.12 新增（注入依赖） ===
    private readonly IConfigService _configService;
    private readonly IMediaRepository _mediaRepository;

    // === v0.12 新增（Token） ===
    private string _currentToken = GenerateToken();
    public string CurrentToken => _currentToken;
    public event Action<string>? OnTokenChanged;  // VM 订阅刷新显示

    // === v0.12 新增（UDP 广播） ===
    private UdpClient? _udpClient;
    private CancellationTokenSource? _udpCts;
    private Task? _udpTask;
    private const int UdpBroadcastPort = 9876;
    private const int UdpBroadcastIntervalMs = 2000;

    // === v0.12 调整：构造时不再绑定 _currentDirectory ===
    // 路径由每次请求从 ProjectConfig 解析
}
```

> 把 `_currentDirectory` 字段删掉，**构造时只接受依赖**。H5 上传（在`/upload` 路径）依然需要一个"默认目录"——单独提供 `LegacyDefaultDirectory` 参数保持向后兼容。

### 3.2 构造函数

```csharp
public UploadServerService(
    IConfigService configService,
    IMediaRepository mediaRepository,
    string legacyDefaultDirectory = "")  // H5 /upload 上传用（向后兼容）
{
    _configService = configService;
    _mediaRepository = mediaRepository;
    _legacyDefaultDirectory = legacyDefaultDirectory;
    _currentToken = GenerateToken();
}
```

### 3.3 启动 / 停止

```csharp
public async Task StartAsync(int port, CancellationToken ct = default) {
    if (_listener != null) throw new InvalidOperationException("Server is already running.");

    Port = port;
    _listener = new HttpListener();
    _listener.Prefixes.Add($"http://+:{port}/");
    _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

    try { _listener.Start(); }
    catch (HttpListenerException ex) when (ex.ErrorCode == 5) {
        throw new InvalidOperationException(
            "Permission denied. Run: sudo netsh http add urlacl url=http://+:" + port + "/ user=<username>");
    }

    Debug.WriteLine($"[UploadServer] Started on port {port}");
    _listenTask = ListenAsync(_cts.Token);

    // v0.12 新增：UDP 广播
    _udpTask = StartUdpBroadcastAsync(_cts.Token);

    OnTokenChanged?.Invoke(_currentToken);  // 通知 VM
    await Task.CompletedTask;
}

public void Stop() {
    // 1. 取消 HTTP listen
    // 2. 取消 UDP 广播
    // 3. 关闭 UdpClient
    // 4. 旧逻辑（Stop + Dispose + IsListening）保留
    // ... 沿用 v0.10，额外加：
    _udpCts?.Cancel();
    _udpClient?.Close();
    _udpClient = null;
}
```

### 3.4 路由分发（HandleRequestAsync 重写）

```csharp
private async Task HandleRequestAsync(HttpListenerContext context) {
    var request = context.Request;
    var response = context.Response;
    var path = request.Url?.AbsolutePath ?? "";
    var method = request.HttpMethod;

    try {
        // ===== 1. 无鉴权端点 =====
        if (method == "GET" && path == "/upload") {
            await ServeUploadPageAsync(response);
            return;
        }

        if (method == "GET" && path == "/api/v1/health") {
            await WriteJsonAsync(response, 200, new HealthResponse {
                Ok = true,
                Service = "starttooler",
                Version = "0.12",
                Name = Environment.MachineName,
                Port = Port,
                Token = _currentToken,         // App 端扫码时一并告知（不暴露给公网）
                CurrentProject = await GetCurrentProjectNameAsync(),
            });
            return;
        }

        // ===== 2. 受保护端点（要 token）=====
        if (!ValidateToken(request)) {
            await WriteJsonAsync(response, 401, new ErrorResponse { Error = "invalid token" });
            return;
        }

        if (method == "GET" && path == "/api/v1/projects") {
            await HandleListProjectsAsync(response);
            return;
        }

        if (method == "POST" && path.StartsWith("/api/v1/projects/") && path.EndsWith("/upload")) {
            var projectName = ExtractProjectName(path);  // 路径段解析
            await HandleUploadToProjectAsync(projectName, request, response);
            return;
        }

        // ===== 3. 兜底：H5 /upload POST（沿用 v0.10）=====
        if (method == "POST" && path == "/upload") {
            await HandleLegacyUploadAsync(request, response);
            return;
        }

        // ===== 4. 未匹配 =====
        await WriteJsonAsync(response, 404, new ErrorResponse { Error = "not found" });
    }
    catch (Exception ex) {
        Debug.WriteLine($"[UploadServer] Handle error: {ex}");
        try {
            await WriteJsonAsync(response, 500, new ErrorResponse { Error = ex.Message });
        }
        catch { /* response 可能已关闭 */ }
    }
}
```

### 3.5 Token 管理

```csharp
private static string GenerateToken() {
    return Random.Shared.Next(0, 1_000_000).ToString("D6");
}

public void RegenerateToken() {
    _currentToken = GenerateToken();
    OnTokenChanged?.Invoke(_currentToken);
    Debug.WriteLine($"[UploadServer] Token regenerated: {_currentToken}");
}

private bool ValidateToken(HttpListenerRequest request) {
    // 优先 query 参数 ?token=
    var token = request.QueryString["token"];
    // 备选 header X-Token（multipart body 里没法塞 query，方便调试）
    if (string.IsNullOrEmpty(token)) token = request.Headers["X-Token"];
    return !string.IsNullOrEmpty(token) && token == _currentToken;
}
```

### 3.6 项目列表（HandleListProjectsAsync）

```csharp
private async Task HandleListProjectsAsync(HttpListenerResponse response) {
    var projectCfg = await _configService.GetAsync<ProjectConfig>(ConfigKeys.Project);
    var items = new List<ProjectListItem>();

    if (projectCfg != null) {
        var currentDir = projectCfg.CurrentDirectory ?? "";

        foreach (var path in projectCfg.RecentDirectories) {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!Directory.Exists(path)) continue;  // 失效目录过滤

            // 统计 media_files 文件数与占用（轻量查询）
            long fileCount = 0;
            long sizeBytes = 0;
            try {
                fileCount = await _mediaRepository.CountByProjectAsync(path);
                sizeBytes = await _mediaRepository.GetLocalSizeAsync(path, null);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[UploadServer] stats fail for {path}: {ex.Message}");
            }

            items.Add(new ProjectListItem {
                Name = Path.GetFileName(path.TrimEnd('/', '\\')),
                Path = path,
                ProjectName = projectCfg.ProjectName,
                FileCount = fileCount,
                SizeMb = sizeBytes / 1024 / 1024,
                IsCurrent = string.Equals(path, currentDir, StringComparison.Ordinal),
            });
        }
    }

    await WriteJsonAsync(response, 200, new ProjectListResponse { Items = items });
}
```

> `IMediaRepository` 已有 `GetLocalSizeAsync`（v0.12 spec）；`CountByProjectAsync` 需要新增（见 §7）。

### 3.7 上传到指定项目（HandleUploadToProjectAsync）

```csharp
private async Task HandleUploadToProjectAsync(
    string projectName, HttpListenerRequest request, HttpListenerResponse response) {

    var projectPath = await ResolveProjectPathAsync(projectName);
    if (projectPath == null) {
        await WriteJsonAsync(response, 404, new ErrorResponse { Error = $"project '{projectName}' not found" });
        return;
    }

    if (!request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true) {
        await WriteJsonAsync(response, 400, new ErrorResponse { Error = "Invalid content type. Use multipart/form-data." });
        return;
    }

    List<ParsedFile> files;
    try {
        files = await Task.Run(() => ParseMultipartFiles(request));
    }
    catch (Exception ex) {
        await WriteJsonAsync(response, 400, new ErrorResponse { Error = $"multipart parse failed: {ex.Message}" });
        return;
    }

    if (files.Count == 0) {
        await WriteJsonAsync(response, 400, new ErrorResponse { Error = "No files uploaded." });
        return;
    }

    var saved = new List<UploadFileItem>();
    var failed = new List<UploadFileItem>();

    foreach (var file in files) {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (Array.IndexOf(AllowedExtensions, ext) < 0) {
            OnUploadError?.Invoke($"Unsupported file type: {ext}");
            failed.Add(new UploadFileItem { Name = file.FileName, Reason = $"unsupported extension {ext}" });
            continue;
        }

        // 单文件大小限制（500MB）
        long sizeHint = 0;
        try { sizeHint = file.Data.Length; } catch { /* stream 不一定支持 Length */ }
        if (sizeHint > 500L * 1024 * 1024) {
            failed.Add(new UploadFileItem { Name = file.FileName, Reason = "exceeds 500MB limit" });
            continue;
        }

        try {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var dateDir = Path.Combine(projectPath, today);
            Directory.CreateDirectory(dateDir);
            var destPath = GetUniqueFileName(Path.Combine(dateDir, file.FileName));

            await using (var output = File.Create(destPath)) {
                await file.Data.CopyToAsync(output);
            }

            saved.Add(new UploadFileItem { Name = Path.GetFileName(destPath), Path = destPath });
            OnUploadSuccess?.Invoke(destPath);
            Debug.WriteLine($"[UploadServer] Uploaded: {destPath} ({new FileInfo(destPath).Length} bytes)");
        }
        catch (Exception ex) {
            Debug.WriteLine($"[UploadServer] save failed for {file.FileName}: {ex.Message}");
            failed.Add(new UploadFileItem { Name = file.FileName, Reason = ex.Message });
        }
    }

    // ★ 异步触发扫描（不阻塞响应）
    if (saved.Count > 0) {
        _ = Task.Run(async () => {
            try {
                await _mediaRepository.ScanDirectoryAsync(projectPath, progress: null, CancellationToken.None);
                Debug.WriteLine($"[UploadServer] Scan after upload done: {projectPath}");
            }
            catch (Exception ex) {
                Debug.WriteLine($"[UploadServer] Scan after upload failed: {ex.Message}");
            }
        });
    }

    await WriteJsonAsync(response, 200, new UploadResponse {
        Success = true,
        Count = saved.Count,
        Files = saved,
        Failed = failed,
    });
}
```

### 3.8 路径解析（ResolveProjectPathAsync）

```csharp
private async Task<(string? path, string projectName)> ResolveProjectPathAsync(string name) {
    var projectCfg = await _configService.GetAsync<ProjectConfig>(ConfigKeys.Project);
    if (projectCfg == null) return (null, "");

    // name 可能是 RecentDirectories 路径的 basename 或完整路径
    foreach (var path in projectCfg.RecentDirectories) {
        if (string.IsNullOrWhiteSpace(path)) continue;
        var basename = Path.GetFileName(path.TrimEnd('/', '\\'));
        if (string.Equals(name, basename, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, path, StringComparison.OrdinalIgnoreCase)) {
            return (Directory.Exists(path) ? path : null, basename);
        }
    }
    return (null, "");
}
```

### 3.9 UDP 广播（StartUdpBroadcastAsync）

```csharp
private async Task StartUdpBroadcastAsync(CancellationToken ct) {
    _udpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var localCt = _udpCts.Token;

    try {
        _udpClient = new UdpClient();
        _udpClient.EnableBroadcast = true;

        var endpoint = new IPEndPoint(IPAddress.Broadcast, UdpBroadcastPort);

        while (!localCt.IsCancellationRequested) {
            var currentProject = await GetCurrentProjectNameAsync();
            var payload = new UdpBroadcastPayload {
                Service = "starttooler",
                Version = "0.12",
                Name = Environment.MachineName,
                Port = Port,
                Token = _currentToken,
                CurrentProject = currentProject ?? "",
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

            try {
                await _udpClient.SendAsync(bytes, endpoint);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[UploadServer] UDP send failed: {ex.Message}");
            }

            try { await Task.Delay(UdpBroadcastIntervalMs, localCt); }
            catch (OperationCanceledException) { break; }
        }
    }
    catch (OperationCanceledException) { /* 正常 */ }
    catch (Exception ex) {
        Debug.WriteLine($"[UploadServer] UDP broadcast error: {ex.Message}");
    }
    finally {
        try { _udpClient?.Close(); } catch { /* ignore */ }
        _udpClient = null;
    }
}

private async Task<string?> GetCurrentProjectNameAsync() {
    try {
        var cfg = await _configService.GetAsync<ProjectConfig>(ConfigKeys.Project);
        if (cfg == null || string.IsNullOrEmpty(cfg.CurrentDirectory)) return null;
        return Path.GetFileName(cfg.CurrentDirectory.TrimEnd('/', '\\'));
    }
    catch { return null; }
}

private static readonly JsonSerializerOptions JsonOpts = new() {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
};
```

### 3.10 JSON 响应统一化

把现有的 `WriteResponseAsync` 替换成接受 `object` 的版本：

```csharp
private static async Task WriteJsonAsync<T>(HttpListenerResponse response, int status, T payload) {
    response.StatusCode = status;
    response.ContentType = "application/json; charset=utf-8";
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
    response.ContentLength64 = bytes.Length;
    await response.OutputStream.WriteAsync(bytes);
    response.Close();
}
```

**移除** 旧的 `WriteResponseAsync` + `EscapeJson`（删掉 `EscapeJson` 方法）。

### 3.11 DTO 定义

```csharp
// 可放在 UploadServerService.cs 末尾，或独立 Services/UploadServerHelpers.cs

public sealed class HealthResponse {
    public bool Ok { get; init; }
    public string Service { get; init; } = "starttooler";
    public string Version { get; init; } = "0.12";
    public string Name { get; init; } = "";
    public int Port { get; init; }
    public string Token { get; init; } = "";
    public string CurrentProject { get; init; } = "";
}

public sealed class ErrorResponse {
    public string Error { get; init; } = "";
}

public sealed class ProjectListResponse {
    public List<ProjectListItem> Items { get; init; } = new();
}

public sealed class ProjectListItem {
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string? ProjectName { get; init; }
    public long FileCount { get; init; }
    public long SizeMb { get; init; }
    public bool IsCurrent { get; init; }
}

public sealed class UploadResponse {
    public bool Success { get; init; }
    public int Count { get; init; }
    public List<UploadFileItem> Files { get; init; } = new();
    public List<UploadFileItem> Failed { get; init; } = new();
}

public sealed class UploadFileItem {
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class UdpBroadcastPayload {
    public string Service { get; init; } = "starttooler";
    public string Version { get; init; } = "0.12";
    public string Name { get; init; } = "";
    public int Port { get; init; }
    public string Token { get; init; } = "";
    public string CurrentProject { get; init; } = "";
}
```

```csharp
private static int ExtractProjectName(HttpListenerRequest request) {
    // placeholder；实际实现是 path 解析，签名以匹配 §3.4 处调用
}

// 正确写法（替换上面）：
private static string? ExtractProjectName(string path) {
    // /api/v1/projects/{name}/upload → {name}
    var prefix = "/api/v1/projects/";
    var suffix = "/upload";
    if (!path.StartsWith(prefix) || !path.EndsWith(suffix)) return null;
    var name = path.Substring(prefix.Length, path.Length - prefix.Length - suffix.Length);
    return string.IsNullOrEmpty(name) ? null : name;
}
```

---

## 4. UploadServerViewModel 改动

### 4.1 新增字段

```csharp
public partial class UploadServerViewModel : ObservableObject, IDisposable {
    // ... v0.10 字段保留 ...

    [ObservableProperty] private string _currentToken = "";
    // 任一变化都触发 QR 码刷新（QR 码内容变为 http://.../upload?t=token）
    partial void OnCurrentTokenChanged(string value) => RefreshQrForCurrentAddress();
}
```

### 4.2 注入点

构造函数不变（`GalleryViewModel gallery, PublicRelayViewModel publicRelayViewModel`）。

`_server` 创建时改为注入依赖：

```csharp
// StartServer 内
_server = new UploadServerService(
    configService: _gallery.ConfigService,    // ← 需要 GalleryViewModel 暴露
    mediaRepository: _gallery.MediaRepository, // ← 需要 GalleryViewModel 暴露
    legacyDefaultDirectory: _gallery.ProjectPath ?? "");
```

> `GalleryViewModel` 当前 `_configService` 和 `_mediaRepository` 是 `private` 字段。要么改 `internal` 暴露，要么在 `UploadServerViewModel` 构造时传入。
>
> **推荐**：在 `UploadServerViewModel` 构造时多传这两个依赖。改 `MainWindowViewModel` 的 wiring。

### 4.3 订阅 Token 变化

```csharp
_server.OnTokenChanged += token => Dispatcher.UIThread.Post(() => {
    CurrentToken = token;
    StatusMessage = $"Token 已刷新：{token}";
});

// 在 StopServer 取消订阅
// 在 Dispose 取消订阅
```

### 4.4 新增 Command：RegenerateToken

```csharp
[RelayCommand]
private void RegenerateToken() {
    _server?.RegenerateToken();
    // OnTokenChanged 回调里会更新 CurrentToken
}
```

### 4.5 QR 码内容扩展

`BuildDisplayUrl` / `GenerateQrCode` 的 URL 加 `?t={token}`：

```csharp
private string BuildDisplayUrl() {
    if (!IsRunning || string.IsNullOrEmpty(UploadUrl)) return string.Empty;
    if (IsPublicMode || LocalAddresses.Count == 0) return UploadUrl;
    var idx = AddressIndex;
    if (idx < 0 || idx >= LocalAddresses.Count) idx = 0;
    try {
        var baseUri = new Uri(UploadUrl);
        var builder = new UriBuilder(baseUri) { Host = LocalAddresses[idx] };
        var url = builder.Uri.ToString().TrimEnd('/');
        // v0.12: 拼 token（扫码后 H5 页 JS 解析后保存，后续上传带上）
        if (!string.IsNullOrEmpty(CurrentToken)) {
            url += $"?t={Uri.EscapeDataString(CurrentToken)}";
        }
        return url;
    }
    catch { return UploadUrl; }
}
```

> H5 上传页 JS 看不到 `?t=`（GET /upload 只返回 HTML），但 App 端 UDP 拿到 token 后用 GET /api/v1/health 替换。

### 4.5.1 修复 `UpdateQrForMode` 不走 `BuildDisplayUrl` 的不一致

**问题**：现状 `UpdateQrForMode`（line 430）直接调 `GenerateQrCode(url)`，url 来自 `_server.UploadUrl`（不含 `?t=token`）。结果：

| 触发 | 走哪条 QR 路径 | URL 是否含 `?t=token` |
|---|---|---|
| `RefreshQrForCurrentAddress`（via `BuildDisplayUrl`） | LAN 模式 | ✅ 含 |
| `UpdateQrForMode`（启动 / 公网 relay 切换） | LAN + 公网 | ❌ 不含 |

—— LAN 模式下两种路径行为不一致。公网模式刻意不含 token（设计合理），但 LAN 模式下两条路径本应一致。

**修复**：`UpdateQrForMode` 改为调 `GenerateQrCode(DisplayUploadUrl)`，与 `RefreshQrForCurrentAddress` 对齐：

```csharp
private void UpdateQrForMode() {
    if (_server == null) return;

    var publicUrl = PublicRelayViewModel.BuildPublicUploadUrl();
    var isPublic = PublicRelayViewModel.IsPublicRelayRunning && !string.IsNullOrEmpty(publicUrl);

    IsPublicMode = isPublic;
    UploadUrl = isPublic ? publicUrl! : _server.UploadUrl;

    // v0.12: 改用 DisplayUploadUrl（已包含 ?t=token + 当前 AddressIndex 选中的 IP）
    //  - LAN 模式：自动拼 ?t={token}
    //  - 公网模式：直接返回 UploadUrl（不含 token，设计合理）
    GenerateQrCode(DisplayUploadUrl);
}
```

**自验证**：
1. 启动服务 → QR 内容 = `http://192.168.1.10:8765/upload?t=123456`
2. 启动公网 relay → 切换 QR → QR 内容 = `http://<pub-host>:8765/upload`（无 token）
3. 关闭公网 relay → 切回 LAN QR → URL 恢复 `?t=token`
4. 重置 token → URL `?t=` 段刷新为新 token

### 4.6 XAML 改动

`UploadServerView.axaml` 新增：

```xml
<StackPanel Orientation="Horizontal" Margin="0,8,0,0">
    <TextBlock Text="Token: " VerticalAlignment="Center" />
    <TextBlock Text="{Binding CurrentToken}" FontFamily="Consolas" FontWeight="Bold" />
    <Button Content="重置" Command="{Binding RegenerateTokenCommand}" Margin="8,0,0,0" />
</StackPanel>
```

---

## 5. IMediaRepository 新增方法

### 5.1 CountByProjectAsync

```csharp
public interface IMediaRepository {
    // ... 现有方法 ...

    /// <summary>
    /// v0.12: 统计某项目路径下的 media_files 行数（不区分 deleted_at），
    /// 用于 LAN 上传 server 的 /api/v1/projects 列表展示。
    /// 不抛异常；失败返回 0（让 HTTP 响应仍能返回）。
    /// </summary>
    Task<long> CountByProjectAsync(string projectPath, CancellationToken ct = default);
}
```

实现：

```csharp
public async Task<long> CountByProjectAsync(string projectPath, CancellationToken ct = default) {
    try {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM media_files WHERE project_path = @p", conn);
        cmd.Parameters.AddWithValue("@p", projectPath);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result ?? 0L);
    }
    catch (Exception ex) {
        Debug.WriteLine($"[MediaRepository] CountByProjectAsync failed: {ex.Message}");
        return 0L;
    }
}
```

> 当前 `MediaRepository` 已有 `GetLocalSizeAsync`（v0.12 引入），可复用。

---

## 6. 边界情况

| 场景 | 处理 |
|---|---|
| Token 错误 / 缺失 | 401 `{ "error": "invalid token" }` |
| 项目 `{name}` 不在 RecentDirectories | 404 `{ "error": "project 'xxx' not found" }` |
| 项目路径已被用户删除 | 启动时 `_configService` 拿的还是路径，`ResolveProjectPathAsync` 内部 `Directory.Exists` 过滤；Get 列表时也过滤 |
| 当前项目为空 | `/api/v1/health` 返回 `currentProject: ""`，App 端提示"PC 当前未打开任何项目" |
| 单文件 > 500MB | 当前实现里 `ParsedFile.Data` 是 `MemoryStream`，`Length` 已经过内存解析失败；spec 改为上传前 enforce（见 §3.7 注释） |
| Content-Type 不是 multipart | 400 `{ "error": "Invalid content type..." }` |
| multipart 解析失败 | 400 `{ "error": "multipart parse failed: ..." }` |
| UDP 端口被占用 | PC 端 netcat 不到，App 端搜不到 PC（用户用"手动输入 IP"兜底） |
| UDP 广播在 Windows 防火墙拦截 | 弹窗提示用户允许（暂不主动提示，靠用户重试发现） |
| UDP 广播没人接 | App 端 5s 扫描后空列表，提示"未发现 PC" |
| 上传后扫描失败 | `Debug.WriteLine` 一行日志，HTTP 响应仍 200（用户感知不到） |
| 同时多上传并发 | `_listenTask` 每请求 `_ = Task.Run(...)`，文件落盘各自独立；扫描 `_ = Task.Run` 多次调用，SQLite 会串行 |
| 服务停止 / 重启 | 旧 token 失效，App 端下次请求 401 → 清理持久化 → 重新输入 |
| 升 token 期间 App 端刚好请求 | 短瞬时 401 在所难免，App 端已实现 401 兜底 |
| `legacyDefaultDirectory` 为空（H5 上传） | 沿用 v0.10 行为：空字符串时 `dateDir = Path.Combine("", today) = "2026-08-15"` 是相对路径，会落到 CWD；有 ProjectPath 时才正常 |

---

## 7. 验证方案

> **不上 PoC**——直接通过现有 UploadServerViewModel 启动服务 + 局域网内 curl 验证。

### 7.1 启动

1. `dotnet run` 启动星助
2. 选项目目录（任何一个）、进 UploadServerView
3. 点"启动" → 看到 Token 显示 + QR 码（带 `?t=token`）

### 7.2 单机验证

```bash
# health：拿 token + 验证服务在跑
curl -s http://localhost:8765/api/v1/health | jq .

# 项目列表
curl -s "http://localhost:8765/api/v1/projects?token=123456" | jq .

# 上传（多文件）
curl -s -X POST \
     "http://localhost:8765/api/v1/projects/deepsky-2025/upload?token=123456" \
     -F "file=@/tmp/test.jpg" \
     -F "file=@/tmp/test2.jpg" | jq .

# 错误场景
curl -s -X POST "http://localhost:8765/api/v1/projects/deepsky-2025/upload" \
     -F "file=@/tmp/test.jpg" | jq .   # 缺 token → 401
curl -s "http://localhost:8765/api/v1/projects/nonexistent?token=123456" | jq .   # 404
```

### 7.3 局域网验证

```bash
# 另一台机器（或手机扫码 → 获取 token 后手动 curl）
# 假设 PC IP = 192.168.1.10
curl -s "http://192.168.1.10:8765/api/v1/health" | jq .

# 上传（用 /api/v1/health 拿到的 token）
curl -s -X POST \
     "http://192.168.1.10:8765/api/v1/projects/deepsky-2025/upload?token=123456" \
     -F "file=@/path/to/photo.jpg" | jq .
```

### 7.4 UDP 验证

```bash
# 用 netcat 监听 UDP 广播
nc -u -l 9876
# 等待 2s 应看到 JSON 进来
```

或 Python：

```python
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(('0.0.0.0', 9876))
while True:
    data, addr = s.recvfrom(4096)
    print(data.decode())
```

### 7.5 验证清单

- [ ] `/api/v1/health` 返回 200 + JSON 含 token、currentProject
- [ ] `/api/v1/projects?token=123456` 返回 RecentDirectories 列表
- [ ] `/api/v1/projects/nonexistent/upload` 返回 404
- [ ] 缺 token 返回 401
- [ ] 错 token 返回 401
- [ ] multipart 上传 1 张图 → 文件落到 `{projectPath}/{yyyy-MM-dd}/`，Gallery 刷新可见
- [ ] multipart 上传多张图 → 全部成功
- [ ] 非白名单扩展名（.txt）→ 累加到 `failed[]`
- [ ] 上传完成后 1-2s 内，`media_files` 表出现新行
- [ ] Token 重置按钮 → `CurrentToken` 变化 → QR 刷新
- [ ] 重置后旧 token 访问 → 401
- [ ] UDP 广播每 2s 发一次
- [ ] 关掉服务 → UDP 停止
- [ ] H5 `/upload` GET/POST 仍正常工作（回归）
- [ ] **QR 修复（spec §4.5.1）**：启动服务后 QR URL 含 `?t=token`；启动公网 relay 后 QR 切换且不含 token；关闭公网 relay 后 QR 恢复 `?t=token`

---

## 8. 实施顺序

| 步骤 | 内容 | 改动量 | 验证 |
|---|---|---|---|
| 1 | IMediaRepository 加 `CountByProjectAsync` | +20 行 | 单元测试或 GalleryVM 现有路径调用 |
| 2 | UploadServerService 重写：路由 + Token + JSON 统一化 + 注入 IConfigService/IMediaRepository | 大改 200-300 行 | curl 验证 4 个端点 |
| 3 | UploadServerService 新增 UDP 广播 | +60 行 | nc 抓包 |
| 4 | UploadServerViewModel 注入、新增 CurrentToken / RegenerateTokenCommand | +40 行 | UI 看 Token 实时刷新 |
| 5 | UploadServerView.axaml 新增 Token 区块 | +10 行 | 视觉确认 |
| 6 | MainWindowViewModel wiring 调整（注入 configService + mediaRepository） | +5 行 | App 启动正常 |
| 7 | 端到端验证（按 §7 清单） | — | 全部 ✓ |

> **App 端不在本 spec 范围内**——iOS / Android 独立仓库起步后由各自 spec 描述。

---

## 9. 不做清单

| 内容 | 理由 |
|---|---|
| HTTPS / 自签证书 | 局域网风险可控，自签体验差 |
| 用户名 / 密码鉴权 | 6 位 token 满足家庭场景 |
| 双向同步 | 超出 v0.12 范围 |
| AI 自动打标 | 需求明确"仅写入本地 + 调整索引" |
| 触发 OSS 上传 | 同上 |
| 断点续传 / 分片上传 | MVP 阶段不做 |
| App 端仓库 | 暂不处理，spec 也独立 |
| 后台持续 UDP | iOS / Android 系统限制 |
| `media.db` 同步 | D02 范围 |
| 文件秒传（hash 预检） | 暂不做 |
| 端口可配置 | 沿用 v0.10（默认 8765，UI 可改） |

---

## 10. 与 D02 / v0.10 的关系

### 10.1 与 v0.10 UploadServerService

- **复用**：HTTP listener、multipart 解析、文件落盘、QR 码生成、IP 获取、状态机
- **不破坏**：H5 `/upload` GET/POST 路由保留
- **新增**：路由分支 `/api/v1/*`、Token 机制、UDP 广播、注入 IConfigService / IMediaRepository、JSON 序列化统一化

### 10.2 与 D02 cross-device-sync

- **互补**：D02 走广域网 OSS，D04 走局域网
- **数据模型**：D04 落盘后由 `MediaRepository.ScanDirectoryAsync` 写入 `media_files.project_name`（D02 字段）
- **不冲突**：本 spec 不动 D02 任何代码

### 10.3 与 ProjectConfig

- `RecentDirectories`：`/api/v1/projects` 直接读
- `CurrentDirectory`：HTTP 响应 `is_current=true` 标记
- `ProjectName`：HTTP 响应 `project_name` 字段（D02 跨设备标识）

### 10.4 与 MediaRepository

- **复用**：`_mediaRepository.ScanDirectoryAsync(projectPath, ...)` 落盘后触发
- **新增**：`_mediaRepository.CountByProjectAsync(projectPath)` 给 `/api/v1/projects` 用
- **不破坏**：所有现有扫描流程不变

---

## 11. 风险与权衡

| 风险 | 缓解 |
|---|---|
| 路由分发重写可能破坏 H5 上传 | 沿用旧 `HandleLegacyUploadAsync`（签名兼容）+ 回归测试 |
| UDP 广播在某些 Windows 防火墙被拦 | 文档说明 + 用户自行放行；后续可加 UDP 探测失败提示 |
| 多上传并发 → 多次 ScanDirectoryAsync | SQL 串行 + 内部已有去重逻辑，理论安全；观察一次 |
| 500MB 单文件限制只在 `MemoryStream.Length` 有效时 enforce | 暂接受（multipart 一次性内存解析总会先 OOM） |
| App 端 UDP 抓不到 | 已在需求文档"边界情况"列入，App 端兜底"手动输入 IP" |
| Token 重置期间 App 端请求 | 短瞬时 401，App 端清理持久化 token 后重新输入 |
| 主项目变更时 UDP 广播出的 currentProject 没变 | 启动时读一次；不订阅 ProjectPath 变更（权衡：复杂度不值得） |

---

## 12. v0.13 预研条目：Token 同源事实与 App 端权威约定

> **状态**：预研条目，**v0.12 不实施**。本节专门记录 PC 端实现中存在但 v0.12 spec 上文未明确的现象，作为 v0.13 升级时的依据。
>
> 上文 §3.5 描述 Token 是 PC 端 "一个实例字段"（`_currentToken`）。App 端在对接过程中发现该字段同时被两个对外通道读取——这一点在 v0.12 spec 文档中未显式记录，导致 App 端协议对接初期把广播 token 与 `/api/v1/health` 响应 token 当作"两个值"处理。
>
> 关联文档（已在 v0.12 PC 端对接过程中同步更新）：
> - [doc/app/02-pc-udp-protocol.md §2.3](../../app/02-pc-udp-protocol.md#L54-L71)（协议层权威性约束）
> - [doc/app/03-mobile-checklist.md §2.3](../../app/03-mobile-checklist.md#L30-L46)（App 端状态机实现要求）

### 12.1 上游事实（已在 v0.12 PC 实现中存在）

PC 端 `UploadServerService.cs`：

- **唯一 token 实例字段**：`_currentToken`
- UDP 广播 payload（`UdpBroadcastPayload.Token`）：每个广播周期从 `_currentToken` 序列化
- HTTP `/api/v1/health` 响应（`HealthResponse.Token`）：构造响应时从 `_currentToken` 赋值
- HTTP 受保护端点 `ValidateToken(req)`：与 `_currentToken` 严格相等判断
- Token 变更时机：
  1. 构造函数调用 `GenerateToken()` 一次
  2. `StartAsync()` 中触发 `OnTokenChanged`（但 `_currentToken` 值不变，仅通知 UI）
  3. UI "重置"按钮调用 `RegenerateToken()` → 重新 `GenerateToken()` + `OnTokenChanged`
  4. 进程退出 → `_currentToken` 内存值丢失；下次启动重新生成

**结论**：广播 token 与 health 响应 token **100% 同源**（同一字段、同时刻读、原子赋值）。`/api/v1/health` 无需鉴权就能取到当前 token 是因为实现上不带鉴权，但**它包含的 token 与广播 token 一致**。

### 12.2 v0.12 落地的协议事实（PC 端维护方需知会 App 端）

| 事实 | 影响 |
|---|---|
| 广播 token 与 health token 同源（同一字段读出） | App 端不应把两个值当独立来源处理 |
| Token 变更最迟 2 s 内反映到下一次广播（每个广播周期重读 `_currentToken`） | App 端不需要主动轮询 / 重试加速缓存同步 |
| Token 无加密 | LAN 嗅探可见，不防中间人；不防广播伪造（明文广播可被同 LAN 客户端重放）|
| `/api/v1/health` 响应明文回带 token | 不仅广播可见，HTTP 嗅探同样可见（KB `API-03 §八` 已记录） |
| Token 变更后旧 token 立刻失效（`ValidateToken` 严格相等）| 重置期间 App 端命中受保护端点必 401，需走"等广播 → 用新 token → 重试一次"路径 |
| 进程崩溃 / 重启 → 新 token 重新生成 | App 端持久化 token 在重启后必失效，需按"试探 health → 等广播"路径重新获取 |

### 12.3 v0.13 升级时应下决定的协议事实（不改实现，仅固化文档）

`doc/02-pc-udp-protocol.md` 与 `doc/03-mobile-checklist.md` 已在 v0.12 文档集中补完 App 端的权威约定；PC 端 v0.13 升级（如有）需同步**固化**下面两条到 PC 端代码层契约（或 KB）：

1. **`_currentToken` 是单一权威实例**——任何对外通道（mDNS、SSR 回包、HTTP 校验头）都从同一字段读出，禁止在 PC 端产生"多个 token"。
2. **变更反映时限 ≤ 2 s**——下一周期广播必须使用最新 `_currentToken`。`UdpBroadcastIntervalMs` 改动时同步在 KB `API-03` 写明。

### 12.4 v0.13 候选升级路径（仅记录，不在 v0.12 范围）

> 以下条目**均不在 v0.12 工作范围**，仅作 v0.13 起步参考；任何 v0.13 启动时应单独走需求 / spec 流程。

| 升级方向 | 现状 | v0.13 可选做法 |
|---|---|---|
| 多 token 来源收敛 | 广播 + health | 移除 `HealthResponse.Token` 回带，让广播承载唯一权威（App 仍可走 health 探活，但不应再用它当 token 来源）|
| App 端长期鉴权 | 6 位数字 + 每次启动轮换 | 上线 32 字节随机 token + PC 首启生成持久化 + App 端持久化（Keychain / EncryptedSharedPreferences）|
| Token 加密传输 | 明文 | 上线 PC 公钥 + App 端 ECDH：广播 token 用 PC 公钥加密，App 用本地私钥解密 |
| 防重放 | 无 | 加 nonce + 时间戳窗口（±60s）|
| 多端并用 | 仅单一广播 / 单一 PC | 加 `deviceId`（App 端设备 UUID），PC 端为每台 App 独立颁发 token |
| 不可信 LAN 防护 | 明文广播 | 广播内字段整体 AEAD 加密（PC 端公私钥对）|

### 12.5 与现有文档的关系

- 本节补充 spec 中**遗漏的实现事实**，**不修改** v0.12 spec 的设计与代码契约。
- 对移动端 App 对接而言，**`doc/app/02-pc-udp-protocol.md` §2.3 与 `doc/app/03-mobile-checklist.md` §2.3 即是本节事实的对外公开版本**——App 端对接不需要回看 0.12 spec。
- v0.13 真要启动升级时，先把本节的"候选路径"细化进 `doc/0.13/spec/*.md`，再走通常的 demand / spec 流程。
