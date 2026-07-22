using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Controls;
using StartTooler.Data;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.ViewModels;

/// <summary>
/// v0.11: 统计仪表盘 ViewModel（spec/19 §7）。
/// Phase 1 实现：KPI + 热力图 + 周期统计 + 目标排行 + 曝光参数分布。
/// 支持年 / 季度 / 月三种时间粒度切换。
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly IMediaRepository _mediaRepo;
    private readonly IConfigService _configService;

    // === 时间维度切换 ===
    [ObservableProperty] private TimeMode _timeMode = TimeMode.Year;
    [ObservableProperty] private int _selectedYear = DateTime.Now.Year;
    [ObservableProperty] private int _selectedQuarter = 1;
    [ObservableProperty] private int _selectedMonth = 1;

    [ObservableProperty] private List<int> _availableYears = new();
    [ObservableProperty] private List<int> _availableMonths = Enumerable.Range(1, 12).ToList();

    public IReadOnlyList<TimeModeOption> TimeModeOptions { get; } = new List<TimeModeOption>
    {
        new(TimeMode.Year, "年"),
        new(TimeMode.Quarter, "季度"),
        new(TimeMode.Month, "月"),
    };

    [ObservableProperty] private TimeModeOption _selectedTimeModeOption = null!;

    public List<int> AvailableQuarters { get; } = new() { 1, 2, 3, 4 };

    public DashboardPeriod CurrentPeriod => TimeMode switch
    {
        TimeMode.Quarter => new DashboardPeriod { Year = SelectedYear, Quarter = SelectedQuarter },
        TimeMode.Month => new DashboardPeriod { Year = SelectedYear, Month = SelectedMonth },
        _ => new DashboardPeriod { Year = SelectedYear },
    };

    public string CurrentPeriodTitle => TimeMode switch
    {
        TimeMode.Quarter => $"{SelectedYear}年 Q{SelectedQuarter}",
        TimeMode.Month => $"{SelectedYear}年 {SelectedMonth}月",
        _ => $"{SelectedYear}年",
    };

    public string PeriodChartTitle => TimeMode == TimeMode.Month ? "每日统计" : "月度统计";

    public bool IsQuarterMode => TimeMode == TimeMode.Quarter;
    public bool IsMonthMode => TimeMode == TimeMode.Month;

    // === KPI ===
    [ObservableProperty] private DashboardKpi? _kpi;

    // === 热力图 ===
    [ObservableProperty] private IReadOnlyList<HeatmapDay> _heatmapDays = Array.Empty<HeatmapDay>();

    // === 周期统计（年→月 / 季度→月 / 月→日）===
    [ObservableProperty] private IReadOnlyList<PeriodStat> _periodStats = Array.Empty<PeriodStat>();

    // === 目标排行 ===
    [ObservableProperty] private IReadOnlyList<TagRank> _tagRanks = Array.Empty<TagRank>();

    // === 曝光参数 ===
    [ObservableProperty] private IReadOnlyList<FocalRangeStat> _focalStats = Array.Empty<FocalRangeStat>();
    [ObservableProperty] private IReadOnlyList<IsoStat> _isoStats = Array.Empty<IsoStat>();
    [ObservableProperty] private IReadOnlyList<ExposureStat> _exposureStats = Array.Empty<ExposureStat>();

    // === 空态 / 加载 ===
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _emptyMessage = "请先在设置中选择项目目录";

    // === 跳转回调（由 MainWindowViewModel 注入）===
    public Action<DateTime>? NavigateToGalleryDate { get; set; }
    public Action<string>? NavigateToGalleryTag { get; set; }

    public DashboardViewModel(IMediaRepository mediaRepo, IConfigService configService)
    {
        _mediaRepo = mediaRepo;
        _configService = configService;
        _selectedTimeModeOption = TimeModeOptions[0];
    }

    public async Task LoadAsync()
    {
        var projectConfig = await _configService.GetOrCreateAsync<ProjectConfig>(ConfigKeys.Project);
        var projectPath = projectConfig.CurrentDirectory;
        if (string.IsNullOrEmpty(projectPath))
        {
            IsEmpty = true;
            EmptyMessage = "请先在设置中选择项目目录";
            return;
        }

        // 首次加载：默认年份为最后一条数据的年份（无数据则当前年）
        if (AvailableYears.Count == 0)
        {
            var latestYear = await _mediaRepo.GetLatestPhotoYearAsync(projectPath, CancellationToken.None);
            SelectedYear = latestYear ?? DateTime.Now.Year;
        }

        IsLoading = true;
        try
        {
            var period = CurrentPeriod;

            // 并行加载所有统计
            var kpiTask = _mediaRepo.GetDashboardKpiAsync(projectPath, period, CancellationToken.None);
            var heatmapTask = _mediaRepo.GetDashboardHeatmapAsync(projectPath, period, CancellationToken.None);
            var periodTask = _mediaRepo.GetDashboardPeriodStatsAsync(projectPath, period, CancellationToken.None);
            var tagRankTask = _mediaRepo.GetDashboardTagRankingAsync(projectPath, period, CancellationToken.None);
            var focalTask = _mediaRepo.GetDashboardFocalDistributionAsync(projectPath, period, CancellationToken.None);
            var isoTask = _mediaRepo.GetDashboardIsoDistributionAsync(projectPath, period, CancellationToken.None);
            var exposureTask = _mediaRepo.GetDashboardExposureDistributionAsync(projectPath, period, CancellationToken.None);

            await Task.WhenAll(kpiTask, heatmapTask, periodTask, tagRankTask,
                               focalTask, isoTask, exposureTask);

            Kpi = kpiTask.Result;
            HeatmapDays = heatmapTask.Result;
            PeriodStats = periodTask.Result;
            TagRanks = tagRankTask.Result;
            FocalStats = focalTask.Result;
            IsoStats = isoTask.Result;
            ExposureStats = exposureTask.Result;

            IsEmpty = Kpi.TotalPhotos == 0;
            EmptyMessage = IsEmpty ? "所选时间段内没有照片" : "";

            // 构建可用年份列表（基于有数据的年份，至少包含当前年份和 SelectedYear）
            BuildAvailableYears();

            NotifyChartPropertiesChanged();
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnTimeModeChanged(TimeMode value)
    {
        SelectedTimeModeOption = TimeModeOptions.First(o => o.Mode == value);
        NormalizeSelection();
        NotifyPeriodPropertiesChanged();
        _ = LoadAsync();
    }

    partial void OnSelectedTimeModeOptionChanged(TimeModeOption value)
    {
        if (value is not null && TimeMode != value.Mode)
        {
            TimeMode = value.Mode;
        }
    }

    partial void OnSelectedYearChanged(int value)
    {
        NormalizeSelection();
        NotifyPeriodPropertiesChanged();
        _ = LoadAsync();
    }

    partial void OnSelectedQuarterChanged(int value)
    {
        NotifyPeriodPropertiesChanged();
        _ = LoadAsync();
    }

    partial void OnSelectedMonthChanged(int value)
    {
        NotifyPeriodPropertiesChanged();
        _ = LoadAsync();
    }

    private void NotifyPeriodPropertiesChanged()
    {
        OnPropertyChanged(nameof(CurrentPeriod));
        OnPropertyChanged(nameof(CurrentPeriodTitle));
        OnPropertyChanged(nameof(PeriodChartTitle));
        OnPropertyChanged(nameof(IsQuarterMode));
        OnPropertyChanged(nameof(IsMonthMode));
    }

    private void NotifyChartPropertiesChanged()
    {
        OnPropertyChanged(nameof(KpiTotalPhotos));
        OnPropertyChanged(nameof(KpiExposureHours));
        OnPropertyChanged(nameof(KpiShootingDays));
        OnPropertyChanged(nameof(KpiTargetCount));
        OnPropertyChanged(nameof(KpiTotalSize));
        OnPropertyChanged(nameof(PeriodBarItems));
        OnPropertyChanged(nameof(TagRankBarItems));
        OnPropertyChanged(nameof(FocalBarItems));
        OnPropertyChanged(nameof(IsoBarItems));
        OnPropertyChanged(nameof(ExposureBarItems));
    }

    private void NormalizeSelection()
    {
        if (SelectedQuarter < 1) SelectedQuarter = 1;
        if (SelectedQuarter > 4) SelectedQuarter = 4;
        if (SelectedMonth < 1) SelectedMonth = 1;
        if (SelectedMonth > 12) SelectedMonth = 12;
    }

    private void BuildAvailableYears()
    {
        var years = new HashSet<int> { DateTime.Now.Year, SelectedYear };
        foreach (var d in HeatmapDays)
        {
            years.Add(d.Date.Year);
        }
        AvailableYears = years.OrderByDescending(y => y).ToList();
    }

    // === KPI 格式化 ===
    public string KpiTotalPhotos => Kpi?.TotalPhotos.ToString("N0") ?? "0";
    public string KpiExposureHours
    {
        get
        {
            if (Kpi == null) return "0h";
            var h = (int)Kpi.TotalExposureHours;
            var m = (int)((Kpi.TotalExposureHours - h) * 60);
            return m > 0 ? $"{h}h{m}m" : $"{h}h";
        }
    }
    public string KpiShootingDays => $"{Kpi?.ShootingDays ?? 0} 天";
    public string KpiTargetCount => $"{Kpi?.TargetCount ?? 0} 目标";
    public string KpiTotalSize
    {
        get
        {
            if (Kpi == null) return "0 B";
            var bytes = Kpi.TotalBytes;
            if (bytes >= 1_099_511_627_776L) return $"{bytes / 1_099_511_627_776.0:F1} TB";
            if (bytes >= 1_073_741_824L) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576L) return $"{bytes / 1_048_576.0:F1} MB";
            return $"{bytes} B";
        }
    }

    // === 图表数据转换 ===
    public IReadOnlyList<BarItem> PeriodBarItems => PeriodStats
        .Select(p => new BarItem
        {
            Label = p.Label ?? GetPeriodLabel(p.Period),
            Value = p.Count,
            DisplayValue = p.Count.ToString(),
        })
        .ToList();

    private string GetPeriodLabel(int period)
    {
        return TimeMode switch
        {
            TimeMode.Month => $"{period}日",
            _ => $"{period}月",
        };
    }

    public IReadOnlyList<BarItem> TagRankBarItems => TagRanks
        .Select(t => new BarItem
        {
            Label = t.TagName,
            Value = t.Count,
            DisplayValue = $"{t.Count} ({t.Percentage:F0}%)",
            Tag = t.TagName,
        })
        .ToList();

    public IReadOnlyList<BarItem> FocalBarItems => FocalStats
        .Select(f => new BarItem
        {
            Label = f.RangeLabel,
            Value = f.Count,
            DisplayValue = $"{f.Count} ({f.Percentage:F0}%)",
        })
        .ToList();

    public IReadOnlyList<BarItem> IsoBarItems => IsoStats
        .Select(i => new BarItem
        {
            Label = i.IsoLabel,
            Value = i.Count,
            DisplayValue = $"{i.Count} ({i.Percentage:F0}%)",
        })
        .ToList();

    public IReadOnlyList<BarItem> ExposureBarItems => ExposureStats
        .Select(e => new BarItem
        {
            Label = e.RangeLabel,
            Value = e.Count,
            DisplayValue = $"{e.Count} ({e.Percentage:F0}%)",
        })
        .ToList();

    // === 跳转命令 ===
    [RelayCommand]
    private void NavigateToDate(DateTime date)
    {
        NavigateToGalleryDate?.Invoke(date);
    }

    [RelayCommand]
    private void NavigateToTag(string tagName)
    {
        NavigateToGalleryTag?.Invoke(tagName);
    }

    /// <summary>
    /// 热力图点击事件处理（由 DashboardView code-behind 转发）。
    /// </summary>
    public void OnHeatmapDayClicked(HeatmapDay day)
    {
        NavigateToGalleryDate?.Invoke(day.Date);
    }

    /// <summary>
    /// 目标排行点击事件处理（由 DashboardView code-behind 转发）。
    /// </summary>
    public void OnTagRankClicked(BarItem item)
    {
        if (!string.IsNullOrEmpty(item.Tag))
        {
            NavigateToGalleryTag?.Invoke(item.Tag);
        }
    }
}

/// <summary>
/// 时间维度选项（模式 + 显示文本）。
/// </summary>
public sealed record TimeModeOption(TimeMode Mode, string Display);
