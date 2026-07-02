using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Tomlyn;

namespace StartTooler.Services;

/// <summary>
/// AI 厂商清单加载器。
///
/// 加载优先级：
///   1) 用户覆盖文件 ~/Library/Application Support/StartTooler/ai-providers.toml
///      （macOS / Windows 通过 Environment.SpecialFolder.ApplicationData 派生）
///   2) embedded Resources/ai-providers.default.toml（跟代码一起发布，永远兜底）
///
/// 失败处理：
///   - 用户文件不存在 → 直接走 embedded（正常路径）
///   - 用户文件存在但解析/校验失败 → log warning + 降级 embedded
///   - embedded 也读不到 → 抛 InvalidOperationException（开发期可见）
///
/// 不识别的厂商 key（不在 AIProvider enum 里）→ log warning + 跳过该条。
/// 不识别的 protocol → 默认 OpenAI（最大兼容）+ warning。
/// </summary>
public static class AIProviderLoader
{
    public const string UserConfigFileName = "ai-providers.toml";
    public const string EmbeddedResourceName = "StartTooler.Resources.ai-providers.default.toml";

    private static IReadOnlyList<AIProviderMeta>? _cached;
    private static readonly object _lock = new();

    /// <summary>加载并缓存。第一次调用走 I/O，后续命中缓存。</summary>
    public static IReadOnlyList<AIProviderMeta> Load()
    {
        if (_cached != null) return _cached;
        lock (_lock)
        {
            if (_cached != null) return _cached;
            _cached = LoadInternal();
            return _cached;
        }
    }

    /// <summary>清缓存（测试用；MVP 不做热更）。</summary>
    public static void InvalidateCache() => _cached = null;

    private static IReadOnlyList<AIProviderMeta> LoadInternal()
    {
        var userPath = GetUserConfigPath();
        if (File.Exists(userPath))
        {
            try
            {
                var text = File.ReadAllText(userPath);
                var cfg = TomlSerializer.Deserialize<AIProvidersConfig>(text) ?? new AIProvidersConfig();
                var metas = MapAndValidate(cfg, $"user:{userPath}");
                if (metas.Count > 0)
                {
                    Trace.WriteLine($"[AIProviderLoader] loaded {metas.Count} providers from user file: {userPath}");
                    return metas;
                }
                Trace.WriteLine($"[AIProviderLoader] user file produced 0 valid entries, fallback to embedded: {userPath}");
            }
            catch (Exception ex)
            {
                // TomlException / 任何 I/O 异常 → 降级 embedded，不让坏配置炸启动
                Trace.WriteLine($"[AIProviderLoader] user file failed, fallback to embedded. path={userPath} err={ex.Message}");
            }
        }

        var embeddedText = ReadEmbeddedDefault();
        var embeddedCfg = TomlSerializer.Deserialize<AIProvidersConfig>(embeddedText) ?? new AIProvidersConfig();
        var embeddedMetas = MapAndValidate(embeddedCfg, "embedded");
        Trace.WriteLine($"[AIProviderLoader] loaded {embeddedMetas.Count} providers from embedded default");
        return embeddedMetas;
    }

    private static string ReadEmbeddedDefault()
    {
        var asm = typeof(AIProviderLoader).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {EmbeddedResourceName}. " +
                "确认 StartTooler.csproj 里 Resources/ai-providers.default.toml 已配 EmbeddedResource。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GetUserConfigPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StartTooler");
        return Path.Combine(dir, UserConfigFileName);
    }

    private static IReadOnlyList<AIProviderMeta> MapAndValidate(AIProvidersConfig cfg, string source)
    {
        var result = new List<AIProviderMeta>();
        if (cfg.Providers == null)
        {
            Trace.WriteLine($"[AIProviderLoader] cfg.Providers is null ({source})");
            return result;
        }
        Trace.WriteLine($"[AIProviderLoader] mapping {cfg.Providers.Count} raw entries ({source})");

        foreach (var entry in cfg.Providers)
        {
            if (entry == null)
            {
                Trace.WriteLine($"[AIProviderLoader] skip null entry ({source})");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                Trace.WriteLine($"[AIProviderLoader] skip entry with empty Key ({source})");
                continue;
            }
            if (!Enum.TryParse<AIProvider>(entry.Key, ignoreCase: false, out var provider))
            {
                Trace.WriteLine($"[AIProviderLoader] skip unknown provider Key='{entry.Key}' ({source}); not in AIProvider enum");
                continue;
            }

            var protocol = ParseProtocol(entry.Protocol, entry.Key, source);

            result.Add(new AIProviderMeta(
                Provider: provider,
                DisplayName: entry.DisplayName ?? "",
                DefaultBaseUrl: entry.DefaultBaseUrl ?? "",
                RecommendedModels: entry.RecommendedModels ?? new List<string>(),
                ModelWatermark: entry.ModelWatermark,
                ProtocolKind: protocol));
            Trace.WriteLine($"[AIProviderLoader]   + mapped Key='{entry.Key}' displayName='{entry.DisplayName}' protocol={protocol} models={entry.RecommendedModels?.Count ?? 0}");
        }
        return result;
    }

    private static ProtocolKind ParseProtocol(string? raw, string key, string source)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ProtocolKind.OpenAI;
        return raw.Trim().ToLowerInvariant() switch
        {
            "anthropic" => ProtocolKind.Anthropic,
            "openai" => ProtocolKind.OpenAI,
            _ => UnknownProtocolFallback(key, raw, source),
        };
    }

    private static ProtocolKind UnknownProtocolFallback(string key, string raw, string source)
    {
        Trace.WriteLine($"[AIProviderLoader] unknown Protocol='{raw}' for Key='{key}' ({source}), fallback to OpenAI");
        return ProtocolKind.OpenAI;
    }
}