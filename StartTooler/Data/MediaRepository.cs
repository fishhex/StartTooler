using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.Data;

public class MediaRepository : IMediaRepository
{
    private readonly string _connectionString;

    public MediaRepository()
    {
        var dbPath = GetDbPath();
        _connectionString = $"Data Source={dbPath}";
        EnsureDatabase();
    }

    private static string GetDbPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appData, "StartTooler");
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "media.db");
    }

    private void EnsureDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS media_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_path TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                media_type INTEGER NOT NULL DEFAULT 0,
                file_size INTEGER NOT NULL DEFAULT 0,
                last_modified INTEGER NOT NULL DEFAULT 0,
                shot_at INTEGER,
                is_uploaded INTEGER NOT NULL DEFAULT 0,
                local_exists INTEGER NOT NULL DEFAULT 1,
                thumbnail_path TEXT,
                remote_url TEXT,
                uploaded_at INTEGER,
                scanned_at INTEGER NOT NULL DEFAULT 0,
                UNIQUE(project_path, relative_path)
            );
            CREATE INDEX IF NOT EXISTS idx_media_files_date ON media_files(shot_at);
            CREATE INDEX IF NOT EXISTS idx_media_files_project ON media_files(project_path);
        ";

        using var cmd = new SqliteCommand(createTableSql, connection);
        cmd.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<DateCount>> GetDateGroupsAsync(string projectPath, CancellationToken ct = default)
    {
        var results = new List<DateCount>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // 规范化路径以匹配扫描时保存的格式
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        var sql = @"
            SELECT
                date(shot_at / 1000, 'unixepoch', 'localtime') as date,
                COUNT(*) as count
            FROM media_files
            WHERE project_path = @projectPath AND shot_at IS NOT NULL
            GROUP BY date(shot_at / 1000, 'unixepoch', 'localtime')
            ORDER BY date DESC";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var dateStr = reader.GetString(0);
            var count = reader.GetInt32(1);

            if (DateTime.TryParse(dateStr, out var date))
            {
                results.Add(new DateCount
                {
                    Date = date,
                    Count = count
                });
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<MediaFile>> GetByDateAsync(string projectPath, DateTime date, CancellationToken ct = default)
    {
        var results = new List<MediaFile>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // 规范化路径以匹配扫描时保存的格式
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        var startOfDay = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);
        var endOfDay = startOfDay.AddDays(1);

        var startTimestamp = new DateTimeOffset(startOfDay).ToUnixTimeMilliseconds();
        var endTimestamp = new DateTimeOffset(endOfDay).ToUnixTimeMilliseconds();

        var sql = @"
            SELECT
                id, project_path, relative_path, file_name, media_type,
                file_size, last_modified, shot_at, is_uploaded, local_exists,
                thumbnail_path, remote_url, uploaded_at, scanned_at
            FROM media_files
            WHERE project_path = @projectPath
              AND shot_at >= @startTime
              AND shot_at < @endTime
            ORDER BY shot_at DESC
            LIMIT 1000";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
        cmd.Parameters.AddWithValue("@startTime", startTimestamp);
        cmd.Parameters.AddWithValue("@endTime", endTimestamp);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MediaFile
            {
                Id = reader.GetInt64(0),
                ProjectPath = reader.GetString(1),
                RelativePath = reader.GetString(2),
                FileName = reader.GetString(3),
                MediaType = (MediaType)reader.GetInt32(4),
                FileSize = reader.GetInt64(5),
                LastModified = reader.GetInt64(6),
                ShotAt = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                IsUploaded = reader.GetInt32(8) == 1,
                LocalExists = reader.GetInt32(9) == 1,
                ThumbnailPath = reader.IsDBNull(10) ? null : reader.GetString(10),
                RemoteUrl = reader.IsDBNull(11) ? null : reader.GetString(11),
                UploadedAt = reader.IsDBNull(12) ? null : reader.GetInt64(12),
                ScannedAt = reader.GetInt64(13)
            });
        }

        return results;
    }

    // 支持的媒体扩展名
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp", ".heic",
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".pef"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".mpg", ".mpeg"
    };

    public async Task<ScanResult> ScanDirectoryAsync(string projectPath, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new ScanResult { Total = 0, Processed = 0, Failed = 0, NewFiles = 0, UpdatedFiles = 0 };

        // 第一遍：统计文件数量
        var files = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var ext in ImageExtensions) files.Add(ext);
                foreach (var ext in VideoExtensions) files.Add(ext);

                var allFiles = Directory.EnumerateFiles(projectPath, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                });

                foreach (var file in allFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    var ext = Path.GetExtension(file);
                    if (ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext))
                    {
                        files.Add(file);
                    }
                }
            }, ct);

            result.Total = files.Count;
            progress?.Report(new ScanProgress { Total = result.Total, Processed = 0 });
        }
        catch (Exception)
        {
            result.Total = 0;
        }

        if (result.Total == 0)
        {
            progress?.Report(new ScanProgress { Total = 0, Processed = 0 });
            return result;
        }

        // 第二遍：写入数据库
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var insertSql = @"
            INSERT INTO media_files (project_path, relative_path, file_name, media_type, file_size, last_modified, shot_at, thumbnail_path, scanned_at)
            VALUES (@projectPath, @relativePath, @fileName, @mediaType, @fileSize, @lastModified, @shotAt, @thumbnailPath, @scannedAt)
            ON CONFLICT(project_path, relative_path) DO UPDATE SET
                file_size = @fileSize,
                last_modified = @lastModified,
                shot_at = COALESCE(shot_at, @shotAt),
                thumbnail_path = @thumbnailPath,
                scanned_at = @scannedAt";

        await using var cmd = new SqliteCommand(insertSql, connection);
        var projectPathNormalized = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var fileInfo = new FileInfo(filePath);
                var relativePath = Path.GetRelativePath(projectPath, filePath);
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var mediaType = ImageExtensions.Contains(ext) ? 0 : 1;
                var lastModified = new DateTimeOffset(fileInfo.LastWriteTime).ToUnixTimeMilliseconds();
                var scannedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@projectPath", projectPathNormalized);
                cmd.Parameters.AddWithValue("@relativePath", relativePath);
                cmd.Parameters.AddWithValue("@fileName", fileInfo.Name);
                cmd.Parameters.AddWithValue("@mediaType", mediaType);
                cmd.Parameters.AddWithValue("@fileSize", fileInfo.Length);
                cmd.Parameters.AddWithValue("@lastModified", lastModified);
                cmd.Parameters.AddWithValue("@shotAt", lastModified); // 用文件修改时间作为 shot_at
                cmd.Parameters.AddWithValue("@thumbnailPath", filePath); // 使用文件路径作为缩略图
                cmd.Parameters.AddWithValue("@scannedAt", scannedAt);

                var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
                if (rowsAffected > 0)
                {
                    result.NewFiles++;
                }

                result.Processed++;
                progress?.Report(new ScanProgress
                {
                    Total = result.Total,
                    Processed = result.Processed,
                    Failed = result.Failed,
                    CurrentFile = fileInfo.Name
                });
            }
            catch (Exception)
            {
                result.Failed++;
            }
        }

        progress?.Report(new ScanProgress
        {
            Total = result.Total,
            Processed = result.Processed,
            Failed = result.Failed
        });

        return result;
    }

    public async Task GenerateThumbnailsAsync(string projectPath, IThumbnailService thumbnailService, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        // 获取所有没有缩略图的文件
        var selectSql = @"
            SELECT id, project_path, relative_path, file_name
            FROM media_files
            WHERE project_path = @projectPath
              AND (thumbnail_path IS NULL OR thumbnail_path = '')";

        var updateSql = "UPDATE media_files SET thumbnail_path = @thumbnailPath WHERE id = @id";

        var files = new List<(long Id, string RelativePath)>();

        await using (var cmd = new SqliteCommand(selectSql, connection))
        {
            cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                files.Add((reader.GetInt64(0), reader.GetString(2)));
            }
        }

        var total = files.Count;
        var processed = 0;

        foreach (var (id, relativePath) in files)
        {
            if (ct.IsCancellationRequested) break;

            var fullPath = Path.Combine(normalizedPath, relativePath);
            if (!File.Exists(fullPath)) continue;

            var thumbnailPath = await thumbnailService.GenerateThumbnailAsync(fullPath, normalizedPath, ct);

            if (thumbnailPath != null)
            {
                await using var cmd = new SqliteCommand(updateSql, connection);
                cmd.Parameters.AddWithValue("@thumbnailPath", thumbnailPath);
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            processed++;
            progress?.Report(new ScanProgress
            {
                Total = total,
                Processed = processed,
                CurrentFile = Path.GetFileName(fullPath)
            });
        }
    }
}
