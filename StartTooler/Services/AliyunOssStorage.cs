using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aliyun.OSS;
using Aliyun.OSS.Common;

namespace StartTooler.Services;

/// <summary>
/// 阿里云 OSS 实现。桶为私有：
///   - Upload：服务端 PUT（不需要签名 URL）
///   - GetCover：签发带过期时间的 GET 签名 URL
///   - Download：服务端用凭据流式 GET 落盘（不需要外部签名）
///
/// 凭据来源：构造时传入 OssConfig（来自 Settings 持久化的 config）。
/// 注意：OssConfig.AccessKeySecret 目前为明文持久化（v0.1），
///       后续应迁移到安全存储（Keychain / DPAPI / 加密 config）。
/// </summary>
public sealed class AliyunOssStorage : IOssStorage, IDisposable
{
    private readonly OssClient _client;
    private readonly OssConfig _config;

    public string Provider => "Aliyun";

    public AliyunOssStorage(OssConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        if (string.IsNullOrWhiteSpace(_config.Region))
            throw new InvalidOperationException("OSS Region 未配置");
        if (string.IsNullOrWhiteSpace(_config.Bucket))
            throw new InvalidOperationException("OSS Bucket 未配置");
        if (string.IsNullOrWhiteSpace(_config.AccessKeyId))
            throw new InvalidOperationException("OSS AccessKey 未配置");
        if (string.IsNullOrWhiteSpace(_config.AccessKeySecret))
            throw new InvalidOperationException("OSS SecretKey 未配置");

        // endpoint = https://oss-{region}.aliyuncs.com
        var endpoint = BuildEndpoint(_config.Region);
        _client = new OssClient(endpoint, _config.AccessKeyId, _config.AccessKeySecret);

        // ① 构造时打印解析后的真实 endpoint + 用户传入的 region。
        // 关键：之前出现过 region="oss-cn-hangzhou.aliyuncs.com" 拼出 oss-oss-cn-hangzhou.aliyuncs.com.aliyuncs.com 的事故，
        // 这一行就是用来一眼确认 BuildEndpoint 输出对不对。
        Debug.WriteLine($"[OSS] ctor endpoint={endpoint}, bucket={_config.Bucket}, region(in)='{_config.Region}'");
    }

    public async Task<OssUploadResult> UploadAsync(string localPath, string objectKey, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
        {
            return new OssUploadResult
            {
                ObjectKey = objectKey,
                Success = false,
                Error = $"本地文件不存在：{localPath}",
            };
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            // v0.11 spec/09 §5: PutObject 走重试包装,瞬时错误指数退避 1s/3s/9s,永久错误(鉴权/404 等)立即抛
            var result = await ExecuteWithRetryAsync(
                operation: () => Task.Run(() =>
                {
                    using var stream = File.OpenRead(localPath);
                    var request = new PutObjectRequest(_config.Bucket, objectKey, stream);
                    return _client.PutObject(request);
                }, ct),
                operationName: $"Upload({objectKey})",
                ct: ct);

            return new OssUploadResult
            {
                ObjectKey = objectKey,
                ETag = result?.ETag,
                Success = true,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OssException ex)
        {
            // ExecuteWithRetryAsync 抛出的永久错误走到这里(瞬时错误已在重试内消化)
            return new OssUploadResult
            {
                ObjectKey = objectKey,
                Success = false,
                Error = $"OSS 错误 [{ex.ErrorCode}]: {ex.Message}",
            };
        }
        catch (Exception ex)
        {
            // 重试耗尽 / 其他异常
            return new OssUploadResult
            {
                ObjectKey = objectKey,
                Success = false,
                Error = $"上传异常：{ex.Message}",
            };
        }
    }

    public Task<string> GetCoverUrlAsync(string objectKey, TimeSpan expiry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 私有桶 → 必须签发带过期的 GET URL
        // 阿里云 SDK 签名 URL 用绝对 DateTime 表达过期时刻。
        // 用本地时间（OSS 服务端按本地时区校验，不能用 UTC）。
        var expiresAt = DateTime.Now.Add(expiry);
        var uri = _client.GeneratePresignedUri(_config.Bucket, objectKey, expiresAt);

        return Task.FromResult(uri.ToString());
    }

    public async Task DownloadAsync(string objectKey, string localPath, CancellationToken ct = default)
    {
        var localDir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
        {
            Directory.CreateDirectory(localDir);
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            // v0.11 spec/09 §5: GetObject 走重试包装
            await ExecuteWithRetryAsync(
                operation: () => Task.Run(() =>
                {
                    var ossObject = _client.GetObject(_config.Bucket, objectKey);
                    using var requestStream = ossObject.Content;
                    using var fileStream = File.OpenWrite(localPath);
                    requestStream.CopyTo(fileStream);
                }, ct),
                operationName: $"Download({objectKey})",
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            // 取消时清掉可能已创建的半截文件
            if (File.Exists(localPath))
            {
                try { File.Delete(localPath); } catch { /* ignore */ }
            }
            throw;
        }
    }

    // ==================== Multipart（断点续传走这一组） ====================

    /// <summary>multipart 阈值 5MB。&lt; 5MB 走单 PUT，&gt;= 走分片。</summary>
    public long MultipartThresholdBytes => 5 * 1024 * 1024;

    /// <summary>分片大小 5MB。OSS 限制每个分片 1B-5GB，5MB 是较优的平衡点。</summary>
    private const int PartSizeBytes = 5 * 1024 * 1024;

    public async Task<MultipartHandle> InitiateMultipartAsync(string objectKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var result = await Task.Run(() =>
            {
                var req = new InitiateMultipartUploadRequest(_config.Bucket, objectKey);
                return _client.InitiateMultipartUpload(req);
            }, ct);

            var handle = new MultipartHandle
            {
                ObjectKey = objectKey,
                UploadId = result.UploadId,
                PartSize = PartSizeBytes,
            };

            // ② Initiate 成功
            Debug.WriteLine($"[OSS] InitiateMultipart ok: objectKey={objectKey}, uploadId={ShortId(result.UploadId)}");
            return handle;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OssException ex)
        {
            // ③ Initiate 失败
            Debug.WriteLine($"[OSS] InitiateMultipart fail: objectKey={objectKey}, code={ex.ErrorCode}, msg={ex.Message}");
            throw;
        }
    }

    public async Task<PartETag> UploadPartAsync(MultipartHandle handle, int partNumber, Stream data, long length, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 阿里云 SDK 的 UploadPartRequest 需要自己持有 stream 生命周期，Task.Run 内部传入。
        var result = await Task.Run(() =>
        {
            var req = new UploadPartRequest(_config.Bucket, handle.ObjectKey, handle.UploadId)
            {
                InputStream = data,
                PartSize = length,
                PartNumber = partNumber,
            };
            return _client.UploadPart(req);
        }, ct);

        // ④ 每片上传结果。ETag 截前 8 位够去 OSS 控制台定位。
        Debug.WriteLine($"[OSS] UploadPart: #{partNumber}, length={length}B, etag={ShortId(result.PartETag?.ETag)}");

        return new PartETag
        {
            PartNumber = partNumber,
            ETag = result.PartETag?.ETag ?? "",
        };
    }

    public async Task<IReadOnlyList<PartETag>> ListPartsAsync(MultipartHandle handle, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var result = await Task.Run(() =>
        {
            var req = new ListPartsRequest(_config.Bucket, handle.ObjectKey, handle.UploadId);
            return _client.ListParts(req);
        }, ct);

        var parts = new List<PartETag>(result.Parts.Count());
        foreach (var p in result.Parts)
        {
            parts.Add(new PartETag
            {
                PartNumber = p.PartNumber,
                ETag = p.ETag,
            });
        }

        // ⑤ OSS 端已存分片数（续传时这个最关键）
        Debug.WriteLine($"[OSS] ListParts ok: uploadId={ShortId(handle.UploadId)}, count={parts.Count}");

        return parts;
    }

    public async Task CompleteMultipartAsync(MultipartHandle handle, IReadOnlyList<PartETag> parts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            // SDK 自己的 PartETag 与我们的 PartETag 同名不同类型，要做一次映射。
            var req = new CompleteMultipartUploadRequest(_config.Bucket, handle.ObjectKey, handle.UploadId);
            foreach (var p in parts)
            {
                req.PartETags.Add(new Aliyun.OSS.PartETag(p.PartNumber, p.ETag));
            }
            _client.CompleteMultipartUpload(req);
        }, ct);

        // ⑥ 合并完成
        Debug.WriteLine($"[OSS] CompleteMultipart: objectKey={handle.ObjectKey}, parts={parts.Count}");
    }

    public async Task AbortMultipartAsync(MultipartHandle handle, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            var req = new AbortMultipartUploadRequest(_config.Bucket, handle.ObjectKey, handle.UploadId);
            _client.AbortMultipartUpload(req);
        }, ct);

        // ⑦ 放弃 multipart session。v0.1 没有调用方（取消时 job 留底不 Abort），这里只是占位实现。
        Debug.WriteLine($"[OSS] AbortMultipart: objectKey={handle.ObjectKey}, uploadId={ShortId(handle.UploadId)}");
    }

    public async Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // v0.8 垃圾筒彻底删除用（spec doc/14-delete-and-trash.md §3.2）。
        // 阿里云 SDK DeleteObject 对不存在的 key 不抛异常（幂等），无需额外检查。
        await Task.Run(() =>
        {
            var req = new DeleteObjectRequest(_config.Bucket, objectKey);
            _client.DeleteObject(req);
        }, ct);

        Debug.WriteLine($"[OSS] DeleteObject: objectKey={objectKey}");
    }

    /// <summary>
    /// v0.11 §4.2: 用 DoesBucketExist 验证 Endpoint / 凭据 / Bucket 三者联通。
    /// true = 全部正常；false = 任一项失败（SDK 内部已 catch 网络 / 鉴权错误并返回 false）。
    /// 不抛异常 —— 调用方在 UI 上把 false 翻译成"连接失败"提示。
    /// </summary>
    public Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            try
            {
                var ok = _client.DoesBucketExist(_config.Bucket);
                Debug.WriteLine($"[OSS] TestConnection: bucket={_config.Bucket}, ok={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OSS] TestConnection exception: {ex.Message}");
                return false;
            }
        }, ct);
    }

    /// <summary>ETag / UploadId 太长打日志看花眼，截前 16 字符 + ... 标识。</summary>
    private static string ShortId(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "<null>";
        return s.Length <= 16 ? s : s.Substring(0, 16) + "...";
    }

    /// <summary>
    /// 把对象 key 与 PathPrefix 拼接。PathPrefix 可为空。
    /// </summary>
    public static string BuildObjectKey(string pathPrefix, string relativePath)
    {
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');

        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            return relativePath;
        }

        var prefix = pathPrefix.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(prefix) ? relativePath : $"{prefix}/{relativePath}";
    }

    private static string BuildEndpoint(string region)
    {
        region = region.Trim();
        if (region.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            region.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return region.TrimEnd('/');
        }
        return $"https://oss-{region}.aliyuncs.com";
    }

    public void Dispose()
    {
        // OssClient 内部使用 HttpClient，.NET 5+ 会自动管理
        // 显式置空便于 GC
    }

    // ==================== v0.11 spec/09 §5: OSS 重试 ====================

    /// <summary>
    /// 包装单次 OSS 调用:瞬时错误按 1s/3s/9s 指数退避重试 3 次,永久错误立即抛。
    /// CancellationToken 一律透传——取消时不退避。
    /// </summary>
    /// <param name="operationName">操作名,用于 trace 日志（如 "Upload" / "Download" / "InitiateMultipart"）</param>
    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken ct)
    {
        const int maxRetries = 3;
        int delaySeconds = 1;

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 0)
                {
                    Trace.WriteLine($"[OSS] {operationName} 第 {attempt} 次重试...");
                }
                return await operation();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsPermanentError(ex))
            {
                Trace.WriteLine($"[OSS] {operationName} 永久错误: [{GetErrorCode(ex)}] {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries)
                {
                    Trace.WriteLine($"[OSS] {operationName} 重试 {maxRetries} 次后仍失败: {ex.Message}");
                    throw;
                }
                Trace.WriteLine($"[OSS] {operationName} 瞬时错误, {delaySeconds}s 后重试: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                delaySeconds *= 3;  // 1 → 3 → 9
            }
        }
    }

    /// <summary>无返回值的重试包装（用于 Download/Delete 等 void 风格操作）。</summary>
    private async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        string operationName,
        CancellationToken ct)
    {
        await ExecuteWithRetryAsync<bool>(
            async () => { await operation(); return true; },
            operationName, ct);
    }

    /// <summary>
    /// 永久错误判定(spec §5.1):鉴权失败 / 桶不存在 / 对象不存在 / 拒绝访问,不应重试。
    /// 限流 429/503 不在此列,会被当作瞬时错误走指数退避(用更短退避 3/9/27 也可,这里统一走 1/3/9 简单点)。
    /// </summary>
    private static bool IsPermanentError(Exception ex)
    {
        if (ex is OssException ossEx)
        {
            var code = ossEx.ErrorCode ?? string.Empty;
            return code.Contains("InvalidAccessKey")
                || code.Contains("SignatureDoesNotMatch")
                || code.Contains("NoSuchBucket")
                || code.Contains("NoSuchKey")
                || code.Contains("AccessDenied");
        }
        return false;
    }

    private static string GetErrorCode(Exception ex)
        => ex is OssException ossEx ? (ossEx.ErrorCode ?? "(null)") : ex.GetType().Name;
}

/// <summary>
/// 限流 Stream：只允许从 inner 读最多 <c>length</c> 字节，然后返回 EOF。
/// 用在 <see cref="IOssStorage.UploadPartAsync"/>：调用方从一个长文件流里切片上传，
/// 包一层 BoundedReadStream 后阿里云 SDK 读够 length 字节就会停。
///
/// 关键不变量：inner 流的位置由调用方负责 seek 到正确 offset，
/// 本类只负责「读够 N 字节就停」，同时透传 inner 的 seekable 能力让阿里云 SDK
/// 内部的 PartialWrapperStream 校验通过（之前 CanSeek=false 会触发
/// "Base stream of PartialWrapperStream must be seekable" 报错）。
/// </summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _initialLength;
    private long _remaining;

    public BoundedReadStream(Stream inner, long length)
    {
        if (inner == null) throw new ArgumentNullException(nameof(inner));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        _inner = inner;
        _initialLength = length;
        _remaining = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _initialLength;
    public override long Position
    {
        get => _initialLength - _remaining;
        set
        {
            if (value < 0 || value > _initialLength)
                throw new ArgumentOutOfRangeException(nameof(value));
            var delta = value - Position;
            _inner.Seek(delta, SeekOrigin.Current);
            _remaining -= delta;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        var toRead = (int)Math.Min(count, _remaining);
        var n = _inner.Read(buffer, offset, toRead);
        _remaining -= n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _initialLength + offset,
            _ => throw new ArgumentException($"Unknown SeekOrigin: {origin}", nameof(origin)),
        };
        Position = newPos;
        return newPos;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
