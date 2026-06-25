using System;
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
    /// 上传本地文件到 OSS。
    /// </summary>
    /// <param name="localPath">本地文件绝对路径。</param>
    /// <param name="objectKey">远端对象 key（相对 bucket 根，已包含 PathPrefix）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>上传结果（远端 key + ETag）。</returns>
    Task<OssUploadResult> UploadAsync(string localPath, string objectKey, CancellationToken ct = default);

    /// <summary>
    /// 获取对象封面的临时签名 URL（私有桶场景下唯一可读方式）。
    /// 调用方应在 expiry 之内使用；过期后需重新获取。
    /// </summary>
    /// <param name="objectKey">远端对象 key。</param>
    /// <param name="expiry">签名 URL 有效期。建议 5 分钟 ~ 1 小时。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可被 HTTP 客户端直接 GET 的 URL（含签名参数）。</returns>
    Task<string> GetCoverUrlAsync(string objectKey, TimeSpan expiry, CancellationToken ct = default);

    /// <summary>
    /// 下载对象到本地文件。私有桶场景下 SDK 用内部凭据直接流式拉取，
    /// 不需要外部再请求签名 URL。
    /// </summary>
    /// <param name="objectKey">远端对象 key。</param>
    /// <param name="localPath">本地目标路径（目录必须存在）。</param>
    /// <param name="ct">取消令牌。</param>
    Task DownloadAsync(string objectKey, string localPath, CancellationToken ct = default);
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
