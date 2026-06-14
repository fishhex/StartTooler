using System;
using System.IO;
using System.Threading.Tasks;
using Aliyun.OSS;
using Aliyun.OSS.Common;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.Services;

/// <summary>
/// 阿里云 OSS 上传服务
/// </summary>
public class OssUploadService
{
    private readonly OssClient _ossClient;
    private readonly string _bucketName;
    private readonly string _dir;

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
            await Task.Run(() =>
            {
                _ossClient.PutObject(_bucketName, objectKey, filePath);
            });
            return objectKey;
        }
        catch (OssException ex)
        {
            Console.WriteLine($"OSS 上传失败 [{fileName}]: {ex.Message}");
            throw;
        }
    }

    private static string ComputeFileMd5(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
