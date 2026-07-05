namespace StartTooler.Services;

/// <summary>
/// 通用 AI 配置。落库到 config.db 的 "ai" key。
///
/// 字段语义：
/// - Provider：厂商枚举（存为字符串，跨枚举值变更可恢复）
/// - ApiKey：对应厂商的 API Key
/// - BaseUrl：API 端点，留空时 UI 用厂商默认
/// - Model：模型名，留空时 UI 用厂商推荐列表第一个
/// - Protocol：API 协议类型（"OpenAI" / "Anthropic"），决定 AITagger 怎么发请求。
///   默认空串 —— **强制**用户在 UI 显式选（不允许协议字段有静默默认值，避免误打）。
///   老 config.db 反序列化时 System.Text.Json 忽略未知字段 → 自动得到空串 → UI 强制让用户选。
///
/// 历史字段 "anthropic" key 已被废弃；本次重构不再读取，旧数据保留在 db 不动。
/// </summary>
public class AIConfig
{
    public string Provider { get; set; } = nameof(AIProvider.Anthropic);
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string Protocol { get; set; } = "";
}