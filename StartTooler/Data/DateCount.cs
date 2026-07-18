using System;
using System.Collections.Generic;

namespace StartTooler.Data;

public sealed class DateCount
{
    public DateTime Date { get; init; }
    public int Count { get; init; }
}

/// <summary>
/// v0.11 spec/15：年/月/日 三级层级数据源（GetDateGroupsAsync 仍返回扁平 DateCount，
/// 但上层 GalleryViewModel 同步按 SQL 聚合出 (Year, Month, Day, Count) 树）。
/// 仓储层只暴露「按日期数量分组」原子查询，UI 层负责组装 TimelineNode 树。
/// </summary>
public sealed class DateBucket
{
    public int Year { get; init; }
    public int Month { get; init; }
    public int Day { get; init; }
    public int Count { get; init; }
}

public sealed class DateBucketSet
{
    public IReadOnlyList<DateBucket> Buckets { get; init; } = Array.Empty<DateBucket>();
}
