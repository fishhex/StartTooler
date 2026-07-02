namespace StartTooler.Services;

/// <summary>
/// 通用 AI 配置。落库到 config.db 的 "ai" key。
///
/// 字段语义：
/// - Provider：厂商枚举（存为字符串，跨枚举值变更可恢复）
/// - ApiKey：对应厂商的 API Key
/// - BaseUrl：API 端点，留空时 UI 用厂商默认
/// - Model：模型名，留空时 UI 用厂商推荐列表第一个
///
/// 历史字段 "anthropic" key 已被废弃；本次重构不再读取，旧数据保留在 db 不动。
/// </summary>
public class AIConfig
{
    public string Provider { get; set; } = nameof(AIProvider.Anthropic);
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
}