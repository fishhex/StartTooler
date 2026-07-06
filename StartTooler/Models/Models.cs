using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartTooler.Models;

// UI 内部状态枚举，用于转换器桥接
public enum SyncStatus
{
    UploadedAndLocal,
    UploadedButMissingLocal,
    NotUploaded
}

public enum RefreshState
{
    Idle,
    Scanning,
    Completed,
    Stopped
}

// === v0.6 AI 打标排序/分类（spec doc/12-ai-toolbar-buttons.md §3.2） ===

/// <summary>
/// Gallery 左栏分类模式。Date = 按拍摄日期分组（现有行为）；Tag = 按 AI 标签分组（v0.6 预留，本期不实现 UI）。
/// </summary>
public enum GroupMode
{
    Date,
    Tag,
}

/// <summary>
/// Gallery 文件排序方式。TimeDesc = 拍摄时间倒序（现有默认）；ScoreDesc = AI 评分降序（v0.6 新增，null 排最后）。
/// </summary>
public enum SortMode
{
    TimeDesc,
    ScoreDesc,
}

public partial class TimelineEntry : ObservableObject
{
    public DateTime Date { get; }
    public int PhotoCount { get; }

    [ObservableProperty] private bool isSelected;

    public TimelineEntry(DateTime date, int photoCount)
    {
        Date = date;
        PhotoCount = photoCount;
    }
}

public sealed class ScanProgress
{
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Failed { get; set; }
    public string? CurrentFile { get; set; }
}

// === v0.6.1 标签分类（spec doc/11-ai-tagging.md §5.2） ===

/// <summary>
/// 左栏「标签」tab 的单个标签项（标签名 + 在该项目下的文件数）。
/// 取代 v0.6 用的 (string Tag, int Count) tuple，XAML x:DataType 写起来更顺。
/// init-only 防 VM 误改字段（构造后不可变）。
/// </summary>
public sealed class TagGroupItem
{
    public string Tag { get; init; } = "";
    public int Count { get; init; }
}
