using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Data;
using StartTooler.Services;

namespace StartTooler.ViewModels;

/// <summary>
    /// v0.11: 灯箱预览窗口的 ViewModel（图片 + 视频缩略图）。
    ///
    /// 核心职责：
    ///   1. 持有当前查看的 <see cref="MediaFile"/> 列表 + 当前索引（图片 + 视频混合），提供翻页 / 缩放命令。
    ///   2. 图片模式：异步探测原图尺寸（ImageDimensionProbe），驱动 ScrollViewer 渲染尺寸。
    ///   3. 视频模式：直接渲染 MediaFile.ThumbnailPath（不探测尺寸，spec §4.3 视频模式）。
    ///   4. 通过 <see cref="Close"/> 命令关闭；窗口 Closed 事件兜底再 Dispose 一次。
    ///
    /// 数据流：
    ///   GalleryViewModel.PreviewCommand → new LightboxViewModel(files, index) → LightboxWindow
    ///
    /// 文件生命周期：LightboxWindow 非模态（spec §6.1），
    /// Gallery 切日期不影响当前 Lightbox（持有 Files 快照）。
    ///
    /// 视频文件不内嵌播放 —— spec §8 决策，天文 rawvideo AVI 单文件 GB 级，不适合 in-app 播放。
    /// 用户可在灯箱底栏「打开外部」按钮（或视频模式 Space 键）触发系统默认播放器。
    ///
    /// 调试日志：Trace.WriteLine 按项目惯例，分 step 1/3 拆。关键路径：
    ///   ctor / Dispose / 翻页 / 缩放 / 关闭。
    /// </summary>
    public partial class LightboxViewModel : ObservableObject, IDisposable
    {
        private readonly ISystemShellService _systemShell;

        // === step 1/3: 翻页节流（spec §3.4 长按方向键 debounce）===
        private DateTime _lastNavTime = DateTime.MinValue;
        private const int NavThrottleMs = 200;

        // === step 2/3: 资源释放标记 ===
        private bool _disposed;

        /// <summary>
        /// 当前查看的全部文件快照（构造时由 Gallery 传入，运行时不变 —— Gallery 切日期不影响）。
        /// 图片 + 视频混合（spec §6.1）。
        /// </summary>
        public IReadOnlyList<MediaFile> Files { get; }

        [ObservableProperty]
        private int _currentIndex;

        /// <summary>
        /// 当前显示的 MediaFile。CurrentIndex 变化时联动更新（手动 OnPropertyChanged，因为是派生）。
        /// </summary>
        public MediaFile? CurrentFile =>
            Files != null && CurrentIndex >= 0 && CurrentIndex < Files.Count
                ? Files[CurrentIndex]
                : null;

        /// <summary>
        /// 当前文件类型（get-only 派生）。XAML 视频/图片模式分支用。
        /// </summary>
        public bool IsImage => CurrentFile?.MediaType == MediaType.Image;
        public bool IsVideo => CurrentFile?.MediaType == MediaType.Video;

        /// <summary>
        /// 是否有多张可翻（用于 GoNext / GoPrev 的 CanExecute）。
        /// </summary>
        public bool CanGoNext => Files != null && CurrentIndex < Files.Count - 1;
        public bool CanGoPrev => Files != null && CurrentIndex > 0;

        [ObservableProperty]
        private double _scale = 1.0;

        /// <summary>
        /// 当前文件原图宽高（图片模式专用）。由 <see cref="RefreshImageDimensionsAsync"/> 异步填充。
        /// null 时 UI 退化为 Stretch=Uniform 填满 ScrollViewer（spec §4.3 图片模式）。
        /// 视频模式不探测 —— 缩略图已是生成好的，Image 自然渲染。
        /// </summary>
        [ObservableProperty]
        private int? _imageWidth;

        [ObservableProperty]
        private int? _imageHeight;

        /// <summary>
        /// 窗口标题：`{Index+1}/{Total} — {FileName}`（get-only 派生）。
        /// </summary>
        public string Title =>
            CurrentFile == null
                ? "灯箱预览"
                : $"{CurrentIndex + 1}/{Files.Count} — {CurrentFile.FileName}";

        public LightboxViewModel(IReadOnlyList<MediaFile> files, int startIndex, ISystemShellService systemShell)
        {
            _systemShell = systemShell ?? throw new ArgumentNullException(nameof(systemShell));
            Files = files ?? throw new ArgumentNullException(nameof(files));
            if (startIndex < 0 || startIndex >= files.Count)
                throw new ArgumentOutOfRangeException(nameof(startIndex), $"startIndex={startIndex} but Files.Count={files.Count}");

            CurrentIndex = startIndex;

            Trace.WriteLine($"[Lightbox] step 1/3 ctor: index={CurrentIndex}/{files.Count - 1}, first={CurrentFile?.FileName ?? "(null)"}");

            // 构造后立即触发首次加载（图片探测尺寸 / 视频不做事）
            _ = LoadCurrentAsync();
        }

    // ============================================================
    //  翻页
    // ============================================================

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task GoNextAsync()
    {
        if (!ThrottleNav()) return;
        Trace.WriteLine($"[Lightbox] step 1/3 nav: {CurrentIndex} -> {CurrentIndex + 1} (next)");

        CurrentIndex++;
        Scale = 1.0;
        NotifyCanExecuteChangedForNav();
        await LoadCurrentAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrev))]
    private async Task GoPrevAsync()
    {
        if (!ThrottleNav()) return;
        Trace.WriteLine($"[Lightbox] step 1/3 nav: {CurrentIndex} -> {CurrentIndex - 1} (prev)");

        CurrentIndex--;
        Scale = 1.0;
        NotifyCanExecuteChangedForNav();
        await LoadCurrentAsync();
    }

    /// <summary>
    /// 节流：200ms 内重复翻页直接丢弃（spec §3.4）。
    /// </summary>
    private bool ThrottleNav()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastNavTime).TotalMilliseconds < NavThrottleMs)
        {
            Trace.WriteLine($"[Lightbox] nav throttled: {(now - _lastNavTime).TotalMilliseconds:F0}ms < {NavThrottleMs}ms");
            return false;
        }
        _lastNavTime = now;
        return true;
    }

    private void NotifyCanExecuteChangedForNav()
    {
        GoNextCommand.NotifyCanExecuteChanged();
        GoPrevCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentFile));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(Title));
    }

    // ============================================================
    //  缩放
    // ============================================================

    private const double MinScale = 0.25;
    private const double MaxScale = 5.0;
    private const double ZoomStep = 0.25;

    [RelayCommand]
    private void ZoomIn()
    {
        Scale = Math.Min(MaxScale, Math.Round(Scale + ZoomStep, 2));
        Trace.WriteLine($"[Lightbox] zoom in: scale={Scale}");
    }

    [RelayCommand]
    private void ZoomOut()
    {
        Scale = Math.Max(MinScale, Math.Round(Scale - ZoomStep, 2));
        Trace.WriteLine($"[Lightbox] zoom out: scale={Scale}");
    }

    [RelayCommand]
    private void ZoomReset()
    {
        Scale = 1.0;
        Trace.WriteLine($"[Lightbox] zoom reset: scale={Scale}");
    }

    /// <summary>
    /// 鼠标滚轮缩放：向上 +0.1 / 向下 -0.1，范围 [0.25, 5.0]。
    /// 由 View 把鼠标滚轮事件转成命令调用（spec §3.3）。
    /// </summary>
    public void ZoomByWheel(int delta)
    {
        var next = Math.Round(Scale + (delta > 0 ? 0.1 : -0.1), 2);
        Scale = Math.Clamp(next, MinScale, MaxScale);
    }

    /// <summary>
    /// 双击图片：1.0 ↔ 2.0（spec §3.3）
    /// </summary>
    public void ZoomToggle()
    {
        Scale = Math.Abs(Scale - 1.0) < 0.01 ? 2.0 : 1.0;
        Trace.WriteLine($"[Lightbox] zoom toggle: scale={Scale}");
    }

    // ============================================================
    //  兜底命令（用系统看图器打开 + 在文件夹中显示）
    // ============================================================

    [RelayCommand]
    private void OpenExternally()
    {
        if (CurrentFile == null) return;
        var path = Path.Combine(CurrentFile.ProjectPath, CurrentFile.RelativePath);
        if (!File.Exists(path))
        {
            Trace.WriteLine($"[Lightbox] OpenExternally skipped (file missing): {path}");
            return;
        }
        try
        {
            _systemShell.OpenWithDefaultApp(path);
            Trace.WriteLine($"[Lightbox] OpenExternally: {path}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Lightbox] OpenExternally failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RevealInFolder()
    {
        if (CurrentFile == null) return;
        var path = Path.Combine(CurrentFile.ProjectPath, CurrentFile.RelativePath);
        try
        {
            _systemShell.RevealInFolder(path);
            Trace.WriteLine($"[Lightbox] RevealInFolder: {path}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Lightbox] RevealInFolder failed: {ex.Message}");
        }
    }

    // ============================================================
    //  加载当前文件
    // ============================================================

    /// <summary>
    /// 异步加载当前文件：
    ///   - 图片：探测原图尺寸填 ImageWidth / ImageHeight。
    ///   - 视频：不探测（缩略图已是生成好的，Image 自然渲染）。
    /// 失败一律不抛，外层 try/catch 留 trace，UI 优雅降级（spec §8）。
    /// </summary>
    private async Task LoadCurrentAsync()
    {
        if (CurrentFile == null) return;

        ImageWidth = null;
        ImageHeight = null;

        if (IsImage)
        {
            await RefreshImageDimensionsAsync();
        }
        // 视频模式：ImageWidth/Height 保持 null，XAML 用 Image 自然渲染 + MinWidth/MinHeight 兜底
    }

    private async Task RefreshImageDimensionsAsync()
    {
        if (CurrentFile == null) return;
        var path = Path.Combine(CurrentFile.ProjectPath, CurrentFile.RelativePath);

        // ImageDimensionProbe 是纯 CPU，跑在 thread pool 上不阻塞 UI
        try
        {
            var dim = await Task.Run(() => ImageDimensionProbe.Probe(path));
            if (dim.HasValue)
            {
                ImageWidth = dim.Value.Width;
                ImageHeight = dim.Value.Height;
            }
            else
            {
                Trace.WriteLine($"[Lightbox] step 2/3 image dim probe returned null: {path}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Lightbox] step 2/3 image dim probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ============================================================
    //  关闭
    // ============================================================

    [RelayCommand]
    private void Close()
    {
        Trace.WriteLine($"[Lightbox] Close: index={CurrentIndex} file={CurrentFile?.FileName}");
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Trace.WriteLine($"[Lightbox] Dispose: clearing state");
        GC.SuppressFinalize(this);
    }
}