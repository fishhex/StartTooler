using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Controls;
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
    public partial class LightboxViewModel : ObservableObject, IDisposable, StartTooler.Controls.ITagEditorHost
    {
        private readonly ISystemShellService _systemShell;

        // === v0.12 手动编辑标签（spec doc/15-manual-tag-edit.md §3 / §6.1）===
        private readonly IMediaRepository? _mediaRepo;
        private readonly GalleryViewModel? _galleryVm;
        private List<string> _originalTags = new();      // 进入编辑态时 CurrentFile.Tags 的快照

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

        // === v0.12 手动编辑标签（spec doc/15-manual-tag-edit.md §3）===

        /// <summary>
        /// 编辑态下的 tag 列表（与 CurrentFile.Tags 独立）。进入编辑态时拷贝 CurrentFile.Tags，
        /// 退出编辑态（保存/取消/翻页自动保存）后回填到 CurrentFile.Tags。
        /// 实现 ITagEditorHost.Tags —— 供 TagChipEditor UserControl 复用。
        /// </summary>
        public ObservableCollection<string> Tags { get; } = new();

        /// <summary>标签输入框文本（XAML TwoWay 绑 TextBox.Text）。</summary>
        [ObservableProperty]
        private string _newTagInput = "";

        // === ITagEditorHost 静态属性（spec §4）===
        public int MaxTagLength => 20;
        public string Watermark => "输入标签后回车添加";
        public bool ShowInputBox => true;
        ICommand ITagEditorHost.AddTagCommand => AddTagCommand;
        ICommand ITagEditorHost.RemoveTagCommand => RemoveTagCommand;

        /// <summary>是否处于编辑态。true 时灯箱右侧显示 chip 编辑器 + 输入框 + 保存/取消按钮。</summary>
        [ObservableProperty]
        private bool _isEditingTags;

        /// <summary>
        /// 当前编辑内容与原始 tag 是否不同（派生）。绑定 Save 按钮 IsEnabled / IsDirty 视觉提示。
        /// 派生逻辑：长度不同 → dirty；逐个不区分大小写比较 → 都等 → clean。
        /// </summary>
        public bool IsDirty
        {
            get
            {
                var current = CurrentFile?.Tags ?? new List<string>();
                if (Tags.Count != current.Count) return true;
                for (var i = 0; i < Tags.Count; i++)
                {
                    if (!string.Equals(Tags[i], current[i], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 是否允许进入编辑态（spec §3.7 / §7 边界 & 锁）：
        ///   - CurrentFile 非空
        ///   - 文件未软删除（DeletedAt == null）
        ///   - AI 没有正在打标（galleryVm?.IsTagging == false）
        /// mediaRepo / galleryVm 都为 null 时不能保存（无写入路径），按 false 处理。
        /// </summary>
        public bool CanEditTags =>
            CurrentFile != null
            && CurrentFile.DeletedAt == null
            && (_galleryVm?.IsTagging ?? false) == false
            && _mediaRepo != null;

        /// <summary>
        /// 关闭原因的简短说明（XAML tooltip 用）。CanEditTags=true 时返回 "编辑标签"（默认文案），
        /// false 时返回具体原因（"AI 正在打标" / "文件已删除" / "数据访问未就绪" / "无文件"）。
        /// </summary>
        public string EditTagsBlockReason
        {
            get
            {
                if (CurrentFile == null) return "无文件";
                if (CurrentFile.DeletedAt != null) return "文件已删除";
                if (_mediaRepo == null) return "数据访问未就绪";
                if (_galleryVm?.IsTagging == true) return "AI 正在打标";
                return "编辑标签";
            }
        }

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

        public LightboxViewModel(
            IReadOnlyList<MediaFile> files,
            int startIndex,
            ISystemShellService systemShell,
            IMediaRepository? mediaRepo = null,
            GalleryViewModel? galleryVm = null)
        {
            _systemShell = systemShell ?? throw new ArgumentNullException(nameof(systemShell));
            Files = files ?? throw new ArgumentNullException(nameof(files));
            _mediaRepo = mediaRepo;
            _galleryVm = galleryVm;
            if (startIndex < 0 || startIndex >= files.Count)
                throw new ArgumentOutOfRangeException(nameof(startIndex), $"startIndex={startIndex} but Files.Count={files.Count}");

            CurrentIndex = startIndex;

            Trace.WriteLine($"[Lightbox] step 1/3 ctor: index={CurrentIndex}/{files.Count - 1}, first={CurrentFile?.FileName ?? "(null)"}, mediaRepo={mediaRepo != null}, galleryVm={galleryVm != null}");

            // v0.12: 订阅 GalleryVM.IsTagging 变化以刷新 CanEditTags
            if (galleryVm != null)
            {
                galleryVm.PropertyChanged += OnGalleryVmPropertyChanged;
            }

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

        // v0.12: 翻页前自动保存正在编辑的 tag（spec §3.6 方案 A）
        await SaveEditTagsIfNeededAsync();

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

        // v0.12: 翻页前自动保存正在编辑的 tag（spec §3.6 方案 A）
        await SaveEditTagsIfNeededAsync();

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

        // v0.12: CurrentFile 变化时，CanEditTags / IsDirty / EditTagsBlockReason 派生属性都受影响
        OnPropertyChanged(nameof(CanEditTags));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(EditTagsBlockReason));
        EnterEditTagsCommand.NotifyCanExecuteChanged();
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
    //  v0.12 手动编辑标签（spec doc/15-manual-tag-edit.md §3 / §6.1）
    // ============================================================

    /// <summary>
    /// IsEditingTags 变化钩子：通知 Save 按钮重新评估 CanExecute（IsEditingTags 自身），
    /// 以及 IsDirty 重新计算（因为 CurrentFile.Tags 引用没变，但 EditingTags 可能空了）。
    /// </summary>
    partial void OnIsEditingTagsChanged(bool value)
    {
        SaveEditTagsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDirty));
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

    // ============================================================
    //  v0.12 手动编辑标签（spec doc/15-manual-tag-edit.md §3 / §6.1）
    // ============================================================

    /// <summary>
    /// 进入编辑态（spec §3.3）：快照 CurrentFile.Tags 到 _originalTags + EditingTags 同步填充。
    /// CanExecute = CanEditTags（软删除 / AI 打标中 / 无 mediaRepo 都拒绝进入）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditTags))]
    private void EnterEditTags()
    {
        if (CurrentFile == null) return;
        _originalTags = CurrentFile.Tags.ToList();
        Tags.Clear();
        foreach (var t in _originalTags) Tags.Add(t);
        NewTagInput = "";
        IsEditingTags = true;
        OnPropertyChanged(nameof(IsDirty));
        Trace.WriteLine($"[Lightbox] EnterEditTags: file={CurrentFile.FileName}, originalCount={_originalTags.Count}");
    }

    /// <summary>
    /// 取消编辑：把 EditingTags / NewTagInput 还原回 _originalTags，IsEditingTags = false。
    /// 注意：不取消已在内存里写回 CurrentFile.Tags 的乐观更新（设计如此：避免双重状态机），
    /// 所以 _originalTags 一旦构造，EditingTags 操作不会触达 CurrentFile.Tags 直至 Save。
    /// </summary>
    [RelayCommand]
    private void CancelEditTags()
    {
        Tags.Clear();
        foreach (var t in _originalTags) Tags.Add(t);
        NewTagInput = "";
        IsEditingTags = false;
        OnPropertyChanged(nameof(IsDirty));
        Trace.WriteLine($"[Lightbox] CancelEditTags: file={CurrentFile?.FileName}, restored={_originalTags.Count}");
    }

    /// <summary>
    /// 保存编辑：把 EditingTags 写回 CurrentFile.Tags + 调 MediaRepository.UpdateTagsOnlyAsync 持久化。
    /// 失败回滚（spec §3.5）：
    ///   1. 先改内存 CurrentFile.Tags = newList（乐观更新，UI 立即刷）
    ///   2. 调 _mediaRepo.UpdateTagsOnlyAsync 写库
    ///   3. catch 后回滚 CurrentFile.Tags = _originalTags，toast 报错
    /// 无 diff（IsDirty == false）时直接 IsEditingTags = false，不写库。
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsEditingTags))]
    private async Task SaveEditTagsAsync()
    {
        if (CurrentFile == null || _mediaRepo == null) return;
        if (!IsDirty)
        {
            // 没改动：直接退出编辑态，不写库
            IsEditingTags = false;
            Trace.WriteLine($"[Lightbox] SaveEditTags: no diff, skip DB write: file={CurrentFile.FileName}");
            return;
        }

        var originalForRollback = _originalTags.ToList();
        var newList = Tags.ToList();

        Trace.WriteLine($"[Lightbox] step 1/3 SaveEditTags: file={CurrentFile.FileName}, from={originalForRollback.Count} to={newList.Count}");

        // 1. 乐观更新内存（ObservableProperty 触发 UI 刷）
        CurrentFile.Tags = newList;

        // 2. 写库
        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _mediaRepo.UpdateTagsOnlyAsync(CurrentFile.Id, newList, nowMs);
            IsEditingTags = false;
            OnPropertyChanged(nameof(IsDirty));
            Trace.WriteLine($"[Lightbox] step 2/3 SaveEditTags: ok, fileId={CurrentFile.Id}, tags={newList.Count}");

            // 3. 通知 GalleryViewModel：左栏 TagGroups 需要刷新（500ms debounce 由 GalleryVM 内部处理）
            _galleryVm?.OnFileTagsChanged(CurrentFile);
        }
        catch (Exception ex)
        {
            // 回滚
            CurrentFile.Tags = originalForRollback;
            Trace.WriteLine($"[Lightbox] step 2/3 SaveEditTags failed, rolled back: {ex.GetType().Name}: {ex.Message}");
            _galleryVm?.ShowToastPublic($"保存标签失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从编辑列表移除单个 tag（chip 右上 × 按钮触发）。
    /// 同步触发 IsDirty 通知让 Save 按钮 IsEnabled 刷新。
    /// </summary>
    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        if (Tags.Remove(tag))
        {
            OnPropertyChanged(nameof(IsDirty));
            Trace.WriteLine($"[Lightbox] RemoveTag: removed='{tag}', remaining={Tags.Count}");
        }
    }

    /// <summary>
    /// 从输入框（NewTagInput）添加 tag。TagChipEditor 通过 ITagEditorHost.AddTagCommand 触发。
    /// 内部走 AddTagFromInputRaw 做 trim/去空/去重/长度校验。
    /// </summary>
    [RelayCommand]
    private void AddTag()
    {
        var raw = NewTagInput;
        if (AddTagFromInputRaw(raw))
        {
            // 成功添加才清空输入框 + 焦点回输入框（XAML 端用 IsEditingTags 触发 re-focus）
            NewTagInput = "";
        }
    }

    /// <summary>
    /// 输入框内容校验 + 加入 Tags（spec §3.4）：
    ///   1. trim
    ///   2. 空字符串 / 纯空白 → 静默丢弃
    ///   3. 长度 > MaxTagLength(20) → 静默丢弃（XAML MaxLength 已硬限，double-check 兜底）
    ///   4. 大小写不敏感去重（已存在同 tag 忽略）
    /// 返回 true 表示成功添加，false 表示被丢弃。
    /// </summary>
    private bool AddTagFromInputRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.Length == 0) return false;
        if (text.Length > MaxTagLength)
        {
            Trace.WriteLine($"[Lightbox] AddTag rejected (too long): len={text.Length}, max={MaxTagLength}");
            return false;
        }
        if (Tags.Any(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase)))
        {
            Trace.WriteLine($"[Lightbox] AddTag rejected (duplicate, case-insensitive): '{text}'");
            return false;
        }
        Tags.Add(text);
        OnPropertyChanged(nameof(IsDirty));
        Trace.WriteLine($"[Lightbox] AddTag: added='{text}', total={Tags.Count}");
        return true;
    }

    /// <summary>
    /// 翻页时调（spec §3.6 自动 Save 方案 A）：若 IsEditingTags 状态，调 SaveEditTagsAsync。
    /// 无 diff（IsDirty == false）则 SaveEditTagsAsync 内部直接 IsEditingTags = false，不写库。
    /// 失败由 SaveEditTagsAsync 内部 catch + toast，不阻塞翻页。
    /// </summary>
    private async Task SaveEditTagsIfNeededAsync()
    {
        if (!IsEditingTags) return;
        Trace.WriteLine($"[Lightbox] SaveEditTagsIfNeededAsync: auto-save on nav, file={CurrentFile?.FileName}, dirty={IsDirty}");
        await SaveEditTagsAsync();
    }

    /// <summary>
    /// 订阅 GalleryViewModel 的 IsTagging 变化（spec §6.2 边界锁）。
    /// GalleryVM 改 IsTagging 时 → LightboxVM 刷新 CanEditTags + 通知 EnterEditTagsCommand CanExecute。
    /// 灯箱在 AI 打标中不能进入编辑态，按钮自动 disable。
    /// </summary>
    private void OnGalleryVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.IsTagging))
        {
            OnPropertyChanged(nameof(CanEditTags));
            OnPropertyChanged(nameof(EditTagsBlockReason));
            EnterEditTagsCommand.NotifyCanExecuteChanged();
            Trace.WriteLine($"[Lightbox] GalleryVM.IsTagging changed → refresh CanEditTags={CanEditTags}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Trace.WriteLine($"[Lightbox] Dispose: clearing state");

        // v0.12: 取消订阅 GalleryVM
        if (_galleryVm != null)
        {
            _galleryVm.PropertyChanged -= OnGalleryVmPropertyChanged;
        }

        GC.SuppressFinalize(this);
    }
}