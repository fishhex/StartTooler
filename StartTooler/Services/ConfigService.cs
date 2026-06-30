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

        // 兼容旧版大写表名 Config → config（见 10-trap-book.md §5）
        MigrateLegacyTableNameIfNeeded(connection);

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS config (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            )";
        command.ExecuteNonQuery();
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

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM config WHERE Key = @key";
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

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO config (Key, Value, UpdatedAt)
            VALUES (@key, @value, @updatedAt)";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", json);
        command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));

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
