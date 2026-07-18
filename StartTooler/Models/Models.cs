using System;
using System.Collections.ObjectModel;
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

/// <summary>
/// 时间轴层级类型：年 → 月 → 日（spec §15-15-gallery-timeline §2.3）。
/// UI 用 Kind 决定节点缩进尺寸与「年/月/日」标识前缀。
/// </summary>
public enum TimelineNodeKind
{
    Year,
    Month,
    Day,
}

/// <summary>
/// 时间轴节点：支持三级折叠层次结构（Year → Month → Day）。
/// v0.11 spec/15：每个节点有 IsExpanded（折叠/展开）、IsSelected（当前选中态）；
/// 含 Key（日期字符串用于选中态同步）、Count（文件数）、Children（子节点），
/// 以及通过 Level 计算缩进（Year=0, Month=1, Day=2）。
/// 为兼容 GetByDateAsync 的现有调用，保留 Date 属性（Kind=Day 时使用）。
/// </summary>
public partial class TimelineNode : ObservableObject
{
    public TimelineNodeKind Kind { get; }
    public string Key { get; }     // "2026" / "2026-07" / "2026-07-16"
    public string Label { get; }   // "2026 年" / "07 月" / "07-16"
    public int Count { get; }

    /// <summary>Kind=Day 时使用；其他层级为 default。</summary>
    public DateTime Date { get; }

    /// <summary>缩进等级（Year=0, Month=1, Day=2）。</summary>
    public int Level => (int)Kind;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public ObservableCollection<TimelineNode> Children { get; } = new();

    public TimelineNode(TimelineNodeKind kind, string key, string label, int count, DateTime date = default)
    {
        Kind = kind;
        Key = key;
        Label = label;
        Count = count;
        Date = date;
    }
}

// TimelineEntry 已升级为 TimelineNode（支持年/月/日三级）。
// 保留旧名注释作为迁移提示；新代码请直接使用 TimelineNode。

/// <summary>
/// v0.11 时间轴快捷胶囊过滤器（spec/15 §5.5 预留扩展位）。
/// All / Today / ThisWeek / ThisMonth / ThisYear 五个档位。
/// QuickFilter 不直接改 SQL，而是覆写 shot_at 的起止区间（Date 视图）或扩成全标签列表（Tag 视图）。
/// </summary>
public enum TimelineQuickFilter
{
    All,
    Today,
    ThisWeek,
    ThisMonth,
    ThisYear,
}

/// <summary>
/// 快捷胶囊项（XAML 绑定用，spec §2.2 截图）。
/// </summary>
public sealed partial class QuickFilterItem : ObservableObject
{
    public TimelineQuickFilter Key { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isSelected;

    public QuickFilterItem(TimelineQuickFilter key, string label)
    {
        Key = key;
        Label = label;
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
public sealed partial class TagGroupItem : ObservableObject
{
    public string Tag { get; init; } = "";
    public int Count { get; init; }

    /// <summary>
    /// v0.11: 标签列表选中态（spec §15.4）—— 选中项文字变色 / 加粗。
    /// 由 GalleryViewModel.SelectTagAsync 维护。
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;
}
