using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.Data;

public class MediaRepository : IMediaRepository
{
    private readonly string _connectionString;

    public MediaRepository()
    {
        var dbPath = AppPaths.MediaDbPath;
        _connectionString = $"Data Source={dbPath}";
        EnsureDatabase();
    }

    /// <summary>
    /// 写 tags 列时用的 JsonSerializerOptions：用 UnsafeRelaxedJsonEscaping 让中文等非 ASCII 字符
    /// 原样写入（"月亮"），不被转义成 "\u6708\u4EAE"。
    /// 默认的 JavaScriptEncoder.Default 会把所有非 ASCII 转成 \uXXXX，导致
    /// GetByTagAsync 的 LIKE '%"标签"%' 模式失效。
    /// </summary>
    private static readonly JsonSerializerOptions s_writeTagsOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // === v0.11 tag 字典缓存（方案 B）===
    // project_path -> tag_id -> tag_name
    private readonly Dictionary<string, Dictionary<long, string>> _tagNameCache = new();
    // project_path -> tag_name -> tag_id
    private readonly Dictionary<string, Dictionary<string, long>> _tagIdCache = new();
    private readonly ReaderWriterLockSlim _tagCacheLock = new();

    private void EnsureDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS media_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_path TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                media_type INTEGER NOT NULL DEFAULT 0,
                file_size INTEGER NOT NULL DEFAULT 0,
                last_modified INTEGER NOT NULL DEFAULT 0,
                shot_at INTEGER,
                is_uploaded INTEGER NOT NULL DEFAULT 0,
                local_exists INTEGER NOT NULL DEFAULT 1,
                thumbnail_path TEXT,
                remote_url TEXT,
                uploaded_at INTEGER,
                scanned_at INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                UNIQUE(project_path, relative_path)
            );
            CREATE INDEX IF NOT EXISTS idx_media_files_date ON media_files(shot_at);
            CREATE INDEX IF NOT EXISTS idx_media_files_project ON media_files(project_path);
        ";

        using (var cmd = new SqliteCommand(createTableSql, connection))
        {
            cmd.ExecuteNonQuery();
        }

        // 兼容路径：老库（v0.4 之前）缺 created_at/updated_at 列。ALTER ADD 时用 epoch 起点
        // 兜底回填老行；新行写入时会用 SQL DEFAULT 取真实时间。
        SqliteMigrations.AddColumnIfMissing(
            connection, "media_files", "created_at",
            "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000Z'");
        SqliteMigrations.AddColumnIfMissing(
            connection, "media_files", "updated_at",
            "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000Z'");

        // === v0.6 AI 打标字段（spec doc/12-ai-toolbar-buttons.md §3.1.3） ===
        // tags 用 JSON 数组（System.Text.Json 序列化），默认 '[]' 占位让老行非空可 LIKE 查询。
        SqliteMigrations.AddColumnIfMissing(
            connection, "media_files", "tags",
            "TEXT NOT NULL DEFAULT '[]'");
        SqliteMigrations.AddColumnIfMissing(
            connection, "media_files", "score",
            "INTEGER");
        SqliteMigrations.AddColumnIfMissing(
            connection, "media_files", "tagged_at",
            "INTEGER");
        SqliteMigrations.AddColumnIfMissing(
            connection, "media_files", "tag_error",
            "TEXT");
        // v0.7: 质量评价标签独立列（与 tags 同结构）
        SqliteMigrations.AddColumnIfMissing(
            connection, "media_files", "quality_tags",
            "TEXT NOT NULL DEFAULT '[]'");

        // v0.8: 软删除时间戳（spec doc/14-delete-and-trash.md §2.1）
        // NULL = 正常文件；NOT NULL = 已移入垃圾筒（值为 unix ms 时间戳）
        SqliteMigrations.AddColumnIfMissing(
            connection, "media_files", "deleted_at",
            "INTEGER");

        // 评分排序的 B-tree 索引。tags 索引对未来 SQLite JSON1 查询有用，
        // 当前 LIKE '%"标签"%' 仍走全表扫（B-tree 不加速前缀模糊）。
        using (var idxCmd = new SqliteCommand(@"
            CREATE INDEX IF NOT EXISTS idx_media_files_score ON media_files(score);
            CREATE INDEX IF NOT EXISTS idx_media_files_tags ON media_files(tags);
        ", connection))
        {
            idxCmd.ExecuteNonQuery();
        }

        // === v0.11: 标签字典表（方案 B：media_files.tags 存 tag id 数组）===
        EnsureTagsTable(connection);

        // 一次性迁移：把老数据里被 JavaScriptEncoder.Default 转义的 \uXXXX 中文 tag
        // 反序列化 + 用 UnsafeRelaxedJsonEscaping 重新写回。新数据用 s_writeTagsOptions 直接写原始中文。
        // 幂等：跑过一次后 tags 列已无 \u 转义，第二次不会命中 LIKE 模式。
        MigrateEscapedTagsToRaw(connection);

        // 一次性迁移：把 media_files.tags 从字符串数组 ["行星","月亮"] 转成 tag id 数组 [1,2]。
        // 幂等：转换后的列只含数字 / 中括号 / 逗号，不再匹配下面的 json_type='text'。
        MigrateTagsToIdArray(connection);
    }

    private static void EnsureTagsTable(SqliteConnection connection)
    {
        using var cmd = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_path TEXT NOT NULL,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                UNIQUE(project_path, name)
            );
            CREATE INDEX IF NOT EXISTS idx_tags_project ON tags(project_path);
        ", connection);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 老版本 UpdateTagAsync 写入的 tags 形如 ["\u6708\u4EAE", "\u5E7F\u89D2"]，导致 GetByTagAsync
    /// 的 LIKE '%"标签"%' 模式匹配不上（DB 里实际是 \u6708\u4EAE，不是中文"月亮"）。
    ///
    /// 这里把命中 \u 转义的行读出来 → 反序列化（JsonSerializer 透明处理 \uXXXX）→ 用
    /// s_writeTagsOptions 重新序列化 → UPDATE。第二次跑不会命中 LIKE，幂等。
    /// </summary>
    private static void MigrateEscapedTagsToRaw(SqliteConnection connection)
    {
        var selectSql = @"SELECT id, tags FROM media_files WHERE tags LIKE '%\u%'";
        var rows = new List<(long Id, string Tags)>();
        using (var cmd = new SqliteCommand(selectSql, connection))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        if (rows.Count == 0) return;

        Trace.WriteLine($"[MediaRepository] MigrateEscapedTagsToRaw: 命中 {rows.Count} 行待修复");

        using var tx = connection.BeginTransaction();
        var updateSql = "UPDATE media_files SET tags = @tags, updated_at = @updatedAt WHERE id = @id";
        foreach (var (id, oldJson) in rows)
        {
            var tags = ParseTags(oldJson);
            var newJson = JsonSerializer.Serialize(tags, s_writeTagsOptions);
            if (newJson == oldJson) continue;  // 防御性：万一转义形式未变化就不写

            using var cmd = new SqliteCommand(updateSql, connection, tx);
            cmd.Parameters.AddWithValue("@tags", newJson);
            cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        Trace.WriteLine($"[MediaRepository] MigrateEscapedTagsToRaw: 完成");
    }

    /// <summary>
    /// v0.11 一次性迁移：把 media_files.tags 从字符串数组 ["行星","月亮"] 转成 tag id 数组 [1,2]。
    /// 步骤：
    ///   1. 在 C# 中读取 media_files.tags（过滤有效 JSON 字符串数组）。
    ///   2. 把所有字符串 tag 插入 tags 表（幂等：INSERT OR IGNORE）。
    ///   3. 在 C# 中把 name 映射成 id，写回 JSON id 数组。
    /// 幂等：已转换的 tags 列不含字符串，ParseTags 返回空/数字，不会再被处理。
    /// </summary>
    private static void MigrateTagsToIdArray(SqliteConnection connection)
    {
        // 1. 读取所有需要迁移的行（有效 JSON 字符串数组）。
        const string selectSql = "SELECT id, project_path, tags FROM media_files WHERE tags IS NOT NULL AND tags != '[]'";
        var candidateRows = new List<(long Id, string ProjectPath, string Tags)>();
        using (var cmd = new SqliteCommand(selectSql, connection))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                candidateRows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        // 2. 只保留还能解析成字符串数组的行；已是 id 数组的行自然返回空 names。
        var rows = new List<(long Id, string ProjectPath, List<string> Names)>();
        var uniqueTags = new HashSet<(string ProjectPath, string Name)>();
        foreach (var (id, projectPath, json) in candidateRows)
        {
            var names = SafeParseStringArray(json);
            if (names.Count == 0) continue; // 空或已迁移
            rows.Add((id, projectPath, names));
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    uniqueTags.Add((projectPath, name));
            }
        }

        if (rows.Count == 0)
        {
            Trace.WriteLine("[MediaRepository] MigrateTagsToIdArray: 无需迁移");
            return;
        }

        Trace.WriteLine($"[MediaRepository] MigrateTagsToIdArray: 命中 {rows.Count} 行待转换，共 {uniqueTags.Count} 个唯一标签");

        // 3. 插入所有唯一标签。
        const string insertTagSql = "INSERT OR IGNORE INTO tags (project_path, name) VALUES (@projectPath, @name)";
        foreach (var (projectPath, name) in uniqueTags)
        {
            using var cmd = new SqliteCommand(insertTagSql, connection);
            cmd.Parameters.AddWithValue("@projectPath", projectPath);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.ExecuteNonQuery();
        }

        // 4. 批量查询 tag id 映射。
        var tagIdsByName = new Dictionary<(string ProjectPath, string Name), long>();
        const string mapSql = "SELECT project_path, name, id FROM tags";
        using (var cmd = new SqliteCommand(mapSql, connection))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                tagIdsByName[key] = reader.GetInt64(2);
            }
        }

        // 5. 更新 media_files.tags 为 id 数组。
        using var tx = connection.BeginTransaction();
        const string updateSql = "UPDATE media_files SET tags = @tags, updated_at = @updatedAt WHERE id = @id";
        foreach (var (id, projectPath, names) in rows)
        {
            var ids = new List<long>();
            foreach (var name in names)
            {
                if (tagIdsByName.TryGetValue((projectPath, name), out var tagId))
                    ids.Add(tagId);
            }
            var newJson = ids.Count == 0 ? "[]" : JsonSerializer.Serialize(ids, s_writeTagsOptions);

            using var cmd = new SqliteCommand(updateSql, connection, tx);
            cmd.Parameters.AddWithValue("@tags", newJson);
            cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        Trace.WriteLine($"[MediaRepository] MigrateTagsToIdArray: 完成");
    }

    /// <summary>
    /// 仅当 JSON 是字符串数组时返回元素列表；其他情况（已迁移的 id 数组、无效 JSON、null、[]）返回空列表。
    /// </summary>
    private static List<string> SafeParseStringArray(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<string>();
            var result = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    result.Add(el.GetString() ?? "");
                }
                else
                {
                    // 任一元素不是字符串 → 认为已迁移或格式不符，整体丢弃。
                    return new List<string>();
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public async Task<IReadOnlyList<DateCount>> GetDateGroupsAsync(string projectPath, CancellationToken ct = default)
    {
        var results = new List<DateCount>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // 规范化路径以匹配扫描时保存的格式
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        var sql = @"
            SELECT
                date(shot_at / 1000, 'unixepoch', 'localtime') as date,
                COUNT(*) as count
            FROM media_files
            WHERE project_path = @projectPath
              AND shot_at IS NOT NULL
              AND deleted_at IS NULL
            GROUP BY date(shot_at / 1000, 'unixepoch', 'localtime')
            ORDER BY date DESC";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var dateStr = reader.GetString(0);
            var count = reader.GetInt32(1);

            if (DateTime.TryParse(dateStr, out var date))
            {
                results.Add(new DateCount
                {
                    Date = date,
                    Count = count
                });
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<MediaFile>> GetByTimeRangeAsync(string projectPath, DateTimeOffset startTime, DateTimeOffset endTime, SortMode sortMode = SortMode.TimeDesc, CancellationToken ct = default)
    {
        var results = new List<MediaFile>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        await LoadTagCacheAsync(connection, normalizedPath, ct);

        var startTimestamp = startTime.ToUnixTimeMilliseconds();
        var endTimestamp = endTime.ToUnixTimeMilliseconds();

        var orderBy = sortMode switch
        {
            SortMode.ScoreDesc => "ORDER BY score IS NULL, score DESC, shot_at DESC, file_name ASC",
            _ => "ORDER BY shot_at DESC, file_name ASC",
        };

        var sql = $@"
            SELECT
                id, project_path, relative_path, file_name, media_type,
                file_size, last_modified, shot_at, is_uploaded, local_exists,
                thumbnail_path, remote_url, uploaded_at, scanned_at,
                created_at, updated_at,
                tags, score, tagged_at, tag_error,
                quality_tags,
                deleted_at
            FROM media_files
            WHERE project_path = @projectPath
              AND shot_at >= @startTime
              AND shot_at < @endTime
              AND deleted_at IS NULL
            {orderBy}
            LIMIT 2000";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
        cmd.Parameters.AddWithValue("@startTime", startTimestamp);
        cmd.Parameters.AddWithValue("@endTime", endTimestamp);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadMediaFileRow(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<MediaFile>> GetByDateAsync(string projectPath, DateTime date, SortMode sortMode = SortMode.TimeDesc, CancellationToken ct = default)
    {
        var results = new List<MediaFile>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // 规范化路径以匹配扫描时保存的格式
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        await LoadTagCacheAsync(connection, normalizedPath, ct);

        var startOfDay = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local);
        var endOfDay = startOfDay.AddDays(1);

        var startTimestamp = new DateTimeOffset(startOfDay).ToUnixTimeMilliseconds();
        var endTimestamp = new DateTimeOffset(endOfDay).ToUnixTimeMilliseconds();

        // sortMode 决定 ORDER BY 子句。SQL 拼接安全（orderBy 是硬编码常量，无用户输入）。
        var orderBy = sortMode switch
        {
            SortMode.ScoreDesc => "ORDER BY score IS NULL, score DESC, shot_at DESC, file_name ASC",
            _ => "ORDER BY shot_at DESC, file_name ASC",
        };

        var sql = $@"
            SELECT
                id, project_path, relative_path, file_name, media_type,
                file_size, last_modified, shot_at, is_uploaded, local_exists,
                thumbnail_path, remote_url, uploaded_at, scanned_at,
                created_at, updated_at,
                tags, score, tagged_at, tag_error,
                quality_tags,
                deleted_at
            FROM media_files
            WHERE project_path = @projectPath
              AND shot_at >= @startTime
              AND shot_at < @endTime
              AND deleted_at IS NULL
            {orderBy}
            LIMIT 1000";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
        cmd.Parameters.AddWithValue("@startTime", startTimestamp);
        cmd.Parameters.AddWithValue("@endTime", endTimestamp);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadMediaFileRow(reader));
        }

        return results;
    }

    // 支持的媒体扩展名
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp", ".heic",
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".pef"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".mpg", ".mpeg"
    };

    public async Task<ScanResult> ScanDirectoryAsync(string projectPath, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new ScanResult { Total = 0, Processed = 0, Failed = 0, NewFiles = 0, UpdatedFiles = 0 };

        // 第一遍：统计文件数量
        var files = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var ext in ImageExtensions) files.Add(ext);
                foreach (var ext in VideoExtensions) files.Add(ext);

                var allFiles = Directory.EnumerateFiles(projectPath, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                });

                foreach (var file in allFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    var ext = Path.GetExtension(file);
                    if (ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext))
                    {
                        files.Add(file);
                    }
                }
            }, ct);

            result.Total = files.Count;
            progress?.Report(new ScanProgress { Total = result.Total, Processed = 0 });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MediaRepository] ScanDirectoryAsync 文件枚举失败: {ex.Message}");
            result.Total = 0;
        }

        if (result.Total == 0)
        {
            progress?.Report(new ScanProgress { Total = 0, Processed = 0 });
            return result;
        }

        // 第二遍：写入数据库
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // INSERT ... ON CONFLICT DO UPDATE：
        //   created_at 不在 SET 列表 → 保留原值（首次插入用 SQL DEFAULT，续扫描保留初次入库时间）
        //   updated_at 每次扫描时刷新
        var insertSql = @"
            INSERT INTO media_files (project_path, relative_path, file_name, media_type,
                file_size, last_modified, shot_at, thumbnail_path, scanned_at,
                created_at, updated_at)
            VALUES (@projectPath, @relativePath, @fileName, @mediaType,
                @fileSize, @lastModified, @shotAt, @thumbnailPath, @scannedAt,
                @createdAt, @updatedAt)
            ON CONFLICT(project_path, relative_path) DO UPDATE SET
                file_size = @fileSize,
                last_modified = @lastModified,
                shot_at = COALESCE(shot_at, @shotAt),
                thumbnail_path = @thumbnailPath,
                scanned_at = @scannedAt,
                created_at = created_at,
                updated_at = @updatedAt";

        await using var cmd = new SqliteCommand(insertSql, connection);
        var projectPathNormalized = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var fileInfo = new FileInfo(filePath);
                var relativePath = Path.GetRelativePath(projectPath, filePath);
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var mediaType = ImageExtensions.Contains(ext) ? 0 : 1;
                var fileName = Path.GetFileName(filePath);
                var fileSize = fileInfo.Length;
                var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                var scannedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                // created_at/updated_at 都用当前 UTC：新行用 @createdAt 走 INSERT（与 DEFAULT 等价），
                // 老行走 ON CONFLICT 时 @createdAt 被忽略（SQL 写死 created_at = created_at 保留原值）。
                var nowIso = SqliteDateTime.ToDb(DateTime.UtcNow);

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@projectPath", projectPathNormalized);
                cmd.Parameters.AddWithValue("@relativePath", relativePath);
                cmd.Parameters.AddWithValue("@fileName", fileName);
                cmd.Parameters.AddWithValue("@mediaType", mediaType);
                cmd.Parameters.AddWithValue("@fileSize", fileSize);
                cmd.Parameters.AddWithValue("@lastModified", lastModified);
                cmd.Parameters.AddWithValue("@shotAt", lastModified); // 用文件修改时间作为 shot_at
                cmd.Parameters.AddWithValue("@thumbnailPath", DBNull.Value); // 缩略图稍后生成
                cmd.Parameters.AddWithValue("@scannedAt", scannedAt);
                cmd.Parameters.AddWithValue("@createdAt", nowIso);
                cmd.Parameters.AddWithValue("@updatedAt", nowIso);

                var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
                if (rowsAffected > 0)
                {
                    result.NewFiles++;
                }

                result.Processed++;
                progress?.Report(new ScanProgress
                {
                    Total = result.Total,
                    Processed = result.Processed,
                    Failed = result.Failed,
                    CurrentFile = fileInfo.Name
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[MediaRepository] 处理文件失败: {filePath}, {ex.Message}");
                result.Failed++;
            }
        }

        progress?.Report(new ScanProgress
        {
            Total = result.Total,
            Processed = result.Processed,
            Failed = result.Failed
        });

        return result;
    }

    public async Task GenerateThumbnailsAsync(string projectPath, IThumbnailService thumbnailService, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        // 获取所有没有缩略图的文件
        var selectSql = @"
            SELECT id, project_path, relative_path, file_name
            FROM media_files
            WHERE project_path = @projectPath
              AND (thumbnail_path IS NULL OR thumbnail_path = '')";

        var updateSql = @"
            UPDATE media_files
            SET thumbnail_path = @thumbnailPath,
                updated_at = @updatedAt
            WHERE id = @id";

        var files = new List<(long Id, string RelativePath)>();

        await using (var cmd = new SqliteCommand(selectSql, connection))
        {
            cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                files.Add((reader.GetInt64(0), reader.GetString(2)));
            }
        }

        var total = files.Count;
        var processed = 0;

        foreach (var (id, relativePath) in files)
        {
            if (ct.IsCancellationRequested) break;

            var fullPath = Path.Combine(normalizedPath, relativePath);
            if (!File.Exists(fullPath)) continue;

            var thumbnailPath = await thumbnailService.GenerateThumbnailAsync(fullPath, normalizedPath, ct);

            if (thumbnailPath != null)
            {
                await using var cmd = new SqliteCommand(updateSql, connection);
                cmd.Parameters.AddWithValue("@thumbnailPath", thumbnailPath);
                cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            processed++;
            progress?.Report(new ScanProgress
            {
                Total = total,
                Processed = processed,
                CurrentFile = Path.GetFileName(fullPath)
            });
        }
    }

    public async Task UpdateUploadStateAsync(long fileId, bool isUploaded, long? uploadedAt, string? remoteUrl, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = @"
            UPDATE media_files
            SET is_uploaded = @isUploaded,
                uploaded_at = @uploadedAt,
                remote_url = @remoteUrl,
                updated_at = @updatedAt
            WHERE id = @id";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", fileId);
        cmd.Parameters.AddWithValue("@isUploaded", isUploaded ? 1 : 0);
        cmd.Parameters.AddWithValue("@uploadedAt", (object?)uploadedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@remoteUrl", (object?)remoteUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // === v0.6 AI 打标实现（spec doc/12-ai-toolbar-buttons.md §3.1.2 + §3.1.3） ===

    public async Task UpdateTagAsync(long fileId, IEnumerable<string> tags, IEnumerable<string> qualityTags, int score, long taggedAt, string? tagError, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // v0.11: tags 列改存 tag id 数组；需要先拿到文件的 project_path 再映射 name -> id。
        var projectPath = await GetProjectPathAsync(connection, fileId, ct);
        if (projectPath == null) throw new InvalidOperationException($"找不到文件 id={fileId} 对应的项目路径");

        var tagsList = tags.ToList();
        var tagIds = await ResolveTagNamesToIdsAsync(connection, projectPath, tagsList, createIfMissing: true, ct);
        var tagsJson = JsonSerializer.Serialize(tagIds, s_writeTagsOptions);

        var qualityTagsList = qualityTags.ToList();
        var qualityTagsJson = JsonSerializer.Serialize(qualityTagsList, s_writeTagsOptions);

        var sql = @"
            UPDATE media_files
            SET tags = @tags,
                quality_tags = @qualityTags,
                score = @score,
                tagged_at = @taggedAt,
                tag_error = @tagError,
                updated_at = @updatedAt
            WHERE id = @id";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", fileId);
        cmd.Parameters.AddWithValue("@tags", tagsJson);
        cmd.Parameters.AddWithValue("@qualityTags", qualityTagsJson);
        cmd.Parameters.AddWithValue("@score", score);
        cmd.Parameters.AddWithValue("@taggedAt", taggedAt);
        cmd.Parameters.AddWithValue("@tagError", (object?)tagError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 手动编辑主体标签专用（spec doc/15-manual-tag-edit.md §2.2 / §8）。
    /// 关键差异 vs UpdateTagAsync：
    ///   - 不动 score（UpdateTagAsync 必须传 int score，对 Score=null 的未打标文件会写 0，UI 误显示角标）
    ///   - 不动 quality_tags（用户拍板：手动编辑只管主体标签）
    ///   - tag_error 一律清空（手动编辑总是成功路径，调用方传 ct 失败的话靠异常向上传）
    /// JSON 编码沿用 s_writeTagsOptions（UnsafeRelaxedJsonEscaping），中文原样写避免 LIKE 失效。
    /// </summary>
    public async Task UpdateTagsOnlyAsync(long fileId, IEnumerable<string> tags, long taggedAt, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // v0.11: tags 列改存 tag id 数组；需要先拿到文件的 project_path 再映射 name -> id。
        var projectPath = await GetProjectPathAsync(connection, fileId, ct);
        if (projectPath == null) throw new InvalidOperationException($"找不到文件 id={fileId} 对应的项目路径");

        var tagsList = tags.ToList();
        var tagIds = await ResolveTagNamesToIdsAsync(connection, projectPath, tagsList, createIfMissing: true, ct);
        var tagsJson = JsonSerializer.Serialize(tagIds, s_writeTagsOptions);

        Trace.WriteLine($"[MediaRepository] step 1/3 UpdateTagsOnlyAsync: fileId={fileId}, tags={tagsList.Count}, taggedAt={taggedAt}");

        var sql = @"
            UPDATE media_files
            SET tags = @tags,
                tagged_at = @taggedAt,
                tag_error = NULL,
                updated_at = @updatedAt
            WHERE id = @id";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", fileId);
        cmd.Parameters.AddWithValue("@tags", tagsJson);
        cmd.Parameters.AddWithValue("@taggedAt", taggedAt);
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        Trace.WriteLine($"[MediaRepository] step 2/3 UpdateTagsOnlyAsync done: fileId={fileId}, rowsAffected={rows}");
    }

    public async Task<IReadOnlyList<TagGroupItem>> GetTagGroupsAsync(string projectPath, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        var results = new List<TagGroupItem>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await LoadTagCacheAsync(connection, normalizedPath, ct);

        // v0.11: 通过 tags 表 + json_each(id 数组) 统计每个标签的文件数。
        const string sql = @"
            SELECT t.id, t.name, COUNT(*) AS count
            FROM tags t
            JOIN media_files m ON m.project_path = t.project_path
            WHERE t.project_path = @projectPath
              AND m.deleted_at IS NULL
              AND EXISTS (
                  SELECT 1 FROM json_each(m.tags)
                  WHERE json_each.value = t.id
              )
            GROUP BY t.id, t.name
            ORDER BY count DESC, t.name";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new TagGroupItem
            {
                TagId = reader.GetInt64(0),
                Tag = reader.GetString(1),
                Count = reader.GetInt32(2)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<MediaFile>> GetByTagAsync(string projectPath, string tag, SortMode sortMode = SortMode.TimeDesc, CancellationToken ct = default)
    {
        var results = new List<MediaFile>();
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await LoadTagCacheAsync(connection, normalizedPath, ct);

        // v0.6.1: 跟 GetByDateAsync 对齐 ORDER BY 分支。
        // sortMode 是硬编码常量（无用户输入），SQL 拼接安全。
        var orderBy = sortMode switch
        {
            SortMode.ScoreDesc => "ORDER BY score IS NULL, score DESC, shot_at DESC, file_name ASC",
            _ => "ORDER BY shot_at DESC, file_name ASC",
        };

        // v0.11: 先解析 tag name -> id，再用 JSON1 匹配 media_files.tags 数组中的 id。
        var tagId = await GetTagIdAsync(connection, normalizedPath, tag, createIfMissing: false, ct);
        if (tagId == null) return results;

        var sql = $@"
            SELECT
                id, project_path, relative_path, file_name, media_type,
                file_size, last_modified, shot_at, is_uploaded, local_exists,
                thumbnail_path, remote_url, uploaded_at, scanned_at,
                created_at, updated_at,
                tags, score, tagged_at, tag_error,
                quality_tags,
                deleted_at
            FROM media_files
            WHERE project_path = @projectPath
              AND deleted_at IS NULL
              AND EXISTS (
                  SELECT 1 FROM json_each(media_files.tags)
                  WHERE json_each.value = @tagId
              )
            {orderBy}
            LIMIT 1000";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
        cmd.Parameters.AddWithValue("@tagId", tagId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadMediaFileRow(reader));
        }

        return results;
    }

    // === v0.11 标签字典管理（方案 B）===

    private async Task<string?> GetProjectPathAsync(SqliteConnection connection, long fileId, CancellationToken ct)
    {
        const string sql = "SELECT project_path FROM media_files WHERE id = @id LIMIT 1";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", fileId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : null;
    }

    private async Task<List<long>> ResolveTagNamesToIdsAsync(SqliteConnection connection, string projectPath, IReadOnlyList<string> tagNames, bool createIfMissing, CancellationToken ct)
    {
        var ids = new List<long>(tagNames.Count);
        foreach (var name in tagNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var tagId = await GetTagIdAsync(connection, projectPath, name, createIfMissing, ct);
            if (tagId.HasValue)
            {
                ids.Add(tagId.Value);
            }
        }
        return ids;
    }

    private async Task<List<string>> ResolveTagIdsToNamesAsync(SqliteConnection connection, string projectPath, IReadOnlyList<long> tagIds, CancellationToken ct)
    {
        var names = new List<string>(tagIds.Count);
        await LoadTagCacheAsync(connection, projectPath, ct);

        _tagCacheLock.EnterReadLock();
        try
        {
            if (_tagNameCache.TryGetValue(projectPath, out var nameById))
            {
                foreach (var id in tagIds)
                {
                    if (nameById.TryGetValue(id, out var name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        finally
        {
            _tagCacheLock.ExitReadLock();
        }

        return names;
    }

    private async Task LoadTagCacheAsync(SqliteConnection connection, string projectPath, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        _tagCacheLock.EnterUpgradeableReadLock();
        try
        {
            if (_tagNameCache.ContainsKey(normalizedPath)) return;

            _tagCacheLock.EnterWriteLock();
            try
            {
                if (_tagNameCache.ContainsKey(normalizedPath)) return;

                var nameById = new Dictionary<long, string>();
                var idByName = new Dictionary<string, long>(StringComparer.Ordinal);

                const string sql = "SELECT id, name FROM tags WHERE project_path = @projectPath";
                await using var cmd = new SqliteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@projectPath", normalizedPath);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetInt64(0);
                    var name = reader.GetString(1);
                    nameById[id] = name;
                    idByName[name] = id;
                }

                _tagNameCache[normalizedPath] = nameById;
                _tagIdCache[normalizedPath] = idByName;
            }
            finally
            {
                _tagCacheLock.ExitWriteLock();
            }
        }
        finally
        {
            _tagCacheLock.ExitUpgradeableReadLock();
        }
    }

    private void InvalidateTagCache(string projectPath)
    {
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        _tagCacheLock.EnterWriteLock();
        try
        {
            _tagNameCache.Remove(normalizedPath);
            _tagIdCache.Remove(normalizedPath);
        }
        finally
        {
            _tagCacheLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 在指定连接上把 tag name 解析成 id；createIfMissing=true 时自动创建。
    /// </summary>
    private async Task<long?> GetTagIdAsync(SqliteConnection connection, string projectPath, string name, bool createIfMissing, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        await LoadTagCacheAsync(connection, normalizedPath, ct);

        _tagCacheLock.EnterReadLock();
        try
        {
            if (_tagIdCache.TryGetValue(normalizedPath, out var idByName)
                && idByName.TryGetValue(name, out var cachedId))
            {
                return cachedId;
            }
        }
        finally
        {
            _tagCacheLock.ExitReadLock();
        }

        if (!createIfMissing) return null;

        // 缓存未命中且允许创建：INSERT OR IGNORE 后重新加载缓存。
        const string insertSql = @"
            INSERT OR IGNORE INTO tags (project_path, name, updated_at)
            VALUES (@projectPath, @name, @updatedAt);
            SELECT id FROM tags WHERE project_path = @projectPath AND name = @name;";
        await using var cmd = new SqliteCommand(insertSql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return null;

        var tagId = Convert.ToInt64(result);

        _tagCacheLock.EnterWriteLock();
        try
        {
            if (_tagNameCache.TryGetValue(normalizedPath, out var nameById))
                nameById[tagId] = name;
            if (_tagIdCache.TryGetValue(normalizedPath, out var idByName))
                idByName[name] = tagId;
        }
        finally
        {
            _tagCacheLock.ExitWriteLock();
        }

        return tagId;
    }

    public async Task<IReadOnlyList<Tag>> GetTagsAsync(string projectPath, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        var results = new List<Tag>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await LoadTagCacheAsync(connection, normalizedPath, ct);

        _tagCacheLock.EnterReadLock();
        try
        {
            if (_tagNameCache.TryGetValue(normalizedPath, out var nameById))
            {
                foreach (var kv in nameById)
                {
                    results.Add(new Tag { Id = kv.Key, ProjectPath = normalizedPath, Name = kv.Value });
                }
            }
        }
        finally
        {
            _tagCacheLock.ExitReadLock();
        }

        return results;
    }

    public async Task<Tag> EnsureTagAsync(string projectPath, string name, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var tagId = await GetTagIdAsync(connection, normalizedPath, name, createIfMissing: true, ct);
        if (tagId == null) throw new InvalidOperationException($"无法创建或获取标签: {name}");

        return new Tag { Id = tagId.Value, ProjectPath = normalizedPath, Name = name };
    }

    public async Task RenameTagAsync(string projectPath, string oldName, string newName, CancellationToken ct = default)
    {
        if (oldName == newName) return;
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            // 1. 确保目标标签存在，获取其 id。
            var newTagId = await GetTagIdAsync(connection, normalizedPath, newName, createIfMissing: true, ct);
            if (newTagId == null) throw new InvalidOperationException($"无法创建目标标签: {newName}");

            // 2. 获取旧标签 id。
            var oldTagId = await GetTagIdAsync(connection, normalizedPath, oldName, createIfMissing: false, ct);
            if (oldTagId == null || oldTagId == newTagId) return;

            // 3. 把所有旧 id 替换为新 id，同时避免重复 id（SQLite JSON 数组去重）。
            const string updateSql = @"
                UPDATE media_files
                SET tags = (
                    SELECT json_group_array(DISTINCT value)
                    FROM (
                        SELECT json_each.value
                        FROM json_each(media_files.tags)
                        WHERE json_each.value != @oldTagId
                        UNION ALL
                        SELECT @newTagId
                    )
                ),
                updated_at = @updatedAt
                WHERE project_path = @projectPath
                  AND EXISTS (
                      SELECT 1 FROM json_each(media_files.tags)
                      WHERE json_each.value = @oldTagId
                  )";
            await using (var cmd = new SqliteCommand(updateSql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@oldTagId", oldTagId.Value);
                cmd.Parameters.AddWithValue("@newTagId", newTagId.Value);
                cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));
                cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 4. 删除旧标签行。
            const string deleteSql = "DELETE FROM tags WHERE project_path = @projectPath AND name = @oldName";
            await using (var cmd = new SqliteCommand(deleteSql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
                cmd.Parameters.AddWithValue("@oldName", oldName);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            InvalidateTagCache(normalizedPath);
        }
    }

    public async Task RemoveTagAsync(string projectPath, string name, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            var tagId = await GetTagIdAsync(connection, normalizedPath, name, createIfMissing: false, ct);
            if (tagId == null) return;

            // 1. 从所有文件的 tags 数组中移除该 id。
            const string updateSql = @"
                UPDATE media_files
                SET tags = (
                    SELECT json_group_array(value)
                    FROM json_each(media_files.tags)
                    WHERE value != @tagId
                ),
                updated_at = @updatedAt
                WHERE project_path = @projectPath
                  AND EXISTS (
                      SELECT 1 FROM json_each(media_files.tags)
                      WHERE value = @tagId
                  )";
            await using (var cmd = new SqliteCommand(updateSql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@tagId", tagId.Value);
                cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));
                cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2. 删除标签行。
            const string deleteSql = "DELETE FROM tags WHERE project_path = @projectPath AND name = @name";
            await using (var cmd = new SqliteCommand(deleteSql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@projectPath", normalizedPath);
                cmd.Parameters.AddWithValue("@name", name);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            InvalidateTagCache(normalizedPath);
        }
    }

    // === v0.8 软删除 / 恢复 / 永久删除 / 垃圾筒 / 释放本地空间 ===
    // （spec doc/14-delete-and-trash.md §2.3）

    public async Task SoftDeleteAsync(long fileId, long deletedAt, CancellationToken ct = default)
    {
        Trace.WriteLine($"[MediaRepository] SoftDeleteAsync: id={fileId}, deletedAt={deletedAt}");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // 只标记 deleted_at，不动 local_exists —— 用户仍可能想"释放本地空间"
        // 或"恢复"回来。local_exists 反映真实磁盘状态，独立于 deleted_at 语义。
        var sql = "UPDATE media_files SET deleted_at = @deletedAt, updated_at = @updatedAt WHERE id = @id";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@deletedAt", deletedAt);
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@id", fileId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RestoreAsync(long fileId, CancellationToken ct = default)
    {
        Trace.WriteLine($"[MediaRepository] RestoreAsync: id={fileId}");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // deleted_at = NULL → 文件重新出现在 Gallery 查询中。
        // 不修改 local_exists —— 用户在垃圾筒期间可能手动删了本地文件，
        // 恢复后显示为「云端有、本地无」，与正常状态一致。
        var sql = "UPDATE media_files SET deleted_at = NULL, updated_at = @updatedAt WHERE id = @id";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@id", fileId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PermanentDeleteAsync(long fileId, CancellationToken ct = default)
    {
        Trace.WriteLine($"[MediaRepository] PermanentDeleteAsync: id={fileId}");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // 限定 deleted_at IS NOT NULL —— 防止误删未走软删除流程的文件。
        // 关联的 upload_jobs 由 TrashViewModel 在调用前清理（Repository 之间不依赖）。
        var sql = "DELETE FROM media_files WHERE id = @id AND deleted_at IS NOT NULL";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", fileId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MediaFile>> GetDeletedAsync(string projectPath, CancellationToken ct = default)
    {
        Trace.WriteLine($"[MediaRepository] GetDeletedAsync: projectPath={projectPath}");

        var results = new List<MediaFile>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        await LoadTagCacheAsync(connection, normalizedPath, ct);

        // 按 deleted_at DESC 排序（最新删除在前）。
        // 这里不加 WHERE deleted_at IS NULL —— 垃圾筒列表就是要看已删除的。
        // SELECT 列序与 ReadMediaFileRow 一致（含 deleted_at）。
        var sql = @"
            SELECT
                id, project_path, relative_path, file_name, media_type,
                file_size, last_modified, shot_at, is_uploaded, local_exists,
                thumbnail_path, remote_url, uploaded_at, scanned_at,
                created_at, updated_at,
                tags, score, tagged_at, tag_error,
                quality_tags,
                deleted_at
            FROM media_files
            WHERE project_path = @projectPath
              AND deleted_at IS NOT NULL
            ORDER BY deleted_at DESC
            LIMIT 5000";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@projectPath", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadMediaFileRow(reader));
        }

        Trace.WriteLine($"[MediaRepository] GetDeletedAsync: 返回 {results.Count} 个已删除文件");
        return results;
    }

    public async Task UpdateLocalExistsAsync(long fileId, bool exists, CancellationToken ct = default)
    {
        Trace.WriteLine($"[MediaRepository] UpdateLocalExistsAsync: id={fileId}, exists={exists}");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = "UPDATE media_files SET local_exists = @exists, updated_at = @updatedAt WHERE id = @id";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@exists", exists ? 1 : 0);
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@id", fileId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UndoDeleteAsync(long fileId, long deletedAt, CancellationToken ct = default)
    {
        Trace.WriteLine($"[MediaRepository] UndoDeleteAsync: id={fileId}, deletedAt={deletedAt}");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // 重新把 deleted_at 设回原值。仅当 deleted_at 仍为 NULL 时生效（说明当前确实处于
        // "Restore 之后的 Gallery 状态"），否则用户已经又做了别的操作（删除/恢复链），不覆盖。
        var sql = @"UPDATE media_files
                    SET deleted_at = @deletedAt, updated_at = @updatedAt
                    WHERE id = @id AND deleted_at IS NULL";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@deletedAt", deletedAt);
        cmd.Parameters.AddWithValue("@updatedAt", SqliteDateTime.ToDb(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@id", fileId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        Trace.WriteLine($"[MediaRepository] UndoDeleteAsync: rowsAffected={rows} (0 表示无操作，状态已变)");
    }

    public async Task<MediaFile?> GetByIdAsync(long fileId, CancellationToken ct = default)
    {
        Trace.WriteLine($"[MediaRepository] GetByIdAsync: id={fileId}");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // v0.11: 先取 project_path 加载 tag 缓存，ReadMediaFileRow 才能把 tags id 数组解析为 name。
        var projectPath = await GetProjectPathAsync(connection, fileId, ct);
        if (projectPath != null)
        {
            await LoadTagCacheAsync(connection, projectPath, ct);
        }

        // 不加 deleted_at 过滤——垃圾筒撤销场景需要读到刚被改回 deleted_at 的行。
        var sql = @"
            SELECT
                id, project_path, relative_path, file_name, media_type,
                file_size, last_modified, shot_at, is_uploaded, local_exists,
                thumbnail_path, remote_url, uploaded_at, scanned_at,
                created_at, updated_at,
                tags, score, tagged_at, tag_error,
                quality_tags,
                deleted_at
            FROM media_files
            WHERE id = @id
            LIMIT 1";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", fileId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadMediaFileRow(reader);
        }
        return null;
    }

    // === Row 映射 helpers（GetByDateAsync / GetByTagAsync / GetDeletedAsync 共用） ===

    /// <summary>
    /// SELECT 列序：16 基础列 + 5 AI 列（tags/score/tagged_at/tag_error/quality_tags）+ 1 删除列（deleted_at），共 22 列。
    /// SELECT 模板见 GetByDateAsync / GetByTagAsync / GetDeletedAsync 的 sql 字符串。
    /// v0.11: tags 列存 id 数组，ReadMediaFileRow 通过 tag 缓存把 id 解析为 name。
    /// </summary>
    private MediaFile ReadMediaFileRow(SqliteDataReader reader)
    {
        var projectPath = reader.GetString(1);
        return new MediaFile
        {
            Id = reader.GetInt64(0),
            ProjectPath = projectPath,
            RelativePath = reader.GetString(2),
            FileName = reader.GetString(3),
            MediaType = (MediaType)reader.GetInt32(4),
            FileSize = reader.GetInt64(5),
            LastModified = reader.GetInt64(6),
            ShotAt = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            IsUploaded = reader.GetInt32(8) == 1,
            LocalExists = reader.GetInt32(9) == 1,
            ThumbnailPath = reader.IsDBNull(10) ? null : reader.GetString(10),
            RemoteUrl = reader.IsDBNull(11) ? null : reader.GetString(11),
            UploadedAt = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            ScannedAt = reader.GetInt64(13),
            CreatedAt = SqliteDateTime.FromDb(reader.GetString(14)),
            UpdatedAt = SqliteDateTime.FromDb(reader.GetString(15)),
            Tags = ParseTagIds(reader.IsDBNull(16) ? null : reader.GetString(16), projectPath),
            Score = reader.IsDBNull(17) ? null : reader.GetInt32(17),
            TaggedAt = reader.IsDBNull(18) ? null : reader.GetInt64(18),
            TagError = reader.IsDBNull(19) ? null : reader.GetString(19),
            QualityTags = ParseTags(reader.IsDBNull(20) ? null : reader.GetString(20)),
            DeletedAt = reader.IsDBNull(21) ? null : reader.GetInt64(21),
        };
    }

    /// <summary>JSON 字符串数组 → List&lt;string&gt;。空/损坏 → 空 list。（quality_tags 仍用）</summary>
    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    /// <summary>JSON id 数组 → 通过缓存解析为 name 列表。空/损坏 → 空 list。</summary>
    private List<string> ParseTagIds(string? json, string projectPath)
    {
        var ids = ParseTagIdsRaw(json);
        if (ids.Count == 0) return new List<string>();

        var names = new List<string>(ids.Count);
        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        _tagCacheLock.EnterReadLock();
        try
        {
            if (_tagNameCache.TryGetValue(normalizedPath, out var nameById))
            {
                foreach (var id in ids)
                {
                    if (nameById.TryGetValue(id, out var name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        finally
        {
            _tagCacheLock.ExitReadLock();
        }

        return names;
    }

    private static List<long> ParseTagIdsRaw(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return new List<long>();
        try
        {
            return JsonSerializer.Deserialize<List<long>>(json) ?? new List<long>();
        }
        catch (JsonException)
        {
            return new List<long>();
        }
    }
}
