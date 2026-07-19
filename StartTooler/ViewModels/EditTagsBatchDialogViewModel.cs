using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Controls;
using StartTooler.Data;

namespace StartTooler.ViewModels;

/// <summary>
/// 批量编辑标签 scope（spec doc/15-manual-tag-edit.md §5.3）。
/// </summary>
public enum EditTagScope
{
    /// <summary>替换为：用 editorTags 完全替换原 tags（原 tags 全删）</summary>
    Replace,
    /// <summary>追加：editorTags ∪ file.Tags 去重</summary>
    Append,
    /// <summary>删除：file.Tags - editorTags</summary>
    Remove,
}

/// <summary>
/// 单文件的 diff 预览条目（spec doc/15-manual-tag-edit.md §5.4）。
/// </summary>
public class TagDiffEntry
{
    public string FileName { get; set; } = "";
    public IReadOnlyList<string> Added { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Removed { get; set; } = Array.Empty<string>();
    public bool HasDiff => Added.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// 批量编辑标签模态弹窗 VM（spec doc/15-manual-tag-edit.md §5 / §6.3）。
/// 接收 N 张文件 + scope，编辑器里维护 chip 列表，预览区实时算每张文件的 diff。
/// Apply 时：
///   1. 乐观更新每张 file.Tags = ComputeNewTags(file.Tags)
///   2. 批量写库（每张调 UpdateTagsOnlyAsync）
///   3. 任一文件失败时，把已经成功写库的文件回滚到原 tags，保持整批一致
/// </summary>
public partial class EditTagsBatchDialogViewModel : ObservableObject, ITagEditorHost
{
    private readonly IReadOnlyList<MediaFile> _files;
    private readonly IMediaRepository _mediaRepo;
    private readonly GalleryViewModel _galleryVm;

    public int FileCount => _files.Count;
    public string Title => $"编辑标签 — 选中 {_files.Count} 个文件";

    public ObservableCollection<string> Tags { get; } = new();

    [ObservableProperty]
    private string _newTagInput = "";

    [ObservableProperty]
    private EditTagScope _scope = EditTagScope.Append;

    public int MaxTagLength => 20;
    public string Watermark => "输入标签后回车添加";
    public bool ShowInputBox => true;
    public ICommand AddTagCommand { get; }
    public ICommand RemoveTagCommand { get; }

    /// <summary>实时 diff 预览（每张文件一行）。</summary>
    public ObservableCollection<TagDiffEntry> DiffPreview { get; } = new();

    [ObservableProperty]
    private bool _isApplying;

    /// <summary>true = 全部成功 Apply（可由调用方读此值决定是否 Reload）。</summary>
    public bool Applied { get; private set; }

    /// <summary>全部成功后请求关闭弹窗。</summary>
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

        // CanApply 依赖 Tags.Count，订阅集合变化自动刷新 ApplyCommand 的 CanExecute。
        // 防止 AddTagFromInputRaw / RemoveTag 任何分支漏通知。
        Tags.CollectionChanged += (_, _) => ApplyCommand.NotifyCanExecuteChanged();

        Trace.WriteLine($"[EditTagsBatch] ctor: fileCount={_files.Count}, scope={_scope}");

        // 初始 preview（空 chip = 无 diff）
        RefreshDiffPreview();
    }

    /// <summary>scope 变化时刷新 preview。</summary>
    partial void OnScopeChanged(EditTagScope value)
    {
        Trace.WriteLine($"[EditTagsBatch] scope changed → {value}");
        RefreshDiffPreview();
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnNewTagInputChanged(string value) { }

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
        if (Tags.Remove(tag))
        {
            RefreshDiffPreview();
            OnPropertyChanged(nameof(CanApply));
        }
    }

    private bool AddTagFromInputRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.Length == 0 || text.Length > MaxTagLength) return false;
        if (Tags.Any(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase))) return false;
        Tags.Add(text);
        RefreshDiffPreview();
        OnPropertyChanged(nameof(CanApply));
        Trace.WriteLine($"[EditTagsBatch] AddTag: added='{text}', total={Tags.Count}");
        return true;
    }

    /// <summary>根据 scope + Tags + 原 tags 算新 tags（spec §5.3）。</summary>
    private List<string> ComputeNewTags(IReadOnlyList<string> original)
    {
        var editor = Tags.ToList();
        switch (Scope)
        {
            case EditTagScope.Replace:
                return editor.ToList();
            case EditTagScope.Append:
                return original.Concat(editor.Where(t => !original.Any(o => string.Equals(o, t, StringComparison.OrdinalIgnoreCase)))).ToList();
            case EditTagScope.Remove:
                return original.Where(o => !editor.Any(t => string.Equals(t, o, StringComparison.OrdinalIgnoreCase))).ToList();
            default:
                return original.ToList();
        }
    }

    /// <summary>重新计算每张文件的 diff（spec §5.4）。</summary>
    private void RefreshDiffPreview()
    {
        DiffPreview.Clear();
        if (Tags.Count == 0)
        {
            return;
        }

        var editor = Tags.ToList();
        foreach (var file in _files)
        {
            var original = file.Tags ?? new List<string>();
            var (added, removed) = Scope switch
            {
                EditTagScope.Replace => (
                    editor.Except(original, StringComparer.OrdinalIgnoreCase).ToList(),
                    original.Except(editor, StringComparer.OrdinalIgnoreCase).ToList()),
                EditTagScope.Append => (
                    editor.Except(original, StringComparer.OrdinalIgnoreCase).ToList(),
                    new List<string>()),
                EditTagScope.Remove => (
                    new List<string>(),
                    original.Intersect(editor, StringComparer.OrdinalIgnoreCase).ToList()),
                _ => (new List<string>(), new List<string>()),
            };

            if (added.Count > 0 || removed.Count > 0)
            {
                DiffPreview.Add(new TagDiffEntry
                {
                    FileName = file.FileName,
                    Added = added,
                    Removed = removed,
                });
            }
        }
    }

    /// <summary>Apply 按钮 IsEnabled：editor 非空 + 没有正在 apply + 文件数 > 0。</summary>
    public bool CanApply => Tags.Count > 0 && !IsApplying && _files.Count > 0;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (Tags.Count == 0 || _files.Count == 0) return;
        IsApplying = true;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var originalSnapshots = new Dictionary<long, List<string>>();
        var succeededFiles = new List<MediaFile>();
        Exception? firstFailure = null;
        int fail = 0;

        try
        {
            // 1. 逐文件乐观更新 + 写库；失败时立即恢复该文件内存态
            foreach (var file in _files)
            {
                var original = file.Tags?.ToList() ?? new List<string>();
                originalSnapshots[file.Id] = original;
                var newList = ComputeNewTags(original);

                file.Tags = newList;

                try
                {
                    await _mediaRepo.UpdateTagsOnlyAsync(file.Id, newList, nowMs);
                    succeededFiles.Add(file);
                }
                catch (Exception ex)
                {
                    fail++;
                    if (firstFailure == null) firstFailure = ex;
                    // 失败文件立即恢复内存态
                    file.Tags = original;
                    Trace.WriteLine($"[EditTagsBatch] Apply failed for fileId={file.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (fail > 0)
            {
                // 2. 回滚已经成功写库的文件（内存 + DB）
                int rollbackOk = 0, rollbackFail = 0;
                foreach (var file in succeededFiles)
                {
                    if (!originalSnapshots.TryGetValue(file.Id, out var original))
                        continue;

                    try
                    {
                        file.Tags = original;
                        await _mediaRepo.UpdateTagsOnlyAsync(file.Id, original, nowMs);
                        rollbackOk++;
                    }
                    catch (Exception rbEx)
                    {
                        rollbackFail++;
                        Trace.WriteLine($"[EditTagsBatch] Rollback failed for fileId={file.Id}: {rbEx.GetType().Name}: {rbEx.Message}");
                    }
                }

                Applied = false;
                var message = rollbackFail == 0
                    ? $"批量保存失败：{firstFailure?.Message}（{fail} 个文件未保存，已回滚其余 {rollbackOk} 个文件）"
                    : $"批量保存失败：{firstFailure?.Message}（{fail} 个文件未保存，回滚 {rollbackOk} 个，另有 {rollbackFail} 个回滚失败，请刷新视图）";
                _galleryVm.ShowToastPublic(message);
                Trace.WriteLine($"[EditTagsBatch] Apply aborted: fail={fail}, rollbackOk={rollbackOk}, rollbackFail={rollbackFail}");
                return;
            }

            // 3. 全部成功：通知 GalleryVM 刷新左栏 TagGroups，关闭弹窗
            Applied = true;
            foreach (var file in _files)
            {
                _galleryVm.OnFileTagsChanged(file);
            }

            _galleryVm.ShowToastPublic($"已更新 {_files.Count} 个文件的标签");
            Trace.WriteLine($"[EditTagsBatch] Apply done: total={_files.Count}");
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsApplying = false;
        }
    }
}
