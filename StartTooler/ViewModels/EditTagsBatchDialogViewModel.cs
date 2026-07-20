using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Controls;
using StartTooler.Data;

namespace StartTooler.ViewModels;

/// <summary>
/// 批量编辑标签模态弹窗 VM（v0.11 重写）。
///
/// 提供两个互不影响的操作：
///   1. 清除所有标签 — 二次确认后将选中文件的所有标签清空
///   2. 添加标签     — 把 chip 列表合并追加到选中文件已有标签中
///
/// 设计要点：
///   - 移除原 EditTagScope 单选 + Diff 预览（认知负担大于收益）
///   - 两个操作分别走独立的乐观更新 + 失败回滚路径
///   - 操作期间 IsApplying=true，禁用其余入口
/// </summary>
public partial class EditTagsBatchDialogViewModel : ObservableObject, ITagEditorHost
{
    private readonly IReadOnlyList<MediaFile> _files;
    private readonly IMediaRepository _mediaRepo;
    private readonly GalleryViewModel _galleryVm;

    public int FileCount => _files.Count;
    public string Title => $"编辑标签 — 选中 {_files.Count} 个文件";

    /// <summary>待追加的 tag chip 集合。</summary>
    public ObservableCollection<string> Tags { get; } = new();

    [ObservableProperty]
    private string _newTagInput = "";

    public int MaxTagLength => 20;
    public string Watermark => "输入标签后回车添加";
    public bool ShowInputBox => true;
    public ICommand AddTagCommand { get; }
    public ICommand RemoveTagCommand { get; }

    [ObservableProperty]
    private bool _isApplying;

    [ObservableProperty]
    private bool _isConfirmingClear;

    /// <summary>true = 任一操作成功 Apply（调用方据此刷新）。</summary>
    public bool Applied { get; private set; }

    /// <summary>操作成功后请求关闭弹窗。</summary>
    public event EventHandler? RequestClose;

    public EditTagsBatchDialogViewModel(
        IReadOnlyList<MediaFile> files,
        IMediaRepository mediaRepo,
        GalleryViewModel galleryVm)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _mediaRepo = mediaRepo ?? throw new ArgumentNullException(nameof(mediaRepo));
        _galleryVm = galleryVm ?? throw new ArgumentNullException(nameof(galleryVm));

        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand<string>(RemoveTag);

        // chip 变化时刷新 ApplyAddTagsCommand 的可用性
        Tags.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanApplyAddTags));
            ApplyAddTagsCommand.NotifyCanExecuteChanged();
        };

        Trace.WriteLine($"[EditTagsBatch] ctor: fileCount={_files.Count}");
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplyAddTags));
        OnPropertyChanged(nameof(CanClearAllTags));
    }

    partial void OnIsConfirmingClearChanged(bool value)
    {
        OnPropertyChanged(nameof(ClearButtonText));
    }

    /// <summary>清除按钮显示文案：确认中切换为「确认清除？」。</summary>
    public string ClearButtonText => IsConfirmingClear ? "确认清除？" : "清除所有标签";

    /// <summary>添加按钮可用条件：chip 非空 + 无正在执行。</summary>
    public bool CanApplyAddTags => Tags.Count > 0 && !IsApplying && _files.Count > 0;

    /// <summary>清除按钮可用条件：任意时候都可点（无前置条件，自身走二次确认防误触）。</summary>
    public bool CanClearAllTags => !IsApplying && _files.Count > 0;

    // --- 内部 TagChipEditor 事件 ---

    private void AddTag()
    {
        var raw = NewTagInput;
        if (AddTagFromInputRaw(raw))
        {
            NewTagInput = "";
        }
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
        Trace.WriteLine($"[EditTagsBatch] AddTag: added='{text}', total={Tags.Count}");
        return true;
    }

    // --- 二次确认清除流程 ---

    /// <summary>
    /// 点击「清除所有标签」按钮：
    ///   - 第一次点击：进入确认态，按钮文字切换为「确认清除？」；3 秒内未再次点击则自动回滚
    ///   - 第二次点击（确认态）：执行 ClearAllTagsAsync
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearAllTags))]
    private async Task ClearAllTagsAsync()
    {
        if (!IsConfirmingClear)
        {
            IsConfirmingClear = true;

            // 3 秒后自动撤销确认态
            await Task.Delay(3000);
            if (IsConfirmingClear)
            {
                IsConfirmingClear = false;
            }
            return;
        }

        IsConfirmingClear = false;
        await DoClearAllTagsAsync();
    }

    // --- 操作 1：清除所有标签 ---

    private async Task DoClearAllTagsAsync()
    {
        if (_files.Count == 0) return;
        IsApplying = true;
        Applied = false;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var originalSnapshots = new Dictionary<long, List<string>>();
        var succeededFiles = new List<MediaFile>();
        Exception? firstFailure = null;
        int fail = 0;

        try
        {
            foreach (var file in _files)
            {
                var original = file.Tags?.ToList() ?? new List<string>();
                originalSnapshots[file.Id] = original;
                file.Tags = new List<string>();

                try
                {
                    await _mediaRepo.UpdateTagsOnlyAsync(file.Id, new List<string>(), nowMs);
                    succeededFiles.Add(file);
                }
                catch (Exception ex)
                {
                    fail++;
                    if (firstFailure == null) firstFailure = ex;
                    file.Tags = original;
                    Trace.WriteLine($"[EditTagsBatch] ClearAll failed for fileId={file.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (fail > 0)
            {
                int rollbackOk = 0, rollbackFail = 0;
                foreach (var file in succeededFiles)
                {
                    if (!originalSnapshots.TryGetValue(file.Id, out var original)) continue;
                    try
                    {
                        file.Tags = original;
                        await _mediaRepo.UpdateTagsOnlyAsync(file.Id, original, nowMs);
                        rollbackOk++;
                    }
                    catch (Exception rbEx)
                    {
                        rollbackFail++;
                        Trace.WriteLine($"[EditTagsBatch] ClearAll rollback failed for fileId={file.Id}: {rbEx.GetType().Name}: {rbEx.Message}");
                    }
                }

                var message = rollbackFail == 0
                    ? $"批量清除失败：{firstFailure?.Message}（{fail} 个文件未清除，已回滚 {rollbackOk} 个文件）"
                    : $"批量清除失败：{firstFailure?.Message}（{fail} 个文件未清除，回滚 {rollbackOk} 个，另有 {rollbackFail} 个回滚失败，请刷新视图）";
                _galleryVm.ShowToastPublic(message);
                return;
            }

            Applied = true;
            foreach (var file in _files)
            {
                _galleryVm.OnFileTagsChanged(file);
            }

            _galleryVm.ShowToastPublic($"已清除 {_files.Count} 个文件的所有标签");
            Trace.WriteLine($"[EditTagsBatch] ClearAll done: total={_files.Count}");
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsApplying = false;
        }
    }

    // --- 操作 2：添加标签 ---

    [RelayCommand(CanExecute = nameof(CanApplyAddTags))]
    private async Task ApplyAddTagsAsync()
    {
        if (Tags.Count == 0 || _files.Count == 0) return;
        IsApplying = true;
        Applied = false;

        var editor = Tags.ToList();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var originalSnapshots = new Dictionary<long, List<string>>();
        var succeededFiles = new List<MediaFile>();
        Exception? firstFailure = null;
        int fail = 0;

        try
        {
            foreach (var file in _files)
            {
                var original = file.Tags?.ToList() ?? new List<string>();
                originalSnapshots[file.Id] = original;

                // 追加 + 去重（大小写不敏感）
                var merged = original
                    .Concat(editor.Where(t => !original.Any(o => string.Equals(o, t, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                // 没有任何新增则跳过该文件
                if (editor.All(t => original.Any(o => string.Equals(o, t, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                file.Tags = merged;

                try
                {
                    await _mediaRepo.UpdateTagsOnlyAsync(file.Id, merged, nowMs);
                    succeededFiles.Add(file);
                }
                catch (Exception ex)
                {
                    fail++;
                    if (firstFailure == null) firstFailure = ex;
                    file.Tags = original;
                    Trace.WriteLine($"[EditTagsBatch] AddTags failed for fileId={file.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (fail > 0)
            {
                int rollbackOk = 0, rollbackFail = 0;
                foreach (var file in succeededFiles)
                {
                    if (!originalSnapshots.TryGetValue(file.Id, out var original)) continue;
                    try
                    {
                        file.Tags = original;
                        await _mediaRepo.UpdateTagsOnlyAsync(file.Id, original, nowMs);
                        rollbackOk++;
                    }
                    catch (Exception rbEx)
                    {
                        rollbackFail++;
                        Trace.WriteLine($"[EditTagsBatch] AddTags rollback failed for fileId={file.Id}: {rbEx.GetType().Name}: {rbEx.Message}");
                    }
                }

                var message = rollbackFail == 0
                    ? $"批量添加标签失败：{firstFailure?.Message}（{fail} 个文件未保存，已回滚 {rollbackOk} 个文件）"
                    : $"批量添加标签失败：{firstFailure?.Message}（{fail} 个文件未保存，回滚 {rollbackOk} 个，另有 {rollbackFail} 个回滚失败，请刷新视图）";
                _galleryVm.ShowToastPublic(message);
                return;
            }

            Applied = true;
            foreach (var file in succeededFiles)
            {
                _galleryVm.OnFileTagsChanged(file);
            }

            _galleryVm.ShowToastPublic(
                succeededFiles.Count == 0
                    ? "所选文件已包含所有标签，无变化"
                    : $"已为 {succeededFiles.Count} 个文件添加标签");
            Trace.WriteLine($"[EditTagsBatch] AddTags done: applied={succeededFiles.Count}, total={_files.Count}");
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsApplying = false;
        }
    }
}
