using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Aliyun.OSS;
using Aliyun.OSS.Common;
using StartTooler.Models;

namespace StartTooler.Services;

/// <summary>
/// 上传进度回调
/// </summary>
public class UploadProgressEventArgs : EventArgs
{
    public string FileName { get; }
    public int FileProgress { get; } // 0-100

    public UploadProgressEventArgs(string fileName, int fileProgress)
    {
        FileName = fileName;
        FileProgress = fileProgress;
    }
}

/// <summary>
/// 阿里云 OSS 上传服务
/// </summary>
public class OssUploadService
{
    private readonly OssClient _ossClient;
    private readonly string _bucketName;
    private readonly string _dir;

    // 超过 5MB 使用分片上传
    private const long MultipartSizeThreshold = 5 * 1024 * 1024;
    // 分片大小 5MB
    private const long PartSize = 5 * 1024 * 1024;

    public event EventHandler<UploadProgressEventArgs>? ProgressChanged;

    public OssUploadService(CloudStorageSetting setting)
    {
        _ossClient = new OssClient(setting.Endpoint, setting.AccessKeyId, setting.AccessKeySecret);
        _bucketName = setting.BucketName;
        _dir = string.IsNullOrWhiteSpace(setting.Dir) ? string.Empty : setting.Dir.TrimEnd('/') + "/";
    }

    /// <summary>
    /// 上传单个文件
    /// 上传路径规则: {dir}/{fileModifiedDate}/{md5}.{ext}
    /// </summary>
    public async Task<string> UploadAsync(string filePath, DateTime modifiedTime)
    {
        var fileName = Path.GetFileName(filePath);
        var extension = Path.GetExtension(fileName);
        var md5 = ComputeFileMd5(filePath);
        var dateFolder = modifiedTime.ToString("yyyy-MM-dd");
        var objectKey = $"{_dir}{dateFolder}/{md5}{extension}";

        try
        {
            var fileInfo = new FileInfo(filePath);

            // 大文件使用分片上传
            if (fileInfo.Length > MultipartSizeThreshold)
            {
                await UploadMultipartAsync(filePath, objectKey, fileName);
            }
            else
            {
                // 小文件直接上传，模拟进度
                for (int i = 0; i <= 100; i += 20)
                {
                    OnProgressChanged(fileName, i);
                    await Task.Delay(50);
                }
                await Task.Run(() =>
                {
                    _ossClient.PutObject(_bucketName, objectKey, filePath);
                });
                OnProgressChanged(fileName, 100);
            }

            return objectKey;
        }
        catch (OssException ex)
        {
            Console.WriteLine($"OSS 上传失败 [{fileName}]: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 分片上传大文件
    /// </summary>
    private async Task UploadMultipartAsync(string filePath, string objectKey, string fileName)
    {
        // 初始化分片上传
        var initRequest = new InitiateMultipartUploadRequest(_bucketName, objectKey);
        var initResult = await Task.Run(() => _ossClient.InitiateMultipartUpload(initRequest));
        var uploadId = initResult.UploadId;

        try
        {
            // 计算分片数量
            var fileSize = new FileInfo(filePath).Length;
            var partCount = (int)((fileSize + PartSize - 1) / PartSize);
            var partETags = new List<PartETag>();

            using (var fs = File.Open(filePath, FileMode.Open, FileAccess.Read))
            {
                for (var i = 0; i < partCount; i++)
                {
                    var skipBytes = PartSize * i;
                    fs.Seek(skipBytes, 0);

                    // 计算当前分片大小（最后一片可能小于 PartSize）
                    var size = (PartSize < fileSize - skipBytes) ? PartSize : (fileSize - skipBytes);

                    var request = new UploadPartRequest(_bucketName, objectKey, uploadId)
                    {
                        InputStream = fs,
                        PartSize = size,
                        PartNumber = i + 1
                    };

                    var result = await Task.Run(() => _ossClient.UploadPart(request));
                    partETags.Add(result.PartETag);

                    // 报告进度
                    var progress = (int)((i + 1) * 100.0 / partCount);
                    OnProgressChanged(fileName, progress);
                }
            }

            // 完成分片上传
            var completeRequest = new CompleteMultipartUploadRequest(_bucketName, objectKey, uploadId);
            foreach (var partETag in partETags)
            {
                completeRequest.PartETags.Add(partETag);
            }
            await Task.Run(() => _ossClient.CompleteMultipartUpload(completeRequest));
        }
        catch
        {
            // 上传失败时中止分片上传，释放资源
            await Task.Run(() =>
            {
                var abortRequest = new AbortMultipartUploadRequest(_bucketName, objectKey, uploadId);
                _ossClient.AbortMultipartUpload(abortRequest);
            });
            throw;
        }
    }

    protected virtual void OnProgressChanged(string fileName, int progress)
    {
        ProgressChanged?.Invoke(this, new UploadProgressEventArgs(fileName, progress));
    }

    private static string ComputeFileMd5(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
