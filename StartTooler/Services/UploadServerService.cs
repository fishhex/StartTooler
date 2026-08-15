using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartTooler.Data;

namespace StartTooler.Services;

/// <summary>
/// v0.12: 局域网 HTTP 上传服务。在 v0.10 基础上扩展：
///   - 路由：保留 /upload (H5)；新增 /api/v1/health、/api/v1/projects、/api/v1/projects/{name}/upload
///   - 鉴权：6 位数字 token（启动生成 + 手动重置），受保护端点必须带 ?token= 或 X-Token header
///   - UDP 广播：每 2s 广播 JSON 到 255.255.255.255:9876，供 App 端发现
///   - 索引：落盘后异步触发 ScanDirectoryAsync 写入 media_files
/// </summary>
public class UploadServerService : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    // v0.12: 注入依赖（替代 v0.10 构造时绑定的 _currentDirectory）
    private readonly IConfigService _configService;
    private readonly IMediaRepository _mediaRepository;
    private readonly string _legacyDefaultDirectory;  // H5 /upload 用，保持 v0.10 行为

    // v0.12: Token
    private string _currentToken = GenerateToken();
    public string CurrentToken => _currentToken;
    public event Action<string>? OnTokenChanged;

    // v0.12: UDP 广播
    private UdpClient? _udpClient;
    private CancellationTokenSource? _udpCts;
    private Task? _udpTask;
    private const int UdpBroadcastPort = 9876;
    private const int UdpBroadcastIntervalMs = 2000;

    // 允许上传的文件扩展名
    private static readonly string[] AllowedExtensions =
    {
        ".jpg", ".jpeg", ".png", ".raw", ".avi", ".mp4", ".mov", ".mkv", ".webm", ".m4v", ".mpg", ".mpeg"
    };

    // v0.12: JSON 序列化选项（统一用 camelCase）
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int Port { get; private set; }
    public string UploadUrl => $"http://{GetLocalIp()}:{Port}/upload";

    public event Action<string>? OnUploadSuccess;
    public event Action<string>? OnUploadError;

    public UploadServerService(
        IConfigService configService,
        IMediaRepository mediaRepository,
        string legacyDefaultDirectory = "")
    {
        _configService = configService;
        _mediaRepository = mediaRepository;
        _legacyDefaultDirectory = legacyDefaultDirectory;
        _currentToken = GenerateToken();
    }

    public async Task StartAsync(int port, CancellationToken ct = default)
    {
        if (_listener != null)
            throw new InvalidOperationException("Server is already running.");

        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            throw new InvalidOperationException(
                "Permission denied. Run: sudo netsh http add urlacl url=http://+:" + port + "/ user=<username>");
        }

        Debug.WriteLine($"[UploadServer] Started on port {port}");
        _listenTask = ListenAsync(_cts.Token);

        // v0.12: 启动 UDP 广播
        _udpTask = StartUdpBroadcastAsync(_cts.Token);

        // v0.12: 通知 VM 刷新 Token 显示
        OnTokenChanged?.Invoke(_currentToken);

        await Task.CompletedTask;
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经取消/释放，忽略
        }

        // 先清空字段，避免后续并发路径重复操作同一个 listener
        var listener = _listener;
        _listener = null;
        _cts = null;

        if (listener != null)
        {
            try
            {
                if (listener.IsListening)
                    listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }

            try
            {
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }
        }

        // v0.12: 关闭 UDP 广播
        try
        {
            _udpCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
        try
        {
            _udpClient?.Close();
        }
        catch (ObjectDisposedException) { }
        _udpClient = null;
        _udpCts = null;

        Debug.WriteLine("[UploadServer] Stopped");
    }

    // v0.12: 重置 Token（UI "重置" 按钮调用）
    public void RegenerateToken()
    {
        _currentToken = GenerateToken();
        OnTokenChanged?.Invoke(_currentToken);
        Debug.WriteLine($"[UploadServer] Token regenerated: {_currentToken}");
    }

    private static string GenerateToken()
    {
        return Random.Shared.Next(0, 1_000_000).ToString("D6");
    }

    private bool ValidateToken(HttpListenerRequest request)
    {
        // 优先 query 参数 ?token=
        var token = request.QueryString["token"];
        // 备选 header X-Token
        if (string.IsNullOrEmpty(token))
            token = request.Headers["X-Token"];
        return !string.IsNullOrEmpty(token) && token == _currentToken;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequestAsync(context), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UploadServer] Listen error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "";
        var method = request.HttpMethod;

        try
        {
            // ===== 1. 无鉴权端点 =====
            if (method == "GET" && string.Equals(path, "/upload", StringComparison.OrdinalIgnoreCase))
            {
                await ServeUploadPageAsync(response);
                return;
            }

            if (method == "GET" && string.Equals(path, "/api/v1/health", StringComparison.OrdinalIgnoreCase))
            {
                var currentProject = await GetCurrentProjectNameAsync();
                await WriteJsonAsync(response, 200, new HealthResponse
                {
                    Ok = true,
                    Service = "starttooler",
                    Version = "0.12",
                    Name = Environment.MachineName,
                    Port = Port,
                    Token = _currentToken,
                    CurrentProject = currentProject ?? "",
                });
                return;
            }

            // ===== 2. 鉴权 =====
            if (!ValidateToken(request))
            {
                await WriteJsonAsync(response, 401, new ErrorResponse { Error = "invalid token" });
                return;
            }

            // ===== 3. 受保护端点 =====
            if (method == "GET" && string.Equals(path, "/api/v1/projects", StringComparison.OrdinalIgnoreCase))
            {
                await HandleListProjectsAsync(response);
                return;
            }

            if (method == "POST" && path.StartsWith("/api/v1/projects/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/upload", StringComparison.OrdinalIgnoreCase))
            {
                var projectName = ExtractProjectName(path);
                if (string.IsNullOrEmpty(projectName))
                {
                    await WriteJsonAsync(response, 404, new ErrorResponse { Error = "invalid project name" });
                    return;
                }
                await HandleUploadToProjectAsync(projectName, request, response);
                return;
            }

            // ===== 4. 兜底：H5 /upload POST（沿用 v0.10 行为）=====
            if (method == "POST" && string.Equals(path, "/upload", StringComparison.OrdinalIgnoreCase))
            {
                await HandleLegacyUploadAsync(request, response);
                return;
            }

            // ===== 5. 未匹配 =====
            await WriteJsonAsync(response, 404, new ErrorResponse { Error = "not found" });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UploadServer] Handle error: {ex}");
            try
            {
                await WriteJsonAsync(response, 500, new ErrorResponse { Error = ex.Message });
            }
            catch
            {
                // response 可能已关闭，忽略
            }
        }
    }

    // ========================================================================
    // H5 /upload 旧行为（保留 v0.10 逻辑）
    // ========================================================================

    private async Task HandleLegacyUploadAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
        {
            await WriteJsonAsync(response, 400, new ErrorResponse { Error = "Invalid content type. Use multipart/form-data." });
            return;
        }

        try
        {
            var files = ParseMultipartFiles(request);
            if (files.Count == 0)
            {
                await WriteJsonAsync(response, 400, new ErrorResponse { Error = "No files uploaded." });
                return;
            }

            int successCount = 0;
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (Array.IndexOf(AllowedExtensions, ext) < 0)
                {
                    OnUploadError?.Invoke($"Unsupported file type: {ext}");
                    continue;
                }

                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var dateDir = Path.Combine(_legacyDefaultDirectory, today);
                Directory.CreateDirectory(dateDir);

                var destPath = GetUniqueFileName(Path.Combine(dateDir, file.FileName));

                await using (var output = File.Create(destPath))
                {
                    await file.Data.CopyToAsync(output);
                }

                successCount++;
                OnUploadSuccess?.Invoke(destPath);
                Debug.WriteLine($"[UploadServer] H5 Uploaded: {destPath} ({new FileInfo(destPath).Length} bytes)");
            }

            // v0.12: 与旧行为一致 —— 成功时只用 success + count，失败累加到 OnUploadError
            await WriteJsonAsync(response, 200, new { success = true, count = successCount });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UploadServer] H5 Upload error: {ex}");
            OnUploadError?.Invoke(ex.Message);
            await WriteJsonAsync(response, 500, new ErrorResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// 返回内嵌的 upload.html 模板，运行时替换 {{STARTOOLER_BASE}} 占位符为当前服务地址。
    /// HTML 直接打开（无服务端注入）时占位符不被替换，JS 走 fallback 相对路径。
    /// </summary>
    private async Task ServeUploadPageAsync(HttpListenerResponse response)
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Resources", "upload.html");

        try
        {
            string html;
            if (File.Exists(templatePath))
            {
                html = await File.ReadAllTextAsync(templatePath);
                var baseUrl = $"http://{GetLocalIp()}:{Port}";
                html = html.Replace("{{STARTOOLER_BASE}}", baseUrl);
            }
            else
            {
                Debug.WriteLine($"[UploadServer] Template not found: {templatePath}");
                html =
                    "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Upload</title></head>" +
                    "<body style=\"font-family:sans-serif;padding:24px;\">" +
                    "<h2>Upload page unavailable</h2>" +
                    "<p>Template not found: <code>" + templatePath + "</code></p>" +
                    "</body></html>";
            }

            var buffer = Encoding.UTF8.GetBytes(html);
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UploadServer] Serve page error: {ex}");
            try
            {
                response.StatusCode = 500;
                response.Close();
            }
            catch
            {
                // response 已关闭，忽略
            }
        }
    }

    // ========================================================================
    // /api/v1/* 新端点
    // ========================================================================

    private async Task HandleListProjectsAsync(HttpListenerResponse response)
    {
        var projectCfg = await _configService.GetAsync<ProjectConfig>(ConfigKeys.Project);
        var items = new List<ProjectListItem>();

        if (projectCfg != null)
        {
            var currentDir = projectCfg.CurrentDirectory ?? "";

            foreach (var path in projectCfg.RecentDirectories)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!Directory.Exists(path)) continue;  // 失效目录过滤

                long fileCount = 0;
                long sizeBytes = 0;
                try
                {
                    fileCount = await _mediaRepository.CountByProjectAsync(path);
                    sizeBytes = await _mediaRepository.GetLocalSizeAsync(path, null);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UploadServer] stats fail for {path}: {ex.Message}");
                }

                items.Add(new ProjectListItem
                {
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

    private async Task HandleUploadToProjectAsync(
        string projectName, HttpListenerRequest request, HttpListenerResponse response)
    {
        var projectPath = await ResolveProjectPathAsync(projectName);
        if (projectPath == null)
        {
            await WriteJsonAsync(response, 404, new ErrorResponse { Error = $"project '{projectName}' not found" });
            return;
        }

        if (!request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
        {
            await WriteJsonAsync(response, 400, new ErrorResponse { Error = "Invalid content type. Use multipart/form-data." });
            return;
        }

        List<ParsedFile> files;
        try
        {
            files = await Task.Run(() => ParseMultipartFiles(request));
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(response, 400, new ErrorResponse { Error = $"multipart parse failed: {ex.Message}" });
            return;
        }

        if (files.Count == 0)
        {
            await WriteJsonAsync(response, 400, new ErrorResponse { Error = "No files uploaded." });
            return;
        }

        var saved = new List<UploadFileItem>();
        var failed = new List<UploadFileItem>();

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (Array.IndexOf(AllowedExtensions, ext) < 0)
            {
                OnUploadError?.Invoke($"Unsupported file type: {ext}");
                failed.Add(new UploadFileItem { Name = file.FileName, Reason = $"unsupported extension {ext}" });
                continue;
            }

            // 单文件大小限制（500MB）
            long sizeHint = 0;
            try { sizeHint = file.Data.Length; } catch { /* stream 不一定支持 Length */ }
            if (sizeHint > 500L * 1024 * 1024)
            {
                failed.Add(new UploadFileItem { Name = file.FileName, Reason = "exceeds 500MB limit" });
                continue;
            }

            try
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var dateDir = Path.Combine(projectPath, today);
                Directory.CreateDirectory(dateDir);
                var destPath = GetUniqueFileName(Path.Combine(dateDir, file.FileName));

                await using (var output = File.Create(destPath))
                {
                    await file.Data.CopyToAsync(output);
                }

                saved.Add(new UploadFileItem { Name = Path.GetFileName(destPath), Path = destPath });
                OnUploadSuccess?.Invoke(destPath);
                Debug.WriteLine($"[UploadServer] Uploaded: {destPath} ({new FileInfo(destPath).Length} bytes)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UploadServer] save failed for {file.FileName}: {ex.Message}");
                failed.Add(new UploadFileItem { Name = file.FileName, Reason = ex.Message });
            }
        }

        // v0.12: 异步触发扫描（不阻塞响应）
        if (saved.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _mediaRepository.ScanDirectoryAsync(projectPath, progress: null, CancellationToken.None);
                    Debug.WriteLine($"[UploadServer] Scan after upload done: {projectPath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UploadServer] Scan after upload failed: {ex.Message}");
                }
            });
        }

        await WriteJsonAsync(response, 200, new UploadResponse
        {
            Success = true,
            Count = saved.Count,
            Files = saved,
            Failed = failed,
        });
    }

    private async Task<string?> ResolveProjectPathAsync(string name)
    {
        var projectCfg = await _configService.GetAsync<ProjectConfig>(ConfigKeys.Project);
        if (projectCfg == null) return null;

        foreach (var path in projectCfg.RecentDirectories)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var basename = Path.GetFileName(path.TrimEnd('/', '\\'));
            if (string.Equals(name, basename, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, path, StringComparison.OrdinalIgnoreCase))
            {
                return Directory.Exists(path) ? path : null;
            }
        }
        return null;
    }

    private async Task<string?> GetCurrentProjectNameAsync()
    {
        try
        {
            var cfg = await _configService.GetAsync<ProjectConfig>(ConfigKeys.Project);
            if (cfg == null || string.IsNullOrEmpty(cfg.CurrentDirectory)) return null;
            return Path.GetFileName(cfg.CurrentDirectory.TrimEnd('/', '\\'));
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractProjectName(string path)
    {
        // /api/v1/projects/{name}/upload → {name}
        const string prefix = "/api/v1/projects/";
        const string suffix = "/upload";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;
        var name = path.Substring(prefix.Length, path.Length - prefix.Length - suffix.Length);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    // ========================================================================
    // UDP 广播（v0.12 新增）
    // ========================================================================

    private async Task StartUdpBroadcastAsync(CancellationToken ct)
    {
        _udpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var localCt = _udpCts.Token;

        try
        {
            _udpClient = new UdpClient();
            _udpClient.EnableBroadcast = true;

            var endpoint = new IPEndPoint(IPAddress.Broadcast, UdpBroadcastPort);

            while (!localCt.IsCancellationRequested)
            {
                var currentProject = await GetCurrentProjectNameAsync();
                var payload = new UdpBroadcastPayload
                {
                    Service = "starttooler",
                    Version = "0.12",
                    Name = Environment.MachineName,
                    Port = Port,
                    Token = _currentToken,
                    CurrentProject = currentProject ?? "",
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

                try
                {
                    await _udpClient.SendAsync(bytes, endpoint);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UploadServer] UDP send failed: {ex.Message}");
                }

                try
                {
                    await Task.Delay(UdpBroadcastIntervalMs, localCt);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UploadServer] UDP broadcast error: {ex.Message}");
        }
        finally
        {
            try { _udpClient?.Close(); } catch { /* ignore */ }
            _udpClient = null;
        }
    }

    // ========================================================================
    // JSON 响应（v0.12 统一）
    // ========================================================================

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, int status, T payload)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    // ========================================================================
    // 复用的工具（multipart 解析、重命名、IP 获取）
    // ========================================================================

    /// <summary>
    /// 解析 multipart/form-data 请求，提取所有文件。
    /// </summary>
    private static List<ParsedFile> ParseMultipartFiles(HttpListenerRequest request)
    {
        var files = new List<ParsedFile>();

        var contentType = request.ContentType ?? "";
        var boundaryMatch = contentType.Split(new[] { "boundary=" }, StringSplitOptions.None);
        if (boundaryMatch.Length < 2)
            return files;

        var boundary = "--" + boundaryMatch[1].Trim('"');
        var bodyStream = request.InputStream;
        using var ms = new MemoryStream();
        bodyStream.CopyTo(ms);
        var body = ms.ToArray();

        // 走字节级解析（v0.10 已固化的实现，避免早期字符串切分的乱码问题）
        return ParseMultipartFilesByBytes(body, boundary);
    }

    private static List<ParsedFile> ParseMultipartFilesByBytes(byte[] body, string boundary)
    {
        var files = new List<ParsedFile>();
        var boundaryBytes = Encoding.UTF8.GetBytes("\r\n" + boundary);
        var delimiterBytes = Encoding.UTF8.GetBytes(boundary + "\r\n");
        var closeBytes = Encoding.UTF8.GetBytes(boundary + "--");

        var pos = 0;
        while (pos < body.Length - delimiterBytes.Length)
        {
            var idx = IndexOf(body, delimiterBytes, pos);
            if (idx < 0) break;

            var nextIdx = IndexOf(body, delimiterBytes, idx + delimiterBytes.Length);
            if (nextIdx < 0) nextIdx = IndexOf(body, closeBytes, idx);
            if (nextIdx < 0) nextIdx = body.Length;

            var partData = new byte[nextIdx - idx - 2]; // 去掉开头的 \r\n
            Array.Copy(body, idx + 2, partData, 0, partData.Length);

            var partStr = Encoding.UTF8.GetString(partData);
            var headerEnd = partStr.IndexOf("\r\n\r\n");
            if (headerEnd < 0) { pos = nextIdx; continue; }

            var headers = partStr.Substring(0, headerEnd);
            var fileNameMatch = System.Text.RegularExpressions.Regex.Match(headers, @"filename=""([^""]+)""");
            if (!fileNameMatch.Success) { pos = nextIdx; continue; }

            var fileName = Path.GetFileName(fileNameMatch.Groups[1].Value);
            if (string.IsNullOrEmpty(fileName)) { pos = nextIdx; continue; }

            var dataStart = Encoding.UTF8.GetByteCount(partStr.Substring(0, headerEnd + 4));
            var dataLen = partData.Length - dataStart;
            if (dataLen > 2) dataLen -= 2;

            var data = new byte[dataLen];
            Array.Copy(partData, dataStart, data, 0, dataLen);

            files.Add(new ParsedFile(fileName, new MemoryStream(data)));
            pos = nextIdx;
        }

        return files;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { found = false; break; }
            }
            if (found) return i;
        }
        return -1;
    }

    private static string GetUniqueFileName(string path)
    {
        if (!File.Exists(path))
            return path;

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

    private static string GetLocalIp()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !ip.Equals(System.Net.IPAddress.Loopback))
                {
                    return ip.ToString();
                }
            }
            return "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _udpCts?.Dispose();
    }

    private sealed class ParsedFile
    {
        public string FileName { get; }
        public Stream Data { get; }

        public ParsedFile(string fileName, Stream data)
        {
            FileName = fileName;
            Data = data;
        }
    }
}

// =====================================================================
// JSON DTO（v0.12 新增）
// =====================================================================

public sealed class HealthResponse
{
    public bool Ok { get; init; }
    public string Service { get; init; } = "starttooler";
    public string Version { get; init; } = "0.12";
    public string Name { get; init; } = "";
    public int Port { get; init; }
    public string Token { get; init; } = "";
    public string CurrentProject { get; init; } = "";
}

public sealed class ErrorResponse
{
    public string Error { get; init; } = "";
}

public sealed class ProjectListResponse
{
    public List<ProjectListItem> Items { get; init; } = new();
}

public sealed class ProjectListItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string? ProjectName { get; init; }
    public long FileCount { get; init; }
    public long SizeMb { get; init; }
    public bool IsCurrent { get; init; }
}

public sealed class UploadResponse
{
    public bool Success { get; init; }
    public int Count { get; init; }
    public List<UploadFileItem> Files { get; init; } = new();
    public List<UploadFileItem> Failed { get; init; } = new();
}

public sealed class UploadFileItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class UdpBroadcastPayload
{
    public string Service { get; init; } = "starttooler";
    public string Version { get; init; } = "0.12";
    public string Name { get; init; } = "";
    public int Port { get; init; }
    public string Token { get; init; } = "";
    public string CurrentProject { get; init; } = "";
}
