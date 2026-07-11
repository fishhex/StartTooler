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

    /// <summary>
    /// 项目目录历史（List&lt;string&gt;，最多 10 条，新条目插队首，去重）。
    /// v0.11 起独立于 ProjectConfig.RecentDirectories，Save 时同步写两边保兼容。
    /// spec doc/0.11/02-settings-improve.md §3.1
    /// </summary>
    public const string ProjectHistory = "project_history";
}
