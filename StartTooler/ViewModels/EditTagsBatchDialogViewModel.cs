using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Controls;
using StartTooler.Data;

namespace StartTooler.ViewModels;

/// <summary>
/// 批量编辑标签模态弹窗 VM（v0.11 重写）。
///
/// 三个操作：
///   1. 清除所有标签 — 二次确认
///   2. 添加标签     — chip 追加合并
///   3. 删除标签     — 从选中文件移除指定标签
///
/// 增强：
///   - 顶部显示选中文件的已有标签上下文
///   - 标签建议云（项目已有标签，点击快速添加）
///   - 应用进度条
///   - 输入提示
/// </summary>
public partial class EditTagsBatchDialogViewModel : ObservableObject, ITagEditorHost
{
    private readonly IReadOnlyList<MediaFile> _files;
    private readonly IMediaRepository _mediaRepo;
    private readonly GalleryViewModel _galleryVm;
    private readonly string _projectPath;

    public int FileCount => _files.Count;
    public string Title => $"编辑标签 — 选中 {_files.Count} 个文件";

    // ============ 已有标签上下文 ============

    /// <summary>选中文件已有的全部标签（去重），用于顶部展示。</summary>
    public string ExistingTagsText { get; }

    /// <summary>选中文件的共同标签，用于顶部展示。</summary>
    public string CommonTagsText { get; }

    /// <summary>是否有已有标签可展示。</summary>
    public bool HasExistingTags { get; }

    // ============ 标签建议云 ============

    /// <summary>项目已有标签，点击快速添加到 chip 列表。</summary>
    public ObservableCollection<string> SuggestedTags { get; } = new();

    /// <summary>是否有建议标签可展示。</summary>
    public bool HasSuggestedTags => SuggestedTags.Count > 0;

    // ============ 操作 2：添加标签 ============

    public ObservableCollection<string> Tags { get; } = new();

    [ObservableProperty]
    private string _newTagInput = "";

    public int MaxTagLength => 20;
    public string Watermark => "输入标签后按 Enter 添加";
    public bool ShowInputBox => true;
    public ICommand AddTagCommand { get; }
    public ICommand RemoveTagCommand { get; }

    /// <summary>添加标签 — 将影响的文件数（排除已有该标签的文件）。</summary>
    public int AddAffectedCount => ComputeAffectedCount(Tags, AddMode: true);

    /// <summary>添加按钮可用条件：chip 非空 + 无正在执行。</summary>
    public bool CanApplyAddTags => Tags.Count > 0 && !IsApplying && _files.Count > 0;

    // ============ 操作 3：删除标签 ============

    /// <summary>待删除的标签 chip 集合。</summary>
    public ObservableCollection<string> RemoveTags { get; } = new();

    [ObservableProperty]
    private string _removeTagInput = "";

    /// <summary>删除标签 — 将影响的文件数（至少包含一个待删标签的文件）。</summary>
    public int RemoveAffectedCount => ComputeAffectedCount(RemoveTags, AddMode: false);

    /// <summary>删除按钮可用条件：chip 非空 + 无正在执行。</summary>
    public bool CanApplyRemoveTags => RemoveTags.Count > 0 && !IsApplying && _files.Count > 0;

    // ============ 进度 ============

    [ObservableProperty]
    private int _applyProgressCurrent;

    [ObservableProperty]
    private int _applyProgressTotal;

    [ObservableProperty]
    private string _applyProgressText = "";

    // ============ 状态 ============

    [ObservableProperty]
    private bool _isApplying;

    [ObservableProperty]
    private bool _isConfirmingClear;

    private CancellationTokenSource? _clearConfirmCts;

    public bool Applied { get; private set; }

    public event EventHandler? RequestClose;

    // ============ 清除确认态 UI 切换 ============

    public bool ShouldShowClearInitialButton => !IsConfirmingClear;
    public bool ShouldShowClearConfirmRow => IsConfirmingClear;
    public bool CanClearAllTags => !IsApplying && _files.Count > 0;

    // ============ 移除标签的 ITagEditorHost（内部委托） ============

    /// <summary>内部移除标签宿主，供第二个 TagChipEditor 绑定。</summary>
    public RemoveTagEditorHost RemoveHost { get; }

    // ============ 构造 ============

    public EditTagsBatchDialogViewModel(
        IReadOnlyList<MediaFile> files,
        IMediaRepository mediaRepo,
        GalleryViewModel galleryVm)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _mediaRepo = mediaRepo ?? throw new ArgumentNullException(nameof(mediaRepo));
        _galleryVm = galleryVm ?? throw new ArgumentNullException(nameof(galleryVm));
        _projectPath = _files.FirstOrDefault()?.ProjectPath ?? "";

        // 已有标签上下文
        var allTags = _files
            .SelectMany(f => f.Tags ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();
        HasExistingTags = allTags.Count > 0;
        ExistingTagsText = allTags.Count > 0 ? string.Join("、", allTags) : "（无）";

        var common = _files
            .Select(f => f.Tags as IEnumerable<string> ?? Enumerable.Empty<string>())
            .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase))
            .OrderBy(t => t)
            .ToList();
        CommonTagsText = common.Count > 0 ? string.Join("、", common) : "（无）";

        // 标签建议云（异步加载，不阻塞构造）
        _ = LoadSuggestedTagsAsync();

        SuggestedTags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSuggestedTags));

        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand<string>(RemoveTag);

        Tags.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanApplyAddTags));
            OnPropertyChanged(nameof(AddAffectedCount));
            ApplyAddTagsCommand.NotifyCanExecuteChanged();
        };

        RemoveTags.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanApplyRemoveTags));
            OnPropertyChanged(nameof(RemoveAffectedCount));
            ApplyRemoveTagsCommand.NotifyCanExecuteChanged();
        };

        // 内部移除标签宿主
        RemoveHost = new RemoveTagEditorHost(this);

        Trace.WriteLine($"[EditTagsBatch] ctor: fileCount={_files.Count}, existingTags={allTags.Count}, commonTags={common.Count}");
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplyAddTags));
        OnPropertyChanged(nameof(CanApplyRemoveTags));
        OnPropertyChanged(nameof(CanClearAllTags));
        RequestClearTagsCommand.NotifyCanExecuteChanged();
        ConfirmClearTagsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConfirmingClearChanged(bool value)
    {
        OnPropertyChanged(nameof(ShouldShowClearConfirmRow));
        OnPropertyChanged(nameof(ShouldShowClearInitialButton));
    }

    partial void OnApplyProgressCurrentChanged(int value) => UpdateProgressText();
    partial void OnApplyProgressTotalChanged(int value) => UpdateProgressText();

    private void UpdateProgressText()
    {
        ApplyProgressText = ApplyProgressTotal > 0
            ? $"正在处理 {ApplyProgressCurrent}/{ApplyProgressTotal}..."
            : "";
    }

    // ============ 标签建议云 ============

    private async Task LoadSuggestedTagsAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_projectPath)) return;
            var tags = await _mediaRepo.GetTagsAsync(_projectPath);
            // 排除已在选中文件中的共同标签，避免重复建议
            var commonSet = new HashSet<string>(CommonTagsText.Split("、", StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            foreach (var t in tags)
            {
                if (!commonSet.Contains(t.Name))
                    SuggestedTags.Add(t.Name);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EditTagsBatch] LoadSuggestedTags failed: {ex.Message}");
        }
    }

    /// <summary>点击建议标签 → 添加到添加 chip 列表。</summary>
    [RelayCommand]
    private void AddSuggestedTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) return;
        if (tag.Length > MaxTagLength) return;
        Tags.Add(tag);
    }

    // ============ 内部 TagChipEditor（添加） ============

    private void AddTag()
    {
        var raw = NewTagInput;
        if (AddTagFromInputRaw(raw))
            NewTagInput = "";
    }

    private void RemoveTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        Tags.Remove(tag);
    }

    private bool AddTagFromInputRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.Length == 0 || text.Length > MaxTagLength) return false;
        if (Tags.Any(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase))) return false;
        Tags.Add(text);
        Trace.WriteLine($"[EditTagsBatch] AddTag: '{text}', total={Tags.Count}");
        return true;
    }

    // ============ 影响文件数计算 ============

    private int ComputeAffectedCount(ObservableCollection<string> chips, bool AddMode)
    {
        if (chips.Count == 0) return 0;
        var chipSet = new HashSet<string>(chips, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var file in _files)
        {
            var fileTags = file.Tags ?? new List<string>();
            if (AddMode)
            {
                // 添加模式：文件至少缺少一个 chip → 会被影响
                if (chipSet.Any(c => !fileTags.Any(f => string.Equals(f, c, StringComparison.OrdinalIgnoreCase))))
                    count++;
            }
            else
            {
                // 删除模式：文件至少包含一个 chip → 会被影响
                if (fileTags.Any(f => chipSet.Contains(f)))
                    count++;
            }
        }
        return count;
    }

    // ============ 二次确认清除 ============

    [RelayCommand(CanExecute = nameof(CanClearAllTags))]
    private async Task RequestClearTagsAsync()
    {
        IsConfirmingClear = true;
        ArmAutoCancelClear();
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanClearAllTags))]
    private async Task ConfirmClearTagsAsync()
    {
        CancelAutoCancelClear();
        IsConfirmingClear = false;
        await DoClearAllTagsAsync();
    }

    [RelayCommand]
    private void CancelClearTags()
    {
        CancelAutoCancelClear();
        IsConfirmingClear = false;
    }

    private void ArmAutoCancelClear()
    {
        CancelAutoCancelClear();
        _clearConfirmCts = new CancellationTokenSource();
        var ct = _clearConfirmCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsConfirmingClear) IsConfirmingClear = false;
            });
        });
    }

    private void CancelAutoCancelClear()
    {
        try { _clearConfirmCts?.Cancel(); } catch { }
        _clearConfirmCts?.Dispose();
        _clearConfirmCts = null;
    }

    // ============ 操作 1：清除所有标签 ============

    private async Task DoClearAllTagsAsync()
    {
        if (_files.Count == 0) return;
        IsApplying = true;
        Applied = false;
        ApplyProgressTotal = _files.Count;
        ApplyProgressCurrent = 0;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshots = new Dictionary<long, List<string>>();
        var succeeded = new List<MediaFile>();
        Exception? firstFailure = null;
        int fail = 0;

        try
        {
            foreach (var file in _files)
            {
                var original = file.Tags?.ToList() ?? new List<string>();
                snapshots[file.Id] = original;
                file.Tags = new List<string>();

                try
                {
                    await _mediaRepo.UpdateTagsOnlyAsync(file.Id, new List<string>(), nowMs);
                    succeeded.Add(file);
                }
                catch (Exception ex)
                {
                    fail++;
                    firstFailure ??= ex;
                    file.Tags = original;
                }
                finally
                {
                    ApplyProgressCurrent++;
                }
            }

            if (fail > 0)
            {
                await RollbackAsync(succeeded, snapshots, nowMs);
                _galleryVm.ShowToastPublic($"清除失败：{firstFailure?.Message}（{fail} 个未清除，已回滚）");
                return;
            }

            Applied = true;
            NotifyChanges(_files);
            _galleryVm.ShowToastPublic($"已清除 {_files.Count} 个文件的所有标签");
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsApplying = false;
            ApplyProgressText = "";
        }
    }

    // ============ 操作 2：添加标签 ============

    [RelayCommand(CanExecute = nameof(CanApplyAddTags))]
    private async Task ApplyAddTagsAsync()
    {
        if (Tags.Count == 0 || _files.Count == 0) return;
        IsApplying = true;
        Applied = false;
        ApplyProgressTotal = _files.Count;
        ApplyProgressCurrent = 0;

        var editor = Tags.ToList();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshots = new Dictionary<long, List<string>>();
        var succeeded = new List<MediaFile>();
        Exception? firstFailure = null;
        int fail = 0;

        try
        {
            foreach (var file in _files)
            {
                var original = file.Tags?.ToList() ?? new List<string>();
                snapshots[file.Id] = original;

                var merged = original
                    .Concat(editor.Where(t => !original.Any(o => string.Equals(o, t, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                if (editor.All(t => original.Any(o => string.Equals(o, t, StringComparison.OrdinalIgnoreCase))))
                {
                    ApplyProgressCurrent++;
                    continue;
                }

                file.Tags = merged;
                try
                {
                    await _mediaRepo.UpdateTagsOnlyAsync(file.Id, merged, nowMs);
                    succeeded.Add(file);
                }
                catch (Exception ex)
                {
                    fail++;
                    firstFailure ??= ex;
                    file.Tags = original;
                }
                finally
                {
                    ApplyProgressCurrent++;
                }
            }

            if (fail > 0)
            {
                await RollbackAsync(succeeded, snapshots, nowMs);
                _galleryVm.ShowToastPublic($"添加失败：{firstFailure?.Message}（{fail} 个未保存，已回滚）");
                return;
            }

            Applied = true;
            NotifyChanges(succeeded);
            _galleryVm.ShowToastPublic(
                succeeded.Count == 0
                    ? "所选文件已包含所有标签，无变化"
                    : $"已为 {succeeded.Count} 个文件添加标签");
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsApplying = false;
            ApplyProgressText = "";
        }
    }

    // ============ 操作 3：删除标签 ============

    [RelayCommand(CanExecute = nameof(CanApplyRemoveTags))]
    private async Task ApplyRemoveTagsAsync()
    {
        if (RemoveTags.Count == 0 || _files.Count == 0) return;
        IsApplying = true;
        Applied = false;
        ApplyProgressTotal = _files.Count;
        ApplyProgressCurrent = 0;

        var toRemove = RemoveTags.ToList();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshots = new Dictionary<long, List<string>>();
        var succeeded = new List<MediaFile>();
        Exception? firstFailure = null;
        int fail = 0;

        try
        {
            foreach (var file in _files)
            {
                var original = file.Tags?.ToList() ?? new List<string>();
                snapshots[file.Id] = original;

                var remaining = original
                    .Where(o => !toRemove.Any(t => string.Equals(t, o, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                // 无变化则跳过
                if (remaining.Count == original.Count)
                {
                    ApplyProgressCurrent++;
                    continue;
                }

                file.Tags = remaining;
                try
                {
                    await _mediaRepo.UpdateTagsOnlyAsync(file.Id, remaining, nowMs);
                    succeeded.Add(file);
                }
                catch (Exception ex)
                {
                    fail++;
                    firstFailure ??= ex;
                    file.Tags = original;
                }
                finally
                {
                    ApplyProgressCurrent++;
                }
            }

            if (fail > 0)
            {
                await RollbackAsync(succeeded, snapshots, nowMs);
                _galleryVm.ShowToastPublic($"删除失败：{firstFailure?.Message}（{fail} 个未保存，已回滚）");
                return;
            }

            Applied = true;
            NotifyChanges(succeeded);
            _galleryVm.ShowToastPublic(
                succeeded.Count == 0
                    ? "所选文件均不包含这些标签，无变化"
                    : $"已从 {succeeded.Count} 个文件中删除标签");
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsApplying = false;
            ApplyProgressText = "";
        }
    }

    // ============ 回滚 ============

    private async Task RollbackAsync(
        List<MediaFile> succeeded,
        Dictionary<long, List<string>> snapshots,
        long nowMs)
    {
        int ok = 0, failCount = 0;
        foreach (var file in succeeded)
        {
            if (!snapshots.TryGetValue(file.Id, out var original)) continue;
            try
            {
                file.Tags = original;
                await _mediaRepo.UpdateTagsOnlyAsync(file.Id, original, nowMs);
                ok++;
            }
            catch (Exception ex)
            {
                failCount++;
                Trace.WriteLine($"[EditTagsBatch] Rollback failed fileId={file.Id}: {ex.Message}");
            }
        }
        if (failCount > 0)
            _galleryVm.ShowToastPublic($"回滚警告：{failCount} 个文件回滚失败，请刷新视图");
    }

    private void NotifyChanges(IReadOnlyList<MediaFile> files)
    {
        foreach (var file in files)
            _galleryVm.OnFileTagsChanged(file);
    }

    // ============ 移除标签内部宿主（实现 ITagEditorHost） ============

    /// <summary>
    /// 内部类，实现 ITagEditorHost，将 RemoveTags / RemoveTagInput 等封装为独立契约，
    /// 供第二个 TagChipEditor 通过 DataContext="{Binding RemoveHost}" 绑定。
    /// </summary>
    public sealed class RemoveTagEditorHost : ITagEditorHost
    {
        private readonly EditTagsBatchDialogViewModel _parent;

        public RemoveTagEditorHost(EditTagsBatchDialogViewModel parent)
        {
            _parent = parent;
        }

        public ObservableCollection<string> Tags => _parent.RemoveTags;
        public string NewTagInput { get => _parent.RemoveTagInput; set => _parent.RemoveTagInput = value; }
        public int MaxTagLength => _parent.MaxTagLength;
        public string Watermark => "输入要删除的标签后按 Enter 添加";
        public bool ShowInputBox => true;
        public ICommand AddTagCommand => _parent.AddRemoveTagCommand;
        public ICommand RemoveTagCommand => _parent.RemoveRemoveTagCommand;
    }

    // 移除标签的 add/remove 命令（由 [RelayCommand] 自动生成，供 RemoveTagEditorHost 使用）

    [RelayCommand]
    private void AddRemoveTag()
    {
        var raw = RemoveTagInput;
        if (string.IsNullOrWhiteSpace(raw)) return;
        var text = raw.Trim();
        if (text.Length == 0 || text.Length > MaxTagLength) return;
        if (RemoveTags.Any(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase))) return;
        RemoveTags.Add(text);
        RemoveTagInput = "";
    }

    [RelayCommand]
    private void RemoveRemoveTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        RemoveTags.Remove(tag);
    }
}