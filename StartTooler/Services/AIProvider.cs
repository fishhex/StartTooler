using System.Collections.Generic;

namespace StartTooler.Services;

/// <summary>
/// AI 模型厂商枚举 + 元数据。
///
/// 新增厂商只需：
///   1. 在 enum 里加一项
///   2. 在 <see cref="AIProviderMeta.All"/> 里登记 BaseUrl + 推荐模型列表
///
/// UI 切厂商时，<see cref="StartTooler.ViewModels.SettingsViewModel"/> 会自动同步
/// 默认 BaseUrl 和推荐 Model，用户可自由覆盖。
/// </summary>
public enum AIProvider
{
    Anthropic,
    OpenAI,
    Gemini,
    DeepSeek,
    Zhipu,
    Moonshot,
    DashScope,
    Custom,
}

public sealed record AIProviderMeta(
    AIProvider Provider,
    string DisplayName,
    string DefaultBaseUrl,
    IReadOnlyList<string> RecommendedModels,
    string? ModelWatermark)
{
    /// <summary>
    /// UI 切厂商时给的"默认值"。
    /// - BaseUrl：始终用厂商默认（私有化部署 / 代理用户自己改）
    /// - Model：取推荐列表第一个
    /// </summary>
    public string DefaultModel => RecommendedModels.Count > 0 ? RecommendedModels[0] : "";
}

public static class AIProviderCatalog
{
    public static readonly IReadOnlyList<AIProviderMeta> All = new AIProviderMeta[]
    {
        new(AIProvider.Anthropic,
            "Anthropic (Claude)",
            "https://api.anthropic.com",
            new[] { "claude-sonnet-4-5", "claude-opus-4-1", "claude-3-5-haiku-latest" },
            "claude-sonnet-4-5"),

        new(AIProvider.OpenAI,
            "OpenAI (GPT)",
            "https://api.openai.com/v1",
            new[] { "gpt-4o", "gpt-4o-mini", "o1", "o1-mini" },
            "gpt-4o"),

        new(AIProvider.Gemini,
            "Google Gemini",
            "https://generativelanguage.googleapis.com/v1beta",
            new[] { "gemini-2.0-flash", "gemini-1.5-pro", "gemini-1.5-flash" },
            "gemini-2.0-flash"),

        new(AIProvider.DeepSeek,
            "DeepSeek",
            "https://api.deepseek.com",
            new[] { "deepseek-chat", "deepseek-reasoner" },
            "deepseek-chat"),

        new(AIProvider.Zhipu,
            "智谱 GLM",
            "https://open.bigmodel.cn/api/paas/v4",
            new[] { "glm-4-plus", "glm-4-flash", "glm-4-air" },
            "glm-4-plus"),

        new(AIProvider.Moonshot,
            "月之暗面 Kimi",
            "https://api.moonshot.cn/v1",
            new[] { "kimi-k2-0711-preview", "moonshot-v1-128k", "moonshot-v1-32k" },
            "moonshot-v1-128k"),

        new(AIProvider.DashScope,
            "阿里云百炼 (Qwen)",
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            new[] { "qwen-max", "qwen-plus", "qwen-turbo" },
            "qwen-max"),

        new(AIProvider.Custom,
            "自定义",
            "",
            System.Array.Empty<string>(),
            "请输入模型名"),
    };

    public static AIProviderMeta Get(AIProvider provider)
    {
        foreach (var meta in All)
        {
            if (meta.Provider == provider) return meta;
        }
        return All[0];
    }
}