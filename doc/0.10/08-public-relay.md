# 08 — 公网代理（C# 端 + Go relay）

> 对应代码（C#）：`Services/PublicRelayService.cs`、`Services/PublicRelayConfig.cs`、`Services/RelayBinaryExtractor.cs`、`ViewModels/PublicRelayViewModel.cs`、`Views/UploadServerView.axaml`（折叠 Expander 部分）。
>
> 对应代码（Go）：`tools/upload-relay/main.go`、`web/index.html`、`go.mod`、`scripts/build-relay.{sh,ps1}`。

---

## 1. 系统结构

```
┌────────────────────────── macOS / Windows / Linux (开发机) ──────────────────────────┐
│ StartTooler (.NET)                                                                   │
│                                                                                       │
│   PublicRelayViewModel         ◄──── settings / dirty-tracking / 命令                 │
│      │                                                                                │
│      ▼                                                                                │
│   PublicRelayService           ◄──── SSH.NET (SshClient + ScpClient)                    │
│      ├─ DeployAsync   (SSH: mkdir + scp binary + chmod)                              │
│      ├─ StartRemoteAsync (SSH: setsid + nohup + 写 PID file)                        │
│      ├─ StopRemoteAsync  (SSH: kill PID file)                                         │
│      ├─ ResolveArchAsync (SSH: uname -m 自动 / 配置指定)                              │
│      ├─ StartClient (TCP 长连接 + Poller 异步 scp)                                    │
│      │     ├─ RunClientLoopAsync: 指数退避重连 + 按行解析 file_pending JSON          │
│      │     │     └─ OnFilePendingReceived: NotificationService.Show + InsertIfNew     │
│      │     └─ RunPollerLoopAsync: PeriodicTimer 5s → GetPendingBatch → DrainBatch   │
│      ├─ StopClientAsync                                                                  │
│      └─ EnsureRemoteKilledOnExitAsync                                                  │
│                                                                                       │
│   RelayBinaryExtractor.Extract(arch)                                                  │
│      └─ 从 .NET dll embedded resource 解压到 {Temp}/starttooler/upload-relay-linux-*  │
└──────────────────────────────────────┬───────────────────────────────────────────────┘
                                       │ SSH (deploy)
                                       ▼
┌────────────────────────── VPS / 公网机器 ─────────────────────────────────────────────┐
│  $ ls -la ~/starttooler/                                                              │
│     upload-relay-linux-amd64    (or arm64, chmod 0755)                                │
│     relay.pid                   (PID file)                                            │
│     relay.log                   (stdin/stdout/stderr)                                 │
│     tmp/{sanitized_original_name}    (pending 上传文件；v0.4+ 改成原始文件名直接落盘)        │
│                                                                                       │
│   upload-relay (Go)                                                                    │
│      ├─ HTTP (:8765)                                                                   │
│      │     ├─ GET  /upload  → 返回 index.html (替换 {{STARTOOLER_BASE}})               │
│      │     ├─ POST /upload → multipart 接收 → 写 tmp/ → Broadcast                    │
│      │     ├─ POST /ack/{id} → 从 pending 删 + rm tmp                                  │
│      │     └─ GET  /health  → 返回 JSON 状态                                            │
│      └─ TCP (:8766)                                                                    │
│            └─ 每 client goroutine: Subscribe → ReplayPending → 转发 file_pending       │
└──────────────────────────────────────┬───────────────────────────────────────────────┘
                                       │ HTTP POST (browser)              │ TCP JSON line
                                       ▼                                    ▼
                              ┌──────────────────┐                  ┌──────────────────┐
                              │ 手机浏览器         │                  │ StartTooler       │
                              │ 选文件 → 上传      │                  │ .NET RunClient... │
                              └──────────────────┘                  └──────────────────┘
```

---

## 2. PublicRelayConfig（`Services/PublicRelayConfig.cs`）

```csharp
public class PublicRelayConfig {
    // SSH 认证（Key 优先于 Password）
    public string SshHost { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "";
    public string? SshPassword { get; set; }
    public string? SshKeyPath { get; set; }

    public string SshRemotePath { get; set; } = "~/starttooler";

    public int HttpPort { get; set; } = 8765;
    public int TcpPort { get; set; } = 8766;

    public string? PublicHost { get; set; }   // 留空 = 用 SshHost
    public string RemoteArch { get; set; } = RelayArch.Auto;   // auto / amd64 / arm64
}

public static class RelayArch {
    public const string Auto = "auto", Amd64 = "amd64", Arm64 = "arm64";
    public static string? FromUnameM(string s);   // x86_64 → amd64, aarch64 → arm64, else null
}
```

### 2.1 字段语义

- `SshRemotePath = "~/starttooler"`：VPS 端 binary 所在目录。SSH 远端 shell 自动展开 `~`，**保留原样**
- `HttpPort` / `TcpPort`：VPS 上监听端口（公网要保证防火墙放过）
- `PublicHost`：仅用于 UI 展示 / QR / ACK HTTP host；**留空等于 SshHost**——> `PublicRelayService.AckFileAsync` 用此值决定 HTTP 客户端连哪
- `RemoteArch`：见下节

### 2.2 VPS 架构自动 / 手动

`PublicRelayService.ResolveArchAsync`（`PublicRelayService.cs:218-237`）：
```csharp
public async Task<string> ResolveArchAsync(PublicRelayConfig cfg, IProgress<string>? log, CancellationToken ct) {
    var configured = (cfg.RemoteArch ?? RelayArch.Auto).Trim().ToLowerInvariant();
    if (configured != RelayArch.Auto) {
        if (!RelayBinaryExtractor.SupportedArchs.Contains(configured))
            throw new InvalidOperationException($"RemoteArch 配置无效: '{configured}'。可选：auto / {string.Join(" / ", RelayBinaryExtractor.SupportedArchs)}");
        return configured;
    }
    log?.Report("自动检测 VPS 架构 (uname -m)...");
    var output = await RunSshAsync(cfg, "uname -m", null, ct);
    var detected = RelayArch.FromUnameM(output);
    if (detected == null)
        throw new InvalidOperationException(
            $"无法识别 VPS 架构 ('{output.Trim()}')。请在「公网代理设置」里把架构手动指定为 amd64 或 arm64。");
    return detected;
}
```

`uname -m` 输出：
- x86_64 → amd64
- aarch64 → arm64
- 其他（i686 / armv7l / …）→ 抛 + UI 引导用户手动指定

---

## 3. RelayBinaryExtractor（嵌入资源解压）

### 3.1 嵌入声明（`StartTooler.csproj:26-33`）

```xml
<EmbeddedResource Include="Resources\relay-binaries\upload-relay-linux-amd64">
  <LogicalName>StartTooler.Resources.relay-binaries.upload-relay-linux-amd64</LogicalName>
  <CopyToOutputDirectory>Never</CopyToOutputDirectory>
</EmbeddedResource>
```

**不**复制到 publish 输出（已经是 dll 内部资源），逻辑名固定 prefix `StartTooler.Resources.relay-binaries.upload-relay-linux-{arch}`。

### 3.2 解压流程（`RelayBinaryExtractor.cs:37-63`）

```csharp
public static string Extract(string arch) {
    if (string.IsNullOrEmpty(arch) || !SupportedArchs.Contains(arch))
        throw new ArgumentException($"unsupported arch: '{arch}'. Supported: {string.Join(", ", SupportedArchs)}");
    var dest = Path.Combine(_extractDir, $"upload-relay-linux-{arch}");

    lock (_lock) {
        var resourceName = ResourcePrefix + $"upload-relay-linux-{arch}";
        var assembly = typeof(RelayBinaryExtractor).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) throw new InvalidOperationException($"Embedded relay binary not found: '{resourceName}'. ...");

        using var fs = File.Create(dest);   // File.Create truncate = 总是覆盖
        stream.CopyTo(fs);
    }

    TryChmod755(dest);
    return dest;
}
```

#### 3.2.1 关键设计决策

- **总是覆盖**（`File.Create` 截断已有文件）：嵌入资源每次 build 都可能变，scp 出去必须是最新版。二进制 ~6MB，解压 <50ms
- **解压到 `{Temp}/starttooler/upload-relay-linux-{arch}`**：跨进程稳定位置，多次 Extract 同一个 arch 幂等
- **`chmod 0755`**：跨平台 — `OperatingSystem.IsLinux/IsMacOS()` 时 `File.SetUnixFileMode`；Windows skip
- **进程级 lock**：并发 Extract 调用不冲突（虽然实际只一处用）

### 3.3 检测嵌入资源（debug）

```csharp
public static string[] AvailableArchs() {
    return typeof(RelayBinaryExtractor).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix))
        .Select(n => n.Substring(ResourcePrefix.Length))
        .ToArray();
}
```

测「嵌入资源是否真在 dll 里」用。

---

## 4. SSH 工具（`PublicRelayService.BuildConnectionInfo` + `RunSshAsync`）

```csharp
private static ConnectionInfo BuildConnectionInfo(PublicRelayConfig cfg) {
    AuthenticationMethod method;
    if (!string.IsNullOrEmpty(cfg.SshKeyPath))
        method = new PrivateKeyAuthenticationMethod(cfg.SshUser, new PrivateKeyFile(cfg.SshKeyPath));
    else
        method = new PasswordAuthenticationMethod(cfg.SshUser, cfg.SshPassword ?? "");
    return new ConnectionInfo(cfg.SshHost, cfg.SshPort, cfg.SshUser, method) {
        Timeout = TimeSpan.FromSeconds(10),
    };
}
```

### 4.1 部署（`DeployAsync` — `PublicRelayService.cs:100-142`）

```
DeployAsync(cfg, arch, log, ct)
  ├─ SetState(Deploying)
  ├─ RunSshAsync(cfg, "mkdir -p {remotePath}")
  ├─ localBin = RelayBinaryExtractor.Extract(arch)        // 从嵌入资源拉
  ├─ log "local binary: {localBin} (size KB)"
  ├─ ScpClient.Connect()
  ├─ remoteBin = {remotePath}/upload-relay-linux-{arch}
  ├─ scp.Upload(File.OpenRead(localBin), remoteBin)
  ├─ RunSshAsync(cfg, "chmod +x {remoteBin}")
  ├─ RunSshAsync(cfg, "rm -f {remotePath}/upload_relay.py {remotePath}/upload.html")    ← 清理早期 Python 实现
  └─ SetState(... 但不切到 Running，停在这里等 Start)
```

**注意**：早期有 Python 版（commit history 已删）—— `rm -f upload_relay.py upload.html` 是迁移期清理。新部署环境无残留，命令 `rm -f` 不报错。

### 4.2 启动（`StartRemoteAsync` — `PublicRelayService.cs:144-177`）

```
StartRemoteAsync(cfg, arch, log, ct)
  ├─ SetState(Starting)
  ├─ tmpDir  = {remotePath}/tmp
  ├─ pidFile = {remotePath}/relay.pid
  ├─ logFile = {remotePath}/relay.log
  ├─ RunSshAsync(cfg, "mkdir -p {tmpDir}")
  ├─ RunSshAsync(cfg, "if [ -f {pidFile} ]; then PID=$(cat {pidFile}); kill $PID 2>/dev/null || true; rm -f {pidFile}; fi")  ← 清理残留
  ├─ cmd = setsid nohup {remoteBin}
  │          --http-port {HttpPort} --tcp-port {TcpPort} --tmp-dir {tmpDir}
  │          < /dev/null > {logFile} 2>&1 & echo $! > {pidFile}
  ├─ RunSshAsync(cfg, cmd)
  ├─ await Task.Delay(500)  ← 等进程起来
  └─ Read pidFile for log message
```

#### 4.2.1 关键启动技巧

- `setsid`：开新 session，防 SSH 退出 SIGHUP 把进程杀
- `nohup`：兜底，但 `setsid` 已足
- `< /dev/null > log 2>&1`：stdin 关掉，stdout/stderr 走 log 文件
- `& echo $! > pidFile`：取后台进程 PID
- `Task.Delay(500)`：让进程有机会 fork + 写完 PID 文件

### 4.3 停止（`StopRemoteAsync`）

```bash
if [ -f {pidFile} ]; then PID=$(cat {pidFile}); kill $PID && echo killed; rm -f {pidFile}; else echo no pid file; fi
```

`SIGTERM` → Go `signal.Notify(SIGTERM)` 优雅退出。`IsRemoteRunningAsync` 用 `kill -0 $PID` 判进程在不在（不发信号，只探测）。

---

## 5. TCP 通知客户端 + Poller 异步下载（v0.3 重构：pull 模型）

> **v0.3 重构**：从 push 模型（TCP → channel → 凑批/idle → scp）改为 pull 模型（TCP → notify + 入库 → Poller 定期查 db → scp）。
>
> 核心变化：`sync_for_vps_task` 表成为 single source of truth。TCP 通知仅入库不下载，下载完全异步、跨进程、崩溃可恢复。

### 5.1 协议契约

```
C# 端 TCP 客户端 ←→ Go relay :8766

C# ─── (no request) ───→ Go
C# ←── {"type":"file_pending","id":"...","name":"...","size":N}\n ─── Go
C# 内部: JSON parse → NotificationService.Show "📥 待下载 {name}"
              └→ InsertIfNewAsync(fileId, name, size, remotePath, status=Pending)
                 （UNIQUE(fileId) 幂等；已存在则 noop）
              └→ RefreshPendingCountAsync → UI 更新待下载计数
                 ↓
[Poller 后台，每 SyncPollIntervalSec=5s 触发一次]
   SELECT * FROM sync_for_vps_task WHERE status=Pending
   ORDER BY created_at ASC LIMIT SyncBatchSize=5
                 ↓
   DrainBatchAsync(cfg, projectPath, batch, ct)
      ├─ Process.Start("scp", ...一次传 N 个文件...) → {today}/
      ├─ 解析 exit code + stderr → failedIds
      ├─ 成功：MarkReceivedAsync(id, local_path), NotificationService.Show "已下载"
      ├─ 失败：MarkFailedAsync(id, stderr), NotificationService.Show "N 个文件失败"
      │       **v0.3 不重试**：标 Failed 后等待用户手动处理
      ├─ SSH rm VPS tmp/（仅成功的）
      └─ 每成功文件 Task.Run(AckFileAsync)（HTTP POST /ack/{id}）
```

- **单行 JSON + `'\n'`** —— 没有 4 字节长度前缀
- C# 端 `ReadLineAsync` 按字节读到 `\n`（行尾 `\r` 去 strip）
- **v0.3 双通知**：
  - 收到通知时：UI notify "📥 待下载 file.jpg"（写入 Pending 行后立即触发）
  - 下载完成时：UI notify "✅ 已下载 file.jpg"（Poller MarkReceived 后触发）

### 5.2 TCP 客户端重连策略

```csharp
int retryDelaySec = 1;
while (!ct.IsCancellationRequested) {
    try {
        using var client = new TcpClient();
        await client.ConnectAsync(host, cfg.TcpPort, ct);
        using var stream = client.GetStream();
        retryDelaySec = 1;
        LastLog = $"已连接 {host}:{cfg.TcpPort}";

        while (!ct.IsCancellationRequested) {
            string? line;
            try { line = await ReadLineAsync(stream, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                Trace.WriteLine($"[PublicRelay] read line err: {ex.Message}");
                break;
            }
            if (line == null) break;
            line = line.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            
            // v0.3: 解析 → OnFilePendingReceived (notify + InsertIfNew)
            await OnFilePendingReceivedAsync(fileId, name, size, ct);
        }
    } catch (OperationCanceledException) {
        return;
    }
    catch (Exception ex) {
        LastLog = $"连接断开: {ex.Message}";
        // 注意：v0.3 不再 flush channel（已删）；Pending 行本来就在 db 里，Poller 会继续处理
    }
    if (!ct.IsCancellationRequested) {
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryDelaySec, 10)), ct);
        retryDelaySec = Math.Min(retryDelaySec * 2, 10);
    }
}
```

#### 5.2.1 关键设计

- **指数退避**：1s 起步，封顶 10s —— 正常 VPS 短闪断秒重连，长期断网（VPS 关停）也只 10s 一试
- **每次成功重置 retryDelaySec=1**：上线后立即赶上突发流量
- **TCP 断线不再 flush**（v0.3）：Pending 行本来就在 db 里，Poller 在跑就继续处理；Poller 没跑 = 进程没启
- **`ReadLineAsync` 1MB 上限**：防恶意 peer 一直不换行撑爆内存
- **TCP 接收与下载解耦**：TCP 重连期间不影响 Poller 拉取已有的 Pending 行

### 5.3 JSON 解析

```csharp
using var doc = JsonDocument.Parse(line);
if (doc.RootElement.GetProperty("type").GetString() != "file_pending") continue;
fileId = doc.RootElement.GetProperty("id").GetString() ?? "";
name   = doc.RootElement.GetProperty("name").GetString() ?? "";
size   = doc.RootElement.GetProperty("size").GetInt64();
```

> 当前 Go 端**只**发 `file_pending` 类型，未来加 ping/pong / state 同步时这里要加分支（目前 `continue` 即可）。

### 5.4 Poller（v0.3 新增，替代 BatchWorker）

**字段**：
```csharp
public int SyncPollIntervalSec { get; set; } = 5;    // poll 周期
public int SyncBatchSize { get; set; } = 5;          // 每次 scp 最多拉的文件数
```

**触发策略**：
- 启动时立即跑一次（避免 5s 等待 + 处理上次进程崩溃遗留的 Pending）
- 之后每 `SyncPollIntervalSec` 秒跑一次（`PeriodicTimer`）
- ct 取消时退出

**Worker 主循环**：
```csharp
private async Task RunPollerLoopAsync(PublicRelayConfig cfg, string projectPath, CancellationToken ct)
{
    var interval = TimeSpan.FromSeconds(cfg.SyncPollIntervalSec > 0 ? cfg.SyncPollIntervalSec : 5);
    var batchSize = cfg.SyncBatchSize > 0 ? cfg.SyncBatchSize : 5;

    // 启动立即跑一次
    await PollOnceAsync(cfg, projectPath, batchSize, ct);

    using var timer = new PeriodicTimer(interval);
    try
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            await PollOnceAsync(cfg, projectPath, batchSize, ct);
        }
    }
    catch (OperationCanceledException) { /* 正常退出 */ }
}

private async Task PollOnceAsync(PublicRelayConfig cfg, string projectPath, int batchSize, CancellationToken ct)
{
    var pending = await _syncTaskRepo.GetPendingBatchAsync(batchSize, ct);
    if (pending.Count == 0)
    {
        await RefreshPendingCountAsync(ct);
        return;
    }
    Trace.WriteLine($"[PublicRelay] poll tick: {pending.Count} pending");
    await DrainBatchAsync(cfg, projectPath, pending, ct);
    await RefreshPendingCountAsync(ct);
}
```

#### 5.4.1 关键设计

- **db 是 single source of truth**：TCP 通知入库即落地，Poller 异步消费
- **ORDER BY created_at ASC LIMIT N**：先到先下载（FIFO）
- **进程崩溃兜底（v0.3 实现）**：未下载的 Pending 行留在 db 里，下次启动 Poller 立即跑一次拉取
- **失败不重试**：v0.3 简化决策，MarkFailed 后等待用户手动处理（v0.4 计划加 retry UI）
- **每次 tick 结束都 RefreshPendingCountAsync**：UI 上"待下载 N 个"实时刷新

### 5.5 Stop / 退出 / 进程崩溃

**StopClientAsync**（v0.3 简化）：
```csharp
public async Task StopClientAsync()
{
    _clientCts?.Cancel();              // TCP loop + Poller 都退出
    if (_clientTask != null) await _clientTask;
    if (_pollerTask != null) await _pollerTask;
    _clientCts?.Dispose();
    // 无需 flush channel（已删）；Pending 行留在 db，下次启动自动补拉
}
```

**进程崩溃兜底（v0.3 ✅ 实现）**：
- Poller 退出 = 停止读取 db Pending 行
- 但 Pending 行还在 db 里（TCP 已 InsertIfNewAsync 落地）
- 下次启动 → StartClient → Poller 立即跑一次 → 拉取所有遗留 Pending → DrainBatchAsync
- **零数据丢失**

**ProcessExit 兜底**：
```csharp
// MainWindowViewModel.cs
AppDomain.CurrentDomain.ProcessExit += (s, e) => {
    try {
        UploadServerViewModel?.PublicRelayViewModel
            ?.EnsureRemoteKilledOnExitAsync()
            .Wait(TimeSpan.FromSeconds(5));
    } catch { /* ignore */ }
};
```

> 注意：进程崩溃（kill -9 / 断电）时 ProcessExit handler 不一定跑得到。但因为 Poller 启动立刻跑一次，崩溃遗留的 Pending 仍然能被下次启动消化。

### 5.6 `DrainBatchAsync`（v0.3：从 db 读取，DB 是 single source of truth）

```
DrainBatchAsync(cfg, projectPath, batch, ct)
  ├─ today = DateTime.Now.ToString("yyyy-MM-dd")
  ├─ dateDir = Path.Combine(projectPath, today)
  ├─ Directory.CreateDirectory(dateDir)
  ├─ 构造 scp 命令参数（包含 SSH ControlMaster）：
  │   scp -P {port} -o StrictHostKeyChecking=accept-new
  │       -o ConnectTimeout=10
  │       -o ControlMaster=auto
  │       -o ControlPath={Temp}/starttooler-ssh-{host}-{port}
  │       -o ControlPersist=10m
  │       {user}@{host}:{t.RemotePath}     ← 直接用 db 的 RemotePath
  │       ...
  │       {dateDir}/
  ├─ Process.Start("scp", ...).WaitForExitAsync (5min hard timeout)
  │   finally: 若 proc 还活着 → Kill 防孤儿
  ├─ 解析 exit code + stderr → failedIds（按 db 行的 FileId 拆解）
  ├─ 成功文件：MarkReceivedAsync(id, local_path)
  │           + NotificationService.Show "已下载 {name}"
  │           + FileReceived?.Invoke(local_path) → Gallery 刷新
  ├─ 失败文件：MarkFailedAsync(id, stderr片段)
  │           **v0.3 不重试**
  ├─ 失败 >0：UI notify "N 个文件失败：name1、name2..."
  ├─ SSH rm VPS tmp/（仅成功的，批量化）
  └─ 每成功文件 Task.Run(AckFileAsync)（HTTP POST /ack/{id}）
```

**关键代码骨架**：
```csharp
private async Task DrainBatchAsync(
    PublicRelayConfig cfg, string projectPath,
    IReadOnlyList<SyncForVpsTask> batch, CancellationToken ct)
{
    var today = DateTime.Now.ToString("yyyy-MM-dd");
    var dateDir = Path.Combine(projectPath, today);
    Directory.CreateDirectory(dateDir);

    var psi = new ProcessStartInfo("scp") { /* redirect */ };
    // ... ControlMaster + ssh opts ...

    foreach (var t in batch)
    {
        // 优先用 db 的 RemotePath；fallback 拼路径
        var remoteBin = !string.IsNullOrEmpty(t.RemotePath)
            ? $"{cfg.SshUser}@{cfg.SshHost}:{t.RemotePath}"
            : $"{cfg.SshUser}@{cfg.SshHost}:{ExpandRemotePath(cfg.SshRemotePath)}/tmp/{t.FileName}";
        psi.ArgumentList.Add(remoteBin);
    }
    psi.ArgumentList.Add(dateDir + Path.DirectorySeparatorChar);

    var sw = Stopwatch.StartNew();
    Process? proc = null;
    string stderr = "";
    try
    {
        // GUI 进程无 TTY：密码登录走 SSH_ASKPASS
        if (string.IsNullOrEmpty(cfg.SshKeyPath) && !string.IsNullOrEmpty(cfg.SshPassword))
        {
            psi.Environment["SSH_ASKPASS"] = GetAskpassScriptPath();
            psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            psi.Environment["DISPLAY"] = ":0";
            psi.Environment["STARTTOOLER_SSHPASS"] = cfg.SshPassword;
        }

        proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
        await proc.WaitForExitAsync(timeoutCts.Token);

        stderr = await stderrTask;
        _ = await stdoutTask;
    }
    catch (Exception ex) { /* mark all Failed */ }
    finally
    {
        // 关键：scp 没正常退出时显式 Kill，避免 ControlMaster socket 缓存毒化
        try { if (proc != null && !proc.HasExited) { proc.Kill(true); Trace.WriteLine("[PublicRelay] scp killed (timeout)"); } } catch { }
        proc?.Dispose();
    }

    var failedIds = ParseScpStderr(stderr, batch);
    var successIds = batch.Where(t => !failedIds.Contains(t.FileId)).ToList();
    var failedList = batch.Where(t => failedIds.Contains(t.FileId)).ToList();

    // 成功
    foreach (var t in successIds)
    {
        var localPath = Path.Combine(dateDir, SanitizeFileName(t.FileName));
        await _syncTaskRepo.MarkReceivedAsync(t.Id, localPath, ct);
        LastLog = $"✅ 已下载：{t.FileName}";
        NotificationService.Current.Show("公网接收", $"已下载 {t.FileName}");
        FileReceived?.Invoke(localPath);
    }

    // 失败（v0.3 不重试）
    foreach (var t in failedList)
    {
        await _syncTaskRepo.MarkFailedAsync(t.Id, ExtractScpErrorFor(stderr, t.FileId) ?? "scp failed", ct);
    }
    if (failedList.Count > 0)
    {
        var names = string.Join("、", failedList.Select(t => t.FileName));
        NotificationService.Current.Show("公网接收失败", $"{failedList.Count} 个文件下载失败：{names}", NotificationType.Error);
    }

    // 批量 rm + ack
    // ...
}
```

#### 5.6.1 关键设计

- **不用 SSH.NET 的 ScpClient**：ScpClient 是单文件 channel，每次新连接，浪费；本地 `scp` 命令行天然支持多文件 + multiplex
- **SSH ControlMaster**：多次 batch 复用单 SSH 连接（`-o ControlMaster=auto -o ControlPath=... -o ControlPersist=10m`）；首次启动要 SSH 握手，后续 batch 直接复用 socket，零握手开销
- **scp stderr 解析**：识别 `scp: .../tmp/{filename}: <reason>` 行拆出失败文件 ID（v0.4+ VPS 用原文件名落盘）
- **scp 超时显式 Kill**：5min timeout 触发后 `proc.Kill(true)`，避免 ControlMaster socket 缓存一个已死的子进程
- **rm 阶段批量化**：一次 SSH.NET `RunCommand("rm -f a b c d e")`
- **ack 不批量化**：HTTP POST /ack 仍然每文件一次
- **v0.3 失败不重试**：MarkFailed 后等待用户手动处理（v0.4 计划加 retry UI）

### 5.7 `FetchOneAsync`（v0.1 旧实现，已废弃）

> **v0.2 已删除**。旧实现见 git history（commit `c91db80` 之前）。

### 5.8 v0.2 BatchWorker（已废弃）

> **v0.3 已删除**。v0.2 的 push 模型（Channel + BatchWorker + idle timer）整体被 Poller 取代。详见 git history。

### 5.9 PublicRelayConfig 新增字段（v0.3）

```csharp
public class PublicRelayConfig {
    // ... 原有字段 ...

    public int SyncPollIntervalSec { get; set; } = 5;   // Poller 周期（秒）
    public int SyncBatchSize { get; set; } = 5;         // 每次 scp 最多拉的文件数
}
```

UI 暂不暴露（v0.3 沿用默认值；v0.4 加 UI 配置项）。

## 6. Go relay（`tools/upload-relay/main.go`）

### 6.1 入口与 flag

```go
func main() {
    httpPort := flag.Int("http-port", 8765, "HTTP server port")
    tcpPort  := flag.Int("tcp-port", 8766, "TCP server port (file_pending notify)")
    tmpDir   := flag.String("tmp-dir", "", "temp file directory (required)")
    showVer  := flag.Bool("version", false, "print version and exit")
    flag.Parse()
    if *showVer { ... }
    if *tmpDir == "" { log.Fatal("--tmp-dir is required") }
    if err := os.MkdirAll(*tmpDir, 0755); err != nil { log.Fatalf("...") }

    state := newState(*tmpDir)
    httpState = state                                  // 给 HTTP handler 用
    go runHTTP(*httpPort, indexHTML)
    go runTCP(*tcpPort, state)

    sig := make(chan os.Signal, 1)
    signal.Notify(sig, syscall.SIGTERM, syscall.SIGINT)
    sigRecv := <-sig
    log.Printf("received signal %v, shutting down", sigRecv)
}
```

### 6.2 共享 state（`type State`）

```go
type State struct {
    mu          sync.Mutex
    tmpDir      string
    pending     map[string]pendingFile
    startedAt   time.Time
    subMu       sync.Mutex
    subscribers []chan pendingFile
}
```

- `pending` map：暂存 `{ID, Name, Path}` —— 用于 `ReplayPending`
- `subscribers` slice of channels：TCP 已连接的客户端订阅
- 双锁分离：`mu` 保护 pending map；`subMu` 保护 subscribers slice —— 减少锁竞争

### 6.3 上传接收（`handleUpload` — `main.go:304-357`）

```go
if err := r.ParseMultipartForm(32 << 20); err != nil { ... }    // 32MB threshold spill to file
files := r.MultipartForm.File["file"]                           // 固定 part name "file"
if len(files) == 0 {
    for _, v := range r.MultipartForm.File { files = append(files, v...) }
}

for _, fh := range files {
    f, _ := fh.Open()
    data, _ := io.ReadAll(f)                                    // 整体读到内存（跟 Python 版语义一致）
    f.Close()
    name := filepath.Base(fh.Filename)
    if name == "" || name == "." || name == "/" {
        name = fmt.Sprintf("upload_%s.bin", time.Now().Format("150405"))
    }
    state.saveUploaded(name, data)
}
```

**注意**：32MB threshold for `ParseMultipartForm` —— 超过 spill to disk；之后 `io.ReadAll` 整体读内存（与 Python 版行为一致）。视频文件可能几百 MB，**会被读到内存里**——v0.2 应改 streaming。

### 6.4 saveUploaded + Broadcast（`main.go:85-130`）

**v0.4+ 改动**：磁盘直接存原始文件名（`tmp/{sanitized_name}`），不再写 `<id>.bin` / `<id>.meta`。`id` 仍生成并放进 pending map 用于 ack / broadcast。

```go
func (s *State) saveUploaded(filename string, data []byte) (string, error) {
    id := newID()                                                                     // 32 hex
    safeName := sanitizeFilename(filename)                                            // 防 ../ + 控制字符 + 限长 200
    if safeName == "" {
        safeName = fmt.Sprintf("upload_%s.bin", time.Now().Format("150405"))           // 兜底
    }
    binPath := filepath.Join(s.tmpDir, safeName)
    if _, err := os.Stat(binPath); err == nil {                                        // 同名碰撞
        ext := filepath.Ext(safeName); stem := safeName[:len(safeName)-len(ext)]
        for i := 2; ; i++ {
            candidate := filepath.Join(s.tmpDir, fmt.Sprintf("%s_%d%s", stem, i, ext))
            if _, err := os.Stat(candidate); os.IsNotExist(err) { binPath = candidate; break }
        }
    }
    if err := os.WriteFile(binPath, data, 0644); err != nil { return "", ... }
    s.mu.Lock()
    s.pending[id] = pendingFile{ID: id, Name: filename, Path: binPath}
    s.mu.Unlock()
    s.Broadcast(pendingFile{ID: id, Name: filename, Path: binPath})                   // 推所有 client
    return id, nil
}

func (s *State) Broadcast(p pendingFile) {                                           // 满了丢
    s.subMu.Lock(); defer s.subMu.Unlock()
    for _, ch := range s.subscribers {
        select { case ch <- p: default: }
    }
}
```

- `newID()`: 16 字节随机 → 32 字符 hex（与 Python 版 `uuid.uuid4().hex` 完全兼容）
- `Broadcast` 满了丢：单 client + 16 buffer，正常场景不会满

### 6.5 ACK 端点（`/ack/{id}`）

```go
mux.HandleFunc("/ack/", func(w http.ResponseWriter, r *http.Request) {
    if r.Method != http.MethodPost { ... }
    id := strings.TrimPrefix(r.URL.Path, "/ack/")
    if id == "" || strings.Contains(id, "/") { ... }
    if err := state.Ack(id); err != nil {
        http.Error(w, err.Error(), http.StatusNotFound); return
    }
    _, _ = io.WriteString(w, `{"ok":true}` + "\n")
})
```

`Ack`：`delete(pending, id)` + `os.Remove(p.Path)`（v0.4+ 删的就是原始文件名；不再写 .meta 所以也不删 .meta；文件不存在时 `os.IsNotExist` 不算错）。

### 6.6 TCP server（`main.go:363-442`）

```go
ln, _ := net.Listen("tcp", fmt.Sprintf(":%d", port))
defer ln.Close()

for {
    conn, _ := ln.Accept()
    if tcpConn, ok := conn.(*net.TCPConn); ok {
        _ = tcpConn.SetKeepAlive(true)
        _ = tcpConn.SetKeepAlivePeriod(30 * time.Second)         // 防 NAT 老化
    }
    go handleTCPClient(conn, state)
}
```

#### 6.6.1 `handleTCPClient` 关键

```go
defer conn.Close()
ch := state.Subscribe()
defer state.Unsubscribe(ch)

state.ReplayPending(ch)                                           // 新连接补发当前所有 pending

// 单独 goroutine 跑 read：仅用于消费 client 消息（暂时忽略），保持 TCP 活跃
readErr := make(chan error, 1)
go func() {
    buf := make([]byte, 1024)
    for {
        conn.SetReadDeadline(time.Now().Add(180 * time.Second))  // 3 分钟 idle 超时
        _, err := conn.Read(buf)
        if err != nil { readErr <- err; return }
    }
}()

for {
    select {
    case err := <-readErr:
        ...    // 上报 idle timeout / conn closed
        return
    case p, ok := <-ch:
        if !ok { ... return }
        stat, _ := os.Stat(p.Path)
        size := int64(0); if stat == nil { size = stat.Size() }
        msg := fmt.Sprintf(
            `{"type":"file_pending","id":"%s","name":"%s","size":%d}`+"\n",
            p.ID, p.Name, size)
        conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
        if _, err := conn.Write([]byte(msg)); err != nil { ...; return }
    }
}
```

#### 6.6.2 重要设计

- **`ReplayPending` 修复断线漏掉**（`main.go:174-186`）：client 断线期间上传的文件，重连后会重新收到通知（client 来不及消费 16 buffer 满就丢，但 pending map 还在，client 端 ack 失败可重试）
- **TCP read goroutine**：仅用于检测连接活性（180s idle timeout），不解析消息
- **`conn.SetReadDeadline` 不保持更新 180s**：v1.0 只设一次；极限 idle 180s 会断，C# 端在 retry loop 立刻重连

### 6.7 优雅退出（`main()` 末尾）

```go
sig := make(chan os.Signal, 1)
signal.Notify(sig, syscall.SIGTERM, syscall.SIGINT)
sigRecv := <-sig
log.Printf("received signal %v, shutting down", sigRecv)
```

`ln.Close()` defer 在 `runTCP` return 时触发；HTTP `http.ListenAndServe` 不响应 SIGTERM（已知），重启会被 SSH kill 强杀。

**注意**：`http.Server` 没 `Shutdown` 调用（`runHTTP:214-285`），主进程退出由 deferred `ln.Close` 触发，**ListenAndServe 仍在阻塞**：SSH `kill <pid>` 直接终止 Go 进程，HTTP handler 中进行中的请求会**截断** —— 实测 publish 文件时 NetworkError 是常见表现，对一次性长传用户能感知。

---

## 7. C# 端 PublicRelayViewModel（`ViewModels/PublicRelayViewModel.cs`）

### 7.1 字段

| 分组 | 字段 |
|---|---|
| **配置** | `SshHost / SshPort / SshUser / SshPassword / SshKeyPath / SshRemotePath / HttpPort / TcpPort / PublicHost / RemoteArch` |
| **认证** | `AuthMethod { Password, Key }`, `ShowPasswordFields`, `ShowKeyFields`, `AuthMethodIndex` |
| **状态** | `IsDirty / IsBusy / StatusMessage / LastError / LastLog / IsProjectPathSet / RelayStateText` |

`RemoteArchIndex`：ComboBox 用，0=Auto, 1=amd64, 2=arm64。

### 7.2 快照 + dirty

```csharp
private PublicRelayConfig? _lastSaved;
private bool _isInitialized;

private void RecomputeDirty() {
    if (!_isInitialized || _lastSaved == null) return;
    var current = BuildConfigFromVm();
    var s = _lastSaved;
    var dirty = current.SshHost != s.SshHost
              || current.SshPort != s.SshPort
              || ...
              || current.RemoteArch != s.RemoteArch;
    if (IsDirty != dirty) IsDirty = dirty;
}
```

所有字段 `On*Changed(string value) { if (_isInitialized) RecomputeDirty(); }`。

### 7.3 命令

| Command | CanExecute | 行为 |
|---|---|---|
| `BrowseKey` | always | `IFilePickerService.PickFileAsync("选择 SSH 私钥文件", null)` |
| `Save` | always | 写到 ConfigDb |
| `Deploy` | `!IsBusy && IsProjectPathSet` | `ResolveArch → DeployAsync` |
| `Start` | `!IsBusy && IsProjectPathSet` | `ResolveArch → DeployAsync (best-effort) → StartRemoteAsync → StartClient` |
| `Stop` | `!IsBusy && RelayState.Running` | `StopClientAsync → StopRemoteAsync` |

#### 7.3.1 `Start` 流程（关键路径）

```
StartCommand
  └─ RunWithBusyAsync("启动中...")
        ├─ cfg = BuildConfigFromVm()
        ├─ progress = new Progress<string>(s => LastLog = s)
        ├─ arch = await _relayService.ResolveArchAsync(cfg, progress, default)
        ├─ try { await _relayService.DeployAsync(cfg, arch, progress, default); }
        │     catch (Exception ex) { LastLog = "部署步骤失败: " + ex.Message; }   ← 容错：允许 VPS 上已有二进制
        ├─ await _relayService.StartRemoteAsync(cfg, arch, progress, default)
        ├─ _relayService.StartClient(cfg, _gallery.ProjectPath ?? "")
        └─ StatusMessage = "公网代理已启动"
```

### 7.4 退出兜底

`MainWindowViewModel.cs:78-90`：

```csharp
AppDomain.CurrentDomain.ProcessExit += (s, e) => {
    try {
        UploadServerViewModel?.PublicRelayViewModel
            ?.EnsureRemoteKilledOnExitAsync()
            .Wait(TimeSpan.FromSeconds(5));
    } catch { /* ignore */ }
};
```

`PublicRelayViewModel.EnsureRemoteKilledOnExitAsync`：
```csharp
public async Task EnsureRemoteKilledOnExitAsync() {
    try {
        await _relayService.StopClientAsync();
        var cfg = await _configService.GetAsync<PublicRelayConfig>(ConfigKeys.PublicRelay);
        if (cfg == null || string.IsNullOrEmpty(cfg.SshHost)) return;
        await _relayService.StopRemoteAsync(cfg, null, default);
    } catch (Exception ex) {
        Trace.WriteLine($"[PublicRelay] exit kill failed: {ex.Message}");
    }
}
```

**5s 超时**（`MainWindowViewModel.cs:84`）：进程退出后随时被 SIGKILL，wait 不能太久。

---

## 8. End-to-End 流程（手机扫码上传 → 本地项目目录）

```
[1] 用户 StartTooler 上点 Start
    PublicRelayService.StartRemoteAsync → SSH → setsid nohup relay &
    PublicRelayService.StartClient → TCP 连 localhost:8766 (or public)

[2] 用户手机扫码 → http://vps:8765/upload → browser GET → HTML 上传页
    （HTML 里的 {{STARTOOLER_BASE}} 被 Go 用 r.Host 替换为 http://vps:8765）

[3] 手机选文件 + 点上传
    fetch(POST http://vps:8765/upload, multipart "file"=<binary>)
    Go handleUpload:
       ├─ saveUploaded: tmp/{sanitized_original_name}        (v0.4+ 直接存原文件名)
       ├─ state.Broadcast: fan-out 给已连 TCP clients
       └─ return 200 {"success":true,"count":1}

[4] Go handleTCPClient:
       ch <- pendingFile → 写 JSON line + \n
    C# ReadLineAsync → JSON parse → file_id, name, size
    C# OnFilePendingReceived:
       ├─ NotificationService.Show("📥 待下载 file.jpg")
       └─ InsertIfNewAsync(fileId, name, size, remotePath, status=Pending)
                                     ← v0.3: 立即入库（db 是 single source of truth）
       ↓
    RunPollerLoopAsync（独立后台 task，每 5s 触发一次 + 启动立即跑）：
       ├─ GetPendingBatchAsync(5) ORDER BY created_at ASC
       ├─ DrainBatchAsync:
       │   ├─ Process.Start("scp", ... 一次传 N 个文件 ...)
       │   │   -o ControlMaster=auto + ControlPath={Temp}/starttooler-ssh-...
       │   │   user@host:tmp/<name1> ... user@host:tmp/<nameN> → {today}/      (v0.4+ 用原始文件名)
       │   ├─ 解析 exit code + stderr → failedIds
       │   ├─ 成功文件 MarkReceivedAsync(id, local_path) + NotificationService.Show("已下载")
       │   ├─ 失败文件 MarkFailedAsync(id, stderr)  ← v0.3 不重试
       │   ├─ 失败 >0 时 NotificationService.Show("N 个文件下载失败：name1、name2...")
       │   ├─ SSH.NET RunCommand("rm -f tmp/{成功name1} tmp/{成功name2} ...")   (v0.4+ 用原始文件名)
       │   └─ 每成功文件 Task.Run(AckFileAsync)（HTTP POST /ack/{id}）
       └─ FileReceived?.Invoke(local_path)         ← 订阅者刷新 Gallery

[5] 用户回到 StartTooler：
    Gallery 自动刷新（订阅了 FileReceived，待启用）
    右下角看到通知（**两次**：收到通知时 + 下载完成时）
    项目目录 yyyy-MM-dd/ 下有文件
    sync_for_vps_task 表中对应行：status=Received, local_path=<本地路径>, AttemptCount=1
    （进程崩溃 / 重启后 Pending 行由 Poller 启动立即补拉）
    sync_for_vps_task 表有持久化记录
```

#### 8.1 流程演进（v0.1 → v0.2 → v0.3）

| 维度 | v0.1（单文件 FetchOneAsync）| v0.2（push: Channel + BatchWorker）| v0.3（pull: db + Poller） |
|---|---|---|---|
| 100 张图触发 SSH 握手次数 | 300 次（每文件 3 次：SshClient + ScpClient + rm） | 20 次（每 batch 1 次）+ 后续 ControlMaster 复用 | 20 次（同 v0.2） |
| 100 张图触发 HTTP POST | 100 次（每文件 ack） | 100 次（不变） | 100 次（不变） |
| 触发 sshd MaxStartups 概率 | 高（300 并发 SSH connect）| 低（20 次 scp + ControlMaster 复用 socket） | 同 v0.2 |
| 链路慢导致失败 | 频繁（单文件 10s Timeout 太短）| 缓解（一次 scp 拉多个）| 同 v0.2 |
| 失败粒度 | 单文件 | 单文件（解析 scp stderr 拆失败 ID）| 单文件 |
| 持久化记录 | 无 | scp 完成后写 sync_for_vps_task | TCP 通知时立即写（single source of truth）|
| 进程崩溃兜底 | 完全丢失 | 接受丢失 | **db 行留存，下次启动 Poller 立即补拉** |
| UI notify 时机 | scp 完成时 | scp 完成时 | **TCP 通知时 + scp 完成时（双通知）** |
| 凑批/idle timer 复杂度 | 无（单文件）| 高（channel + idle + flush）| 无（Poller 直接查 db） |

---

## 9. 修改这个模块前的检查清单

| 改动 | 涉及 | 验证 |
|---|---|---|
| 改协议（加新 JSON type）| Go `state.Broadcast` + C# `JSON parse` | 一端加一端不识别会丢消息 |
| 改 C# 端口/PATH 默认 | `PublicRelayConfig.HttpPort / TcpPort` | 现有用户配置可能不变 → 加 migration |
| 改 SSH 超时 | `PublicRelayService.BuildConnectionInfo.Timeout`（已 10s → 60s） | 长 ssh 命令下用户感知 |
| 加 TLS/SSL | Go + C# 双端加证书 | 拒绝降级——明文 HTTP risk surface |
| 改流式上传 | Go 当前 `io.ReadAll` | 改 streaming write file，内存峰值 |
| 改 binary 嵌入 | `StartTooler.csproj` + `RelayBinaryExtractor.cs` | 检查 `GetManifestResourceNames` |
| 加架构（riscv64） | `RelayBinaryExtractor.SupportedArchs` + `scripts/build-relay.{sh,ps1}` | `GOARCH=riscv64` 试 build |
| Start 时自动 ResolveArch | 已存在，不改 | 若失败 → 整 Start 链失败 |
| 退出兜底加 SSH 前置确认（让用户决定 kill 远端）| `AppDomain.ProcessExit` 不能弹框 | 不适合本场景 |
| **改 Poller 周期 / batch 大小** | `PublicRelayConfig.SyncPollIntervalSec / SyncBatchSize` | 边界测试：1 张/999 张/Stop 中断 |
| **改 scp 调用方式**（本地 scp → SftpClient 等） | `DrainBatchAsync` + 跨平台依赖 | Windows 老版本无 scp 兜底 |
| **加 Failed 重试 UI** | 新 ViewModel Command | v0.3 暂不重试；v0.4 加 |
| **加 sync_for_vps_task UI 展示** | 新 View + ViewModel | 用户能看哪些文件 pending / failed |

---

## 10. 已知陷阱（详见 `10-trap-book.md`）

- **TCP JSON line 解析不带长度前缀** —— 早期 C# 端按 4 字节大端读，新版本已改 `ReadLineAsync`（见 commit history）
- **`SSH.SSH.NET` 阻塞调用需 `Task.Run`** —— `client.Connect()`、`scp.Upload()` 都包（已有）
- **Go relay 32MB + io.ReadAll** —— 大视频拉爆内存（已记 v0.2 改 streaming）
- **Go relay HTTP server `ListenAndServe` 不响应 SIGTERM** —— 客户端长传会被截断（已有 fallback：retry loop）
- **`ReplayPending` 对 buffer 满的 client 丢消息** —— pending map 还在，**client 端 ack 失败重试机制目前没有**——v0.3 加
- **退出兜底 5s timeout** —— SSH 远端 kill 命令会超时；接受（不阻塞进程退出）
- **PublicHost 留空用 SshHost** —— UI 公网 QR 时场景，**SshHost 不一定是公网 host**；用户必须显式填
- **`TryChmod755` 在 Windows 上运行时 `SupportedOSPlatformGuard` 警告** —— 已 `#pragma warning disable CA1416` 压住（`RelayBinaryExtractor.cs:59-61`）
- **ScpClient 重连 → 改 batch scp + ControlMaster** —— v0.1 每文件 3 次 SSH 连触发 sshd MaxStartups；v0.2 改用本地 scp 命令 + SSH ControlMaster 复用，详见 §31 + §5
- **`ConnectionInfo.Timeout = 10s` 太短** —— v0.2 改 60s；链路慢时 KEX / scp 都 timeout
- **缺 SSH KeepAlive** —— v0.2 加 `KeepAlive = new SshKeepAlive(30s/60s)`；防 NAT 老化断连
- **BatchWorker 触发条件要全** —— v0.2 push 模型；v0.3 已删，Poller 直接查 db 不需要凑批/idle
- **`SetLastError` 不重置 State** —— `Deploy → Start` 不调用重置；如果 `Deploy` 已 Error，`Start` 也走 Error 不动 —— 实际上 `Start` 会覆盖
- **进程崩溃 channel 残留** —— v0.2 接受丢失；v0.3 ✅ Poller 启动立即跑一次，db Pending 行自动补拉；**v0.4 进一步简化**：scp 落盘改用原文件名（`tmp/{filename}`）去 `.bin/.meta`，崩溃恢复方案改 re-upload（详见 `02-data-layer.md` §10.5）
- **scp StrictHostKeyChecking=accept-new 自动接受** —— 安全权衡（首次自动 trust），可改 `ask` 弹框但 UX 差
- **`Process.Dispose()` 后读 `ExitCode` 抛 "No process is associated with this object"** —— 见 trap-book §37；`finally` 释放 Process 之后任何 `ExitCode`/`HasExited`/`Kill` 都会抛；`?.` 不防 disposed。预防：finally 之前先 `exitCode = proc.ExitCode` 抓到本地变量。`PublicRelayService.cs:540-602`
