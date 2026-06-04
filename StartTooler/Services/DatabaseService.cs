using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLite;
using StartTooler.Helpers;
using StartTooler.Models;

namespace StartTooler.Services;

public class DatabaseService : IDisposable
{
    private readonly SQLiteConnection _connection;
    private static DatabaseService? _instance;
    private static readonly object _lock = new();

    public static DatabaseService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new DatabaseService();
                }
            }
            return _instance;
        }
    }

    private DatabaseService()
    {
        var dbPath = PathHelper.GetDatabasePath();
        _connection = new SQLiteConnection(dbPath);
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        // 创建最近打开文件夹表
        _connection.CreateTable<RecentFolder>();
        
        // 创建媒体文件记录表
        _connection.CreateTable<MediaFileRecord>();
    }

    /// <summary>
    /// 保存或更新最近打开的文件夹
    /// </summary>
    public void SaveRecentFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        try
        {
            var folderName = Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(folderName))
            {
                folderName = folderPath;
            }

            // 查找是否已存在
            var existing = _connection.Table<RecentFolder>()
                .FirstOrDefault(f => f.FolderPath == folderPath);

            if (existing != null)
            {
                // 更新现有记录
                existing.LastOpenedTime = DateTime.Now;
                existing.OpenCount++;
                _connection.Update(existing);
            }
            else
            {
                // 插入新记录
                var recentFolder = new RecentFolder
                {
                    FolderPath = folderPath,
                    FolderName = folderName,
                    LastOpenedTime = DateTime.Now,
                    OpenCount = 1
                };
                _connection.Insert(recentFolder);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"保存最近文件夹失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取最近打开的文件夹列表（按最后打开时间倒序）
    /// </summary>
    public List<RecentFolder> GetRecentFolders(int maxCount = 10)
    {
        try
        {
            return _connection.Table<RecentFolder>()
                .OrderByDescending(f => f.LastOpenedTime)
                .Take(maxCount)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取最近文件夹失败: {ex.Message}");
            return new List<RecentFolder>();
        }
    }

    /// <summary>
    /// 删除指定的最近文件夹记录
    /// </summary>
    public void DeleteRecentFolder(int id)
    {
        try
        {
            _connection.Delete<RecentFolder>(id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"删除最近文件夹记录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清空所有最近文件夹记录
    /// </summary>
    public void ClearAllRecentFolders()
    {
        try
        {
            _connection.DeleteAll<RecentFolder>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"清空最近文件夹记录失败: {ex.Message}");
        }
    }

    #region MediaFileRecord Operations

    /// <summary>
    /// 保存或更新媒体文件记录
    /// </summary>
    public void SaveMediaFileRecord(MediaFileRecord record)
    {
        if (record == null)
            return;

        try
        {
            record.UpdatedTime = DateTime.Now;

            // 查找是否已存在（通过本地路径）
            var existing = _connection.Table<MediaFileRecord>()
                .FirstOrDefault(r => r.LocalPath == record.LocalPath);

            if (existing != null)
            {
                // 更新现有记录
                existing.FeatureCode = record.FeatureCode;
                existing.FileName = record.FileName;
                existing.IsUploaded = record.IsUploaded;
                existing.UpdatedTime = record.UpdatedTime;
                _connection.Update(existing);
            }
            else
            {
                // 插入新记录
                _connection.Insert(record);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"保存媒体文件记录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 根据特征码查找记录
    /// </summary>
    public MediaFileRecord? GetMediaFileRecordByFeatureCode(string featureCode)
    {
        try
        {
            return _connection.Table<MediaFileRecord>()
                .FirstOrDefault(r => r.FeatureCode == featureCode);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"查询媒体文件记录失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 根据本地路径查找记录
    /// </summary>
    public MediaFileRecord? GetMediaFileRecordByPath(string localPath)
    {
        try
        {
            return _connection.Table<MediaFileRecord>()
                .FirstOrDefault(r => r.LocalPath == localPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"查询媒体文件记录失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取所有未上传的记录
    /// </summary>
    public List<MediaFileRecord> GetUnuploadedRecords()
    {
        try
        {
            return _connection.Table<MediaFileRecord>()
                .Where(r => !r.IsUploaded)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"查询未上传记录失败: {ex.Message}");
            return new List<MediaFileRecord>();
        }
    }

    /// <summary>
    /// 获取所有已上传的记录
    /// </summary>
    public List<MediaFileRecord> GetUploadedRecords()
    {
        try
        {
            return _connection.Table<MediaFileRecord>()
                .Where(r => r.IsUploaded)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"查询已上传记录失败: {ex.Message}");
            return new List<MediaFileRecord>();
        }
    }

    /// <summary>
    /// 更新上传状态
    /// </summary>
    public void UpdateUploadStatus(int id, bool isUploaded)
    {
        try
        {
            var record = _connection.Table<MediaFileRecord>().FirstOrDefault(r => r.Id == id);
            if (record != null)
            {
                record.IsUploaded = isUploaded;
                record.UpdatedTime = DateTime.Now;
                _connection.Update(record);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"更新上传状态失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除记录
    /// </summary>
    public void DeleteMediaFileRecord(int id)
    {
        try
        {
            _connection.Delete<MediaFileRecord>(id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"删除媒体文件记录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清空所有媒体文件记录
    /// </summary>
    public void ClearAllMediaFileRecords()
    {
        try
        {
            _connection.DeleteAll<MediaFileRecord>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"清空媒体文件记录失败: {ex.Message}");
        }
    }

    #endregion

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
