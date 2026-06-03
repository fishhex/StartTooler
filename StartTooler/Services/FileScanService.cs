using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StartTooler.Models;

namespace StartTooler.Services;

/// <summary>
/// 文件扫描进度信息
/// </summary>
public class ScanProgressInfo
{
    public int CurrentFile { get; set; }
    public int TotalFiles { get; set; }
    public string CurrentFileName { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
}

/// <summary>
/// 文件扫描服务类，负责扫描目录并同步到数据库
/// </summary>
public class FileScanService
{
    private readonly DatabaseService _databaseService;
    private readonly FFmpegService _ffmpegService;

    // 支持的视频文件扩展名
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", 
        ".m4v", ".mpg", ".mpeg", ".3gp", ".3g2"
    };

    public FileScanService(DatabaseService databaseService, FFmpegService ffmpegService)
    {
        _databaseService = databaseService;
        _ffmpegService = ffmpegService;
    }

    /// <summary>
    /// 扫描指定目录并增量更新数据库
    /// </summary>
    /// <param name="directoryPath">要扫描的目录路径</param>
    /// <param name="progressCallback">进度回调函数</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task ScanDirectoryAsync(
        string directoryPath,
        Action<ScanProgressInfo>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"目录不存在: {directoryPath}");
        }

        var progressInfo = new ScanProgressInfo();
        
        try
        {
            // 获取所有视频文件
            var videoFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            progressInfo.TotalFiles = videoFiles.Count;
            progressInfo.StatusMessage = $"找到 {videoFiles.Count} 个视频文件";
            progressCallback?.Invoke(progressInfo);

            int processedCount = 0;

            foreach (var filePath in videoFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(filePath);
                progressInfo.CurrentFile = ++processedCount;
                progressInfo.CurrentFileName = fileInfo.Name;
                progressInfo.StatusMessage = $"正在处理: {fileInfo.Name} ({processedCount}/{videoFiles.Count})";
                progressCallback?.Invoke(progressInfo);

                // 检查数据库中是否已存在该文件
                var existingFile = await _databaseService.GetMediaFileByPathAsync(filePath);

                if (existingFile != null)
                {
                    // 增量更新：仅当文件大小或修改时间发生变化时才更新
                    if (existingFile.FileSize == fileInfo.Length && 
                        existingFile.LastModified == fileInfo.LastWriteTime)
                    {
                        // 文件未变更，跳过
                        continue;
                    }

                    // 文件已变更，更新记录
                    existingFile.FileSize = fileInfo.Length;
                    existingFile.LastModified = fileInfo.LastWriteTime;
                    existingFile.FileName = fileInfo.Name;
                    existingFile.Extension = fileInfo.Extension.ToLowerInvariant();

                    await _databaseService.UpdateMediaFileAsync(existingFile);
                }
                else
                {
                    // 新文件，创建记录
                    var mediaFile = new MediaFile
                    {
                        FilePath = filePath,
                        FileName = fileInfo.Name,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime,
                        DirectoryPath = directoryPath,
                        Extension = fileInfo.Extension.ToLowerInvariant()
                    };

                    await _databaseService.InsertMediaFileAsync(mediaFile);
                }

                // 提取缩略图（如果还没有）
                var currentRecord = await _databaseService.GetMediaFileByPathAsync(filePath);
                if (currentRecord != null && string.IsNullOrEmpty(currentRecord.ThumbnailPath))
                {
                    progressInfo.StatusMessage = $"正在生成缩略图: {fileInfo.Name}";
                    progressCallback?.Invoke(progressInfo);

                    var thumbnailPath = await _ffmpegService.ExtractThumbnailAsync(filePath, cancellationToken);
                    if (!string.IsNullOrEmpty(thumbnailPath))
                    {
                        currentRecord.ThumbnailPath = thumbnailPath;
                        await _databaseService.UpdateMediaFileAsync(currentRecord);
                    }
                }
            }

            progressInfo.StatusMessage = "扫描完成！";
            progressInfo.CurrentFile = progressInfo.TotalFiles;
            progressCallback?.Invoke(progressInfo);
        }
        catch (OperationCanceledException)
        {
            progressInfo.StatusMessage = "扫描已取消";
            progressCallback?.Invoke(progressInfo);
            throw;
        }
    }

    /// <summary>
    /// 构建目录树结构
    /// </summary>
    /// <param name="rootPath">根目录路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>目录树节点</returns>
    public Task<DirectoryNode?> BuildDirectoryTreeAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(rootPath))
            {
                return (DirectoryNode?)null;
            }

            var rootDir = new DirectoryInfo(rootPath);
            var rootNode = new DirectoryNode
            {
                Name = rootDir.Name,
                FullPath = rootDir.FullName,
                IsRoot = true,
                IsExpanded = true
            };

            // 递归构建子目录树
            BuildSubDirectories(rootNode, cancellationToken);

            return (DirectoryNode?)rootNode;
        }, cancellationToken);
    }

    /// <summary>
    /// 递归构建子目录
    /// </summary>
    private void BuildSubDirectories(DirectoryNode parentNode, CancellationToken cancellationToken)
    {
        try
        {
            var subDirs = Directory.GetDirectories(parentNode.FullPath);

            foreach (var subDirPath in subDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var subDir = new DirectoryInfo(subDirPath);
                
                // 跳过隐藏目录和系统目录
                if ((subDir.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    continue;

                var childNode = new DirectoryNode
                {
                    Name = subDir.Name,
                    FullPath = subDir.FullName,
                    IsRoot = false
                };

                parentNode.Children.Add(childNode);

                // 递归处理子目录
                BuildSubDirectories(childNode, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 跳过无权限访问的目录
        }
        catch (DirectoryNotFoundException)
        {
            // 跳过已删除的目录
        }
    }
}
