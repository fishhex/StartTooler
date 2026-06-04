using System;
using System.IO;
using System.Security.Cryptography;

namespace StartTooler.Services;

public static class MediaFileService
{
    /// <summary>
    /// 获取文件特征码
    /// </summary>
    public static string GetMultiExposureSignature(string filePath)
    {
        if (!File.Exists(filePath)) return string.Empty;

        var fileInfo = new FileInfo(filePath);
        long fileSize = fileInfo.Length;
        
        // 1. 引入文件最后修改时间（精确到 100 纳秒），这是应对高频连拍的第一道防线
        long lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;

        // 如果文件太小（小于 1MB），直接全量哈希，最保险
        if (fileSize < 1024 * 1024)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            byte[] fullHash = md5.ComputeHash(stream);
            return $"{fileSize}_{Convert.ToHexString(fullHash).ToLowerInvariant()}";
        }

        // 2. 针对大图/RAW文件，进行"三点跨度采样"（头部、绝对正中间、1/4 像素区）
        // 避开可能完全相同的纯尾部
        byte[] buffer = new byte[8192 * 3]; // 总共只读 24KB
        
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // 读头部 8KB（包含部分元数据）
            fs.Read(buffer, 0, 8192);
            
            // 读 1/4 处的 8KB（这通常是图像数据区的开始，多曝画面不同的地方）
            fs.Seek(fileSize / 4, SeekOrigin.Begin);
            fs.Read(buffer, 8192, 8192);
            
            // 读 1/2 正中间处的 8KB（核心像素区）
            fs.Seek(fileSize / 2, SeekOrigin.Begin);
            fs.Read(buffer, 8192 * 2, 8192);
        }

        using var md5Engine = MD5.Create();
        byte[] sampleHash = md5Engine.ComputeHash(buffer);
        string hexHash = Convert.ToHexString(sampleHash).ToLowerInvariant();

        // 最终特征码：结合了 【文件大小】_【写入时间】_【像素区采样哈希】
        return $"{fileSize}_{lastWriteTicks}_{hexHash}";
    }
}
