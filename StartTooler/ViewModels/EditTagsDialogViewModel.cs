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
/// 单张文件编辑标签的模态弹窗 ViewModel（spec doc/15-manual-tag-edit.md §4.2 / §6）。
/// 构造时拷贝 MediaFile.Tags 到内部 Tags（不直接绑 CurrentFile.Tags，避免与 GalleryVM 状态混淆）。
/// Save 时调 MediaRepository.UpdateTagsOnlyAsync 写库 + 把新 tags 写回 MediaFile.Tags（乐观更新）。
/// </summary>
public partial class EditTagsDialogViewModel : ObservableObject, ITagEditorHost
{
    private readonly MediaFile _file;
    private readonly IMediaRepository _mediaRepo;
    private readonly GalleryViewModel _galleryVm;

    public MediaFile File => _file;
    public string FileName => _file.FileName;

    public ObservableCollection<string> Tags { get; } = new();

    [ObservableProperty]
    private string _newTagInput = "";

    public int MaxTagLength => 20;
    public string Watermark => "输入标签后回车添加";
    public bool ShowInputBox => true;
    public ICommand AddTagCommand { get; }
    public ICommand RemoveTagCommand { get; }

    /// <summary>Tags 与原 CurrentFile.Tags 是否不同（派生），用于 Save 按钮 IsEnabled。</summary>
    public bool IsDirty
    {
        get
        {
            var current = _file.Tags ?? new List<string>();
            if (Tags.Count != current.Count) return true;
            for (var i = 0; i < Tags.Count; i++)
            {
                if (!string.Equals(Tags[i], current[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    [ObservableProperty]
    private bool _isSaving;

    /// <summary>最终保存的 tags（成功时）。null = 用户取消 / 保存失败。</summary>
    public List<string>? SavedTags { get; private set; }

    public EditTagsDialogViewModel(MediaFile file, IMediaRepository mediaRepo, GalleryViewModel galleryVm)
    {
        _file = file ?? throw new System.ArgumentNullException(nameof(file));
        _mediaRepo = mediaRepo ?? throw new System.ArgumentNullException(nameof(mediaRepo));
        _galleryVm = galleryVm ?? throw new System.ArgumentNullException(nameof(galleryVm));

        // 拷贝当前 tags 到编辑器（保持现状）
        foreach (var t in _file.Tags ?? new List<string>()) Tags.Add(t);
        // 监听集合变化 → 触发 IsDirty 通知 + SaveCommand.CanExecute 重新求值
        // （IsDirty 不是 [ObservableProperty]，RelayCommand 不会自动追踪，必须手动 NotifyCanExecuteChanged）
        Tags.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsDirty));
            SaveCommand.NotifyCanExecuteChanged();
        };

        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand<string>(RemoveTag);

        Trace.WriteLine($"[EditTagsDialog] ctor: file={_file.FileName}, initialTags={Tags.Count}");
    }

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
            Trace.WriteLine($"[EditTagsDialog] RemoveTag: removed='{tag}', remaining={Tags.Count}");
        }
    }

    private bool AddTagFromInputRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.Length == 0 || text.Length > MaxTagLength) return false;
        if (Tags.Any(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase))) return false;
        Tags.Add(text);
        Trace.WriteLine($"[EditTagsDialog] AddTag: added='{text}', total={Tags.Count}");
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!IsDirty) { SavedTags = Tags.ToList(); return; }

        var originalForRollback = _file.Tags.ToList();
        var newList = Tags.ToList();

        IsSaving = true;
        try
        {
            // 1. 乐观更新内存
            _file.Tags = newList;

            // 2. 写库
            var nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _mediaRepo.UpdateTagsOnlyAsync(_file.Id, newList, nowMs);
            SavedTags = newList;

            // 3. 通知 GalleryVM 刷新左栏 TagGroups
            _galleryVm.OnFileTagsChanged(_file);

            Trace.WriteLine($"[EditTagsDialog] Save: ok, fileId={_file.Id}, tags={newList.Count}");
        }
        catch (System.Exception ex)
        {
            _file.Tags = originalForRollback;
            SavedTags = null;
            _galleryVm.ShowToastPublic($"保存标签失败：{ex.Message}");
            Trace.WriteLine($"[EditTagsDialog] Save failed, rolled back: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => IsDirty && !IsSaving;
}
