using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using StartTooler.Helpers;
using StartTooler.Models;

namespace StartTooler.Services;

/// <summary>
/// 数据库服务类，负责所有 SQLite 数据库操作
/// </summary>
public class DatabaseService : IDisposable
{
    private readonly SQLiteAsyncConnection _database;
    private bool _disposed = false;

    public DatabaseService()
    {
        var dbPath = PathHelper.GetDatabasePath();
        _database = new SQLiteAsyncConnection(dbPath);
    }

    /// <summary>
    /// 确保数据库表已初始化
    /// </summary>
    private async Task EnsureInitializedAsync()
    {
        await _database.CreateTableAsync<MediaFile>();
    }

    /// <summary>
    /// 根据文件路径查询媒体文件
    /// </summary>
    public async Task<MediaFile?> GetMediaFileByPathAsync(string filePath)
    {
        await EnsureInitializedAsync();
        return await _database.Table<MediaFile>()
            .Where(mf => mf.FilePath == filePath)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// 插入新的媒体文件记录
    /// </summary>
    public async Task<int> InsertMediaFileAsync(MediaFile mediaFile)
    {
        await EnsureInitializedAsync();
        mediaFile.CreatedAt = DateTime.Now;
        mediaFile.UpdatedAt = DateTime.Now;
        return await _database.InsertAsync(mediaFile);
    }

    /// <summary>
    /// 更新媒体文件记录
    /// </summary>
    public async Task<int> UpdateMediaFileAsync(MediaFile mediaFile)
    {
        await EnsureInitializedAsync();
        mediaFile.UpdatedAt = DateTime.Now;
        return await _database.UpdateAsync(mediaFile);
    }

    /// <summary>
    /// 批量插入媒体文件记录
    /// </summary>
    public async Task<int> InsertMediaFilesAsync(IEnumerable<MediaFile> mediaFiles)
    {
        await EnsureInitializedAsync();
        var now = DateTime.Now;
        foreach (var mediaFile in mediaFiles)
        {
            mediaFile.CreatedAt = now;
            mediaFile.UpdatedAt = now;
        }
        return await _database.InsertAllAsync(mediaFiles);
    }

    /// <summary>
    /// 根据目录路径获取媒体文件列表
    /// </summary>
    public async Task<List<MediaFile>> GetMediaFilesByDirectoryAsync(string directoryPath)
    {
        await EnsureInitializedAsync();
        return await _database.Table<MediaFile>()
            .Where(mf => mf.DirectoryPath == directoryPath)
            .ToListAsync();
    }

    /// <summary>
    /// 获取所有媒体文件
    /// </summary>
    public async Task<List<MediaFile>> GetAllMediaFilesAsync()
    {
        await EnsureInitializedAsync();
        return await _database.Table<MediaFile>().ToListAsync();
    }

    /// <summary>
    /// 删除媒体文件记录
    /// </summary>
    public async Task<int> DeleteMediaFileAsync(MediaFile mediaFile)
    {
        await EnsureInitializedAsync();
        return await _database.DeleteAsync(mediaFile);
    }

    /// <summary>
    /// 清空指定目录的媒体文件记录
    /// </summary>
    public async Task<int> ClearMediaFilesByDirectoryAsync(string directoryPath)
    {
        await EnsureInitializedAsync();
        // 使用 SQL 直接删除，比逐个删除更高效
        var sql = "DELETE FROM MediaFiles WHERE DirectoryPath = ?";
        return await _database.ExecuteAsync(sql, directoryPath);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _database.CloseAsync().Wait();
            }
            _disposed = true;
        }
    }
}
