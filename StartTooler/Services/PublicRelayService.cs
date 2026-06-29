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
using Renci.SshNet.Common;

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

    // ============================================================
    // SSH 工具
    // ============================================================

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
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private static string ExpandRemotePath(string path)
    {
        // SSH 远程 shell 自己展开 ~，保留原样
        return path;
    }

    private static async Task<string> RunSshAsync(PublicRelayConfig cfg, string command, IProgress<string>? log, CancellationToken ct)
    {
        using var client = new SshClient(BuildConnectionInfo(cfg));
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

        _clientCts = new CancellationTokenSource();
        var token = _clientCts.Token;
        _clientTask = Task.Run(() => RunClientLoopAsync(cfg, projectPath, token), token);
        SetState(RelayState.Running);
    }

    public async Task StopClientAsync()
    {
        _clientCts?.Cancel();
        if (_clientTask != null)
        {
            try { await _clientTask; } catch { /* ignore */ }
        }
        _clientCts?.Dispose();
        _clientCts = null;
        _clientTask = null;
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

                    LastLog = $"通知 {name} ({size} bytes)，开始 SSH 拉取";
                    Trace.WriteLine($"[PublicRelay] file_pending id={fileId} name={name} size={size}");

                    // 异步拉取：不让 TCP 读取循环卡住后续通知
                    _ = FetchOneAsync(cfg, projectPath, fileId, name, size, ct);
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
    /// 收到 file_pending 通知后：通过 SSH scp 拉文件到本地项目目录（按日期归档），
    /// 然后 HTTP POST /ack/{id} 让 Go relay 删 VPS 端 tmp，最后右下角 notify。
    /// </summary>
    private async Task FetchOneAsync(PublicRelayConfig cfg, string projectPath, string id, string name, long size, CancellationToken? outerCt = null)
    {
        var ct = outerCt ?? CancellationToken.None;
        try
        {
            var safeName = Path.GetFileName(name);
            if (string.IsNullOrEmpty(safeName)) safeName = $"upload_{DateTime.Now:HHmmss}.bin";
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var dateDir = Path.Combine(projectPath, today);
            Directory.CreateDirectory(dateDir);
            var finalPath = GetUniqueFileName(Path.Combine(dateDir, safeName));

            Trace.WriteLine($"[PublicRelay] FetchOneAsync start id={id} name={safeName} size={size} -> {finalPath}");

            // 1) SSH scp 拉文件
            using var ssh = new SshClient(BuildConnectionInfo(cfg));
            await Task.Run(() => ssh.Connect(), ct);
            using var scp = new ScpClient(BuildConnectionInfo(cfg));
            await Task.Run(() => scp.Connect(), ct);
            using (var fs = File.Create(finalPath))
            {
                var remoteBin = $"{ExpandRemotePath(cfg.SshRemotePath)}/tmp/{id}.bin";
                await Task.Run(() => scp.Download(remoteBin, fs), ct);
            }
            scp.Disconnect();
            ssh.Disconnect();

            // 2) ssh 删 VPS 端 tmp（双保险：这里 rm 一次，ack 端点会再删一次）
            try
            {
                ssh.Connect();
                ssh.RunCommand($"rm -f {ExpandRemotePath(cfg.SshRemotePath)}/tmp/{id}.bin {ExpandRemotePath(cfg.SshRemotePath)}/tmp/{id}.meta");
                ssh.Disconnect();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PublicRelay] ssh rm warning: {ex.Message}");
            }

            // 3) HTTP POST /ack/{id} —— 让 Go relay 从 pending 删 + 删 bin/meta
            // 失败不阻塞本地写盘（VPS 端 tmp 会在下次清理时被删）
            _ = Task.Run(() => AckFileAsync(cfg, id), ct);

            // 4) 右下角 notify
            var sizeKb = (size + 1023) / 1024;
            NotificationService.Current.Show("公网接收", $"已收到 {safeName} ({sizeKb} KB)");

            // 5) 触发 FileReceived 事件（之前没人订阅，现在通知 gallery 刷新）
            LastLog = $"已接收 {safeName} ({size} bytes)";
            FileReceived?.Invoke(finalPath);

            Trace.WriteLine($"[PublicRelay] FetchOneAsync done -> {finalPath}");
        }
        catch (Exception ex)
        {
            LastLog = $"拉取失败 {name}: {ex.Message}";
            Trace.WriteLine($"[PublicRelay] FetchOneAsync FAILED id={id}: {ex}");
            NotificationService.Current.Show("公网接收失败", $"{name}: {ex.Message}", NotificationType.Error);
        }
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