using System;
using System.Collections.Generic;

namespace StartTooler.Models;

/// <summary>
/// 会话统计（spec/03-shooting-diary.md §2.5）。
/// 由 MediaRepository.GetSessionStatsAsync 单次聚合返回。
/// </summary>
public sealed class SessionStats
{
    public int TotalPhotos { get; init; }
    public int TargetCount { get; init; }
    public double TotalExposureHours { get; init; }
    public IReadOnlyList<string> TopTags { get; init; } = Array.Empty<string>();
}