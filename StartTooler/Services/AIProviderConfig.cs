using System.Collections.Generic;

namespace StartTooler.Services;

/// <summary>
/// AI 厂商的请求协议风格。决定 AITester 怎么发请求。
/// </summary>
public enum ProtocolKind
{
    /// <summary>Anthropic 原生协议：header `x-api-key` + `anthropic-version`，POST /v1/messages。</summary>
    Anthropic,

    /// <summary>OpenAI 兼容协议：header `Authorization: Bearer`，POST {baseUrl}/chat/completions。</summary>
    OpenAI,
}

/// <summary>
/// ai-providers.toml 的根结构。Tomlyn 用 PascalCase 直接绑属性（大小写敏感）。
/// 文件里也写 PascalCase 跟 DTO 对齐，省去命名策略配置。
/// </summary>
public sealed class AIProvidersConfig
{
    public int Version { get; set; } = 1;
    public List<AIProviderEntry> Providers { get; set; } = new();
}

/// <summary>
/// 单个厂商条目。Loader 读取后映射成 AIProviderMeta。
/// </summary>
public sealed class AIProviderEntry
{
    /// <summary>枚举字符串（"Anthropic" / "OpenAI" / ...）。Loader 会用 Enum.TryParse 校验。</summary>
    public string Key { get; set; } = "";

    /// <summary>UI 显示名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>厂商默认 API 端点。Custom 留空。</summary>
    public string DefaultBaseUrl { get; set; } = "";

    /// <summary>"anthropic" / "openai"。Loader 转 ProtocolKind，不识别 → "openai"（最大兼容）+ warning。</summary>
    public string Protocol { get; set; } = "openai";

    /// <summary>推荐模型列表。空数组合法（Custom 用）。</summary>
    public List<string> RecommendedModels { get; set; } = new();

    /// <summary>切换厂商时默认填入的模型。</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>Model 输入框 placeholder。null = 不显示。</summary>
    public string? ModelWatermark { get; set; }
}