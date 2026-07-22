using System;
using System.Collections.Generic;

namespace StartTooler.Models;

// === v0.11: 统计仪表盘数据模型（spec/19 §6.1）===

/// <summary>
/// 时间维度模式。
/// </summary>
public enum TimeMode
{
    Year,
    Quarter,
    Month,
}

/// <summary>
/// 统计周期（年 / 季度 / 月）。
/// </summary>
public sealed class DashboardPeriod
{
    public int Year { get; init; }
    public int? Quarter { get; init; }
    public int? Month { get; init; }
    public TimeMode Mode => Quarter.HasValue ? (Month.HasValue ? TimeMode.Month : TimeMode.Quarter) : TimeMode.Year;

    public DateTime StartDate
    {
        get
        {
            if (Month.HasValue)
                return new DateTime(Year, Month.Value, 1);
            if (Quarter.HasValue)
                return new DateTime(Year, (Quarter.Value - 1) * 3 + 1, 1);
            return new DateTime(Year, 1, 1);
        }
    }

    public DateTime EndDate
    {
        get
        {
            if (Month.HasValue)
                return StartDate.AddMonths(1).AddDays(-1);
            if (Quarter.HasValue)
                return StartDate.AddMonths(3).AddDays(-1);
            return new DateTime(Year, 12, 31);
        }
    }
}

public sealed class DashboardKpi
{
    public int TotalPhotos { get; init; }
    public double TotalExposureHours { get; init; }
    public int ShootingDays { get; init; }
    public int TargetCount { get; init; }
    public long TotalBytes { get; init; }
}

public sealed class HeatmapDay
{
    public DateTime Date { get; init; }
    public int Count { get; init; }
    public string? TopTarget { get; init; }
}

public sealed class MonthStat
{
    public int Month { get; init; }
    public int Count { get; init; }
}

/// <summary>
/// 通用周期统计项（年视图按月份、季度视图按月份、月视图按日）。
/// </summary>
public sealed class PeriodStat
{
    public int Period { get; init; }
    public int Count { get; init; }
    public string? Label { get; init; }
}

public sealed class TagRank
{
    public string TagName { get; init; } = "";
    public int Count { get; init; }
    public double Percentage { get; init; }
}

public sealed class FocalRangeStat
{
    public string RangeLabel { get; init; } = "";
    public int Count { get; init; }
    public double Percentage { get; init; }
}

public sealed class IsoStat
{
    public string IsoLabel { get; init; } = "";
    public int Count { get; init; }
    public double Percentage { get; init; }
}

public sealed class ExposureStat
{
    public string RangeLabel { get; init; } = "";
    public int Count { get; init; }
    public double Percentage { get; init; }
}

// === 天文复盘 Phase 2 模型（预留）===

public sealed class MoonPhaseStat
{
    public string PhaseName { get; init; } = "";
    public string PhaseIcon { get; init; } = "";
    public int SessionCount { get; init; }
    public int TotalPhotos { get; init; }
    public double AvgQuality { get; init; }
}

public sealed class EventSessionMatch
{
    public DateTime Date { get; init; }
    public string EventName { get; init; } = "";
    public string PhaseName { get; init; } = "";
    public string PhaseIcon { get; init; } = "";
    public bool IsAttended { get; init; }
    public int PhotoCount { get; init; }
    public double? AvgQuality { get; init; }
}

public sealed class SessionSummary
{
    public string SessionId { get; init; } = "";
    public DateTime Date { get; init; }
    public string Title { get; init; } = "";
    public string EventName { get; init; } = "";
    public string PhaseName { get; init; } = "";
    public string PhaseIcon { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public int PhotoCount { get; init; }
    public int TargetCount { get; init; }
    public double? AvgQuality { get; init; }
}
