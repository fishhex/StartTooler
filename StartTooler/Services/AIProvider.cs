using System;
using System.Collections.Generic;

namespace StartTooler.Services;

/// <summary>
/// AI 模型厂商枚举 + 元数据。
///
/// 厂商元数据来源：<see cref="AIProviderLoader"/>（TOML 配置），不再硬编码。
/// 改厂商/模型/默认 URL：编辑
///   - 内置默认：Resources/ai-providers.default.toml（重编译生效）
///   - 用户覆盖：~/Library/Application Support/StartTooler/ai-providers.toml（重启生效）
///
/// 新增厂商：枚举加一项 + TOML 里加一条；只改枚举不写 TOML 会导致该厂商被 loader 跳过 + warning。
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

/// <summary>
/// 单个厂商的元数据。来源 TOML，由 <see cref="AIProviderLoader"/> 构造。
/// UI / VM 只读，不需要写。
/// </summary>
public sealed record AIProviderMeta(
    AIProvider Provider,
    string DisplayName,
    string DefaultBaseUrl,
    IReadOnlyList<string> RecommendedModels,
    string? ModelWatermark,
    ProtocolKind ProtocolKind)
{
    /// <summary>
    /// UI 切厂商时给的"默认值"。
    /// - BaseUrl：始终用厂商默认（私有化部署 / 代理用户自己改）
    /// - Model：取推荐列表第一个
    /// </summary>
    public string DefaultModel => RecommendedModels.Count > 0 ? RecommendedModels[0] : "";
}

/// <summary>
/// 厂商元数据目录。数据来自 <see cref="AIProviderLoader"/>（TOML）。
/// </summary>
public static class AIProviderCatalog
{
    /// <summary>
    /// 所有厂商元数据。Lazy 加载：首次访问触发 TOML 读盘 + 解析，之后命中缓存。
    /// </summary>
    public static IReadOnlyList<AIProviderMeta> All => AIProviderLoader.Load();

    public static AIProviderMeta Get(AIProvider provider)
    {
        foreach (var meta in All)
        {
            if (meta.Provider == provider) return meta;
        }
        if (All.Count > 0) return All[0];
        throw new InvalidOperationException(
            "AI 厂商列表为空，请检查 Resources/ai-providers.default.toml 是否作为 EmbeddedResource 正确打包。");
    }
}