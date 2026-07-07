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

            // 阿里云 SDK 是同步阻塞 IO，放到 Task.Run 让出调用线程
            var result = await Task.Run(() =>
            {
                using var stream = File.OpenRead(localPath);
                var request = new PutObjectRequest(_config.Bucket, objectKey, stream);
                return _client.PutObject(request);
            }, ct);

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
            return new OssUploadResult
            {
                ObjectKey = objectKey,
                Success = false,
                Error = $"OSS 错误 [{ex.ErrorCode}]: {ex.Message}",
            };
        }
        catch (Exception ex)
        {
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

            await Task.Run(() =>
            {
                var ossObject = _client.GetObject(_config.Bucket, objectKey);
                using var requestStream = ossObject.Content;
                using var fileStream = File.OpenWrite(localPath);
                requestStream.CopyTo(fileStream);
            }, ct);
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
