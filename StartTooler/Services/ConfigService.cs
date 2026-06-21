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

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Config (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            )";
        command.ExecuteNonQuery();
    }

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Config WHERE Key = @key";
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
            INSERT OR REPLACE INTO Config (Key, Value, UpdatedAt)
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
