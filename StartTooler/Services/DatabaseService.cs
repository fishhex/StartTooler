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
        EnsureMediaFileRecordSchema();
        
        // 创建 AI 设置表
        _connection.CreateTable<AiSetting>();
        
        // 创建云存储设置表
        _connection.CreateTable<CloudStorageSetting>();
        EnsureCloudStorageSettingSchema();
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
                existing.RootPath = record.RootPath;
                existing.PerceptualHash = record.PerceptualHash;
                existing.GroupId = record.GroupId;
                existing.CloudStorage = record.CloudStorage;
                existing.Bucket = record.Bucket;
                existing.BucketPath = record.BucketPath;
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
    /// 根据根目录获取记录
    /// </summary>
    public List<MediaFileRecord> GetMediaFileRecordsByRootPath(string rootPath)
    {
        try
        {
            return _connection.Table<MediaFileRecord>()
                .Where(r => r.RootPath == rootPath)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"查询媒体文件记录失败: {ex.Message}");
            return new List<MediaFileRecord>();
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

    private void EnsureMediaFileRecordSchema()
    {
        try
        {
            var columns = _connection.GetTableInfo("MediaFileRecords");
            if (columns.All(c => c.Name != "RootPath"))
            {
                _connection.Execute("ALTER TABLE MediaFileRecords ADD COLUMN RootPath TEXT DEFAULT ''");
            }

            if (columns.All(c => c.Name != "PerceptualHash"))
            {
                _connection.Execute("ALTER TABLE MediaFileRecords ADD COLUMN PerceptualHash INTEGER DEFAULT 0");
            }

            if (columns.All(c => c.Name != "GroupId"))
            {
                _connection.Execute("ALTER TABLE MediaFileRecords ADD COLUMN GroupId TEXT");
            }

            if (columns.All(c => c.Name != "CloudStorage"))
            {
                _connection.Execute("ALTER TABLE MediaFileRecords ADD COLUMN CloudStorage INTEGER DEFAULT 0");
            }

            if (columns.All(c => c.Name != "Bucket"))
            {
                _connection.Execute("ALTER TABLE MediaFileRecords ADD COLUMN Bucket TEXT DEFAULT ''");
            }

            if (columns.All(c => c.Name != "BucketPath"))
            {
                _connection.Execute("ALTER TABLE MediaFileRecords ADD COLUMN BucketPath TEXT DEFAULT ''");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"更新媒体文件记录表结构失败: {ex.Message}");
        }
    }

    private void EnsureCloudStorageSettingSchema()
    {
        try
        {
            var columns = _connection.GetTableInfo("CloudStorageSettings");
            if (columns.All(c => c.Name != "Dir"))
            {
                _connection.Execute("ALTER TABLE CloudStorageSettings ADD COLUMN Dir TEXT DEFAULT ''");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"更新云存储设置表结构失败: {ex.Message}");
        }
    }

    #region AiSetting Operations

    /// <summary>
    /// 保存 AI 设置
    /// </summary>
    public void SaveAiSetting(AiSetting setting)
    {
        if (setting == null)
            return;

        try
        {
            setting.UpdatedTime = DateTime.Now;
            setting.Id = 1; // 确保是单例

            var existing = _connection.Table<AiSetting>().FirstOrDefault();
            if (existing != null)
            {
                existing.ApiUrl = setting.ApiUrl;
                existing.ApiToken = setting.ApiToken;
                existing.ModelName = setting.ModelName;
                existing.UpdatedTime = setting.UpdatedTime;
                _connection.Update(existing);
            }
            else
            {
                _connection.Insert(setting);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"保存 AI 设置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取 AI 设置
    /// </summary>
    public AiSetting? GetAiSetting()
    {
        try
        {
            return _connection.Table<AiSetting>().FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取 AI 设置失败: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region CloudStorageSetting Operations

    /// <summary>
    /// 保存云存储设置
    /// </summary>
    public void SaveCloudStorageSetting(CloudStorageSetting setting)
    {
        if (setting == null)
            return;

        try
        {
            setting.UpdatedTime = DateTime.Now;

            var existing = _connection.Table<CloudStorageSetting>()
                .FirstOrDefault(s => s.Provider == setting.Provider);
            if (existing != null)
            {
                existing.AccessKeyId = setting.AccessKeyId;
                existing.AccessKeySecret = setting.AccessKeySecret;
                existing.BucketName = setting.BucketName;
                existing.Endpoint = setting.Endpoint;
                existing.Dir = setting.Dir;
                existing.ExtraConfig = setting.ExtraConfig;
                existing.IsEnabled = setting.IsEnabled;
                existing.UpdatedTime = setting.UpdatedTime;
                _connection.Update(existing);
            }
            else
            {
                _connection.Insert(setting);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"保存云存储设置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取指定提供商的云存储设置
    /// </summary>
    public CloudStorageSetting? GetCloudStorageSetting(CloudStorageProvider provider)
    {
        try
        {
            return _connection.Table<CloudStorageSetting>()
                .FirstOrDefault(s => s.Provider == (int)provider);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取云存储设置失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取所有云存储设置
    /// </summary>
    public List<CloudStorageSetting> GetAllCloudStorageSettings()
    {
        try
        {
            return _connection.Table<CloudStorageSetting>().ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取云存储设置列表失败: {ex.Message}");
            return [];
        }
    }

    #endregion

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
