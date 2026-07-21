using System;
using System.Collections.Generic;

namespace StartTooler.Models;

// === v0.11: 统计仪表盘数据模型（spec/19 §6.1）===

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
