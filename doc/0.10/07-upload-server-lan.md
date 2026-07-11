# 07 — 局域网上传（UploadServerService）

> 对应代码：`Services/UploadServerService.cs`、`ViewModels/UploadServerViewModel.cs`、`Views/UploadServerView.axaml`、HTML 模板 `StartTooler/Resources/upload.html`（编译时同步到 `tools/upload-relay/web/index.html`）。

---

## 1. 模块边界

```
UploadServerViewModel (UI 状态)
  ├─ GalleryViewModel (拿到 ProjectPath)
  ├─ PublicRelayViewModel (订阅 PropertyChanged → QR 跟公网 URL 切换)
  └─ UploadServerService (HTTP listener)

UploadServerService
  ├─ HttpListener 监听 http://+:<port>/
  ├─ GET  /upload       → 返回 HTML 上传页（multi-cast QR 入口）
  ├─ POST /upload       → multipart/form-data 解析 + 落盘
  ├─ OnUploadSuccess(string path) event
  └─ OnUploadError(string msg) event
```

> 局域网扫码共享同一份 HTML 模板（`Resources/upload.html`），公网代理 Go 端**重用同一模板**（`build-relay.sh:27-35` 同步到 `tools/upload-relay/web/index.html`）。

---

## 2. 协议契约

### 2.1 LAN 监听

```
HttpListener.Prefixes.Add($"http://+:{port}/")
_listener.Start()
```

**`http://+:<port>/`** + 端口可绑定所有网卡 IPv4。**macOS / Linux 直接 OK**；Windows 上需 `netsh http add urlacl url=http://+:<port>/ user=<username>`（`UploadServerService.cs:55-58` 已捕获 `HttpListenerException.ErrorCode == 5` 提示用户）。

### 2.2 端点

| 方法 + 路径 | 行为 |
|---|---|
| `GET /upload` | 返回 HTML 上传页（运行时替换 `{{STARTOOLER_BASE}}` 占位符为 `http://<lan-ip>:<port>`）|
| `POST /upload` | multipart/form-data；落盘到 `{projectPath}/{yyyy-MM-dd}/原始文件名` |
| `GET /` | 兜底：任何非 `/upload` 的 GET 返回 HTML |
| 其他 | 404 / 405 |

### 2.3 文件落盘

- 路径模板：`{ProjectPath}/{yyyy-MM-dd}/{原始文件名}`（日期是服务器当前日期，`DateTime.Now.ToString("yyyy-MM-dd")`）
- 重名追加：`name_1.jpg`, `name_2.jpg`…（`GetUniqueFileName`）
- 扩展名白名单：`{ ".jpg", ".jpeg", ".png", ".raw", ".avi", ".mp4", ".mov", ".mkv", ".webm", ".m4v", ".mpg", ".mpeg" }`（`UploadServerService.cs:24-27`）
- 失败的文件 `OnUploadError.Invoke(...)`，整体返回 `{success:true, count:N}`

---

## 3. multipart 解析

### 3.1 已知坑

HttpListenerRequest **没有 `Files` 属性**——必须手写解析（`UploadServerService.cs:242-302` 注释承认）。

#### 3.1.1 早期写得不能用（已固化为反面教材）

`UploadServerService.cs:242-301` 第一版用 `Encoding.UTF8.GetString(body).Split(boundary)` 拿到字符串后用 `\r\n\r\n` 切 header/data：

> **承认**（`UploadServerService.cs:292-298`）：「文件内容可能乱码但 dataStr 只是边界分割用」「重新按字节位置找」「files.Clear() 重新解析」—— 早期实现直接放弃，转 `ParseMultipartFilesByBytes`。

#### 3.1.2 现行实现（`ParseMultipartFilesByBytes`）

```csharp
private static List<ParsedFile> ParseMultipartFilesByBytes(byte[] body, string boundary) {
    var boundaryBytes = Encoding.UTF8.GetBytes("\r\n" + boundary);
    var delimiterBytes = Encoding.UTF8.GetBytes(boundary + "\r\n");
    var closeBytes     = Encoding.UTF8.GetBytes(boundary + "--");

    var pos = 0;
    while (pos < body.Length - delimiterBytes.Length) {
        var idx = IndexOf(body, delimiterBytes, pos);
        if (idx < 0) break;
        var nextIdx = IndexOf(body, delimiterBytes, idx + delimiterBytes.Length);
        if (nextIdx < 0) nextIdx = IndexOf(body, closeBytes, idx);
        if (nextIdx < 0) nextIdx = body.Length;

        var partData = new byte[nextIdx - idx - 2];     // 去掉开头的 \r\n
        Array.Copy(body, idx + 2, partData, 0, partData.Length);

        // 找 headerEnd 切 header / file data
        var partStr = Encoding.UTF8.GetString(partData);
        var headerEnd = partStr.IndexOf("\r\n\r\n");
        ...

        // file data 起点（header 部分用 byte-count 回算）
        var dataStart = Encoding.UTF8.GetByteCount(partStr.Substring(0, headerEnd + 4));
        var dataLen = partData.Length - dataStart;
        if (dataLen > 2) dataLen -= 2;    // 去掉末尾 \r\n

        var data = new byte[dataLen];
        Array.Copy(partData, dataStart, data, 0, dataLen);

        files.Add(new ParsedFile(Path.GetFileName(fileName), new MemoryStream(data)));
        pos = nextIdx;
    }
    return files;
}

private static int IndexOf(byte[] haystack, byte[] needle, int start) {
    for (var i = start; i <= haystack.Length - needle.Length; i++) {
        var found = true;
        for (var j = 0; j < needle.Length; j++)
            if (haystack[i + j] != needle[j]) { found = false; break; }
        if (found) return i;
    }
    return -1;
}
```

> **承认**：`Encoding.UTF8.GetByteCount(partStr.Substring(0, headerEnd + 4))` 是从字符串 byte 位置倒推，对**纯 ASCII header** 是对的；header 里若有非 ASCII 文件名（RFC 5987 编码 `filename*=UTF-8''xxx`）会偏——目前 HTML 前端固定 `filename="原始.jpg"`，未踩过。

### 3.2 落盘

```csharp
foreach (var file in files) {
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (Array.IndexOf(AllowedExtensions, ext) < 0) {
        OnUploadError?.Invoke($"Unsupported file type: {ext}");
        continue;
    }
    var today = DateTime.Now.ToString("yyyy-MM-dd");
    var dateDir = Path.Combine(_currentDirectory, today);
    Directory.CreateDirectory(dateDir);
    var destPath = GetUniqueFileName(Path.Combine(dateDir, file.FileName));
    await using (var output = File.Create(destPath)) {
        await file.Data.CopyToAsync(output);
    }
    successCount++;
    OnUploadSuccess?.Invoke(destPath);
}
```

### 3.3 响应

```csharp
await WriteResponseAsync(response, 200, $"{{\"success\":true,\"count\":{successCount}}}");
// 失败
await WriteResponseAsync(response, 500, $"{{\"error\":\"{EscapeJson(ex.Message)}\"}}");
```

**JSON 字符串只做了基本转义**（`\`、`"`、`\n`、`\r`），**够用但脆弱**——目前响应内容由 `ex.Message` 直出，没人为注入路径。

---

## 4. UploadServerViewModel（`ViewModels/UploadServerViewModel.cs`）

### 4.1 字段

| 属性 | 类型 | 含义 |
|---|---|---|
| `Port` | `int` 默认 8765 | 监听端口 |
| `IsRunning` | `bool` | 监听中 |
| `UploadUrl` | `string?` | 当前 QR 指向的 URL（LAN 或公网）|
| `QrCodeImage` | `Bitmap?` | QRCoder 输出 PNG |
| `StatusMessage` | `string?` | "正在启动..." / "服务已启动" |
| `ErrorMessage` | `string?` | 启动失败 / 上传错误 |
| `RecentUploadMessage` | `string?` | "✓ 已上传: filename.jpg" |
| `IsPublicMode` | `bool` | true = 走公网 relay URL |
| `PublicRelayViewModel` | `PublicRelayViewModel` | 嵌套对象（XAML 绑定其属性）|

### 4.2 Command 链

```
StartServer (CanExecute = !IsRunning)
  ├─ _server = new UploadServerService(_gallery.ProjectPath ?? "")
  ├─ _cts = new CTS
  ├─ _server.OnUploadSuccess += path → Dispatcher.UIThread.Post { RecentUploadMessage = "✓" }
  ├─ _server.OnUploadError   += msg  → Dispatcher.UIThread.Post { ErrorMessage = "..." }
  ├─ await _server.StartAsync(Port, ct)
  ├─ IsRunning = true
  └─ UpdateQrForMode()                  ← 决定用 LAN 还是公网 URL

StopServer (CanExecute = IsRunning)
  ├─ _cts?.Cancel() / _server?.Stop()
  ├─ QrCodeImage?.Dispose() + null
  ├─ IsPublicMode = false
  └─ UploadUrl = null
```

### 4.3 QR 切换（关键）

```csharp
private void OnPublicRelayPropertyChanged(object? sender, PropertyChangedEventArgs e) {
    // 只在 relay 状态切换 / 公网 URL 字段变化时刷新 QR
    if (e.PropertyName != nameof(RelayStateText)
        && e.PropertyName != nameof(PublicHost)
        && e.PropertyName != nameof(SshHost)
        && e.PropertyName != nameof(HttpPort))
        return;

    // 注意：TCP loop 会触发 StateChanged 在后台线程
    Dispatcher.UIThread.Post(() => { if (IsRunning) UpdateQrForMode(); });
}

private void UpdateQrForMode() {
    if (_server == null) return;
    var publicUrl = PublicRelayViewModel.BuildPublicUploadUrl();
    var isPublic = PublicRelayViewModel.IsPublicRelayRunning && !string.IsNullOrEmpty(publicUrl);
    IsPublicMode = isPublic;
    var url = isPublic ? publicUrl! : _server.UploadUrl;
    UploadUrl = url;
    GenerateQrCode(url);
}
```

> **承认**（`UploadServerViewModel.cs:113-131`）：relay 状态变更可能来自后台线程（TCP loop），统一 marshal 到 UI 线程。

#### 4.3.1 QR 切换的决策

| `IsPublicRelayRunning` | `BuildPublicUploadUrl()` | 走谁 |
|---|---|---|
| false | any | LAN URL (`http://<lan-ip>:<port>/upload`) |
| true | null / empty（host 不全）| LAN URL（兜底） |
| true | valid `http://<pub>:8765/upload` | 公网 URL |

### 4.4 QR 生成（`GenerateQrCode`）

```csharp
using var qrGenerator = new QRCodeGenerator();
using var qrCodeData  = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
using var qrCode      = new PngByteQRCode(qrCodeData);
var pngBytes = qrCode.GetGraphic(5);                          // 每个 QR 模块 5px

Dispatcher.UIThread.Post(() => {
    QrCodeImage?.Dispose();                                   // 关键：避免上一张泄漏
    QrCodeImage = new Bitmap(new MemoryStream(pngBytes));
});
```

> **承认**（commit message 历史）：之前把 `new Bitmap(...)` 套在 `Dispatcher.UIThread.Post` 内层但 stream 已被外层 `using` dispose → 触发 `Unable to load bitmap from provided data`。已修：bytes 拿到外层再 Post 构造。

### 4.5 退出与清理（`UploadServerViewModel.Dispose`）

```csharp
public void Dispose() {
    PublicRelayViewModel.PropertyChanged -= OnPublicRelayPropertyChanged;
    StopServer();         // 闭 listener + Dispose + 清 IsRunning
    _cts?.Dispose();
}
```

**注意**：`UploadServerViewModel` 由 `MainWindowVM` 持有，整个进程生命周期内不释放（除非 app 退出）；app 退出时 dispose 链路确保 listener 关闭。

---

## 5. HTML 模板（`Resources/upload.html`）

### 5.1 单源真相

仓库里：
- `StartTooler/Resources/upload.html` — **唯一源**（还 `CopyToOutputDirectory=PreserveNewest` 到 publish 目录）
- `tools/upload-relay/web/index.html` — **Go embed 源**（脚本同步自上面那个）

`build-relay.sh:27-35`：源文件 mtime 比 embed 旧 → 同步；`CopyToOutputDirectory` 让 LAN UploadServerService 运行时用本地文件。

### 5.2 运行时占位符

模板含 `{{STARTOOLER_BASE}}`，`UploadServerService.ServeUploadPageAsync` 替换为：
- LAN: `http://<GetLocalIp()>:<Port>`
- 公网: Go 端用 `http://<r.Host>`（`r.Host` 是 HTTP Host header，可能含端口）

HTML 拿到 `baseUrl` 之后：
- 直接打开（无服务端注入）时占位符不被替换，JS 走 fallback 相对路径（仍能上传）
- 服务端注入后 JS fetch 用 `baseUrl + '/upload'`，跨域无需 CORS（同源）

### 5.3 模板内容约定

不在本规范范围（保持原始 Resources/upload.html 作权威），要点：

- 文件输入 `<input type="file" multiple>` 
- 提交走 fetch + FormData（`multipart/form-data`，multipart name 字段固定 `"file"`）
- UI 提示"上传中..." / 完成

---

## 6. 修改这个模块前的检查清单

| 改动 | 涉及 | 验证 |
|---|---|---|
| 加端点（例：`/health`）| `UploadServerService.HandleRequestAsync` 加分支 | curl 验证 GET 200 OK |
| 改 QR 库 | `UploadServerViewModel.GenerateQrCode` | PNG bytes → Bitmap 流程 |
| 改端口默认 | `UploadServerViewModel.Port = 8765` + `PublicRelayConfig.HttpPort = 8765` | 公网代理 setting UI 也得改 |
| 改日期目录命名 | `UploadServerService.HandleRequestAsync` | 已按 yyyy-MM-dd，签名/天数变化测试 |
| 改扩展名白名单 | `UploadServerService.AllowedExtensions`（LAN）和 Go relay（公网，无显式）| 后端白名单放哪？安全性 vs 易用性 |
| 加多文件签名 | HTML 模板 + 服务端 multipart name 解析 | 必须两端同步 |

---

## 7. 已知陷阱（详见 `10-trap-book.md`）

- **`Encoding.UTF8.GetByteCount` 算偏移** 在 header 含非 ASCII 时不准 —— 当前 HTML 前端用纯 ASCII 文件名，**没踩过**；换前端会炸
- **`OnUploadError` 是 fire-and-forget**，VM 里 `Dispatcher.UIThread.Post`，VM 已 dispose 后会抛 `ObjectDisposedException` → 实际：app 退出时 listener 先停，不会触发
- **`http://+:<port>/` 在 Windows 要 `netsh http add urlacl`**（已有提示）
- **公网 QR 切 LAN QR 的瞬间用户扫错** —— 没动画/loading 提示
- **`RecentUploadMessage` 不自动清** —— 用户上传完最后一条还在，新上传覆盖
- **HTML 模板里嵌入的 baseUrl 不可信**：HTTP Host header 可被 `Host:` 头伪造；LAN 是本地用户自己扫，**局域网内受信任**；公网 Go relay 同理
- **不在 LAN listener 上做基本认证**：纯 IP-白名单 + 端口猜测对家庭 WiFi 够用，公开 WiFi 任何人扫码都能传——已知风险
