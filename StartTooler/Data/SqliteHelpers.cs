using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace StartTooler.Data;

/// <summary>
/// SQLite datetime 字段的 ISO 8601 round-trip 读写约定（见 02-data-layer.md §11）。
///
/// 存储格式：TEXT, ISO 8601 "O" 格式 UTC（例 "2026-07-02T03:09:47.0000000Z"）。
/// 不使用 INTEGER unix ms —— 跨时区不可读、SQLite 函数表达式要除 1000，体验差。
/// </summary>
internal static class SqliteDateTime
{
    private const string RoundTripFormat = "O";

    /// <summary>DateTime → DB 字符串。统一归一为 UTC + InvariantCulture，避免本地化干扰。</summary>
    public static string ToDb(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Local) dt = dt.ToUniversalTime();
        if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return dt.ToString(RoundTripFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>DB 参数 nullable 包装（SqliteParameter 不接受 null DateTime）。</summary>
    public static object ToDbOrNull(DateTime? dt) => dt.HasValue ? ToDb(dt.Value) : (object)DBNull.Value;

    /// <summary>DB 字符串 → DateTime。RoundtripKind 保留原始时区信息。</summary>
    public static DateTime FromDb(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTime? FromDbOrNull(string? s) =>
        string.IsNullOrEmpty(s) ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>
/// SQLite schema 迁移辅助（PRAGMA table_info 检测）。EnsureDatabase 阶段使用，全幂等。
/// </summary>
internal static class SqliteMigrations
{
    /// <summary>表是否存在（大小写不敏感，兼容 PascalCase 老库）。</summary>
    public static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = new SqliteCommand(
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name LIMIT 1",
            connection);
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>列是否存在（大小写不敏感，兼容 PascalCase 老库）。</summary>
    public static bool ColumnExists(SqliteConnection connection, string table, string column)
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

    /// <summary>读取列的 type affinity（PRAGMA table_info 第 3 列，例 "INTEGER" / "TEXT"）。</summary>
    public static string? GetColumnType(SqliteConnection connection, string table, string column)
    {
        using var cmd = new SqliteCommand($"PRAGMA table_info({table})", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        return null;
    }

    /// <summary>列名是否大小写敏感匹配（用于 RENAME COLUMN 前精确锁定老 PascalCase 列名）。</summary>
    public static bool ColumnExistsCaseSensitive(SqliteConnection connection, string table, string column)
    {
        using var cmd = new SqliteCommand($"PRAGMA table_info({table})", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>PascalCase 列名 RENAME 为 snake_case（如存在）。SQLite 3.25+ 自带 SQLite（Microsoft.Data.Sqlite 8/9）支持。</summary>
    public static void RenameColumnIfExists(SqliteConnection connection, string table, string oldName, string newName)
    {
        if (!ColumnExistsCaseSensitive(connection, table, oldName)) return;
        using var cmd = new SqliteCommand(
            $"ALTER TABLE {table} RENAME COLUMN \"{oldName}\" TO \"{newName}\"", connection);
        cmd.ExecuteNonQuery();
        System.Diagnostics.Trace.WriteLine($"[{table}] Renamed column '{oldName}' → '{newName}'");
    }

    /// <summary>缺列就 ADD（带 DEFAULT 兜底回填老行）。</summary>
    public static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        if (ColumnExists(connection, table, column)) return;
        using var cmd = new SqliteCommand($"ALTER TABLE {table} ADD COLUMN {column} {definition}", connection);
        cmd.ExecuteNonQuery();
        System.Diagnostics.Trace.WriteLine($"[{table}] Added column '{column}'");
    }
}
