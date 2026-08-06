using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.Data;

/// <summary>
/// v0.12: 拍摄会话仓储实现（spec/03-shooting-diary.md §2.3）。
/// 共享 MediaRepository 的 SQLite 数据库，独立管理 sessions 表。
/// </summary>
public class SessionRepository : ISessionRepository
{
    private readonly string _connectionString;

    public SessionRepository()
    {
        var dbPath = AppPaths.MediaDbPath;
        _connectionString = $"Data Source={dbPath}";
        EnsureDatabase();
    }

    private void EnsureDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS sessions (
                id          TEXT PRIMARY KEY,
                project_path TEXT NOT NULL,
                title       TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                start_time  TEXT NOT NULL,
                end_time    TEXT NOT NULL,
                location    TEXT NOT NULL DEFAULT '',
                wind_dir    TEXT NOT NULL DEFAULT '',
                wind_level  INTEGER NOT NULL DEFAULT 0,
                cloud_cover TEXT NOT NULL DEFAULT '',
                bortle      INTEGER,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_sessions_project ON sessions(project_path);
        ";
        using (var cmd = new SqliteCommand(createTableSql, connection))
        {
            cmd.ExecuteNonQuery();
        }
    }

    public async Task<IReadOnlyList<Session>> GetByProjectAsync(string projectPath, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        var results = new List<Session>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = @"
            SELECT id, project_path, title, description, start_time, end_time,
                   location, wind_dir, wind_level, cloud_cover, bortle, created_at, updated_at
            FROM sessions
            WHERE project_path = @projectPath
            ORDER BY start_time DESC";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadSession(reader));
        }
        return results;
    }

    public async Task<Session?> GetByIdAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = @"
            SELECT id, project_path, title, description, start_time, end_time,
                   location, wind_dir, wind_level, cloud_cover, bortle, created_at, updated_at
            FROM sessions
            WHERE id = @id";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadSession(reader);
        }
        return null;
    }

    public async Task UpsertAsync(Session session, CancellationToken ct = default)
    {
        if (session.CreatedAt == default)
        {
            session.CreatedAt = DateTime.UtcNow;
        }
        session.UpdatedAt = DateTime.UtcNow;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = @"
            INSERT INTO sessions (id, project_path, title, description, start_time, end_time,
                location, wind_dir, wind_level, cloud_cover, bortle, created_at, updated_at)
            VALUES (@id, @projectPath, @title, @description, @startTime, @endTime,
                @location, @windDir, @windLevel, @cloudCover, @bortle, @createdAt, @updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                description = excluded.description,
                start_time = excluded.start_time,
                end_time = excluded.end_time,
                location = excluded.location,
                wind_dir = excluded.wind_dir,
                wind_level = excluded.wind_level,
                cloud_cover = excluded.cloud_cover,
                bortle = excluded.bortle,
                updated_at = excluded.updated_at";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", session.Id);
        cmd.Parameters.AddWithValue("@projectPath", session.ProjectPath);
        cmd.Parameters.AddWithValue("@title", session.Title);
        cmd.Parameters.AddWithValue("@description", session.Description);
        cmd.Parameters.AddWithValue("@startTime", SqliteDateTime.ToDb(session.StartTime));
        cmd.Parameters.AddWithValue("@endTime", SqliteDateTime.ToDb(session.EndTime));
        cmd.Parameters.AddWithValue("@location", session.Location);
        cmd.Parameters.AddWithValue("@windDir", session.WindDir);
        cmd.Parameters.AddWithValue("@windLevel", session.WindLevel);
        cmd.Parameters.AddWithValue("@cloudCover", session.CloudCover);
        cmd.Parameters.AddWithValue("@bortle", session.Bortle.HasValue ? session.Bortle.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", SqliteDateTime.ToDb(session.CreatedAt));
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(session.UpdatedAt));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = "DELETE FROM sessions WHERE id = @id";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertBatchAsync(IReadOnlyList<Session> sessions, CancellationToken ct = default)
    {
        if (sessions.Count == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        const string sql = @"
            INSERT INTO sessions (id, project_path, title, description, start_time, end_time,
                location, wind_dir, wind_level, cloud_cover, bortle, created_at, updated_at)
            VALUES (@id, @projectPath, @title, @description, @startTime, @endTime,
                @location, @windDir, @windLevel, @cloudCover, @bortle, @createdAt, @updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                description = excluded.description,
                start_time = excluded.start_time,
                end_time = excluded.end_time,
                location = excluded.location,
                wind_dir = excluded.wind_dir,
                wind_level = excluded.wind_level,
                cloud_cover = excluded.cloud_cover,
                bortle = excluded.bortle,
                updated_at = excluded.updated_at";

        foreach (var session in sessions)
        {
            if (session.CreatedAt == default)
            {
                session.CreatedAt = DateTime.UtcNow;
            }
            session.UpdatedAt = DateTime.UtcNow;

            await using var cmd = new SqliteCommand(sql, connection, tx);
            cmd.Parameters.AddWithValue("@id", session.Id);
            cmd.Parameters.AddWithValue("@projectPath", session.ProjectPath);
            cmd.Parameters.AddWithValue("@title", session.Title);
            cmd.Parameters.AddWithValue("@description", session.Description);
            cmd.Parameters.AddWithValue("@startTime", SqliteDateTime.ToDb(session.StartTime));
            cmd.Parameters.AddWithValue("@endTime", SqliteDateTime.ToDb(session.EndTime));
            cmd.Parameters.AddWithValue("@location", session.Location);
            cmd.Parameters.AddWithValue("@windDir", session.WindDir);
            cmd.Parameters.AddWithValue("@windLevel", session.WindLevel);
            cmd.Parameters.AddWithValue("@cloudCover", session.CloudCover);
            cmd.Parameters.AddWithValue("@bortle", session.Bortle.HasValue ? session.Bortle.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", SqliteDateTime.ToDb(session.CreatedAt));
            cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(session.UpdatedAt));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task DeleteBatchAsync(IReadOnlyList<string> sessionIds, CancellationToken ct = default)
    {
        if (sessionIds.Count == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var placeholders = string.Join(",", sessionIds.Select((_, i) => $"@id{i}"));
        var sql = $"DELETE FROM sessions WHERE id IN ({placeholders})";

        await using var cmd = new SqliteCommand(sql, connection);
        for (int i = 0; i < sessionIds.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@id{i}", sessionIds[i]);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Session ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ProjectPath = reader.GetString(1),
        Title = reader.GetString(2),
        Description = reader.GetString(3),
        StartTime = SqliteDateTime.FromDb(reader.GetString(4)),
        EndTime = SqliteDateTime.FromDb(reader.GetString(5)),
        Location = reader.GetString(6),
        WindDir = reader.GetString(7),
        WindLevel = reader.GetInt32(8),
        CloudCover = reader.GetString(9),
        Bortle = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        CreatedAt = SqliteDateTime.FromDb(reader.GetString(11)),
        UpdatedAt = SqliteDateTime.FromDb(reader.GetString(12)),
    };
}