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
using System.Threading.Tasks;
using Renci.SshNet;
using StartTooler.Data;

namespace StartTooler.Services;

/// <summary>
/// 公网代理服务：SSH 部署/启停 upload-relay（Go）远端进程 + 本地 TCP 长连接接收通知
/// + 异步 Poller 从 db 拉取 Pending 文件并 scp。
///
/// 线程模型：
///   - SSH 命令调用方负责串行（一般由 UI 按钮触发）
///   - TCP 客户端后台 loop（重连 + 按行解析 file_pending）
///   - Poller 后台 loop（每 SyncPollIntervalSec 秒拉一批 Pending 行 scp）
///
/// 进程崩溃兜底：sync_for_vps_task 表 Pending 行下次启动被 Poller 自动补拉。
/// </summary>
public class PublicRelayService : IDisposable
{
    public enum RelayState
    {
        Idle,
        Deploying,
        Starting,
        Running,
        Stopping,
        Stopped,
        Error,
    }

    public RelayState State { get; private set; } = RelayState.Idle;
    public string? LastError { get; private set; }
    public string? LastLog { get; private set; }

    public event Action? StateChanged;
    public event Action<string>? FileReceived;   // 参数：scp 成功后的本地路径
    public event Action<string>? FileNotified;   // 参数：TCP 收到通知时的文件名（Pending 状态）
    public event Action<int>? PendingCountChanged;  // 参数：当前 Pending 行数

    private CancellationTokenSource? _clientCts;
    private Task? _clientTask;
    private Task? _pollerTask;

    private PublicRelayConfig? _lastCfg;
    private string? _lastProjectPath;
    private ISyncForVpsTaskRepository _syncTaskRepo = new SyncForVpsTaskRepository();

    // askpass 脚本：scp 在 GUI 进程无 TTY，靠 SSH_ASKPASS 喂密码绕开。
    // 密码走 $STARTTOOLER_SSHPASS 环境变量（不写死在脚本里）。
    private static string? _askpassScriptPath;

    // ============================================================
    // SSH 工具
    // ============================================================

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
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    private static SshClient CreateSshClient(PublicRelayConfig cfg)
    {
        var client = new SshClient(BuildConnectionInfo(cfg));
        client.KeepAliveInterval = SshKeepAliveInterval;
        return client;
    }

    private static string ExpandRemotePath(string path) => path;

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
    // 部署 + 启动 + 停止（远端进程生命周期）
    // ============================================================

    public async Task DeployAsync(PublicRelayConfig cfg, string arch, IProgress<string>? log, CancellationToken ct)
    {
        SetState(RelayState.Deploying);
        try
        {
            var remotePath = ExpandRemotePath(cfg.SshRemotePath);
            await RunSshAsync(cfg, $"mkdir -p {remotePath}", log, ct);

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

            await RunSshAsync(cfg, $"mkdir -p {tmpDir}", log, ct);
            await RunSshAsync(cfg, $"if [ -f {pidFile} ]; then PID=$(cat {pidFile}); kill $PID 2>/dev/null || true; rm -f {pidFile}; fi", log, ct);

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
    // TCP 通知客户端（v0.3: 只负责收通知 + 入库，不直接拉文件）
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

        _clientCts = new CancellationTokenSource();
        var token = _clientCts.Token;
        _clientTask = Task.Run(() => RunClientLoopAsync(cfg, projectPath, token), token);
        _pollerTask = Task.Run(() => RunPollerLoopAsync(cfg, projectPath, token), token);
        SetState(RelayState.Running);
        Trace.WriteLine($"[PublicRelay] StartClient: TCP + Poller launched (poll={cfg.SyncPollIntervalSec}s batch={cfg.SyncBatchSize})");
    }

    public async Task StopClientAsync()
    {
        _clientCts?.Cancel();

        if (_clientTask != null)
        {
            try { await _clientTask; } catch { /* ignore */ }
        }
        if (_pollerTask != null)
        {
            try { await _pollerTask; } catch { /* ignore */ }
        }

        _clientCts?.Dispose();
        _clientCts = null;
        _clientTask = null;
        _pollerTask = null;
        Trace.WriteLine("[PublicRelay] StopClient: done");
    }

    /// <summary>
    /// TCP 长连接 + 指数退避重连。按行读 JSON，type=file_pending → 通知 + 入库。
    /// **不直接拉文件**：拉文件由 Poller 异步处理。
    /// </summary>
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

                    await OnFilePendingReceivedAsync(fileId, name, size, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastLog = $"连接断开: {ex.Message}";
                Trace.WriteLine($"[PublicRelay] client error: {ex.Message}");
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
    /// TCP 收到 file_pending 时：UI notify + db InsertIfNew + 更新 PendingCount。
    /// UNIQUE(fileId) 幂等：已存在的行不覆盖。
    /// </summary>
    private async Task OnFilePendingReceivedAsync(string fileId, string name, long size, CancellationToken ct)
    {
        try
        {
            // 拼 RemotePath 供 db 记录（VPS 现在用原始文件名落盘；这里只是 fallback，实际 scp 时优先用 broadcast 给的 Path）
            var remotePath = BuildRemoteTmpPathFallback(fileId, name);
            var inserted = await _syncTaskRepo.InsertIfNewAsync(fileId, name, size, remotePath, ct);

            if (inserted)
            {
                LastLog = $"📥 待下载：{name} ({size} bytes)";
                Trace.WriteLine($"[PublicRelay] notified id={fileId} name={name} size={size}");
                FileNotified?.Invoke(name);
            }
            else
            {
                Trace.WriteLine($"[PublicRelay] notify dedup id={fileId} (already exists)");
            }

            await RefreshPendingCountAsync(ct);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PublicRelay] onFilePending err id={fileId}: {ex.Message}");
        }
    }

    /// <summary>从 SyncForVpsTask FileId 拼 VPS 上的 fallback tmp 路径（一般走不到，broadcast 时已经把 RemotePath 写进 DB 了）。
    /// VPS 现在直接用原始文件名落盘（v0.4+），不再用 {id}.bin；这里只是给老 DB 行兜底。</summary>
    private string BuildRemoteTmpPathFallback(string fileId, string? fileName)
    {
        var cfg = _lastCfg;
        var name = !string.IsNullOrEmpty(fileName) ? fileName : fileId;
        var tmpDir = cfg == null ? "~/starttooler/tmp" : $"{ExpandRemotePath(cfg.SshRemotePath)}/tmp";
        return $"{tmpDir}/{name}";
    }

    // ============================================================
    // Poller（v0.3 新增）：每 SyncPollIntervalSec 秒拉一批 Pending → scp
    // ============================================================

    /// <summary>
    /// 后台 loop：PeriodicTimer 每 N 秒触发一次，拉 ≤ SyncBatchSize 个 Pending 行调 DrainBatchAsync。
    /// 启动立刻跑一次（避免等待首个 tick）。
    /// ct 取消时退出；进程崩溃后 Pending 行下次启动自动补拉（db 是 single source of truth）。
    /// </summary>
    private async Task RunPollerLoopAsync(PublicRelayConfig cfg, string projectPath, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(cfg.SyncPollIntervalSec > 0 ? cfg.SyncPollIntervalSec : 5);
        var batchSize = cfg.SyncBatchSize > 0 ? cfg.SyncBatchSize : 5;
        Trace.WriteLine($"[PublicRelay] Poller start interval={interval.TotalSeconds}s batch={batchSize}");

        // 启动立即跑一次（避免 5s 等待，且能立即处理上次进程崩溃遗留的 Pending）
        await PollOnceAsync(cfg, projectPath, batchSize, ct);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await PollOnceAsync(cfg, projectPath, batchSize, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }

        Trace.WriteLine("[PublicRelay] Poller exit");
    }

    private async Task PollOnceAsync(PublicRelayConfig cfg, string projectPath, int batchSize, CancellationToken ct)
    {
        try
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
        catch (Exception ex)
        {
            Trace.WriteLine($"[PublicRelay] poll err: {ex.Message}");
        }
    }

    private async Task RefreshPendingCountAsync(CancellationToken ct)
    {
        try
        {
            var n = await _syncTaskRepo.CountPendingAsync(ct);
            PendingCountChanged?.Invoke(n);
        }
        catch { /* UI 计数失败不影响主流程 */ }
    }

    // ============================================================
    // DrainBatchAsync（v0.3 重构）：从 db 行拉文件，DB 是 single source of truth
    // ============================================================

    /// <summary>
    /// 一次 Process.Start scp 拉一批文件到本地；解析 exit code + stderr 拆失败文件；
    /// 成功后 UPDATE Received / 失败 UPDATE Failed（v0.3 不重试）；批量 rm VPS tmp/ + ack。
    /// </summary>
    private async Task DrainBatchAsync(
        PublicRelayConfig cfg, string projectPath,
        IReadOnlyList<SyncForVpsTask> batch, CancellationToken ct)
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

        foreach (var t in batch)
        {
            // 优先用 db 的 RemotePath（broadcast 时存的就是 tmp/{sanitized_original_name}）；fallback 用 FileName
            var remoteBin = !string.IsNullOrEmpty(t.RemotePath)
                ? $"{cfg.SshUser}@{cfg.SshHost}:{t.RemotePath}"
                : $"{cfg.SshUser}@{cfg.SshHost}:{ExpandRemotePath(cfg.SshRemotePath)}/tmp/{t.FileName}";
            psi.ArgumentList.Add(remoteBin);
        }
        psi.ArgumentList.Add(dateDir + Path.DirectorySeparatorChar);

        // 2) 启动 scp
        Process? proc = null;
        var sw = Stopwatch.StartNew();
        string stderr = "";
        int exitCode = -1; // 在 finally Dispose() 之前先抓住；Dispose 会把 m_processId 置 0，再读 ExitCode 会抛 "No process is associated with this object"
        try
        {
            // GUI 进程无 TTY：密码登录走 SSH_ASKPASS；Key 认证无需 askpass
            if (string.IsNullOrEmpty(cfg.SshKeyPath) && !string.IsNullOrEmpty(cfg.SshPassword))
            {
                psi.Environment["SSH_ASKPASS"] = GetAskpassScriptPath();
                psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
                psi.Environment["DISPLAY"] = ":0";
                psi.Environment["STARTTOOLER_SSHPASS"] = cfg.SshPassword;
            }

            proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Process.Start returned null");
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);

            // 5min hard timeout 防 scp hang 卡死 Poller
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
            await proc.WaitForExitAsync(timeoutCts.Token);

            stderr = await stderrTask;
            _ = await stdoutTask;
            sw.Stop();

            // 必须在 finally/Dispose 之前读；读到 -1 说明 handle 已异常
            try { exitCode = proc.ExitCode; }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PublicRelay] scp ExitCode read err: {ex.Message}");
                exitCode = -1;
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            Trace.WriteLine($"[PublicRelay] scp process spawn/exec err: {ex.Message}");
            // 全部 mark Failed
            var errMsg = "scp process err: " + ex.Message;
            foreach (var t in batch)
            {
                try { await _syncTaskRepo.MarkFailedAsync(t.Id, errMsg, ct); } catch { }
                NotificationService.Current.Show(
                    "公网接收失败", $"{t.FileName}: {errMsg}", NotificationType.Error);
            }
            return;
        }
        finally
        {
            // 关键：Dispose 之前显式 Kill（如果还活着），避免 scp 子进程成孤儿
            try
            {
                if (proc != null && !proc.HasExited)
                {
                    proc.Kill(true);
                    Trace.WriteLine($"[PublicRelay] scp process killed (not exited, timeout)");
                }
            }
            catch { /* ignore */ }
            // Dispose 自身也兜底（罕见情况下 handle 已失效）
            try { proc?.Dispose(); } catch { /* ignore */ }
        }

        Trace.WriteLine($"[PublicRelay] scp batch={batch.Count} exit={exitCode} elapsed={sw.ElapsedMilliseconds}ms");
        if (!string.IsNullOrEmpty(stderr))
            Trace.WriteLine($"[PublicRelay] scp stderr: {stderr}");

        // 3) 解析失败 ID
        var failedIds = ParseScpStderr(stderr, batch);
        var successIds = batch.Where(t => !failedIds.Contains(t.FileId)).ToList();
        var failedList = batch.Where(t => failedIds.Contains(t.FileId)).ToList();

        // 4) DB 写 + UI notify
        foreach (var t in successIds)
        {
            var localPath = Path.Combine(dateDir, SanitizeFileName(t.FileName));
            try { await _syncTaskRepo.MarkReceivedAsync(t.Id, localPath, ct); } catch { }
            LastLog = $"✅ 已下载：{t.FileName}";
            NotificationService.Current.Show("公网接收", $"已下载 {t.FileName}");
            FileReceived?.Invoke(localPath);
        }

        // 失败文件：v0.3 直接 mark Failed 不重试，UI 一次性提示
        foreach (var t in failedList)
        {
            var err = ExtractScpErrorFor(stderr, t) ?? "scp failed";
            try { await _syncTaskRepo.MarkFailedAsync(t.Id, err, ct); } catch { }
        }
        if (failedList.Count > 0)
        {
            var names = string.Join("、", failedList.Select(t => t.FileName));
            NotificationService.Current.Show(
                "公网接收失败",
                $"{failedList.Count} 个文件下载失败：{names}",
                NotificationType.Error);
            LastLog = $"❌ {failedList.Count} 个文件失败：{names}";
            LastError = $"{failedList.Count} 个文件下载失败（已 mark Failed，不重试）";
        }

        // 5) 批量 rm VPS tmp/（仅成功的）+ ack（fire-and-forget）
        if (successIds.Count > 0)
        {
            try
            {
                using var ssh = CreateSshClient(cfg);
                ssh.Connect();
                var rmPaths = string.Join(" ",
                    successIds.Select(t =>
                        !string.IsNullOrEmpty(t.RemotePath)
                            ? t.RemotePath
                            : $"{ExpandRemotePath(cfg.SshRemotePath)}/tmp/{t.FileName}"));
                ssh.RunCommand($"rm -f {rmPaths}");
                ssh.Disconnect();
                Trace.WriteLine($"[PublicRelay] batch rm: {successIds.Count} files");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PublicRelay] batch rm warning: {ex.Message}");
            }

            foreach (var t in successIds)
            {
                _ = Task.Run(() => AckFileAsync(cfg, t.FileId), ct);
            }
        }
    }

    /// <summary>
    /// 从 scp stderr 找失败的文件 ID。OpenBSD scp / 新版 scp 都输出
    /// "scp: .../tmp/&lt;id&gt;.bin: &lt;reason&gt;" 这种行。
    /// </summary>
    private static HashSet<string> ParseScpStderr(string stderr, IReadOnlyList<SyncForVpsTask> batch)
    {
        var failed = new HashSet<string>();
        // VPS 现在按原始文件名落盘，scp stderr 行形如 "scp: .../tmp/IMG_001.jpg: <reason>"
        // 按文件名匹配，匹配到 → 标 FileId 为 Failed
        var batchNames = batch.Select(b => b.FileName).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (string.IsNullOrEmpty(stderr) || batchNames.Count == 0) return failed;

        foreach (var line in stderr.Split('\n'))
        {
            foreach (var name in batchNames)
            {
                if (line.Contains($"/tmp/{name}"))
                {
                    var hit = batch.FirstOrDefault(b => b.FileName == name);
                    if (hit != null) failed.Add(hit.FileId);
                    break;
                }
            }
        }
        return failed;
    }

    private static string? ExtractScpErrorFor(string stderr, SyncForVpsTask task)
    {
        if (string.IsNullOrEmpty(stderr)) return "unknown scp error";
        var name = !string.IsNullOrEmpty(task.FileName) ? task.FileName : $"{task.FileId}.bin";
        foreach (var line in stderr.Split('\n'))
        {
            if (line.Contains($"/tmp/{name}")) return line.Trim();
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
        }
    }

    // ============================================================
    // 状态管理 + Dispose + askpass
    // ============================================================

    private void SetState(RelayState s, string? err = null)
    {
        var prev = State;
        State = s;
        LastError = err;
        Trace.WriteLine($"[PublicRelay] SetState: {prev} -> {s}" + (err != null ? $" err={err}" : ""));
        StateChanged?.Invoke();
    }

    private void SetLastError(string? err)
    {
        LastError = err;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        try
        {
            if (_askpassScriptPath != null && File.Exists(_askpassScriptPath))
                File.Delete(_askpassScriptPath);
        }
        catch { /* ignore */ }

        _clientCts?.Cancel();
        _clientCts?.Dispose();
    }

    /// <summary>
    /// 一次性 askpass 脚本写到 temp（chmod 700），里面 echo "$STARTTOOLER_SSHPASS"。
    /// 密码走 env 不进脚本，进程退出 env 自动没；Dispose 时删文件兜底。
    /// </summary>
    private static string GetAskpassScriptPath()
    {
        if (_askpassScriptPath != null && File.Exists(_askpassScriptPath))
            return _askpassScriptPath;

        var path = Path.Combine(Path.GetTempPath(),
            $"starttooler-ssh-askpass-{Environment.ProcessId}.sh");
        File.WriteAllText(path, "#!/bin/sh\necho \"$STARTTOOLER_SSHPASS\"\n");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _askpassScriptPath = path;
        return path;
    }
}