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
/// Phase 1 实现：KPI + 热力图 + 月度统计 + 目标排行 + 曝光参数分布。
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly IMediaRepository _mediaRepo;
    private readonly IConfigService _configService;

    // === 年份切换 ===
    [ObservableProperty] private int _selectedYear = DateTime.Now.Year;
    [ObservableProperty] private List<int> _availableYears = new();

    // === KPI ===
    [ObservableProperty] private DashboardKpi? _kpi;

    // === 热力图 ===
    [ObservableProperty] private IReadOnlyList<HeatmapDay> _heatmapDays = Array.Empty<HeatmapDay>();

    // === 月度统计 ===
    [ObservableProperty] private IReadOnlyList<MonthStat> _monthlyStats = Array.Empty<MonthStat>();

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

        IsLoading = true;
        try
        {
            // 并行加载所有统计
            var kpiTask = _mediaRepo.GetDashboardKpiAsync(projectPath, CancellationToken.None);
            var heatmapTask = _mediaRepo.GetDashboardHeatmapAsync(projectPath, SelectedYear, CancellationToken.None);
            var monthlyTask = _mediaRepo.GetDashboardMonthlyStatsAsync(projectPath, SelectedYear, CancellationToken.None);
            var tagRankTask = _mediaRepo.GetDashboardTagRankingAsync(projectPath, CancellationToken.None);
            var focalTask = _mediaRepo.GetDashboardFocalDistributionAsync(projectPath, CancellationToken.None);
            var isoTask = _mediaRepo.GetDashboardIsoDistributionAsync(projectPath, CancellationToken.None);
            var exposureTask = _mediaRepo.GetDashboardExposureDistributionAsync(projectPath, CancellationToken.None);

            await Task.WhenAll(kpiTask, heatmapTask, monthlyTask, tagRankTask,
                               focalTask, isoTask, exposureTask);

            Kpi = kpiTask.Result;
            HeatmapDays = heatmapTask.Result;
            MonthlyStats = monthlyTask.Result;
            TagRanks = tagRankTask.Result;
            FocalStats = focalTask.Result;
            IsoStats = isoTask.Result;
            ExposureStats = exposureTask.Result;

            IsEmpty = Kpi.TotalPhotos == 0;
            EmptyMessage = IsEmpty ? "导入照片后将生成统计数据" : "";

            // 构建可用年份列表（基于有数据的年份，至少包含当前年份和 SelectedYear）
            BuildAvailableYears();
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedYearChanged(int value)
    {
        _ = LoadAsync();
    }

    private void BuildAvailableYears()
    {
        var years = new HashSet<int> { DateTime.Now.Year, SelectedYear };
        foreach (var d in HeatmapDays)
        {
            years.Add(d.Date.Year);
        }
        foreach (var m in MonthlyStats)
        {
            // MonthlyStats 只有月份，需要结合 SelectedYear
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
    public IReadOnlyList<BarItem> MonthlyBarItems => MonthlyStats
        .Select(m => new BarItem
        {
            Label = $"{m.Month}月",
            Value = m.Count,
            DisplayValue = m.Count.ToString(),
        })
        .ToList();

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
