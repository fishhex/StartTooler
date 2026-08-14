using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using StartTooler.Data;

namespace StartTooler.Services;

/// <summary>
/// 高级页面「数据库浏览器」底层服务。类似 DB Browser for SQLite 的能力：
///   - 枚举所有 SQLite 数据库及其表/列
///   - 分页 SELECT 全表数据
///   - 任意 SQL 执行（带写操作确认）
///   - 行级 INSERT / UPDATE / DELETE
///
/// 设计原则：
///   - 只用 raw SQL，不依赖具体 Repository（高级用户视角，绕开业务封装）
///   - 单连接原则：每次操作一个连接，用完即弃（与 doc/02-data-layer §6.1 一致）
///   - SELECT 返回通用 DataTable，方便 UI 渲染任意 schema 的表
///   - 写操作返回受影响行数，让 UI 决定是否提示
/// </summary>
public class DbInspectorService
{
    /// <summary>
    /// 项目内可浏览的数据库清单。
    /// ConnectionString 用 Microsoft.Data.Sqlite 的标准 Data Source= 写法。
    /// </summary>
    public IReadOnlyList<DatabaseInfo> Databases { get; } = new List<DatabaseInfo>
    {
        new("config", "config.db (用户配置)", AppPaths.ConfigDbPath),
        new("media",  "media.db (媒体 + 任务)", AppPaths.MediaDbPath),
    };

    /// <summary>SQLite 类型名 → 友好显示名（含类型亲和性提示）。</summary>
    public static string FormatColumnType(string sqliteType)
    {
        if (string.IsNullOrEmpty(sqliteType)) return "ANY";
        return sqliteType.ToUpperInvariant() switch
        {
            "INTEGER" => "INTEGER",
            "TEXT" => "TEXT",
            "REAL" => "REAL",
            "BLOB" => "BLOB",
            "NUMERIC" => "NUMERIC",
            _ => sqliteType,
        };
    }

    /// <summary>
    /// 列出指定 DB 的所有用户表（sqlite_master type='table'，排除 sqlite 内部表）。
    /// </summary>
    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(string dbKey, CancellationToken ct = default)
    {
        var (cs, _) = ResolveConnectionString(dbKey);
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(ct);

        const string sql = @"
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name";

        var tables = new List<TableInfo>();
        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tables.Add(new TableInfo(reader.GetString(0)));
        }
        return tables;
    }

    /// <summary>
    /// 列出表的列定义（PRAGMA table_info）。
    /// </summary>
    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string dbKey, string tableName, CancellationToken ct = default)
    {
        var (cs, _) = ResolveConnectionString(dbKey);
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(ct);

        var columns = new List<ColumnInfo>();
        await using var cmd = new SqliteCommand($"PRAGMA table_info({QuoteIdentifier(tableName)})", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // PRAGMA table_info 返回: cid, type, notnull, dflt_value, pk
            columns.Add(new ColumnInfo(
                Name: reader.GetString(1),
                DeclaredType: reader.IsDBNull(2) ? "" : reader.GetString(2),
                NotNull: reader.GetInt32(3) == 1,
                DefaultValue: reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString(),
                IsPrimaryKey: reader.GetInt32(5) != 0
            ));
        }
        return columns;
    }

    /// <summary>
    /// 通用 SELECT：分页查询表数据。返回 (列定义, 行字典列表)。
    /// 返回字典而非 DataTable，让 Avalonia DataGrid 能正确按属性生成列。
    /// </summary>
    /// <param name="whereClause">可选 WHERE 子句（不含 WHERE 关键字），不能含分号避免注入</param>
    /// <param name="orderByClause">可选 ORDER BY 子句</param>
    public async Task<QueryResult> SelectAsync(
        string dbKey, string tableName,
        string? whereClause = null,
        string? orderByClause = null,
        int limit = 500, int offset = 0,
        CancellationToken ct = default)
    {
        var (cs, _) = ResolveConnectionString(dbKey);
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(ct);

        // 拼接：必须保证 tableName 干净（已 QuoteIdentifier），whereClause/orderBy 由调用方负责（高级用户视角）。
        var sql = $"SELECT * FROM {QuoteIdentifier(tableName)}";
        if (!string.IsNullOrWhiteSpace(whereClause)) sql += $" WHERE {whereClause}";
        if (!string.IsNullOrWhiteSpace(orderByClause)) sql += $" ORDER BY {orderByClause}";
        sql += $" LIMIT {Math.Max(1, Math.Min(limit, 5000))} OFFSET {Math.Max(0, offset)}";

        var columnNames = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add(reader.GetName(i));
        }

        while (await reader.ReadAsync(ct))
        {
            var dict = new Dictionary<string, object?>(columnNames.Count);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                dict[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(dict);
        }
        return new QueryResult(columnNames, rows);
    }

    /// <summary>
    /// 统计行数（带 WHERE）。让 UI 显示「共 N 条 · 当前 X-Y 条」。
    /// </summary>
    public async Task<long> CountAsync(string dbKey, string tableName, string? whereClause = null, CancellationToken ct = default)
    {
        var (cs, _) = ResolveConnectionString(dbKey);
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(ct);

        var sql = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)}";
        if (!string.IsNullOrWhiteSpace(whereClause)) sql += $" WHERE {whereClause}";

        await using var cmd = new SqliteCommand(sql, connection);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0L;
    }

    /// <summary>
    /// 按主键删除一行。返回受影响行数。
    /// pkColumn/pkValue 由调用方传入（已通过 GetColumnsAsync 拿到 PK）。
    /// </summary>
    public async Task<int> DeleteByPkAsync(string dbKey, string tableName, string pkColumn, object pkValue, CancellationToken ct = default)
    {
        var (cs, _) = ResolveConnectionString(dbKey);
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(ct);

        var sql = $"DELETE FROM {QuoteIdentifier(tableName)} WHERE {QuoteIdentifier(pkColumn)} = @pk";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@pk", pkValue);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 按 SQL 直接执行写操作（UPDATE / DELETE / INSERT），返回受影响行数。
    ///
    /// 高级用户视角：UI 在调用前已二次确认。这里不做 DDL 拦截（CREATE/DROP/ALTER），
    /// 由 ViewModel 在调用前用 TryClassifySql 提示用户。
    /// </summary>
    public async Task<int> ExecuteNonQueryAsync(string dbKey, string sql, CancellationToken ct = default)
    {
        var (cs, _) = ResolveConnectionString(dbKey);
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(ct);

        await using var cmd = new SqliteCommand(sql, connection);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 把 SQL 按首关键字分类：SELECT / READONLY / DML / DDL。
    /// 用于 UI 在执行前判断是否需要二次确认。
    /// </summary>
    public static SqlKind ClassifySql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return SqlKind.Empty;
        var trimmed = sql.TrimStart();
        // 跳过前置注释
        while (true)
        {
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                var idx = trimmed.IndexOf('\n');
                if (idx < 0) return SqlKind.Empty;
                trimmed = trimmed[(idx + 1)..].TrimStart();
                continue;
            }
            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                var idx = trimmed.IndexOf("*/", StringComparison.Ordinal);
                if (idx < 0) return SqlKind.Empty;
                trimmed = trimmed[(idx + 2)..].TrimStart();
                continue;
            }
            break;
        }

        var upper = trimmed.ToUpperInvariant();
        if (upper.StartsWith("SELECT") || upper.StartsWith("PRAGMA") || upper.StartsWith("WITH"))
            return SqlKind.Select;
        if (upper.StartsWith("INSERT") || upper.StartsWith("UPDATE") || upper.StartsWith("DELETE") || upper.StartsWith("REPLACE"))
            return SqlKind.Dml;
        if (upper.StartsWith("CREATE") || upper.StartsWith("DROP") || upper.StartsWith("ALTER") || upper.StartsWith("TRUNCATE") || upper.StartsWith("RENAME") || upper.StartsWith("ATTACH") || upper.StartsWith("DETACH"))
            return SqlKind.Ddl;
        return SqlKind.Unknown;
    }

    /// <summary>把 identifier 加双引号（防止表名含特殊字符）。</summary>
    private static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    private (string connectionString, string displayName) ResolveConnectionString(string dbKey)
    {
        foreach (var db in Databases)
        {
            if (db.Key == dbKey)
            {
                if (!File.Exists(db.Path))
                {
                    throw new FileNotFoundException($"数据库文件不存在: {db.Path}");
                }
                return ($"Data Source={db.Path}", db.DisplayName);
            }
        }
        throw new ArgumentException($"未知的数据库 key: {dbKey}", nameof(dbKey));
    }
}

public enum SqlKind
{
    Empty,
    Select,
    Dml,
    Ddl,
    Unknown,
}

public sealed record DatabaseInfo(string Key, string DisplayName, string Path);

public sealed record TableInfo(string Name);

public sealed record ColumnInfo(
    string Name,
    string DeclaredType,
    bool NotNull,
    string? DefaultValue,
    bool IsPrimaryKey
);

/// <summary>
/// SELECT/PRAGMA 查询结果：列名顺序 + 行数据（每行是「列名 → 值」的字典）。
/// 用字典而非 DataTable 让 Avalonia DataGrid 正确按属性生成列。
/// </summary>
public sealed record QueryResult(
    IReadOnlyList<string> ColumnNames,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows
);