using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace StartTooler.Data;

/// <summary>
/// 公网接收任务持久化。落库到 media.db 的 sync_for_vps_task 表。
/// 单连接原则：每个方法一个连接，用完即弃（见 02-data-layer.md §6.1）。
/// </summary>
public class SyncForVpsTaskRepository : ISyncForVpsTaskRepository
{
    private readonly string _connectionString;

    public SyncForVpsTaskRepository()
    {
        var dbPath = GetDbPath();
        _connectionString = $"Data Source={dbPath}";
        EnsureTable();
    }

    private static string GetDbPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appData, "StartTooler");
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "media.db");
    }

    private void EnsureTable()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var sql = @"
            CREATE TABLE IF NOT EXISTS sync_for_vps_task (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                local_path TEXT,
                status INTEGER NOT NULL DEFAULT 0,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                UNIQUE(file_id)
            );
            CREATE INDEX IF NOT EXISTS idx_sync_for_vps_task_status ON sync_for_vps_task(status);
            CREATE INDEX IF NOT EXISTS idx_sync_for_vps_task_created ON sync_for_vps_task(created_at);
        ";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    public async Task UpsertAsync(SyncForVpsTask task, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = @"
            INSERT INTO sync_for_vps_task
                (file_id, file_name, size_bytes, local_path, status, attempt_count, last_error, created_at, updated_at)
            VALUES
                (@fileId, @fileName, @sizeBytes, @localPath, @status, @attemptCount, @lastError, @createdAt, @updatedAt)
            ON CONFLICT(file_id) DO UPDATE SET
                file_name = @fileName,
                size_bytes = @sizeBytes,
                local_path = @localPath,
                status = @status,
                attempt_count = @attemptCount,
                last_error = @lastError,
                updated_at = @updatedAt";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@fileId", task.FileId);
        cmd.Parameters.AddWithValue("@fileName", task.FileName);
        cmd.Parameters.AddWithValue("@sizeBytes", task.SizeBytes);
        cmd.Parameters.AddWithValue("@localPath", (object?)task.LocalPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (int)task.Status);
        cmd.Parameters.AddWithValue("@attemptCount", task.AttemptCount);
        cmd.Parameters.AddWithValue("@lastError", (object?)task.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", task.CreatedAt);
        cmd.Parameters.AddWithValue("@updatedAt", task.UpdatedAt);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SyncForVpsTask?> GetByFileIdAsync(string fileId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = @"
            SELECT id, file_id, file_name, size_bytes, local_path, status, attempt_count, last_error, created_at, updated_at
            FROM sync_for_vps_task
            WHERE file_id = @fileId
            LIMIT 1";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@fileId", fileId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadTask(reader);
        }
        return null;
    }

    public async Task<IReadOnlyList<SyncForVpsTask>> GetByStatusAsync(
        SyncForVpsTaskStatus status, CancellationToken ct = default)
    {
        var results = new List<SyncForVpsTask>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = @"
            SELECT id, file_id, file_name, size_bytes, local_path, status, attempt_count, last_error, created_at, updated_at
            FROM sync_for_vps_task
            WHERE status = @status
            ORDER BY created_at DESC";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@status", (int)status);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadTask(reader));
        }
        return results;
    }

    private static SyncForVpsTask ReadTask(SqliteDataReader reader)
    {
        return new SyncForVpsTask
        {
            Id = reader.GetInt64(0),
            FileId = reader.GetString(1),
            FileName = reader.GetString(2),
            SizeBytes = reader.GetInt64(3),
            LocalPath = reader.IsDBNull(4) ? null : reader.GetString(4),
            Status = (SyncForVpsTaskStatus)reader.GetInt32(5),
            AttemptCount = reader.GetInt32(6),
            LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = reader.GetInt64(8),
            UpdatedAt = reader.GetInt64(9),
        };
    }
}