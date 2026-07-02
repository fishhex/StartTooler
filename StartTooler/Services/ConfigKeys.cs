namespace StartTooler.Services;

public static class ConfigKeys
{
    public const string Project = "project";
    public const string App = "app";
    public const string Oss = "oss";
    public const string PublicRelay = "publicRelay";

    /// <summary>通用 AI 配置（多厂商）。v1 起替代旧的 <c>anthropic</c> key。</summary>
    public const string AI = "ai";

    /// <summary>旧 Anthropic 专用 key。新代码不读，保留以便未来需要时回滚。</summary>
    public const string Anthropic = "anthropic";
}
