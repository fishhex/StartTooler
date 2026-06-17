using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace StartTooler.Services;

public class HttpServerService : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private bool _isRunning;
    private int _port;
    private string _rootPath = string.Empty;
    private Action<string>? _statusCallback;

    public bool IsRunning => _isRunning;
    public int Port => _port;
    public string RootPath => _rootPath;

    public void SetStatusCallback(Action<string> callback)
    {
        _statusCallback = callback;
    }

    public async Task StartAsync(int port, string rootPath)
    {
        if (_isRunning)
        {
            await StopAsync();
        }

        _port = port;
        _rootPath = rootPath;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");

        try
        {
            _listener.Start();
            _isRunning = true;
            _cts = new CancellationTokenSource();

            _statusCallback?.Invoke($"服务已启动 :{port}");

            _serverTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        _ = Task.Run(() => HandleRequest(context));
                    }
                    catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            });

            // 等待服务器真正启动
            await Task.Delay(100);
        }
        catch (HttpListenerException ex)
        {
            _isRunning = false;
            _statusCallback?.Invoke($"启动失败: {ex.Message}");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _cts?.Cancel();

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }

        if (_serverTask != null)
        {
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { }
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _isRunning = false;

        _statusCallback?.Invoke("服务已停止");
    }

    private async void HandleRequest(HttpListenerContext context)
    {
        var response = context.Response;
        var requestPath = context.Request.Url?.AbsolutePath ?? "/";

        try
        {
            // 移除前导斜杠并转换为文件路径
            var relativePath = requestPath.TrimStart('/');
            var filePath = Path.Combine(_rootPath, relativePath);

            // 安全检查：防止路径遍历攻击
            var fullRootPath = Path.GetFullPath(_rootPath);
            var fullFilePath = Path.GetFullPath(filePath);
            if (!fullFilePath.StartsWith(fullRootPath))
            {
                response.StatusCode = 403;
                var errorBytes = System.Text.Encoding.UTF8.GetBytes("Forbidden");
                response.ContentType = "text/plain";
                await response.OutputStream.WriteAsync(errorBytes);
                response.Close();
                return;
            }

            if (Directory.Exists(filePath))
            {
                // 返回目录列表
                var html = GenerateDirectoryHtml(filePath, relativePath);
                var buffer = System.Text.Encoding.UTF8.GetBytes(html);
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer);
            }
            else if (File.Exists(filePath))
            {
                // 返回文件
                var fileInfo = new FileInfo(filePath);
                response.ContentType = GetMimeType(filePath);
                response.ContentLength64 = fileInfo.Length;

                if (requestPath.EndsWith(".mp4") || requestPath.EndsWith(".mov") ||
                    requestPath.EndsWith(".avi") || requestPath.EndsWith(".mkv"))
                {
                    response.Headers.Add("Accept-Ranges", "bytes");
                }

                await using var fs = File.OpenRead(filePath);
                await fs.CopyToAsync(response.OutputStream);
            }
            else
            {
                response.StatusCode = 404;
                var errorBytes = System.Text.Encoding.UTF8.GetBytes("Not Found");
                response.ContentType = "text/plain";
                await response.OutputStream.WriteAsync(errorBytes);
            }
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            var errorBytes = System.Text.Encoding.UTF8.GetBytes($"Error: {ex.Message}");
            response.ContentType = "text/plain";
            await response.OutputStream.WriteAsync(errorBytes);
        }
        finally
        {
            response.Close();
        }
    }

    private string GenerateDirectoryHtml(string directoryPath, string urlPath)
    {
        var dirInfo = new DirectoryInfo(directoryPath);
        var items = new List<string>();

        // 父目录链接
        if (!string.IsNullOrEmpty(urlPath))
        {
            var parts = urlPath.Split('/');
            var parentPath = parts.Length > 1
                ? "/" + string.Join("/", parts.Take(parts.Length - 1))
                : "";
            items.Add($@"<li><a href=""{parentPath}"">📁 ../</a></li>");
        }

        // 子目录
        foreach (var dir in dirInfo.GetDirectories())
        {
            if (dir.Name.StartsWith(".")) continue;
            var linkPath = string.IsNullOrEmpty(urlPath) ? dir.Name : $"{urlPath}/{dir.Name}";
            items.Add($@"<li><a href=""/{linkPath}"">📁 {dir.Name}/</a></li>");
        }

        // 文件
        foreach (var file in dirInfo.GetFiles())
        {
            if (file.Name.StartsWith(".")) continue;
            var linkPath = string.IsNullOrEmpty(urlPath) ? file.Name : $"{urlPath}/{file.Name}";
            var icon = GetFileIcon(file.Extension);
            var size = FormatFileSize(file.Length);
            items.Add($@"<li><a href=""/{linkPath}"">{icon} {file.Name} <span class=""size"">({size})</span></a></li>");
        }

        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <title>{urlPath ?? "Root"}</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, sans-serif; margin: 40px; background: #1a1a2e; color: #eee; }}
        h1 {{ color: #e94560; }}
        ul {{ list-style: none; padding: 0; }}
        li {{ padding: 8px 0; border-bottom: 1px solid #333; }}
        a {{ color: #0f9bff; text-decoration: none; }}
        a:hover {{ color: #e94560; }}
        .size {{ color: #888; font-size: 0.9em; }}
        .header {{ background: #16213e; padding: 20px; border-radius: 8px; margin-bottom: 20px; }}
    </style>
</head>
<body>
    <div class=""header"">
        <h1>📂 {urlPath ?? "文件服务"}</h1>
        <p>访问地址: http://localhost:{_port}/{urlPath}</p>
    </div>
    <ul>
        {string.Join("\n", items)}
    </ul>
</body>
</html>";
    }

    private static string GetFileIcon(string extension)
    {
        return extension.ToLower() switch
        {
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" => "🎬",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "🖼️",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" => "🎵",
            ".pdf" => "📄",
            ".zip" or ".rar" or ".7z" => "📦",
            _ => "📄"
        };
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLower();
        return ext switch
        {
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
