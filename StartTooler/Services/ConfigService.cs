using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace StartTooler.Services;

public class ConfigService : IConfigService
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public ConfigService()
    {
        // 数据存放在应用数据目录
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StartTooler");

        if (!Directory.Exists(appDataPath))
            Directory.CreateDirectory(appDataPath);

        _dbPath = Path.Combine(appDataPath, "config.db");
        _connectionString = $"Data Source={_dbPath}";

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 1) 先确保 config 表存在。新装场景：建表；老 PascalCase 库场景：IF NOT EXISTS 不会动
        //    已存在的 Config 表，留给下一步改名处理；snake_case 老库：no-op。
        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS config (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            )";
        command.ExecuteNonQuery();

        // 2) 兼容路径：PascalCase 表名 Config → config（10-trap-book.md §5）
        MigrateLegacyTableNameIfNeeded(connection);

        // 3) 兼容路径：PascalCase 列名 Key/Value/UpdatedAt → snake_case + 加 created_at
        //    详见 02-data-layer.md §11
        MigrateSchemaToSnakeCaseIfNeeded(connection);
    }

    private static void MigrateLegacyTableNameIfNeeded(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='Config' LIMIT 1";
        var exists = check.ExecuteScalar();
        if (exists is null) return;

        // SQLite 对非引号 identifier 用 NOCASE 比较，Config 和 config 视为同名；
        // 单步 ALTER TABLE Config RENAME TO config 会报 "already another table or index with this name"。
        // 用一个跟 Config NOCASE 不等的中转名分两步走：Config → Config_temp → config。
        // PRIMARY KEY 的 sqlite_autoindex_Config_1 也会跟着 RENAME 一起搬过去，零数据损失。
        using (var step1 = connection.CreateCommand())
        {
            step1.CommandText = "ALTER TABLE \"Config\" RENAME TO \"Config_temp\"";
            step1.ExecuteNonQuery();
        }
        using (var step2 = connection.CreateCommand())
        {
            step2.CommandText = "ALTER TABLE \"Config_temp\" RENAME TO \"config\"";
            step2.ExecuteNonQuery();
        }

        System.Diagnostics.Trace.WriteLine("[Config] Migrated legacy table 'Config' → 'config' (via Config_temp)");
    }

    /// <summary>
    /// 把老版本 PascalCase 列名迁到 snake_case，并补 created_at 列。
    /// 所有操作幂等：迁移成功后多次启动不再触发。
    /// </summary>
    private static void MigrateSchemaToSnakeCaseIfNeeded(SqliteConnection connection)
    {
        // 兜底：如果调用方没经过 InitializeDatabase 直接进到这里（例如未来从别的入口走），
        // config 表可能还不存在，下面的 ALTER 操作会报 no such table。这里直接早退。
        if (!Data.SqliteMigrations.TableExists(connection, "config")) return;

        Data.SqliteMigrations.RenameColumnIfExists(connection, "config", "Key", "key");
        Data.SqliteMigrations.RenameColumnIfExists(connection, "config", "Value", "value");
        Data.SqliteMigrations.RenameColumnIfExists(connection, "config", "UpdatedAt", "updated_at");

        // 老库 created_at 缺失，ADD 时用 epoch 起点兜底回填（实际值对老 key 无意义，业务上 created_at
        // 只反映「写入数据库这个 key 的时间」而不是「应用配置变更时间」——这是 v0 设计的妥协）。
        Data.SqliteMigrations.AddColumnIfMissing(
            connection, "config", "created_at",
            "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000Z'");
    }

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM config WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);

        var result = await command.ExecuteScalarAsync();
        if (result is string json)
        {
            return JsonSerializer.Deserialize<T>(json);
        }

        return null;
    }

    public async Task SetAsync<T>(string key, T value) where T : class
    {
        var json = JsonSerializer.Serialize(value);
        var nowIso = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // INSERT ... ON CONFLICT(key) DO UPDATE —— 老行保留 created_at，只刷 value/updated_at
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO config (key, value, created_at, updated_at)
            VALUES (@key, @value, @createdAt, @updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value = @value,
                updated_at = @updatedAt";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", json);
        command.Parameters.AddWithValue("@createdAt", nowIso);
        command.Parameters.AddWithValue("@updatedAt", nowIso);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> GetOrCreateAsync<T>(string key) where T : class, new()
    {
        var result = await GetAsync<T>(key);
        if (result != null)
            return result;

        var newValue = new T();
        await SetAsync(key, newValue);
        return newValue;
    }
}
