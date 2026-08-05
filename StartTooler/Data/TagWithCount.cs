namespace StartTooler.Data;

/// <summary>
/// 标签字典项 + 使用频次（v0.12 autocomplete 数据源）。
/// 由 GetTagsAsync 返回，用于 TagChipEditor 下拉候选列表。
/// </summary>
public sealed class TagWithCount
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int UsageCount { get; init; }
}
