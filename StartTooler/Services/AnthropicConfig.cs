namespace StartTooler.Services;

/// <summary>
/// Anthropic Claude 大模型配置。落库到 config.db 的 "anthropic" key（详见 02-data-layer.md §2.4）。
///
/// 字段最小集：ApiKey（密钥，UI 用 PasswordChar 隐藏）/ BaseUrl（API 端点，默认官方）/ Model（模型名）。
/// 后续如果要加 MaxTokens / Temperature / SystemPrompt 在这里扩字段，UI 同步补一行即可。
/// </summary>
public class AnthropicConfig
{
    /// <summary>Anthropic 控制台申请的 API Key（sk-ant-... 开头）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>API 端点。官方默认 https://api.anthropic.com；走代理或私有化部署时改这里。</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>模型名（例 claude-3-5-sonnet-latest）。后续要加新模型时 UI ComboBox 同步更新即可。</summary>
    public string Model { get; set; } = "claude-3-5-sonnet-latest";
}
