using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using StartTooler.Data;

namespace StartTooler.Models;

/// <summary>
/// v0.12: 日记页数据载体 —— 一页对应一次会话。
/// 由 DiaryViewModel 构造，DiaryPage XAML 绑定此类型。
/// 属性全部可观察，异步加载详情后能自动刷新 UI。
/// </summary>
public sealed partial class DiaryPageData : ObservableObject
{
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Location) or nameof(IsEditingLocation))
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsLocationDisplayVisible)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsLocationEditVisible)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsAddLocationVisible)));
        }
    }

    [ObservableProperty]
    private string _sessionId = "";

    /// <summary>标题（默认 "{date} 出摊"，可手动改）。</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>会话起始日期（本地时间）。</summary>
    [ObservableProperty]
    private DateTime _date;

    /// <summary>农历日期文本，例如 "农历六月初十"。</summary>
    [ObservableProperty]
    private string _lunarDateText = "";

    /// <summary>星期文本，例如 "星期五"。</summary>
    [ObservableProperty]
    private string _weekdayText = "";

    /// <summary>月份第几周标签，例如 "7月第4周"，用于面包屑。</summary>
    [ObservableProperty]
    private string _weekLabel = "";

    /// <summary>目标标签行文本，例如 "M31仙女座星云 / 银河核心 / 月面环形山"。</summary>
    [ObservableProperty]
    private string _targetLabelsText = "";

    /// <summary>会话时长文本（"1h20m" / "45m"）。</summary>
    [ObservableProperty]
    private string _durationText = "";

    /// <summary>地点（行政区域级）。空 = 未填/未获取。</summary>
    [ObservableProperty]
    private string _location = "";

    /// <summary>是否正在编辑地点。</summary>
    [ObservableProperty]
    private bool _isEditingLocation;

    /// <summary>地点编辑框中的临时文本。</summary>
    [ObservableProperty]
    private string _editableLocation = "";

    /// <summary>地点胶囊（只读显示）是否可见。</summary>
    public bool IsLocationDisplayVisible => !string.IsNullOrEmpty(Location) && !IsEditingLocation;

    /// <summary>地点编辑框是否可见。</summary>
    public bool IsLocationEditVisible => IsEditingLocation;

    /// <summary>"添加地点"按钮是否可见。</summary>
    public bool IsAddLocationVisible => string.IsNullOrEmpty(Location) && !IsEditingLocation;

    /// <summary>天气图标资源 Key，对应 Themes/Icons.axaml 中的 Icon.Weather.*。</summary>
    [ObservableProperty]
    private string? _weatherIconKey;

    /// <summary>天气文本（"东南风 3级 · 多云"）。</summary>
    [ObservableProperty]
    private string _weatherText = "";

    /// <summary>用户笔记。</summary>
    [ObservableProperty]
    private string _notes = "";

    /// <summary>笔记保存状态文本，例如 "已自动保存 · 14:23"。</summary>
    [ObservableProperty]
    private string _notesSaveStatus = "";

    /// <summary>笔记当前字数。</summary>
    [ObservableProperty]
    private int _notesCharacterCount;

    /// <summary>笔记最大字数限制。</summary>
    public const int MaxNotesLength = 500;

    /// <summary>笔记内容变化时自动更新字数。</summary>
    partial void OnNotesChanged(string value)
    {
        NotesCharacterCount = value?.Length ?? 0;
    }

    /// <summary>精选照片（Diary 中显示）。</summary>
    [ObservableProperty]
    private IReadOnlyList<MediaFile> _featuredPhotos = Array.Empty<MediaFile>();

    /// <summary>精选照片中在日记页实际展示的前 N 张（默认前 6 张）。</summary>
    [ObservableProperty]
    private IReadOnlyList<FeaturedPhotoItem> _displayedFeaturedPhotos = Array.Empty<FeaturedPhotoItem>();

    /// <summary>精选照片中未展示的数量，用于 "查看全部 +N"。</summary>
    [ObservableProperty]
    private int _hiddenFeaturedCount;

    /// <summary>是否有更多精选照片未展示。</summary>
    [ObservableProperty]
    private bool _hasMoreFeaturedPhotos;

    /// <summary>精选照片排序模式。</summary>
    [ObservableProperty]
    private DiaryPhotoSortMode _featuredSortMode = DiaryPhotoSortMode.TimeAsc;

    /// <summary>精选照片排序模式索引（ComboBox SelectedIndex 用）。</summary>
    [ObservableProperty]
    private int _featuredSortModeIndex;

    /// <summary>精选照片排序选项列表（ComboBox 用）。</summary>
    public IReadOnlyList<FeaturedSortOption> FeaturedSortOptions { get; } = new[]
    {
        new FeaturedSortOption(DiaryPhotoSortMode.TimeAsc, "按拍摄时间升序"),
        new FeaturedSortOption(DiaryPhotoSortMode.TimeDesc, "按拍摄时间降序"),
        new FeaturedSortOption(DiaryPhotoSortMode.ScoreDesc, "按评分排序"),
    };

    /// <summary>精选照片源数据变化时重新排序并截取展示。</summary>
    partial void OnFeaturedPhotosChanged(IReadOnlyList<MediaFile> value)
    {
        ApplyFeaturedSort();
    }

    /// <summary>精选照片排序模式变化时重新排序。</summary>
    partial void OnFeaturedSortModeChanged(DiaryPhotoSortMode value)
    {
        FeaturedSortModeIndex = (int)value;
        ApplyFeaturedSort();
    }

    /// <summary>ComboBox SelectedIndex 变化时同步排序模式。</summary>
    partial void OnFeaturedSortModeIndexChanged(int value)
    {
        if ((int)FeaturedSortMode != value && value >= 0 && value < FeaturedSortOptions.Count)
        {
            FeaturedSortMode = (DiaryPhotoSortMode)value;
        }
    }

    private void ApplyFeaturedSort()
    {
        var sorted = FeaturedPhotos.ToList();
        sorted = FeaturedSortMode switch
        {
            DiaryPhotoSortMode.TimeAsc => sorted.OrderBy(m => m.ShotAtDateTime).ToList(),
            DiaryPhotoSortMode.TimeDesc => sorted.OrderByDescending(m => m.ShotAtDateTime).ToList(),
            DiaryPhotoSortMode.ScoreDesc => sorted.OrderByDescending(m => m.Score ?? 0).ToList(),
            _ => sorted,
        };

        const int DisplayLimit = 6;
        var displayed = sorted.Take(DisplayLimit).Select((photo, index) => new FeaturedPhotoItem
        {
            Photo = photo,
            Index = index + 1,
            TimeText = photo.ShotAtDateTime.HasValue ? photo.ShotAtDateTime.Value.ToString("HH:mm") : "",
        }).ToList();

        DisplayedFeaturedPhotos = displayed;
        HiddenFeaturedCount = Math.Max(0, sorted.Count - DisplayLimit);
        HasMoreFeaturedPhotos = HiddenFeaturedCount > 0;
    }

    /// <summary>总照片数（会话内所有照片）。</summary>
    [ObservableProperty]
    private int _totalPhotoCount;

    /// <summary>目标数（去重 tags）。</summary>
    [ObservableProperty]
    private int _targetCount;

    /// <summary>累计曝光时长文本（"1.5h" / "45m"）。</summary>
    [ObservableProperty]
    private string _totalExposureText = "";

    /// <summary>累计曝光小时数（原始数值，用于统计条大字号显示）。</summary>
    [ObservableProperty]
    private double _totalExposureHours;

    /// <summary>Top 3 标签。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _topTags = Array.Empty<string>();
}
