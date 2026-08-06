using System;

namespace StartTooler.Models;

/// <summary>
/// 拍摄会话：一次完整的外出拍摄（spec/03-shooting-diary.md §2.1）。
/// 通过 SessionRepository 持久化到 sessions 表。
/// </summary>
public sealed class Session
{
    public string Id { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Location { get; set; } = "";
    public string WindDir { get; set; } = "";
    public int WindLevel { get; set; }
    public string CloudCover { get; set; } = "";
    public int? Bortle { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public TimeSpan Duration => EndTime - StartTime;

    public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours}h{(int)Duration.TotalMinutes % 60}m"
        : $"{(int)Duration.TotalMinutes}m";

    public string? WeatherIconKey => CloudCover switch
    {
        "晴" => "Icon.Weather.Sunny",
        "少云" => "Icon.Weather.PartlyCloudy",
        "多云" => "Icon.Weather.Cloudy",
        "阴" => "Icon.Weather.Overcast",
        "雨" => "Icon.Weather.Rain",
        "雪" => "Icon.Weather.Snow",
        "雾" => "Icon.Weather.Fog",
        _ => null,
    };

    public string? WeatherText => string.IsNullOrEmpty(CloudCover) ? null
        : string.IsNullOrEmpty(WindDir) ? CloudCover
        : $"{WindDir}风 {WindLevel}级 · {CloudCover}";
}