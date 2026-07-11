using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace StartTooler.Services;

public interface IConfigService
{
    Task<T?> GetAsync<T>(string key) where T : class;
    Task SetAsync<T>(string key, T value) where T : class;

    /// <summary>
    /// 把 <paramref name="rawJson"/> 字符串原样写入 config.db（不重新序列化）。
    /// 给导入/导出场景用：导入时把备份文件里的 JSON token 当 raw 写回去。
    /// </summary>
    Task SetRawAsync(string key, string rawJson);

    Task<T> GetOrCreateAsync<T>(string key) where T : class, new();

    /// <summary>
    /// 遍历 config.db 所有 key，序列化为 JSON 写入 <paramref name="stream"/>。
    /// 密钥字段（AiApiKey / AccessKeySecret）按 <paramref name="redactSecrets"/> 策略处理。
    /// spec doc/0.11/02-settings-improve.md §3.4
    /// </summary>
    /// <returns>写入的 key 数量。</returns>
    Task<int> ExportToJsonAsync(Stream stream, bool redactSecrets = true);

    /// <summary>
    /// 从 <paramref name="stream"/> 读取 JSON 写回 config.db。密钥字段遇到占位符
    /// （"&lt;请在导入后手动填写&gt;"）时跳过，保留 config.db 现有值。
    /// </summary>
    /// <returns>实际写入的 key 数量。</returns>
    Task<int> ImportFromJsonAsync(Stream stream);
}
