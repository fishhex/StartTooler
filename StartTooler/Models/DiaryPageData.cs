using System;
using System.Collections.Generic;
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
    [ObservableProperty]
    private string _sessionId = "";

    /// <summary>标题（默认 "{date} 出摊"，可手动改）。</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>会话起始日期（本地时间）。</summary>
    [ObservableProperty]
    private DateTime _date;

    /// <summary>会话时长文本（"1h20m" / "45m"）。</summary>
    [ObservableProperty]
    private string _durationText = "";

    /// <summary>地点（行政区域级）。空 = 未填/未获取。</summary>
    [ObservableProperty]
    private string _location = "";

    /// <summary>天气图标资源 Key，对应 Themes/Icons.axaml 中的 Icon.Weather.*。</summary>
    [ObservableProperty]
    private string? _weatherIconKey;

    /// <summary>天气文本（"东南风 3级 · 多云"）。</summary>
    [ObservableProperty]
    private string _weatherText = "";

    /// <summary>用户笔记。</summary>
    [ObservableProperty]
    private string _notes = "";

    /// <summary>精选照片（Diary 中显示）。</summary>
    [ObservableProperty]
    private IReadOnlyList<MediaFile> _featuredPhotos = Array.Empty<MediaFile>();

    /// <summary>总照片数（会话内所有照片）。</summary>
    [ObservableProperty]
    private int _totalPhotoCount;

    /// <summary>目标数（去重 tags）。</summary>
    [ObservableProperty]
    private int _targetCount;

    /// <summary>累计曝光时长文本（"1.5h" / "45m"）。</summary>
    [ObservableProperty]
    private string _totalExposureText = "";

    /// <summary>Top 3 标签。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _topTags = Array.Empty<string>();
}
