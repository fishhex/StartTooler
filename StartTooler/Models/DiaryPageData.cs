using System;
using System.Collections.Generic;
using StartTooler.Data;

namespace StartTooler.Models;

/// <summary>
/// v0.12: 日记页数据载体 —— 一页对应一次会话。
/// 由 DiaryViewModel 构造，DiaryPage XAML 绑定此类型。
/// </summary>
public sealed class DiaryPageData
{
    public string SessionId { get; init; } = "";

    /// <summary>标题（默认 "{date} 出摊"，可手动改）。</summary>
    public string Title { get; init; } = "";

    /// <summary>会话起始日期（本地时间）。</summary>
    public DateTime Date { get; init; }

    /// <summary>会话时长文本（"1h20m" / "45m"）。</summary>
    public string DurationText { get; init; } = "";

    /// <summary>地点（行政区域级）。空 = 未填/未获取。</summary>
    public string Location { get; set; } = "";

    /// <summary>天气图标资源 Key，对应 Themes/Icons.axaml 中的 Icon.Weather.*。</summary>
    public string? WeatherIconKey { get; set; }

    /// <summary>天气文本（"东南风 3级 · 多云"）。</summary>
    public string WeatherText { get; set; } = "";

    /// <summary>用户笔记。</summary>
    public string Notes { get; set; } = "";

    /// <summary>精选照片（Diary 中显示）。</summary>
    public IReadOnlyList<MediaFile> FeaturedPhotos { get; set; } = Array.Empty<MediaFile>();

    /// <summary>总照片数（会话内所有照片）。</summary>
    public int TotalPhotoCount { get; set; }

    /// <summary>目标数（去重 tags）。</summary>
    public int TargetCount { get; set; }

    /// <summary>累计曝光时长文本（"1.5h" / "45m"）。</summary>
    public string TotalExposureText { get; set; } = "";

    /// <summary>Top 3 标签。</summary>
    public IReadOnlyList<string> TopTags { get; set; } = Array.Empty<string>();
}