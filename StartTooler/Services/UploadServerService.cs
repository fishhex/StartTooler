using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Services;

/// <summary>
/// 内置 HTTP 上传服务，用 HttpListener 接收局域网文件上传。
/// 上传路径：$CurrentDirectory/YYYY-MM-DD/原始文件名
/// </summary>
public class UploadServerService : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly string _currentDirectory;
    private Task? _listenTask;

    // 允许上传的文件扩展名
    private static readonly string[] AllowedExtensions =
    {
        ".jpg", ".jpeg", ".png", ".raw", ".avi", ".mp4", ".mov", ".mkv", ".webm", ".m4v", ".mpg", ".mpeg"
    };

    public int Port { get; private set; }
    public string UploadUrl => $"http://{GetLocalIp()}:{Port}/upload";

    public event Action<string>? OnUploadSuccess;
    public event Action<string>? OnUploadError;

    public UploadServerService(string currentDirectory)
    {
        _currentDirectory = currentDirectory;
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
        await Task.CompletedTask;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        _cts = null;
        Debug.WriteLine("[UploadServer] Stopped");
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

        // 只接受 POST /upload
        if (request.HttpMethod != "POST" || !request.Url?.AbsolutePath.Equals("/upload", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }

        // 检查 Content-Type
        if (!request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
        {
            await WriteResponseAsync(response, 400, "{\"error\":\"Invalid content type. Use multipart/form-data.\"}");
            return;
        }

        try
        {
            var files = ParseMultipartFiles(request);
            if (files.Count == 0)
            {
                await WriteResponseAsync(response, 400, "{\"error\":\"No files uploaded.\"}");
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

                // 按日期归档
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var dateDir = Path.Combine(_currentDirectory, today);
                Directory.CreateDirectory(dateDir);

                var destPath = GetUniqueFileName(Path.Combine(dateDir, file.FileName));

                await using (var output = File.Create(destPath))
                {
                    await file.Data.CopyToAsync(output);
                }

                successCount++;
                OnUploadSuccess?.Invoke(destPath);
                Debug.WriteLine($"[UploadServer] Uploaded: {destPath} ({new FileInfo(destPath).Length} bytes)");
            }

            await WriteResponseAsync(response, 200, $"{{\"success\":true,\"count\":{successCount}}}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UploadServer] Upload error: {ex}");
            OnUploadError?.Invoke(ex.Message);
            await WriteResponseAsync(response, 500, $"{{\"error\":\"{EscapeJson(ex.Message)}\"}}");
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, int statusCode, string body)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var buffer = Encoding.UTF8.GetBytes(body);
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

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

        // 按 boundary 分割 parts
        var parts = Encoding.UTF8.GetString(body).Split(new[] { boundary }, StringSplitOptions.None);

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || part.StartsWith("--"))
                continue;

            // 每个 part 包含 header + blank line + data
            var idx = part.IndexOf("\r\n\r\n");
            if (idx < 0) continue;

            var headers = part.Substring(0, idx);
            var dataStr = part.Substring(idx + 4);

            // 去掉末尾的 \r\n
            if (dataStr.EndsWith("\r\n"))
                dataStr = dataStr.Substring(0, dataStr.Length - 2);

            // 解析 Content-Disposition
            var nameMatch = System.Text.RegularExpressions.Regex.Match(headers, @"name=""([^""]+)""");
            var fileNameMatch = System.Text.RegularExpressions.Regex.Match(headers, @"filename=""([^""]+)""");

            if (!fileNameMatch.Success)
                continue; // 非文件字段跳过

            var fieldName = nameMatch.Success ? nameMatch.Groups[1].Value : "";
            var fileName = fileNameMatch.Groups[1].Value;

            if (string.IsNullOrEmpty(fileName))
                continue;

            // 文件名含路径时取最后部分
            fileName = Path.GetFileName(fileName);

            // 把 dataStr (字符串) 转回字节（假设 UTF-8，实际文件内容可能乱码但 dataStr 只是边界分割用）
            // 注意：multipart 里的二进制文件不能简单地用 UTF-8 解码，这里需要用原始字节位置
            // 重新按字节位置找
            var partStart = Encoding.UTF8.GetString(body).IndexOf(boundary + "\r\n");
            // 更准确的方式：用字节范围
            files.Clear(); // 重新解析，用字节偏移
            return ParseMultipartFilesByBytes(body, boundary);
        }

        return files;
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

            // 二进制数据在 partData 里 headerEnd + 4 之后
            var dataStart = Encoding.UTF8.GetByteCount(partStr.Substring(0, headerEnd + 4));
            var dataLen = partData.Length - dataStart;
            // 去掉末尾的 \r\n
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