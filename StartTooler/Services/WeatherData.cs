namespace StartTooler.Services;

/// <summary>
/// v0.12: 历史天气数据（Open-Meteo Archive API 字段子集）。
/// WindDir / CloudCover 与 Models.Session 字符串字面量保持一致。
/// </summary>
public sealed class WeatherData
{
    /// <summary>风向，8 方位："东"/"西"/"南"/"北"/"东南"等。空 = 未知。</summary>
    public string WindDir { get; init; } = "";

    /// <summary>风级（蒲福风级 0-12）。0 = 静风。</summary>
    public int WindLevel { get; init; }

    /// <summary>云量文字："晴"/"少云"/"多云"/"阴"/"雨"/"雪"/"雾"。</summary>
    public string CloudCover { get; init; } = "";
}