using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
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
        _dbPath = AppPaths.ConfigDbPath;
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
        await SetRawAsync(key, json);
    }

    public async Task SetRawAsync(string key, string rawJson)
    {
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
        command.Parameters.AddWithValue("@value", rawJson);
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

    // ============================================================
    // 导入 / 导出 (v0.11 spec doc/0.11/02-settings-improve.md §3.4)
    // ============================================================

    /// <summary>
    /// 密钥字段值占位符 —— 导出时塞这个串进 JSON，导入时遇到跳过写回，
    /// 让用户在新机器上手动重填（避免密钥明文落地到备份文件）。
    /// </summary>
    public const string SecretPlaceholder = "<请在导入后手动填写>";

    /// <summary>
    /// 哪些 key 在导出时属于「密钥」需 redact，导入时跳过空占位。
    /// 这里直接按 key 名匹配 —— 跟 ConfigKeys / 持久化结构对应，零魔法。
    /// </summary>
    private static readonly HashSet<string> SecretKeys = new()
    {
        ConfigKeys.AI,           // 通用 AI key 内部含 ApiKey
        // 单独用 key 名（不是 ConfigKey）匹配也行，这里走"全 key → 字段级 redact"
        // 简化：所有顶层 key 整体当作 string JSON 导出，密钥 redact 在 key 维度判断。
    };

    public async Task<int> ExportToJsonAsync(Stream stream, bool redactSecrets = true)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // 1) 拉所有 key + 原始 JSON value
        var rows = new List<(string Key, string Value)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM config";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        // 2) 构造导出 dict
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in rows)
        {
            if (redactSecrets && IsSecretKey(key))
            {
                // 整 key redact —— 导入时跳过；用户导入后只能从 UI 手动填密钥
                dict[key] = SecretPlaceholder;
            }
            else
            {
                // 把 value 字符串作为 JSON token 直接塞 dict（让它保持原始 JSON 形态）
                // 用 JsonElement 更直观；这里走 TryParse 简单判定
                try
                {
                    using var doc = JsonDocument.Parse(value);
                    dict[key] = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // 退化：value 不是合法 JSON（比如未来某天直接存了非 JSON 字符串），
                    // 原样落 string
                    dict[key] = value;
                }
            }
        }

        // 3) 序列化 —— 用 UnsafeRelaxedJsonEscaping 不把非 ASCII 转成 \uXXXX，
        //    这样用户在备份文件里能看到中文 key 注释（如果有）
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
        };
        await JsonSerializer.SerializeAsync(stream, dict, options);
        return dict.Count;
    }

    public async Task<int> ImportFromJsonAsync(Stream stream)
    {
        // 1) 读 stream → Dictionary<string, JsonElement>
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(stream, options);
        if (dict == null) return 0;

        // 2) 逐 key 写回 —— v0.11 起允许全量导入（含密钥）。旧 redacted 备份里的 SecretPlaceholder
        //    占位符会作为字面量写回（如 "<请在导入后手动填写>"），用户得自己再去 UI 改密钥。
        var count = 0;
        foreach (var (key, value) in dict)
        {
            string jsonValue;
            if (value.ValueKind == JsonValueKind.String)
            {
                // 顶层 string —— 保持为 string JSON（外面加引号）
                jsonValue = JsonSerializer.Serialize(value.GetString());
            }
            else
            {
                // object/array/number/bool —— 重新规范化
                jsonValue = value.GetRawText();
            }

            await SetRawAsync(key, jsonValue);
            count++;
        }
        return count;
    }

    /// <summary>
    /// 判断 key 是否属于「密钥」分类。
    /// v0.11 实现：直接按 key 名匹配 ConfigKeys.AI（内部含 ApiKey）。
    /// OSS 凭据存在 oss key 里（AccessKeyId + AccessKeySecret 两个），SecretKey 一并 redact 防止泄漏。
    /// </summary>
    private static bool IsSecretKey(string key)
    {
        return key == ConfigKeys.AI || key == ConfigKeys.Oss;
    }
}
