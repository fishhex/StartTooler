using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Services;

/// <summary>
/// 对象存储抽象。当前实现：AliyunOssStorage。
/// 为未来扩展（腾讯云 COS / AWS S3 / 自定义）预留接口边界。
///
/// 桶为私有：
///   - Upload 走服务端凭据直传（PUT object），不需要签名 URL
///   - GetCover / Download 桶外不可直链，必须返回带过期时间的签名 URL 给上层使用
/// </summary>
public interface IOssStorage
{
    /// <summary>
    /// v0.11 §4.2: 验证当前凭据 / Region / Bucket 联通性。
    /// 返回 true = 三者都通；false = 任意一项失败（不抛异常，失败原因由调用方记录）。
    /// 实现：阿里云走 DoesBucketExistAsync。
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);


    /// <summary>
    /// 上传本地文件到 OSS。内部按文件大小自动分流：
    ///   - 小于 <see cref="MultipartThresholdBytes"/>：走单 PUT（UploadSingleAsync）
    ///   - 大于阈值：走 multipart（Initiate / UploadPart × N / Complete）
    /// </summary>
    /// <param name="localPath">本地文件绝对路径。</param>
    /// <param name="objectKey">远端对象 key（相对 bucket 根，已包含 PathPrefix）。</param>
    /// <param name="ct">取消令牌。注：Aliyun SDK 不会在分片传输中段响应 CT，cancel 后当前分片会跑完。</param>
    /// <returns>上传结果（远端 key + ETag）。</returns>
    Task<OssUploadResult> UploadAsync(string localPath, string objectKey, CancellationToken ct = default);

    /// <summary>
    /// 获取对象封面的临时签名 URL（私有桶场景下唯一可读方式）。
    /// 调用方应在 expiry 之内使用；过期后需重新获取。
    /// </summary>
    Task<string> GetCoverUrlAsync(string objectKey, TimeSpan expiry, CancellationToken ct = default);

    /// <summary>
    /// 下载对象到本地文件。私有桶场景下 SDK 用内部凭据直接流式拉取。
    /// </summary>
    Task DownloadAsync(string objectKey, string localPath, CancellationToken ct = default);

    /// <summary>
    /// 从 OSS 删除单个对象。调用方确认桶内对象存在。
    /// v0.8 用于垃圾筒彻底删除（spec doc/14-delete-and-trash.md §3.1）。
    /// 阿里云 SDK 对不存在的 key 不抛异常（幂等），调用方无需额外检查。
    /// </summary>
    Task DeleteObjectAsync(string objectKey, CancellationToken ct = default);

    /// <summary>
    /// multipart 阈值。文件 ≥ 这个大小走分片上传，&lt; 走单 PUT。
    /// 默认 5MB，符合阿里云推荐值。
    /// </summary>
    long MultipartThresholdBytes { get; }

    // ===== Multipart API（断点续传走这一组） =====

    /// <summary>
    /// 初始化一个 multipart upload session。
    /// 调用方拿到 handle 后通过 <see cref="UploadPartAsync"/> 逐片上传，
    /// 最后 <see cref="CompleteMultipartAsync"/> 合并，或 <see cref="AbortMultipartAsync"/> 放弃。
    /// </summary>
    Task<MultipartHandle> InitiateMultipartAsync(string objectKey, CancellationToken ct = default);

    /// <summary>
    /// 上传单个分片。返回 (PartNumber, ETag) 给 Complete 用。
    /// </summary>
    /// <param name="partNumber">从 1 开始的分片号。</param>
    /// <param name="data">分片内容（stream 会被读完）。</param>
    /// <param name="length">分片字节数（必须等于 PartSize，最后一片除外）。</param>
    Task<PartETag> UploadPartAsync(MultipartHandle handle, int partNumber, Stream data, long length, CancellationToken ct = default);

    /// <summary>
    /// 列出已上传的分片。续传时调这个拿到断点之前已成功的分片，跳过重传。
    /// </summary>
    Task<IReadOnlyList<PartETag>> ListPartsAsync(MultipartHandle handle, CancellationToken ct = default);

    /// <summary>
    /// 合并所有分片，结束 multipart session。
    /// </summary>
    Task CompleteMultipartAsync(MultipartHandle handle, IReadOnlyList<PartETag> parts, CancellationToken ct = default);

    /// <summary>
    /// 放弃 multipart session，OSS 端清理已上传分片。
    /// </summary>
    Task AbortMultipartAsync(MultipartHandle handle, CancellationToken ct = default);
}

public sealed class OssUploadResult
{
    /// <summary>远端对象 key（已包含 PathPrefix）。</summary>
    public string ObjectKey { get; init; } = "";

    /// <summary>OSS 返回的 ETag。失败时为 null。</summary>
    public string? ETag { get; init; }

    /// <summary>上传是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>失败时的错误信息。成功时为 null。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// multipart session 句柄。由 <see cref="IOssStorage.InitiateMultipartAsync"/> 产生，
/// 后续 UploadPart / ListParts / Complete / Abort 都拿这个。
/// </summary>
public sealed class MultipartHandle
{
    public string ObjectKey { get; init; } = "";
    public string UploadId { get; init; } = "";
    /// <summary>分片大小（字节），由 storage 实现决定（默认 5MB）。</summary>
    public int PartSize { get; init; }
}

/// <summary>
/// 单个分片上传后的返回，Complete 时按 PartNumber 升序传入。
/// </summary>
public sealed class PartETag
{
    public int PartNumber { get; init; }
    public string ETag { get; init; } = "";
}
