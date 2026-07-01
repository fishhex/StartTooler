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
        EnsureSchema();
    }

    private static string GetDbPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appData, "StartTooler");
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "media.db");
    }

    /// <summary>
    /// 建表 + 迁移。v0.3 加 remote_path 列（v0.2 老库迁移兼容，nullable）。
    /// 用 PRAGMA table_info 检查列存在性，避免重复 ALTER。
    /// </summary>
    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 1) 主表
        var createSql = @"
            CREATE TABLE IF NOT EXISTS sync_for_vps_task (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                remote_path TEXT,
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
            CREATE INDEX IF NOT EXISTS idx_sync_for_vps_task_status_created
                ON sync_for_vps_task(status, created_at);
        ";
        using (var cmd = new SqliteCommand(createSql, connection))
        {
            cmd.ExecuteNonQuery();
        }

        // 2) v0.3 migration: 加 remote_path 列（如不存在）
        if (!ColumnExists(connection, "sync_for_vps_task", "remote_path"))
        {
            using var alter = new SqliteCommand(
                "ALTER TABLE sync_for_vps_task ADD COLUMN remote_path TEXT", connection);
            alter.ExecuteNonQuery();
        }
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var cmd = new SqliteCommand($"PRAGMA table_info({table})", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ============================================================
    // v0.3 pull 模型 API
    // ============================================================

    /// <summary>
    /// TCP 收到 file_pending 时调用：UNIQUE(fileId) 幂等插入。
    /// 已存在则 noop（不会覆盖现有 Status / LocalPath——已 Received 的不会被重置回 Pending）。
    /// 返回是否真插了新行（true=新文件 / false=已存在跳过）。
    /// </summary>
    public async Task<bool> InsertIfNewAsync(
        string fileId, string fileName, long sizeBytes, string? remotePath,
        CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sql = @"
            INSERT OR IGNORE INTO sync_for_vps_task
                (file_id, file_name, size_bytes, remote_path, status, attempt_count, created_at, updated_at)
            VALUES
                (@fileId, @fileName, @sizeBytes, @remotePath, @status, 0, @createdAt, @updatedAt)";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@fileId", fileId);
        cmd.Parameters.AddWithValue("@fileName", fileName);
        cmd.Parameters.AddWithValue("@sizeBytes", sizeBytes);
        cmd.Parameters.AddWithValue("@remotePath", (object?)remotePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (int)SyncForVpsTaskStatus.Pending);
        cmd.Parameters.AddWithValue("@createdAt", now);
        cmd.Parameters.AddWithValue("@updatedAt", now);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    /// <summary>
    /// Poller 拉一批 Pending 行：ORDER BY created_at ASC（先到先下载），LIMIT n。
    /// </summary>
    public async Task<IReadOnlyList<SyncForVpsTask>> GetPendingBatchAsync(
        int limit, CancellationToken ct = default)
    {
        var results = new List<SyncForVpsTask>(limit);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = @"
            SELECT id, file_id, file_name, size_bytes, remote_path, local_path,
                   status, attempt_count, last_error, created_at, updated_at
            FROM sync_for_vps_task
            WHERE status = @status
            ORDER BY created_at ASC
            LIMIT @limit";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@status", (int)SyncForVpsTaskStatus.Pending);
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadTask(reader));
        }
        return results;
    }

    /// <summary>scp 成功 → Received + LocalPath + AttemptCount++。</summary>
    public async Task MarkReceivedAsync(long id, string localPath, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = @"
            UPDATE sync_for_vps_task
            SET status = @status,
                local_path = @localPath,
                attempt_count = attempt_count + 1,
                last_error = NULL,
                updated_at = @updatedAt
            WHERE id = @id";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@status", (int)SyncForVpsTaskStatus.Received);
        cmd.Parameters.AddWithValue("@localPath", localPath);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@id", id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>scp 失败 → Failed + LastError + AttemptCount++（v0.3 不重试）。</summary>
    public async Task MarkFailedAsync(long id, string error, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = @"
            UPDATE sync_for_vps_task
            SET status = @status,
                attempt_count = attempt_count + 1,
                last_error = @lastError,
                updated_at = @updatedAt
            WHERE id = @id";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@status", (int)SyncForVpsTaskStatus.Failed);
        cmd.Parameters.AddWithValue("@lastError", error);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@id", id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>UI 显示用：Pending 行数（cheap query，单次 COUNT）。</summary>
    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = "SELECT COUNT(*) FROM sync_for_vps_task WHERE status = @status";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@status", (int)SyncForVpsTaskStatus.Pending);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    private static SyncForVpsTask ReadTask(SqliteDataReader reader)
    {
        return new SyncForVpsTask
        {
            Id = reader.GetInt64(0),
            FileId = reader.GetString(1),
            FileName = reader.GetString(2),
            SizeBytes = reader.GetInt64(3),
            RemotePath = reader.IsDBNull(4) ? null : reader.GetString(4),
            LocalPath = reader.IsDBNull(5) ? null : reader.GetString(5),
            Status = (SyncForVpsTaskStatus)reader.GetInt32(6),
            AttemptCount = reader.GetInt32(7),
            LastError = reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedAt = reader.GetInt64(9),
            UpdatedAt = reader.GetInt64(10),
        };
    }
}