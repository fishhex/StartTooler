namespace StartTooler.Models;

/// <summary>
/// v0.12: 天气选项 —— 天气选择器中的可选项。
/// </summary>
public sealed class WeatherOption
{
    public string Text { get; init; } = "";
    public string IconKey { get; init; } = "";
    public string CloudCover { get; init; } = "";
}
