using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using StartTooler.Models;

namespace StartTooler.Data;

public class UploadJobRepository : IUploadJobRepository
{
    private readonly string _connectionString;

    public UploadJobRepository()
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

        // 1) 全新库 / 已迁移过的库 → 直接建表
        var createSql = @"
            CREATE TABLE IF NOT EXISTS upload_jobs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_path TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                object_key TEXT NOT NULL,
                upload_id TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                part_size INTEGER NOT NULL,
                parts_uploaded TEXT NOT NULL DEFAULT '[]',
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                UNIQUE(project_path, relative_path)
            );
            CREATE INDEX IF NOT EXISTS idx_upload_jobs_project ON upload_jobs(project_path);
        ";
        using (var cmd = new SqliteCommand(createSql, connection))
        {
            cmd.ExecuteNonQuery();
        }

        // 2) v0.4 迁移：老库 created_at/updated_at 是 INTEGER (unix ms)，需要整表迁移到 TEXT
        // SQLite ALTER TABLE 不支持改类型，只能 rename old → create new → copy + strftime → drop old
        if (IsCreatedAtInteger(connection))
        {
            MigrateFromIntegerToText(connection);
        }
    }

    /// <summary>检测老库 created_at 是否还是 INTEGER（v0.4 之前 schema）。</summary>
    private static bool IsCreatedAtInteger(SqliteConnection connection)
    {
        var type = SqliteMigrations.GetColumnType(connection, "upload_jobs", "created_at");
        // 老库可能完全没这列 → null；或存在但类型是 INTEGER
        return type != null && type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);
    }

    private static void MigrateFromIntegerToText(SqliteConnection connection)
    {
        System.Diagnostics.Trace.WriteLine("[upload_jobs] Migrating created_at/updated_at INTEGER → TEXT (ISO 8601)");

        // 老表 → 中转表
        using (var step1 = new SqliteCommand("ALTER TABLE upload_jobs RENAME TO upload_jobs_legacy", connection))
        {
            step1.ExecuteNonQuery();
        }

        // 重建新表
        var createNew = @"
            CREATE TABLE upload_jobs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_path TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                object_key TEXT NOT NULL,
                upload_id TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                part_size INTEGER NOT NULL,
                parts_uploaded TEXT NOT NULL DEFAULT '[]',
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                UNIQUE(project_path, relative_path)
            )";
        using (var cmd = new SqliteCommand(createNew, connection))
        {
            cmd.ExecuteNonQuery();
        }

        // 数据迁移：INTEGER unix ms → TEXT ISO 8601
        // strftime 在 unix ms > 0 时输出 UTC ISO 字符串；= 0 时回落到 1970-01-01。
        var copySql = @"
            INSERT INTO upload_jobs
                (id, project_path, relative_path, object_key, upload_id,
                 file_size, part_size, parts_uploaded, created_at, updated_at)
            SELECT
                id, project_path, relative_path, object_key, upload_id,
                file_size, part_size, parts_uploaded,
                strftime('%Y-%m-%dT%H:%M:%fZ', created_at / 1000, 'unixepoch'),
                strftime('%Y-%m-%dT%H:%M:%fZ', updated_at / 1000, 'unixepoch')
            FROM upload_jobs_legacy";
        using (var cmd = new SqliteCommand(copySql, connection))
        {
            cmd.ExecuteNonQuery();
        }

        // 重建索引
        using var idx = new SqliteCommand(
            "CREATE INDEX IF NOT EXISTS idx_upload_jobs_project ON upload_jobs(project_path)", connection);
        idx.ExecuteNonQuery();

        // 丢弃中转表
        using (var drop = new SqliteCommand("DROP TABLE upload_jobs_legacy", connection))
        {
            drop.ExecuteNonQuery();
        }
    }

    public async Task<IReadOnlyList<UploadJob>> GetInProgressAsync(string projectPath, CancellationToken ct = default)
    {
        var jobs = new List<UploadJob>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        var sql = @"
            SELECT id, project_path, relative_path, object_key, upload_id,
                   file_size, part_size, parts_uploaded, created_at, updated_at
            FROM upload_jobs
            WHERE project_path = @projectPath
            ORDER BY id";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    public async Task<UploadJob?> GetByFileAsync(string projectPath, string relativePath, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        var sql = @"
            SELECT id, project_path, relative_path, object_key, upload_id,
                   file_size, part_size, parts_uploaded, created_at, updated_at
            FROM upload_jobs
            WHERE project_path = @projectPath AND relative_path = @relativePath
            LIMIT 1";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadJob(reader);
        }
        return null;
    }

    public async Task UpsertAsync(UploadJob job, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var partsJson = JsonSerializer.Serialize(job.PartsUploaded);

        // INSERT ... ON CONFLICT DO UPDATE：
        //   created_at 不在 SET 列表 → 续传时保留首次创建时间；
        //   updated_at 每次 Upsert 刷新（应用层传入 now）。
        var sql = @"
            INSERT INTO upload_jobs
                (project_path, relative_path, object_key, upload_id,
                 file_size, part_size, parts_uploaded, created_at, updated_at)
            VALUES
                (@projectPath, @relativePath, @objectKey, @uploadId,
                 @fileSize, @partSize, @partsUploaded, @createdAt, @updatedAt)
            ON CONFLICT(project_path, relative_path) DO UPDATE SET
                object_key = @objectKey,
                upload_id = @uploadId,
                file_size = @fileSize,
                part_size = @partSize,
                parts_uploaded = @partsUploaded,
                created_at = created_at,
                updated_at = @updatedAt";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", job.ProjectPath);
        cmd.Parameters.AddWithValue("@relativePath", job.RelativePath);
        cmd.Parameters.AddWithValue("@objectKey", job.ObjectKey);
        cmd.Parameters.AddWithValue("@uploadId", job.UploadId);
        cmd.Parameters.AddWithValue("@fileSize", job.FileSize);
        cmd.Parameters.AddWithValue("@partSize", job.PartSize);
        cmd.Parameters.AddWithValue("@partsUploaded", partsJson);
        cmd.Parameters.AddWithValue("@createdAt", SqliteDateTime.ToDb(job.CreatedAt));
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(job.UpdatedAt));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = "DELETE FROM upload_jobs WHERE id = @id";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteByFileAsync(string projectPath, string relativePath, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        var sql = "DELETE FROM upload_jobs WHERE project_path = @projectPath AND relative_path = @relativePath";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static UploadJob ReadJob(SqliteDataReader reader)
    {
        var partsJson = reader.GetString(7);
        var parts = JsonSerializer.Deserialize<List<UploadedPart>>(partsJson) ?? new List<UploadedPart>();

        return new UploadJob
        {
            Id = reader.GetInt64(0),
            ProjectPath = reader.GetString(1),
            RelativePath = reader.GetString(2),
            ObjectKey = reader.GetString(3),
            UploadId = reader.GetString(4),
            FileSize = reader.GetInt64(5),
            PartSize = reader.GetInt32(6),
            PartsUploaded = parts,
            CreatedAt = SqliteDateTime.FromDb(reader.GetString(8)),
            UpdatedAt = SqliteDateTime.FromDb(reader.GetString(9)),
        };
    }
}

public interface IUploadJobRepository
{
    Task<IReadOnlyList<UploadJob>> GetInProgressAsync(string projectPath, CancellationToken ct = default);
    Task<UploadJob?> GetByFileAsync(string projectPath, string relativePath, CancellationToken ct = default);
    Task UpsertAsync(UploadJob job, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task DeleteByFileAsync(string projectPath, string relativePath, CancellationToken ct = default);
}
