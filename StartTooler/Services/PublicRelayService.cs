using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using StartTooler.Data;

namespace StartTooler.Services;

/// <summary>
/// 公网代理服务：SSH 部署/启停 upload-relay（Go）远端进程 + 本地 TCP 长连接接收文件。
/// 线程安全：SSH 命令调用方负责串行（一般由 UI 按钮触发）；TCP 客户端后台 loop。
/// </summary>
public class PublicRelayService : IDisposable
{
    public enum RelayState
    {
        Idle,           // 未启用
        Deploying,
        Starting,
        Running,        // 远端进程 + 本地 TCP 客户端都在跑
        Stopping,
        Stopped,
        Error,
    }

    public RelayState State { get; private set; } = RelayState.Idle;
    public string? LastError { get; private set; }
    public string? LastLog { get; private set; }

    public event Action? StateChanged;
    public event Action<string>? FileReceived;   // 参数：接收到的本地路径

    private CancellationTokenSource? _clientCts;
    private Task? _clientTask;

    // v0.2: Batch SCP 下载
    private readonly Channel<PendingVpsFile> _pendingChannel =
        Channel.CreateBounded<PendingVpsFile>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    private Task? _batchWorkerTask;
    private PublicRelayConfig? _lastCfg;
    private string? _lastProjectPath;
    private ISyncForVpsTaskRepository? _syncTaskRepo;

    // ============================================================
    // SSH 工具
    // ============================================================

    /// <summary>
    /// SSH keepalive 间隔（v0.2）：30s 发一次空心跳，防 NAT/防火墙老化断连。
    /// SSH.NET 2024.2.0 的 API 是 BaseClient.KeepAliveInterval（TimeSpan，默认 -1 = 关）。
    /// </summary>
    private static readonly TimeSpan SshKeepAliveInterval = TimeSpan.FromSeconds(30);

    private static ConnectionInfo BuildConnectionInfo(PublicRelayConfig cfg)
    {
        AuthenticationMethod method;
        if (!string.IsNullOrEmpty(cfg.SshKeyPath))
        {
            method = new PrivateKeyAuthenticationMethod(cfg.SshUser, new PrivateKeyFile(cfg.SshKeyPath));
        }
        else
        {
            method = new PasswordAuthenticationMethod(cfg.SshUser, cfg.SshPassword ?? "");
        }
        return new ConnectionInfo(cfg.SshHost, cfg.SshPort, cfg.SshUser, method)
        {
            // v0.2: 10s → 60s，链路慢时 KEX / scp 都不再 timeout
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>
    /// 包装 new SshClient(BuildConnectionInfo(cfg))，统一设 KeepAliveInterval。
    /// </summary>
    private static SshClient CreateSshClient(PublicRelayConfig cfg)
    {
        var client = new SshClient(BuildConnectionInfo(cfg));
        client.KeepAliveInterval = SshKeepAliveInterval;
        return client;
    }

    private static string ExpandRemotePath(string path)
    {
        // SSH 远程 shell 自己展开 ~，保留原样
        return path;
    }

    private static async Task<string> RunSshAsync(PublicRelayConfig cfg, string command, IProgress<string>? log, CancellationToken ct)
    {
        using var client = CreateSshClient(cfg);
        await Task.Run(() => client.Connect(), ct);

        try
        {
            log?.Report($"$ {command}");
            var cmd = await Task.Run(() => client.RunCommand(command), ct);
            var result = cmd.Result ?? "";
            var err = cmd.Error ?? "";
            if (!string.IsNullOrEmpty(result)) log?.Report(result.TrimEnd());
            if (!string.IsNullOrEmpty(err)) log?.Report("stderr: " + err.TrimEnd());
            if (cmd.ExitStatus != 0)
            {
                throw new InvalidOperationException($"SSH command failed (exit {cmd.ExitStatus}): {command}\n{result}\n{err}");
            }
            return result;
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    // ============================================================
    // 部署 + 启动 + 停止
    // ============================================================

    public async Task DeployAsync(PublicRelayConfig cfg, string arch, IProgress<string>? log, CancellationToken ct)
    {
        SetState(RelayState.Deploying);
        try
        {
            var remotePath = ExpandRemotePath(cfg.SshRemotePath);
            await RunSshAsync(cfg, $"mkdir -p {remotePath}", log, ct);

            // 提取嵌入的 Linux 二进制到本地 temp（无第三方依赖）
            var localBin = RelayBinaryExtractor.Extract(arch);
            log?.Report($"local binary ({arch}): {localBin} ({new FileInfo(localBin).Length / 1024} KB)");

            using var scp = new ScpClient(BuildConnectionInfo(cfg));
            await Task.Run(() => scp.Connect(), ct);
            try
            {
                var remoteBinName = $"upload-relay-linux-{arch}";
                log?.Report($"scp {localBin} -> {remotePath}/{remoteBinName}");
                using (var fs = File.OpenRead(localBin))
                {
                    await Task.Run(() => scp.Upload(fs, $"{remotePath}/{remoteBinName}"), ct);
                }
                // 远端 chmod +x + 清理旧 python 实现（如果存在）
                await RunSshAsync(cfg, $"chmod +x {remotePath}/{remoteBinName}", log, ct);
                await RunSshAsync(cfg,
                    $"rm -f {remotePath}/upload_relay.py {remotePath}/upload.html",
                    log, ct);
                log?.Report("清理旧 python 部署（如有）");
            }
            finally
            {
                if (scp.IsConnected) scp.Disconnect();
            }

            SetLastError(null);
            log?.Report("部署完成");
        }
        catch (Exception ex)
        {
            SetState(RelayState.Error, ex.Message);
            throw;
        }
    }

    public async Task StartRemoteAsync(PublicRelayConfig cfg, string arch, IProgress<string>? log, CancellationToken ct)
    {
        SetState(RelayState.Starting);
        try
        {
            var remotePath = ExpandRemotePath(cfg.SshRemotePath);
            var tmpDir = $"{remotePath}/tmp";
            var pidFile = $"{remotePath}/relay.pid";
            var logFile = $"{remotePath}/relay.log";
            var remoteBin = $"{remotePath}/upload-relay-linux-{arch}";

            // 清理残留 + 临时目录
            await RunSshAsync(cfg, $"mkdir -p {tmpDir}", log, ct);
            await RunSshAsync(cfg, $"if [ -f {pidFile} ]; then PID=$(cat {pidFile}); kill $PID 2>/dev/null || true; rm -f {pidFile}; fi", log, ct);

            // 启动：setsid + nohup + 写 PID 文件
            var cmd = $"setsid nohup {remoteBin} " +
                      $"--http-port {cfg.HttpPort} --tcp-port {cfg.TcpPort} " +
                      $"--tmp-dir {tmpDir} " +
                      $"< /dev/null > {logFile} 2>&1 & echo $! > {pidFile}";
            await RunSshAsync(cfg, cmd, log, ct);

            await Task.Delay(500, ct);
            var pidOutput = await RunSshAsync(cfg, $"cat {pidFile} 2>/dev/null", null, ct);
            log?.Report($"远端 PID: {pidOutput.Trim()}");

            SetLastError(null);
        }
        catch (Exception ex)
        {
            SetState(RelayState.Error, ex.Message);
            throw;
        }
    }

    public async Task StopRemoteAsync(PublicRelayConfig cfg, IProgress<string>? log, CancellationToken ct)
    {
        SetState(RelayState.Stopping);
        try
        {
            var remotePath = ExpandRemotePath(cfg.SshRemotePath);
            var pidFile = $"{remotePath}/relay.pid";
            var cmd = $"if [ -f {pidFile} ]; then PID=$(cat {pidFile}); kill $PID 2>/dev/null && echo killed $PID; rm -f {pidFile}; else echo no pid file; fi";
            await RunSshAsync(cfg, cmd, log, ct);
            SetLastError(null);
        }
        catch (Exception ex)
        {
            SetState(RelayState.Error, ex.Message);
            throw;
        }
    }

    public async Task<bool> IsRemoteRunningAsync(PublicRelayConfig cfg, CancellationToken ct)
    {
        try
        {
            var remotePath = ExpandRemotePath(cfg.SshRemotePath);
            var pidFile = $"{remotePath}/relay.pid";
            var output = await RunSshAsync(cfg,
                $"if [ -f {pidFile} ]; then PID=$(cat {pidFile}); kill -0 $PID 2>/dev/null && echo running || echo dead; else echo nofile; fi",
                null, ct);
            return output.Trim() == "running";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析出部署要用的 arch：如果配置是 amd64/arm64 直接用；auto 时 SSH `uname -m` 检测。
    /// 解析失败抛异常（提示用户在 UI 里手动指定）。
    /// </summary>
    public async Task<string> ResolveArchAsync(PublicRelayConfig cfg, IProgress<string>? log, CancellationToken ct)
    {
        var configured = (cfg.RemoteArch ?? RelayArch.Auto).Trim().ToLowerInvariant();
        if (configured != RelayArch.Auto)
        {
            if (!RelayBinaryExtractor.SupportedArchs.Contains(configured))
                throw new InvalidOperationException($"RemoteArch 配置无效: '{configured}'。可选：auto / {string.Join(" / ", RelayBinaryExtractor.SupportedArchs)}");
            log?.Report($"使用配置指定架构: {configured}");
            return configured;
        }

        log?.Report("自动检测 VPS 架构 (uname -m)...");
        var output = await RunSshAsync(cfg, "uname -m", null, ct);
        var detected = RelayArch.FromUnameM(output);
        if (detected == null)
            throw new InvalidOperationException(
                $"无法识别 VPS 架构 ('{output.Trim()}')。请在「公网代理设置」里把架构手动指定为 amd64 或 arm64。");
        log?.Report($"检测到: {output.Trim()} -> {detected}");
        return detected;
    }

    // ============================================================
    // TCP 客户端长连接
    // ============================================================

    public void StartClient(PublicRelayConfig cfg, string projectPath)
    {
        if (_clientTask != null) return;
        if (string.IsNullOrEmpty(projectPath))
        {
            SetState(RelayState.Error, "未选择项目目录，无法启动 TCP 客户端");
            return;
        }

        _lastCfg = cfg;
        _lastProjectPath = projectPath;
        _syncTaskRepo ??= new SyncForVpsTaskRepository();

        _clientCts = new CancellationTokenSource();
        var token = _clientCts.Token;
        _clientTask = Task.Run(() => RunClientLoopAsync(cfg, projectPath, token), token);
        _batchWorkerTask = Task.Run(() => RunBatchWorkerAsync(cfg, projectPath, token), token);
        SetState(RelayState.Running);
        Trace.WriteLine($"[PublicRelay] StartClient: TCP + BatchWorker launched, batch_size={cfg.ScpBatchSize} idle_timeout={cfg.ScpBatchIdleTimeoutSec}s");
    }

    public async Task StopClientAsync()
    {
        _clientCts?.Cancel();

        // 1) 等 client loop + batch worker 都退出
        if (_clientTask != null)
        {
            try { await _clientTask; } catch { /* ignore */ }
        }
        if (_batchWorkerTask != null)
        {
            try { await _batchWorkerTask; } catch { /* ignore */ }
        }

        // 2) 双保险：再 drain 一次 channel 残留（worker 退出时也 drain 过，但 worker 可能因异常退出）
        await FlushPendingChannelOnExitAsync();

        _clientCts?.Dispose();
        _clientCts = null;
        _clientTask = null;
        _batchWorkerTask = null;
        Trace.WriteLine("[PublicRelay] StopClient: done");
    }

    private async Task RunClientLoopAsync(PublicRelayConfig cfg, string projectPath, CancellationToken ct)
    {
        int retryDelaySec = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var host = !string.IsNullOrEmpty(cfg.PublicHost) ? cfg.PublicHost : cfg.SshHost;
                using var client = new TcpClient();
                await client.ConnectAsync(host, cfg.TcpPort, ct);
                using var stream = client.GetStream();
                retryDelaySec = 1;
                LastLog = $"已连接 {host}:{cfg.TcpPort}";
                Trace.WriteLine($"[PublicRelay] connected to {host}:{cfg.TcpPort}");

                while (!ct.IsCancellationRequested)
                {
                    // 协议：Go relay 直接发 JSON 消息 + '\n'，无 4 字节长度前缀
                    // 按行读，解析 file_pending 通知
                    string? line;
                    try
                    {
                        line = await ReadLineAsync(stream, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Trace.WriteLine($"[PublicRelay] read line err: {ex.Message}");
                        break;
                    }
                    if (line == null) break; // EOF
                    line = line.TrimEnd('\r');
                    if (string.IsNullOrEmpty(line)) continue;

                    string fileId, name;
                    long size;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.GetProperty("type").GetString() != "file_pending") continue;
                        fileId = doc.RootElement.GetProperty("id").GetString() ?? "";
                        name = doc.RootElement.GetProperty("name").GetString() ?? "";
                        size = doc.RootElement.GetProperty("size").GetInt64();
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[PublicRelay] parse json err: {ex.Message} line={line}");
                        continue;
                    }

                    Trace.WriteLine($"[PublicRelay] file_pending id={fileId} name={name} size={size} → enqueue");

                    // v0.2: 不再 fire-and-forget FetchOneAsync；入队由 BatchWorker 处理
                    try
                    {
                        await _pendingChannel.Writer.WriteAsync(
                            new PendingVpsFile(fileId, name, size), ct);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[PublicRelay] enqueue err: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // TCP 客户端退出（Stop 时 ct 取消）；不 flush（worker 仍在跑）
                return;
            }
            catch (Exception ex)
            {
                LastLog = $"连接断开: {ex.Message}";
                Trace.WriteLine($"[PublicRelay] client error: {ex.Message}");
                // TCP 断线：channel 里的待下载文件继续由 BatchWorker 处理（不阻塞 scp）
                // 注意：不强制 flush，让 BatchWorker 在 idle 超时或 Stop 时统一处理
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryDelaySec, 10)), ct);
                retryDelaySec = Math.Min(retryDelaySec * 2, 10);
            }
        }
    }

    /// <summary>
    /// 按行读 TCP stream，遇到 '\n' 返回该行（不含 '\n'），EOF 返回 null。
    /// 1MB 上限防止恶意 peer 一直不换行撑爆内存。
    /// </summary>
    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(one, ct);
            if (read == 0) return buf.Count == 0 ? null : Encoding.UTF8.GetString(buf.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray());
            buf.Add(one[0]);
            if (buf.Count > 1 << 20) throw new InvalidDataException("line too long (>1MB)");
        }
    }

    /// <summary>
    /// 收到 file_pending 通知后入队的待下载文件。BatchWorker 凑批后调一次 scp 全部拉本地。
    /// </summary>
    public record PendingVpsFile(string FileId, string Name, long Size);

    /// <summary>
    /// BatchWorker 主循环：凑齐 BatchSize 或距首文件 idleTimeout 触发 DrainBatchAsync。
    /// 触发条件见 doc/08-public-relay.md §5.4。
    /// </summary>
    private async Task RunBatchWorkerAsync(PublicRelayConfig cfg, string projectPath, CancellationToken ct)
    {
        var batchSize = cfg.ScpBatchSize > 0 ? cfg.ScpBatchSize : 5;
        var idleTimeout = TimeSpan.FromSeconds(cfg.ScpBatchIdleTimeoutSec > 0 ? cfg.ScpBatchIdleTimeoutSec : 30);
        Trace.WriteLine($"[PublicRelay] BatchWorker start batch_size={batchSize} idle_timeout={idleTimeout.TotalSeconds}s");

        var batch = new List<PendingVpsFile>(batchSize);
        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            DateTime? firstFileAt = null;

            while (batch.Count < batchSize && !ct.IsCancellationRequested)
            {
                TimeSpan waitFor;
                if (firstFileAt is null)
                {
                    waitFor = idleTimeout;
                }
                else
                {
                    var elapsed = DateTime.UtcNow - firstFileAt.Value;
                    waitFor = idleTimeout - elapsed;
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
                    if (batch.Count >= batchSize) break;
                }
            }

            if (batch.Count == 0) continue;

            Trace.WriteLine($"[PublicRelay] BatchWorker drain batch_count={batch.Count}");
            await DrainBatchAsync(cfg, projectPath, batch, ct);
        }

        // 退出前 flush 残留
        await FlushPendingChannelOnExitAsync();
        Trace.WriteLine("[PublicRelay] BatchWorker exit");
    }

    /// <summary>
    /// 退出兜底：从 channel 抽出残留文件，best-effort drain 一次。
    /// </summary>
    private async Task FlushPendingChannelOnExitAsync()
    {
        var remaining = new List<PendingVpsFile>();
        while (_pendingChannel.Reader.TryRead(out var f)) remaining.Add(f);
        if (remaining.Count == 0) return;

        var cfg = _lastCfg;
        var pp = _lastProjectPath;
        if (cfg == null || string.IsNullOrEmpty(pp))
        {
            Trace.WriteLine($"[PublicRelay] flush on exit: cfg/projectPath null, dropping {remaining.Count} files");
            return;
        }
        Trace.WriteLine($"[PublicRelay] flush on exit: {remaining.Count} files");
        await DrainBatchAsync(cfg, pp, remaining, CancellationToken.None);
    }

    /// <summary>
    /// 一次 Process.Start scp 拉一批文件到本地；解析 exit code + stderr 拆失败文件；
    /// upsert sync_for_vps_task + 通知；批量 rm VPS tmp/；逐文件 HTTP /ack。
    /// </summary>
    private async Task DrainBatchAsync(
        PublicRelayConfig cfg, string projectPath,
        IReadOnlyList<PendingVpsFile> batch, CancellationToken ct)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var dateDir = Path.Combine(projectPath, today);
        Directory.CreateDirectory(dateDir);

        // 1) 构造 scp 命令
        var psi = new ProcessStartInfo("scp")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var masterSock = Path.Combine(
            Path.GetTempPath(),
            $"starttooler-ssh-{SanitizeForPath(cfg.SshHost)}-{cfg.SshPort}");
        psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(cfg.SshPort.ToString());
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=10");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ControlMaster=auto");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add($"ControlPath={masterSock}");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ControlPersist=10m");

        foreach (var f in batch)
        {
            var remoteBin = $"{cfg.SshUser}@{cfg.SshHost}:{ExpandRemotePath(cfg.SshRemotePath)}/tmp/{f.FileId}.bin";
            psi.ArgumentList.Add(remoteBin);
        }
        psi.ArgumentList.Add(dateDir + Path.DirectorySeparatorChar);

        // 2) 启动 scp
        Process? proc = null;
        var sw = Stopwatch.StartNew();
        string stderr = "";
        try
        {
            proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Process.Start returned null");
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            stderr = await stderrTask;
            _ = await stdoutTask;   // 忽略
            sw.Stop();
        }
        catch (Exception ex)
        {
            sw.Stop();
            Trace.WriteLine($"[PublicRelay] scp process spawn/exec err: {ex.Message}");
            // 全部算失败
            foreach (var f in batch)
            {
                await UpsertSyncTaskAsync(f, null, SyncForVpsTaskStatus.Failed,
                    attemptCount: 1, lastError: "scp process err: " + ex.Message);
                NotificationService.Current.Show(
                    "公网接收失败", $"{f.Name}: scp process err: {ex.Message}", NotificationType.Error);
            }
            return;
        }
        finally
        {
            proc?.Dispose();
        }

        Trace.WriteLine($"[PublicRelay] scp batch={batch.Count} exit={proc?.ExitCode} elapsed={sw.ElapsedMilliseconds}ms");
        if (!string.IsNullOrEmpty(stderr))
            Trace.WriteLine($"[PublicRelay] scp stderr: {stderr}");

        // 3) 解析失败 ID
        var failedIds = ParseScpStderr(stderr, batch);
        var successIds = batch.Where(f => !failedIds.Contains(f.FileId))
            .Select(f => f.FileId).ToList();

        // 4) 每文件 upsert + notify
        foreach (var f in batch)
        {
            var localPath = Path.Combine(dateDir, SanitizeFileName(f.Name));
            var failed = failedIds.Contains(f.FileId);

            await UpsertSyncTaskAsync(f, failed ? null : localPath,
                failed ? SyncForVpsTaskStatus.Failed : SyncForVpsTaskStatus.Received,
                attemptCount: 1,
                lastError: failed ? ExtractScpErrorFor(stderr, f.FileId) : null);

            if (failed)
            {
                NotificationService.Current.Show(
                    "公网接收失败",
                    $"{f.Name}: {ExtractScpErrorFor(stderr, f.FileId)}",
                    NotificationType.Error);
            }
            else
            {
                NotificationService.Current.Show("公网接收", $"已收到 {f.Name}");
                FileReceived?.Invoke(localPath);
                LastLog = $"已接收 {f.Name} ({f.Size} bytes)";
            }
        }

        // 5) 批量 rm VPS tmp/（仅成功的）；一次 SSH.NET 连接 + RunCommand
        if (successIds.Count > 0)
        {
            try
            {
                using var ssh = CreateSshClient(cfg);
                ssh.Connect();
                var rmPaths = string.Join(" ",
                    successIds.Select(id =>
                        $"{ExpandRemotePath(cfg.SshRemotePath)}/tmp/{id}.bin"));
                ssh.RunCommand($"rm -f {rmPaths}");
                ssh.Disconnect();
                Trace.WriteLine($"[PublicRelay] batch rm: {successIds.Count} files");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PublicRelay] batch rm warning: {ex.Message}");
            }
        }

        // 6) 每成功文件 HTTP POST /ack/{id}（不批量化，简化）
        foreach (var id in successIds)
        {
            _ = Task.Run(() => AckFileAsync(cfg, id), ct);
        }
    }

    private async Task UpsertSyncTaskAsync(
        PendingVpsFile f, string? localPath, SyncForVpsTaskStatus status,
        int attemptCount, string? lastError)
    {
        if (_syncTaskRepo == null) return;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var existing = await _syncTaskRepo.GetByFileIdAsync(f.FileId);
            var task = new SyncForVpsTask
            {
                FileId = f.FileId,
                FileName = f.Name,
                SizeBytes = f.Size,
                LocalPath = localPath,
                Status = status,
                AttemptCount = (existing?.AttemptCount ?? 0) + attemptCount,
                LastError = lastError,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
            };
            await _syncTaskRepo.UpsertAsync(task);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PublicRelay] upsert sync_for_vps_task FAILED id={f.FileId}: {ex.Message}");
        }
    }

    /// <summary>
    /// 从 scp stderr 找失败的文件 ID。OpenBSD scp / 新版 scp 都输出
    /// "scp: .../tmp/&lt;id&gt;.bin: &lt;reason&gt;" 这种行。
    /// </summary>
    private static HashSet<string> ParseScpStderr(string stderr, IReadOnlyList<PendingVpsFile> batch)
    {
        var failed = new HashSet<string>();
        var batchIds = batch.Select(b => b.FileId).ToHashSet();
        if (string.IsNullOrEmpty(stderr)) return failed;

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
        if (string.IsNullOrEmpty(stderr)) return "unknown scp error";
        foreach (var line in stderr.Split('\n'))
        {
            if (line.Contains($"/tmp/{id}.bin")) return line.Trim();
        }
        return "unknown scp error";
    }

    private static string SanitizeFileName(string name)
    {
        var safe = Path.GetFileName(name);
        if (string.IsNullOrEmpty(safe)) safe = $"upload_{DateTime.Now:HHmmss}.bin";
        return safe;
    }

    private static string SanitizeForPath(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') sb.Append(c);
            else sb.Append('_');
        }
        return sb.ToString();
    }

    private static async Task AckFileAsync(PublicRelayConfig cfg, string id)
    {
        try
        {
            var host = !string.IsNullOrEmpty(cfg.PublicHost) ? cfg.PublicHost : cfg.SshHost;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = $"http://{host}:{cfg.HttpPort}/ack/{id}";
            var resp = await http.PostAsync(url, null);
            Trace.WriteLine($"[PublicRelay] ack {id} -> {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PublicRelay] ack {id} FAILED: {ex.Message}");
            // 不抛：本地文件已写盘，VPS 端残留等下次清理
        }
    }

    private static string GetUniqueFileName(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int counter = 1;
        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{name}_{counter}{ext}");
            counter++;
        } while (File.Exists(newPath));
        return newPath;
    }

    // ============================================================
    // 状态管理 + Dispose
    // ============================================================

    private void SetState(RelayState s, string? err = null)
    {
        State = s;
        LastError = err;
        StateChanged?.Invoke();
    }

    private void SetLastError(string? err)
    {
        LastError = err;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _clientCts?.Cancel();
        _clientCts?.Dispose();
    }
}