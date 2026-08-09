using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Controls;
using StartTooler.Data;
using StartTooler.Helpers;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.ViewModels;

/// <summary>
/// v0.12: 拍摄日记 ViewModel —— 加载会话列表、翻页、笔记保存、环境数据回填。
///
/// 行为：
/// - LoadAsync(projectPath) 触发聚类 + 加载 + 默认定位到最新一页
/// - 翻页：CurrentPageIndex 改变 → 更新 CurrentPage + 触发详情加载
/// - 笔记：Notes 双绑 + LostFocus 持久化
/// - 环境数据：Location/Weather 为空时自动 Fetch（不影响用户手动输入）
/// </summary>
public partial class DiaryViewModel : ObservableObject
{
    private readonly IMediaRepository _mediaRepo;
    private readonly ISessionRepository _sessionRepo;
    private readonly IConfigService _configService;
    private readonly SessionClusteringService _clusteringService;
    private readonly EnvironmentService _envService;

    private CancellationTokenSource? _loadCts;
    private string? _currentProjectPath;

    public DiaryViewModel(
        IMediaRepository mediaRepo,
        ISessionRepository sessionRepo,
        IConfigService configService,
        SessionClusteringService clusteringService,
        EnvironmentService envService)
    {
        _mediaRepo = mediaRepo;
        _sessionRepo = sessionRepo;
        _configService = configService;
        _clusteringService = clusteringService;
        _envService = envService;
    }

    // === 导航回调（由 MainWindowViewModel 注入） ===
    public Action<IReadOnlyList<MediaFile>, int>? NavigateToLightbox { get; set; }
    public Action<DateTime>? NavigateToGalleryDate { get; set; }
    public Action<string>? NavigateToGalleryTag { get; set; }

    // === 数据 ===
    public ObservableCollection<DiaryPageData> AllPages { get; } = new();

    /// <summary>时间轴圆点集合（与 AllPages 一一对应）。</summary>
    public ObservableCollection<TimelineDot> TimelineDots { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    [NotifyPropertyChangedFor(nameof(HasPrev))]
    [NotifyPropertyChangedFor(nameof(HasNext))]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrev))]
    [NotifyPropertyChangedFor(nameof(HasNext))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>刷新环境数据进行中，用于禁用按钮防止重复点击。</summary>
    [ObservableProperty]
    private bool _isRefreshingEnvironment;

    public DiaryPageData? CurrentPage =>
        CurrentPageIndex >= 0 && CurrentPageIndex < AllPages.Count
            ? AllPages[CurrentPageIndex]
            : null;

    public bool HasPrev => CurrentPageIndex > 0;
    public bool HasNext => CurrentPageIndex >= 0 && CurrentPageIndex < AllPages.Count - 1;

    public bool IsEmpty => !IsLoading && AllPages.Count == 0;
    public bool IsContentVisible => !IsLoading && AllPages.Count > 0;

    // === 生命周期 ===

    public async Task LoadAsync(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            AllPages.Clear();
            StatusMessage = "请先选择项目目录";
            return;
        }

        // 同一项目 → 跳过重新加载（避免重复聚类）
        if (_currentProjectPath == projectPath && AllPages.Count > 0)
        {
            return;
        }

        _currentProjectPath = projectPath;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        StatusMessage = "";

        try
        {
            var intervalHours = await GetSessionIntervalHoursAsync(ct);

            // 1. 聚类（处理孤儿照片）
            await _clusteringService.ClusterAsync(projectPath, intervalHours, ct);

            // 2. 加载所有 sessions（按时间倒序）
            var sessions = await _sessionRepo.GetByProjectAsync(projectPath, ct);

            AllPages.Clear();
            foreach (var s in sessions)
            {
                var localDate = s.StartTime.ToLocalTime();
                var page = new DiaryPageData
                {
                    SessionId = s.Id,
                    Title = s.Title,
                    Date = localDate,
                    StartTime = s.StartTime.ToLocalTime(),
                    EndTime = s.EndTime.ToLocalTime(),
                    LunarDateText = LunarDateHelper.GetLunarDateText(localDate),
                    WeekdayText = LunarDateHelper.GetWeekdayText(localDate),
                    WeekLabel = LunarDateHelper.GetMonthWeekLabel(localDate),
                    Location = s.Location,
                    WeatherText = s.WeatherText ?? "",
                    Notes = s.Description,
                };
                // 从 Session.WeatherIconKey 派生（计算属性直接给字符串）
                page.WeatherIconKey = !string.IsNullOrEmpty(s.CloudCover) ? WeatherCoverToIconKey(s.CloudCover) : null;

                AllPages.Add(page);
            }

            // 3. 默认定位到最新一页（AllPages 已按时间倒序，索引 0 为最新）
            CurrentPageIndex = AllPages.Count > 0 ? 0 : -1;
            RebuildTimelineDots();

            // 4. 加载当前页详情
            if (CurrentPage != null)
            {
                await LoadPageDetailsAsync(CurrentPage, ct);
            }

            if (AllPages.Count == 0)
            {
                StatusMessage = "未发现任何拍摄会话，请先导入照片";
            }
        }
        catch (OperationCanceledException)
        {
            // 切换项目导致的中断，静默忽略
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // === 翻页命令 ===

    [RelayCommand]
    private void NavigatePrev()
    {
        if (!HasPrev) return;
        CurrentPageIndex--;
        _ = LoadCurrentPageDetails();
    }

    [RelayCommand]
    private void NavigateNext()
    {
        if (!HasNext) return;
        CurrentPageIndex++;
        _ = LoadCurrentPageDetails();
    }

    [RelayCommand]
    private void NavigateToPage(int index)
    {
        if (index < 0 || index >= AllPages.Count || index == CurrentPageIndex) return;
        CurrentPageIndex = index;
        _ = LoadCurrentPageDetails();
    }

    // === 笔记保存 ===

    [RelayCommand]
    private async Task SaveNotesAsync()
    {
        var page = CurrentPage;
        if (page == null) return;

        try
        {
            var session = await _sessionRepo.GetByIdAsync(page.SessionId);
            if (session == null) return;
            session.Description = page.Notes;
            await _sessionRepo.UpsertAsync(session);
            page.NotesSaveStatus = $"已自动保存 · {DateTime.Now:HH:mm}";
            StatusMessage = "笔记已保存";
        }
        catch (Exception ex)
        {
            page.NotesSaveStatus = $"保存失败 · {DateTime.Now:HH:mm}";
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    // === 删除当前会话 ===

    [RelayCommand]
    private async Task DeleteCurrentSessionAsync()
    {
        var page = CurrentPage;
        if (page == null) return;

        var session = await _sessionRepo.GetByIdAsync(page.SessionId);
        if (session == null) return;

        // 清空所有照片的 session_id（批量 UPDATE NULL）
        var photos = await _mediaRepo.GetBySessionAsync(page.SessionId, limit: int.MaxValue);
        var clearAssignments = photos.Select(p => (p.Id, (string?)null)).ToList();
        if (clearAssignments.Count > 0)
        {
            await _mediaRepo.SetSessionBatchAsync(clearAssignments);
        }

        await _sessionRepo.DeleteAsync(page.SessionId);
        AllPages.RemoveAt(CurrentPageIndex);
        // AllPages 按时间倒序（索引 0 为最新）。删除后优先保持当前索引（显示下一页/更旧），
        // 若删除的是最后一页则回退到上一页（更新），避免越界。
        if (CurrentPageIndex >= AllPages.Count)
        {
            CurrentPageIndex = Math.Max(0, AllPages.Count - 1);
        }
        StatusMessage = "会话已删除";
    }

    // === 刷新环境数据 ===

    [RelayCommand]
    private async Task RefreshEnvironmentAsync()
    {
        var page = CurrentPage;
        if (page == null || IsRefreshingEnvironment) return;

        IsRefreshingEnvironment = true;
        try
        {
            var photos = await _mediaRepo.GetBySessionAsync(page.SessionId, SortMode.TimeAsc, limit: 5);
            if (photos.Count == 0) return;

            var firstPhoto = photos[0];
            var path = string.IsNullOrEmpty(firstPhoto.RelativePath) || string.IsNullOrEmpty(firstPhoto.ProjectPath)
                ? null
                : Path.Combine(firstPhoto.ProjectPath, firstPhoto.RelativePath);

            var env = await _envService.FetchAsync(path, page.Date);
            if (env == null)
            {
                StatusMessage = "未找到 GPS 或网络异常";
                return;
            }

            // 更新 page 显示
            if (!string.IsNullOrEmpty(env.Location))
            {
                page.Location = env.Location;
            }
            if (env.Weather != null)
            {
                page.WeatherText = env.Weather.CloudCover;
                page.WeatherIconKey = WeatherCoverToIconKey(env.Weather.CloudCover);
            }

            // 持久化到 session
            var session = await _sessionRepo.GetByIdAsync(page.SessionId);
            if (session != null)
            {
                if (!string.IsNullOrEmpty(env.Location)) session.Location = env.Location;
                if (env.Weather != null) session.CloudCover = env.Weather.CloudCover;
                await _sessionRepo.UpsertAsync(session);
            }

            StatusMessage = "环境数据已更新";
        }
        finally
        {
            IsRefreshingEnvironment = false;
        }
    }

    // === 标题编辑 ===

    [RelayCommand]
    private void EditTitle()
    {
        var page = CurrentPage;
        if (page == null) return;
        page.EditableTitle = page.Title;
        page.IsEditingTitle = true;
    }

    [RelayCommand]
    private async Task SaveTitleAsync()
    {
        var page = CurrentPage;
        if (page == null) return;

        var newTitle = page.EditableTitle?.Trim() ?? "";
        page.IsEditingTitle = false;

        if (newTitle == page.Title) return;

        try
        {
            var session = await _sessionRepo.GetByIdAsync(page.SessionId);
            if (session == null) return;
            session.Title = newTitle;
            await _sessionRepo.UpsertAsync(session);
            page.Title = newTitle;
            StatusMessage = "标题已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"标题保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelEditTitle()
    {
        var page = CurrentPage;
        if (page == null) return;
        page.IsEditingTitle = false;
    }

    // === 地点编辑 ===

    [RelayCommand]
    private void EditLocation()
    {
        var page = CurrentPage;
        if (page == null) return;
        page.EditableLocation = page.Location;
        page.IsEditingLocation = true;
    }

    [RelayCommand]
    private async Task SaveLocationAsync()
    {
        var page = CurrentPage;
        if (page == null) return;

        var newLocation = page.EditableLocation?.Trim() ?? "";
        page.IsEditingLocation = false;

        if (newLocation == page.Location) return;

        try
        {
            var session = await _sessionRepo.GetByIdAsync(page.SessionId);
            if (session == null) return;
            session.Location = newLocation;
            await _sessionRepo.UpsertAsync(session);
            page.Location = newLocation;
            StatusMessage = "地点已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"地点保存失败：{ex.Message}";
        }
    }

    // === 时段编辑 ===

    [RelayCommand]
    private void EditPeriod()
    {
        var page = CurrentPage;
        if (page == null) return;
        page.EditableStartTime = page.StartTime.ToString("HH:mm");
        page.EditableEndTime = page.EndTime.ToString("HH:mm");
        page.IsEditingPeriod = true;
    }

    [RelayCommand]
    private async Task SavePeriodAsync()
    {
        var page = CurrentPage;
        if (page == null) return;

        if (!TryParseTime(page.EditableStartTime, out var startTimeOfDay))
        {
            StatusMessage = "开始时间格式错误，请使用 HH:mm";
            return;
        }
        if (!TryParseTime(page.EditableEndTime, out var endTimeOfDay))
        {
            StatusMessage = "结束时间格式错误，请使用 HH:mm";
            return;
        }

        try
        {
            var session = await _sessionRepo.GetByIdAsync(page.SessionId);
            if (session == null) return;

            var baseDate = page.Date.Date;
            var newStart = baseDate + startTimeOfDay;
            var newEnd = baseDate + endTimeOfDay;
            if (newEnd < newStart)
            {
                newEnd = newEnd.AddDays(1);
            }

            session.StartTime = newStart;
            session.EndTime = newEnd;
            await _sessionRepo.UpsertAsync(session);

            page.StartTime = newStart;
            page.EndTime = newEnd;
            page.IsEditingPeriod = false;
            StatusMessage = "时段已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"时段保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelEditPeriod()
    {
        var page = CurrentPage;
        if (page == null) return;
        page.IsEditingPeriod = false;
    }

    private static bool TryParseTime(string? input, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input)) return false;
        return TimeSpan.TryParseExact(input.Trim(), "hh\\:mm", null, out time)
            || TimeSpan.TryParseExact(input.Trim(), "HH\\:mm", null, out time);
    }

    // === 内部 ===

    partial void OnCurrentPageIndexChanged(int value)
    {
        RebuildTimelineDots();
    }

    private void RebuildTimelineDots()
    {
        TimelineDots.Clear();
        var accentBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x4F, 0xC3, 0xF7));
        // 非当前节点使用更亮的灰色，确保在深色背景下可见
        var defaultLabelBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xB0, 0xB8, 0xD0));

        for (int i = 0; i < AllPages.Count; i++)
        {
            var page = AllPages[i];
            var isCurrent = i == CurrentPageIndex;
            // 默认全部显示日期标签，节点密集时由 ScrollViewer 横向滚动承载
            TimelineDots.Add(new TimelineDot
            {
                Index = i,
                IsCurrent = isCurrent,
                TooltipText = $"{page.Date:yyyy-MM-dd} · {page.Title}",
                DateLabel = page.Date.ToString("MM/dd"),
                ShowDateLabel = true,
                DotSize = isCurrent ? 14 : 10,
                DotBrush = isCurrent ? accentBrush : null,  // null → 走 XAML FallbackValue
                LabelForeground = isCurrent ? accentBrush : defaultLabelBrush,
                NavigateToPageCommand = NavigateToPageCommand,
            });
        }
    }

    private async Task LoadCurrentPageDetails()
    {
        if (CurrentPage == null) return;
        await LoadPageDetailsAsync(CurrentPage, CancellationToken.None);
    }

    private async Task LoadPageDetailsAsync(DiaryPageData page, CancellationToken ct)
    {
        try
        {
            // 1. 加载精选照片
            var featured = await _mediaRepo.GetDiaryFeaturedAsync(page.SessionId, limit: 12, ct);
            if (featured.Count == 0)
            {
                // 无精选 → 自动选
                featured = await AutoSelectFeaturedAsync(page.SessionId, 12, ct);
            }
            page.FeaturedPhotos = featured;
            // DisplayedFeaturedPhotos / HiddenFeaturedCount / HasMoreFeaturedPhotos
            // 由 DiaryPageData.OnFeaturedPhotosChanged 自动处理

            // 2. 加载统计
            var stats = await _mediaRepo.GetSessionStatsAsync(page.SessionId, ct);
            page.TotalPhotoCount = stats.TotalPhotos;
            page.TargetCount = stats.TargetCount;
            page.TopTags = stats.TopTags;
            page.TargetLabelsText = stats.TopTags.Count > 0
                ? string.Join(" / ", stats.TopTags)
                : "";

            // 初始化笔记字数（已有内容时）
            page.NotesCharacterCount = page.Notes?.Length ?? 0;

            // 3. 环境数据为空时尝试异步获取（不影响手动输入）
            if (string.IsNullOrEmpty(page.Location) || string.IsNullOrEmpty(page.WeatherText))
            {
                _ = TryAutoFillEnvironmentAsync(page, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = $"加载页详情失败：{ex.Message}";
        }
    }

    private async Task TryAutoFillEnvironmentAsync(DiaryPageData page, CancellationToken ct)
    {
        try
        {
            var photos = await _mediaRepo.GetBySessionAsync(page.SessionId, SortMode.TimeAsc, ct: ct, limit: 3);
            // 任一照片有 GPS 即尝试
            foreach (var photo in photos)
            {
                var path = string.IsNullOrEmpty(photo.RelativePath) || string.IsNullOrEmpty(photo.ProjectPath)
                    ? null
                    : Path.Combine(photo.ProjectPath, photo.RelativePath);
                var env = await _envService.FetchAsync(path, page.Date, ct);
                if (env == null) continue;

                if (string.IsNullOrEmpty(page.Location) && !string.IsNullOrEmpty(env.Location))
                {
                    page.Location = env.Location;
                    // 同步更新 session
                    var session = await _sessionRepo.GetByIdAsync(page.SessionId, ct);
                    if (session != null)
                    {
                        session.Location = env.Location;
                        await _sessionRepo.UpsertAsync(session, ct);
                    }
                }
                if (string.IsNullOrEmpty(page.WeatherText) && env.Weather != null)
                {
                    page.WeatherText = env.Weather.CloudCover;
                    page.WeatherIconKey = WeatherCoverToIconKey(env.Weather.CloudCover);
                    var session = await _sessionRepo.GetByIdAsync(page.SessionId, ct);
                    if (session != null)
                    {
                        session.CloudCover = env.Weather.CloudCover;
                        await _sessionRepo.UpsertAsync(session, ct);
                    }
                }
                break;
            }
        }
        catch { /* 静默忽略 */ }
    }

    private async Task<IReadOnlyList<MediaFile>> AutoSelectFeaturedAsync(string sessionId, int limit, CancellationToken ct)
    {
        var all = await _mediaRepo.GetBySessionAsync(sessionId, SortMode.ScoreDesc, ct: ct, limit: int.MaxValue);
        if (all.Count == 0) return Array.Empty<MediaFile>();

        // 优先选有评分的（按评分降序），未评分的按时间均匀采样
        var scored = all.Where(m => m.Score.HasValue).Take(limit).ToList();
        if (scored.Count >= limit) return scored;

        var remaining = limit - scored.Count;
        var pool = all.Where(m => !scored.Contains(m)).ToList();
        if (pool.Count == 0) return scored;

        var step = pool.Count / (double)remaining;
        var sampled = new List<MediaFile>();
        for (int i = 0; i < remaining && i < pool.Count; i++)
        {
            sampled.Add(pool[(int)Math.Min(pool.Count - 1, i * step)]);
        }
        return scored.Concat(sampled).ToList();
    }

    private async Task<int> GetSessionIntervalHoursAsync(CancellationToken ct)
    {
        var config = await _configService.GetAsync<AppConfig>(ConfigKeys.App);
        return config?.SessionIntervalHours ?? 4;
    }

    private static string? WeatherCoverToIconKey(string cloudCover) => cloudCover switch
    {
        "晴" => "Icon.Weather.Sunny",
        "少云" => "Icon.Weather.PartlyCloudy",
        "多云" => "Icon.Weather.Cloudy",
        "阴" => "Icon.Weather.Overcast",
        "雨" => "Icon.Weather.Rain",
        "雪" => "Icon.Weather.Snow",
        "雾" => "Icon.Weather.Fog",
        _ => null,
    };

    // === 点击跳转 ===

    [RelayCommand]
    private void OpenPhoto(MediaFile photo)
    {
        var page = CurrentPage;
        if (page == null || page.FeaturedPhotos.Count == 0) return;
        var index = page.FeaturedPhotos.ToList().FindIndex(m => m.Id == photo.Id);
        if (index < 0) index = 0;
        NavigateToLightbox?.Invoke(page.FeaturedPhotos, index);
    }

    [RelayCommand]
    private void NavigateToDate(DateTime? date)
    {
        if (date.HasValue)
            NavigateToGalleryDate?.Invoke(date.Value);
    }

    [RelayCommand]
    private void NavigateToTag(string tag)
    {
        NavigateToGalleryTag?.Invoke(tag);
    }

    // === 天气选择器 ===

    [RelayCommand]
    private async Task OpenWeatherPickerAsync()
    {
        var page = CurrentPage;
        if (page == null) return;

        var options = new List<WeatherOption>
        {
            new() { Text = "晴", IconKey = "Icon.Weather.Sunny", CloudCover = "晴" },
            new() { Text = "少云", IconKey = "Icon.Weather.PartlyCloudy", CloudCover = "少云" },
            new() { Text = "多云", IconKey = "Icon.Weather.Cloudy", CloudCover = "多云" },
            new() { Text = "阴", IconKey = "Icon.Weather.Overcast", CloudCover = "阴" },
            new() { Text = "雨", IconKey = "Icon.Weather.Rain", CloudCover = "雨" },
            new() { Text = "雪", IconKey = "Icon.Weather.Snow", CloudCover = "雪" },
        };

        var picker = new WeatherIconPicker
        {
            WeatherOptions = options,
        };

        var owner = GetCurrentWindow();
        var selected = await picker.ShowDialogAsync(owner);
        if (selected == null) return;

        page.WeatherText = selected.Text;
        page.WeatherIconKey = selected.IconKey;

        var session = await _sessionRepo.GetByIdAsync(page.SessionId);
        if (session != null)
        {
            session.CloudCover = selected.CloudCover;
            await _sessionRepo.UpsertAsync(session);
        }

        StatusMessage = "天气已更新";
    }

    // === 精选照片管理 ===

    [RelayCommand]
    private async Task OpenFeaturedPhotoPickerAsync()
    {
        var page = CurrentPage;
        if (page == null) return;

        try
        {
            var photos = await _mediaRepo.GetBySessionAsync(page.SessionId, SortMode.TimeAsc, limit: int.MaxValue);
            if (photos.Count == 0) return;

            var featuredIds = new HashSet<long>(page.FeaturedPhotos.Select(p => p.Id));
            var items = new ObservableCollection<FeaturedPhotoSelectableItem>(
                photos.Select(p => new FeaturedPhotoSelectableItem
                {
                    Photo = p,
                    IsSelected = featuredIds.Contains(p.Id),
                }));

            var picker = new FeaturedPhotoPicker
            {
                Items = items,
            };

            var owner = GetCurrentWindow();
            var selectedIds = await picker.ShowDialogAsync(owner);
            if (selectedIds == null) return; // 取消

            var selectedSet = new HashSet<long>(selectedIds);
            foreach (var photo in photos)
            {
                var shouldBeFeatured = selectedSet.Contains(photo.Id);
                if (photo.IsDiaryFeatured != shouldBeFeatured)
                {
                    await _mediaRepo.SetDiaryFeaturedAsync(photo.Id, shouldBeFeatured);
                }
            }

            await LoadCurrentPageDetails();
            StatusMessage = "精选照片已更新";
        }
        catch (Exception ex)
        {
            StatusMessage = $"更新精选照片失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RemoveFeaturedPhotoAsync(MediaFile? photo)
    {
        var page = CurrentPage;
        if (page == null || photo == null) return;

        try
        {
            await _mediaRepo.SetDiaryFeaturedAsync(photo.Id, false);
            await LoadCurrentPageDetails();
            StatusMessage = "已移除精选照片";
        }
        catch (Exception ex)
        {
            StatusMessage = $"移除精选照片失败：{ex.Message}";
        }
    }

    private static Window? GetCurrentWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }
}