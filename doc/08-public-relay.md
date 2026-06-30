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
│      ├─ StartClient (TCP 长连接 → 收 file_pending JSON line)                          │
│      │     └─ RunClientLoopAsync: 指数退避重连 + 按行解析 JSON                        │
│      │           └─ (v0.1) FetchOneAsync / (v0.2) BatchWorker → DrainBatchAsync scp 拉文件 → ack → notify → sync_for_vps_task upsert │
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
│     tmp/<id>.bin / <id>.meta    (pending 上传文件)                                     │
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

## 5. TCP 通知客户端 + Batch SCP 下载（`RunClientLoopAsync` + `RunBatchWorkerAsync` + `DrainBatchAsync` — `PublicRelayService.cs:270-360+`）

> v0.2 起，文件下载从「单文件 scp（SSH.NET ScpClient）」改为「batch scp（本地 scp 命令 + SSH ControlMaster）」。本节描述新实现。

### 5.1 协议契约

```
C# 端 TCP 客户端 ←→ Go relay :8766

C# ─── (no request) ───→ Go
C# ←── {"type":"file_pending","id":"...","name":"...","size":N}\n ─── Go
C# 内部: JSON parse → Channel<PendingVpsFile>.Writer.Write
   ↓
Batch Worker 凑齐 N 个 或 idleTimeout 触发
   ↓
Process.Start("scp", ... 一次传 N 个文件 ...)
   ↓
解析 scp exit code + stderr → 成功/失败分别 upsert sync_for_vps_task
   ↓
NotificationService.Show × N
C# ─── HTTP POST /ack/{id} ───→ Go :8765   （仅成功的）
```

- **单行 JSON + `'\n'`** —— 没有 4 字节长度前缀
- C# 端 `ReadLineAsync` 按字节读到 `\n`（行尾 `\r` 去 strip）
- **已修复陷阱**：早期 C# 端按 4 字节大端长度前缀读 → 不是 JSON 包装，Go relay 改用纯行就崩（commit message 「按行读 JSON」）

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
            
            // v0.2: 不再 fire-and-forget FetchOneAsync
            // 而是 enqueue 到 _pendingChannel，由 BatchWorker 凑批
            var (fileId, name, size) = ParseFilePending(line);
            await _pendingChannel.Writer.WriteAsync(
                new PendingVpsFile(fileId, name, size), ct);
        }
    } catch (OperationCanceledException) {
        // 注意：退出前要 flush channel（见 §5.5）
        await FlushPendingChannelOnExitAsync(cfg, projectPath);
        return;
    }
    catch (Exception ex) {
        LastLog = $"连接断开: {ex.Message}";
        // TCP 断线也要 flush channel
        await FlushPendingChannelOnExitAsync(cfg, projectPath);
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
- **不再有 `FetchOneAsync` 单文件 fire-and-forget** —— 全部进 `_pendingChannel`，由 BatchWorker 处理
- **TCP 断线 / 退出时强制 flush**：避免 channel 残留（详见 §5.5）
- **`ReadLineAsync` 1MB 上限**：防恶意 peer 一直不换行撑爆内存

### 5.3 JSON 解析

```csharp
using var doc = JsonDocument.Parse(line);
if (doc.RootElement.GetProperty("type").GetString() != "file_pending") continue;
fileId = doc.RootElement.GetProperty("id").GetString() ?? "";
name   = doc.RootElement.GetProperty("name").GetString() ?? "";
size   = doc.RootElement.GetProperty("size").GetInt64();
```

> 当前 Go 端**只**发 `file_pending` 类型，未来加 ping/pong / state 同步时这里要加分支（目前 `continue` 即可）。

### 5.4 Batch Accumulator + Worker（v0.2 新增）

**字段**：
```csharp
public static readonly int BatchSize = 5;                       // 可配 PublicRelayConfig.ScpBatchSize
public static readonly TimeSpan BatchIdleTimeout = TimeSpan.FromSeconds(30);  // 可配 ScpBatchIdleTimeoutSec

private readonly Channel<PendingVpsFile> _pendingChannel = 
    Channel.CreateBounded<PendingVpsFile>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.Wait   // 满了让 enqueue 等
    });
private Task? _batchWorkerTask;
```

**Worker 触发策略**（任一满足即触发）：
```
触发条件 1：channel 累积 ≥ BatchSize（默认 5）→ 立即触发
触发条件 2：距 channel 中首文件到达已过 BatchIdleTimeout（默认 30s）→ 触发当前累计
触发条件 3：Stop 按钮 / app 退出（ProcessExit）→ 强制 flush
触发条件 4：TCP 客户端断线 → RunClientLoopAsync 退出前强制 flush
```

**Worker 主循环**：
```csharp
private async Task RunBatchWorkerAsync(PublicRelayConfig cfg, string projectPath, CancellationToken ct)
{
    var batch = new List<PendingVpsFile>(BatchSize);
    while (!ct.IsCancellationRequested)
    {
        batch.Clear();
        DateTime? firstFileAt = null;
        
        while (batch.Count < BatchSize && !ct.IsCancellationRequested)
        {
            TimeSpan waitFor;
            if (firstFileAt == null)
                waitFor = BatchIdleTimeout;
            else
            {
                var elapsed = DateTime.UtcNow - firstFileAt.Value;
                waitFor = BatchIdleTimeout - elapsed;
                if (waitFor <= TimeSpan.Zero) break;   // 已超时
            }
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(waitFor);
            
            try
            {
                if (!await _pendingChannel.Reader.WaitToReadAsync(cts.Token)) break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) 
            { 
                break;   // idle timeout
            }
            
            while (_pendingChannel.Reader.TryRead(out var f))
            {
                firstFileAt ??= DateTime.UtcNow;
                batch.Add(f);
                if (batch.Count >= BatchSize) break;
            }
        }
        
        if (batch.Count == 0) continue;
        
        await DrainBatchAsync(cfg, projectPath, batch, ct);
    }
}
```

#### 5.4.1 关键设计

- **每收一个文件就重置 idle timer**：保证 batch 起来（连续传 5 个不会因为 30s 超时被打断）
- **首文件到达时刻 = 计时起点**：不是 worker 启动时刻；空闲 channel 不会无意义计时
- **`Channel.CreateBounded(1000)` + `Wait` mode**：channel 满时 enqueue 等，避免无限堆积撑爆内存
- **`Stop / 退出` 强制 flush** 在 §5.5

### 5.5 强制 Flush 策略（Stop / 退出 / TCP 断线）

**StopClientAsync 改动**：
```csharp
public async Task StopClientAsync()
{
    _clientCts?.Cancel();                          // 让 RunClientLoopAsync + BatchWorker 都退出
    if (_clientTask != null) await _clientTask;    // 等 RunClientLoopAsync 退出（其退出前已 flush，见 §5.2）
    if (_batchWorkerTask != null) await _batchWorkerTask;
    
    // 双保险：再 drain 一次 channel 残留
    var remaining = new List<PendingVpsFile>();
    while (_pendingChannel.Reader.TryRead(out var f)) remaining.Add(f);
    if (remaining.Count > 0)
    {
        await DrainBatchAsync(_lastCfg!, _lastProjectPath!, remaining, CancellationToken.None);
    }
}
```

**RunClientLoopAsync 退出 / 异常时也 flush**（§5.2 代码里已嵌入 `FlushPendingChannelOnExitAsync`）。

**ProcessExit 兜底**（同 `EnsureRemoteKilledOnExitAsync` 风格）：
```csharp
// MainWindowViewModel.cs
AppDomain.CurrentDomain.ProcessExit += (s, e) => {
    try {
        UploadServerViewModel?.PublicRelayViewModel
            ?.EnsureRemoteKilledOnExitAsync()   // 内部也会触发 channel flush
            .Wait(TimeSpan.FromSeconds(5));
    } catch { /* ignore */ }
};
```

#### 5.5.1 进程崩溃兜底（v0.2 接受丢失 / v0.3 实现）

- **v0.2 行为**：进程崩溃（kill -9 / 断电）时，channel 残留文件**会丢失**——sync_for_vps_task 表里没记录，VPS tmp/ 里的 .bin 也不会被主动重拉
- **v0.3 计划**：启动时 SSH.NET 列 VPS `~/starttooler/tmp/*.bin`，对比 `sync_for_vps_task` 已 Received 的 id，差集 = Pending → 入队重拉

### 5.6 `DrainBatchAsync`（v0.2 新增，替代 `FetchOneAsync`）

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
  │       {user}@{host}:{remote}/tmp/{id1}.bin
  │       {user}@{host}:{remote}/tmp/{id2}.bin
  │       ...
  │       {user}@{host}:{remote}/tmp/{idN}.bin
  │       {dateDir}/
  ├─ Process.Start("scp", ...).WaitForExit
  ├─ 解析 exit code + stderr
  │   - exit 0：全部成功
  │   - exit != 0：解析 stderr "scp: .../tmp/<id>.bin: No such file or directory" → failedIds set
  ├─ 遍历 batch，每文件 Upsert sync_for_vps_task：
  │   - 成功：status=Received, local_path=本地最终路径, last_error=null
  │   - 失败：status=Failed, local_path=null, last_error=stderr 片段
  │   - 然后 NotificationService.Show（成功："公网接收" / 失败："公网接收失败"）
  │   - 成功还触发 FileReceived?.Invoke(local_path) → Gallery 刷新
  ├─ SSH.NET SshClient.RunCommand("rm -f {remote}/tmp/{成功id1}.bin {remote}/tmp/{成功id2}.bin ...")
  │   （一次 SSH 连 + 一次命令，删多个文件）
  └─ 对成功文件分别 Task.Run(AckFileAsync)（HTTP POST /ack/{id}）
```

**关键代码骨架**：
```csharp
private async Task DrainBatchAsync(
    PublicRelayConfig cfg, string projectPath, 
    IReadOnlyList<PendingVpsFile> batch, CancellationToken ct)
{
    var today = DateTime.Now.ToString("yyyy-MM-dd");
    var dateDir = Path.Combine(projectPath, today);
    Directory.CreateDirectory(dateDir);
    
    var psi = new ProcessStartInfo("scp")
    {
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    
    var masterSock = Path.Combine(Path.GetTempPath(), 
        $"starttooler-ssh-{Sanitize(cfg.SshHost)}-{cfg.SshPort}");
    psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(cfg.SshPort.ToString());
    psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
    psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=10");
    psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ControlMaster=auto");
    psi.ArgumentList.Add("-o"); psi.ArgumentList.Add($"ControlPath={masterSock}");
    psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ControlPersist=10m");
    
    foreach (var f in batch)
    {
        var remoteBin = $"{cfg.SshUser}@{cfg.SshHost}:{ExpandRemotePath(cfg.SshRemotePath)}/tmp/{f.Id}.bin";
        psi.ArgumentList.Add(remoteBin);
    }
    psi.ArgumentList.Add(dateDir + "/");
    
    var sw = Stopwatch.StartNew();
    using var proc = Process.Start(psi)!;
    var stderrTask = proc.StandardError.ReadToEndAsync(ct);
    var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
    await proc.WaitForExitAsync(ct);
    var stderr = await stderrTask;
    sw.Stop();
    
    Trace.WriteLine($"[PublicRelay] scp batch={batch.Count} exit={proc.ExitCode} elapsed={sw.ElapsedMilliseconds}ms");
    if (!string.IsNullOrEmpty(stderr)) Trace.WriteLine($"[PublicRelay] scp stderr: {stderr}");
    
    var failedIds = ParseScpStderr(stderr, batch);
    var successIds = batch.Select(f => f.Id).Where(id => !failedIds.Contains(id)).ToList();
    
    // 1) upsert sync_for_vps_task + notify
    foreach (var f in batch)
    {
        var localPath = Path.Combine(dateDir, SanitizeName(f.Name));
        var failed = failedIds.Contains(f.Id);
        
        await _syncTaskRepo.UpsertAsync(new SyncForVpsTask
        {
            FileId = f.Id,
            FileName = f.Name,
            SizeBytes = f.Size,
            LocalPath = failed ? null : localPath,
            Status = failed ? SyncForVpsTaskStatus.Failed : SyncForVpsTaskStatus.Received,
            AttemptCount = 1,
            LastError = failed ? ExtractScpErrorFor(stderr, f.Id) : null,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, ct);
        
        if (failed)
        {
            NotificationService.Current.Show(
                "公网接收失败", $"{f.Name}: {ExtractScpErrorFor(stderr, f.Id)}", 
                NotificationType.Error);
        }
        else
        {
            NotificationService.Current.Show("公网接收", $"已收到 {f.Name}");
            FileReceived?.Invoke(localPath);
        }
    }
    
    // 2) 批量 rm（仅成功的）
    if (successIds.Count > 0)
    {
        try
        {
            using var ssh = new SshClient(BuildConnectionInfo(cfg));
            ssh.Connect();
            var rmPaths = string.Join(" ", successIds.Select(id => 
                $"{ExpandRemotePath(cfg.SshRemotePath)}/tmp/{id}.bin"));
            ssh.RunCommand($"rm -f {rmPaths}");
            ssh.Disconnect();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PublicRelay] batch rm warning: {ex.Message}");
        }
    }
    
    // 3) 批量 ack（每文件一次 HTTP POST，不合并）
    foreach (var id in successIds)
    {
        _ = Task.Run(() => AckFileAsync(cfg, id), ct);
    }
}

private static HashSet<string> ParseScpStderr(
    string stderr, IReadOnlyList<PendingVpsFile> batch)
{
    // scp stderr 格式:
    //   "scp: /remote/path/tmp/<id>.bin: No such file or directory"
    //   "scp: failed to upload /remote/path/tmp/<id>.bin"
    // 跨 OpenBSD scp / 新版 scp 都基本一致
    var failed = new HashSet<string>();
    var batchIds = batch.Select(b => b.Id).ToHashSet();
    foreach (var line in stderr.Split('\n'))
    {
        foreach (var id in batchIds)
        {
            if (line.Contains($"/tmp/{id}.bin"))
            {
                failed.Add(id);
                break;
            }
        }
    }
    return failed;
}

private static string? ExtractScpErrorFor(string stderr, string id)
{
    foreach (var line in stderr.Split('\n'))
    {
        if (line.Contains($"/tmp/{id}.bin")) return line.Trim();
    }
    return "unknown scp error";
}
```

#### 5.6.1 关键设计

- **不用 SSH.NET 的 ScpClient**：ScpClient 是单文件 channel，每次新连接，浪费；本地 `scp` 命令行天然支持多文件 + multiplex
- **SSH ControlMaster**：多次 batch 复用单 SSH 连接（`-o ControlMaster=auto -o ControlPath=... -o ControlPersist=10m`）；首次启动要 SSH 握手，后续 batch 直接复用 socket，零握手开销
- **scp stderr 解析**：识别 `scp: .../tmp/<id>.bin: <reason>` 行拆出失败文件 ID，只对失败文件标 status=Failed
- **rm 阶段也批量化**：一次 SSH.NET `RunCommand("rm -f a b c d e")` —— 一次连接，删多个文件
- **ack 不批量化**：HTTP POST /ack 仍然每文件一次（合并复杂度高、收益小、保持简单）

### 5.7 `FetchOneAsync`（v0.1 旧实现，已废弃）

> **v0.2 删除**。旧实现见 git history（commit `c91db80` 之前）。

旧实现每张图：
- 1 次 `SshClient.Connect`
- 1 次 `ScpClient.Connect`
- 1 次 `scp.Download`
- 1 次 `ssh.RunCommand("rm -f ...")`
- 1 次 HTTP `/ack`

100 张图 = 400 次 SSH 握手 + 100 次 HTTP POST，触发 sshd `MaxStartups`（默认 10:100）+ 单文件 10s Timeout 太短 → 集中失败。

新实现（§5.6）100 张图 = 20 次 batch scp = 20 次 SSH 握手（首次 ControlMaster 握手 + 后续复用）；失败粒度细到单文件。

---

### 5.8 PublicRelayConfig 新增字段（v0.2）

```csharp
public class PublicRelayConfig {
    // ... 原有字段 ...
    
    public int ScpBatchSize { get; set; } = 5;                      // batch 大小
    public int ScpBatchIdleTimeoutSec { get; set; } = 30;            // idle 超时秒
    public string SshMasterSockDir { get; set; } = "";               // 空 = Path.GetTempPath()
}
```

UI 暂不暴露（v0.2 沿用默认值；v0.3 加 UI 配置项）。

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

### 6.4 saveUploaded + Broadcast（`main.go:85-108`）

```go
func (s *State) saveUploaded(filename string, data []byte) (string, error) {
    id := newID()                                                                     // 32 hex
    binPath := filepath.Join(s.tmpDir, id+".bin")
    metaPath := filepath.Join(s.tmpDir, id+".meta")
    if err := os.WriteFile(binPath, data, 0644); err != nil { return "", ... }
    meta, _ := json.Marshal(map[string]string{"id": id, "name": filename})
    if err := os.WriteFile(metaPath, meta, 0644); err != nil {
        os.Remove(binPath)
        return "", ...
    }
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

`Ack`：`delete(pending, id)` + `os.Remove(bin)` + `os.Remove(meta)`（无 .bin/.meta 时不报错）。

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
       ├─ saveUploaded: tmp/<id>.bin + .meta
       ├─ state.Broadcast: fan-out 给已连 TCP clients
       └─ return 200 {"success":true,"count":1}

[4] Go handleTCPClient:
       ch <- pendingFile → 写 JSON line + \n
    C# ReadLineAsync → JSON parse → file_id, name, size
    C# _pendingChannel.Writer.WriteAsync(...)         ← v0.2: 入队而非直接 fetch
       ↓
    RunBatchWorkerAsync（独立后台 task）：
       ├─ 凑齐 BatchSize(5) 或 距首文件 30s idle → 触发 DrainBatchAsync
       ├─ DrainBatchAsync:
       │   ├─ Process.Start("scp", ... 一次传 N 个文件 ...)
       │   │   -o ControlMaster=auto + ControlPath={Temp}/starttooler-ssh-...
       │   │   user@host:tmp/<id1>.bin ... user@host:tmp/<idN>.bin → {today}/
       │   ├─ 解析 exit code + stderr → failedIds
       │   ├─ 每文件 upsert sync_for_vps_task (status=Received/Failed)
       │   ├─ 每文件 NotificationService.Show（成功/失败）
       │   ├─ SSH.NET RunCommand("rm -f tmp/{成功id1}.bin {成功id2}.bin ...")
       │   └─ 每成功文件 Task.Run(AckFileAsync)（HTTP POST /ack/{id}）
       └─ FileReceived?.Invoke(local_path)         ← 订阅者刷新 Gallery

[5] 用户回到 StartTooler：
    Gallery 自动刷新（订阅了 FileReceived，待启用）
    右下角看到通知
    项目目录 yyyy-MM-dd/ 下有文件
    sync_for_vps_task 表有持久化记录
```

#### 8.1 旧流程 vs 新流程（v0.1 → v0.2 对比）

| 维度 | v0.1（单文件 FetchOneAsync）| v0.2（batch DrainBatchAsync） |
|---|---|---|
| 100 张图触发 SSH 握手次数 | 300 次（每文件 3 次：SshClient + ScpClient + rm） | 20 次（每 batch 1 次）+ 后续 ControlMaster 复用 |
| 100 张图触发 HTTP POST | 100 次（每文件 ack） | 100 次（不变） |
| 触发 sshd MaxStartups 概率 | 高（300 并发 SSH connect）| 低（20 次 scp，且 ControlMaster 复用 socket） |
| 链路慢导致失败 | 频繁（单文件 10s Timeout 太短）| 缓解（一次 scp 拉多个，单次失败影响范围 1/N） |
| 失败粒度 | 单文件 | 单文件（解析 scp stderr 拆失败 ID）|
| 持久化记录 | 无 | sync_for_vps_task 表（每文件一行）|
| 进程崩溃兜底 | 完全丢失 | v0.2 接受丢失，v0.3 加启动时扫 VPS tmp/ 重拉 |

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
| **改 BatchSize / BatchIdleTimeout** | `PublicRelayConfig.ScpBatchSize / ScpBatchIdleTimeoutSec` | 边界测试：1 张/999 张/Stop 中断 |
| **改 scp 调用方式**（本地 scp → SftpClient 等） | `DrainBatchAsync` + 跨平台依赖 | Windows 老版本无 scp 兜底 |
| **加 sync_for_vps_task 重试 reader** | 新 `SyncForVpsTaskRetryService` | 启动时扫 VPS tmp/ 跟表对比 |
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
- **BatchWorker 触发条件要全** —— 凑齐 + idle + Stop + TCP 断线，缺一就丢文件
- **`SetLastError` 不重置 State** —— `Deploy → Start` 不调用重置；如果 `Deploy` 已 Error，`Start` 也走 Error 不动 —— 实际上 `Start` 会覆盖
- **进程崩溃 channel 残留** —— v0.2 接受丢失；v0.3 加启动扫 VPS tmp/ 兜底
- **scp StrictHostKeyChecking=accept-new 自动接受** —— 安全权衡（首次自动 trust），可改 `ask` 弹框但 UX 差
