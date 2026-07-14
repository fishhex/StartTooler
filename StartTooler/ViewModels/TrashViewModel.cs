using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aliyun.OSS.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Data;
using StartTooler.Helpers;
using StartTooler.Services;

namespace StartTooler.ViewModels;

/// <summary>
/// 垃圾筒页 ViewModel（spec doc/14-delete-and-trash.md §7 + v0.11 spec/04 §1-10）。
///
/// 数据分组：
///   CloudFiles — 已上传云端的文件（可从云端下载回来）
///   LocalFiles — 未上传的文件（仅本地存在）
///
/// 单文件操作：
///   Restore        — 软删除 → 恢复（DB deleted_at = NULL）→ Toast「已恢复 xxx」+ 跳转链接
///   Download       — 云端文件下载到本地（垃圾筒内不自动恢复）
///   CleanSingle    — 单文件彻底删除（可选从云端删 / 仅删本地 走 UndoDelete 撤销）
///
/// 批量操作（v0.11 多选）：
///   BatchRestore        — 恢复 SelectedCloudIds ∪ SelectedLocalIds 中所有文件
///   BatchCleanSelected  — 清理选中的所有文件（弹窗确认 + 是否同时删云端）
///   SelectAll/DeselectAll — 工具栏全选/取消
///   EnterMultiSelect/ExitMultiSelect — 多选模式状态机
///
/// 操作反馈（v0.11 spec §6.1）：
///   「仅删除本地」可撤销：File.Delete 本地 → Restore → ShowActionToast（5s）「已释放本地空间」+「撤销」
///   撤销 → UndoDeleteAsync 重新软删除 → 文件回到垃圾筒「已在云端」段
/// </summary>
public partial class TrashViewModel : ObservableObject
{
    private readonly IMediaRepository _mediaRepo;
    private readonly IUploadJobRepository _uploadJobRepo;
    private readonly IOssStorageFactory _ossFactory;
    private readonly IConfigService _configService;
    private readonly IThumbnailService _thumbnailService;
    private readonly DontAskAgainService? _dontAskAgain;  // v0.11 spec/08 §5: 可选
    private readonly Func<Task<bool>>? _onOssNotConfigured;
    private readonly Action<long>? _onNavigateToFile;  // v0.11: 跳转 Gallery 回调
    private CancellationTokenSource? _cts;

    // === 状态 ===
    [ObservableProperty] private string? _projectPath;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isCleaning;       // 批量清理中（防重复点击）

    // === Toast（v0.11 扩展：支持操作按钮） ===
    [ObservableProperty] private string? _toastMessage;
    [ObservableProperty] private string? _toastActionText;
    [ObservableProperty] private IRelayCommand? _toastActionCommand;

    // === 多选状态（v0.11 spec §5） ===
    [ObservableProperty] private bool _isMultiSelectMode;
    private bool _isBulkSelecting; // 批量选中时抑制单文件通知

    public HashSet<long> SelectedCloudIds { get; } = new();
    public HashSet<long> SelectedLocalIds { get; } = new();

    [ObservableProperty] private int _selectedCloudCount;
    [ObservableProperty] private int _selectedLocalCount;
    public int TotalSelectedCount => SelectedCloudCount + SelectedLocalCount;

    // === 撤销（spec §6.1） ===
    /// <summary>记录"仅删除本地"前的关键字段，5s 内用户可撤销。</summary>
    private record UndoEntry(long MediaId, string FileName, long DeletedAt);
    private UndoEntry? _lastUndoEntry;
    private CancellationTokenSource? _undoCts;

    // === 数据 ===
    public ObservableCollection<MediaFile> CloudFiles { get; } = new();
    public ObservableCollection<MediaFile> LocalFiles { get; } = new();

    public bool HasCloudFiles => CloudFiles.Count > 0;
    public bool HasLocalFiles => LocalFiles.Count > 0;

    /// <summary>
    /// v0.11: 垃圾筒总文件数（云端 + 本地），供 NavRail 徽章绑定。
    /// CloudFiles / LocalFiles 是 ObservableCollection，Count 变化时需要手动通知。
    /// 在 LoadAsync / Restore / CleanSingle / BatchCleanAll / BatchRestore / BatchCleanSelected 末尾
    /// 已调 OnPropertyChanged(nameof(CapacityStats))，这里同时通知 TrashCount 即可复用。
    /// </summary>
    public int TrashCount => CloudFiles.Count + LocalFiles.Count;

    /// <summary>容量统计：N 个文件 · 总大小（spec §7.1）。</summary>
    public string CapacityStats
    {
        get
        {
            int total = CloudFiles.Count + LocalFiles.Count;
            long totalSize = CloudFiles.Sum(f => f.FileSize) + LocalFiles.Sum(f => f.FileSize);
            return $"{total} 个文件 · {FormatBytes(totalSize)}";
        }
    }

    public TrashViewModel(
        IMediaRepository mediaRepo,
        IUploadJobRepository uploadJobRepo,
        IOssStorageFactory ossFactory,
        IConfigService configService,
        IThumbnailService thumbnailService,
        DontAskAgainService? dontAskAgain = null,
        Func<Task<bool>>? onOssNotConfigured = null,
        Action<long>? onNavigateToFile = null)
    {
        _mediaRepo = mediaRepo;
        _uploadJobRepo = uploadJobRepo;
        _ossFactory = ossFactory;
        _configService = configService;
        _thumbnailService = thumbnailService;
        _dontAskAgain = dontAskAgain;
        _onOssNotConfigured = onOssNotConfigured;
        _onNavigateToFile = onNavigateToFile;

        // 监听 CloudFiles / LocalFiles 集合变化，订阅/解绑 MediaFile.IsSelected
        // 这样 CheckBox 双向绑 IsSelected 时能同步 SelectedCloudIds / SelectedLocalIds。
        CloudFiles.CollectionChanged += OnFilesCollectionChanged;
        LocalFiles.CollectionChanged += OnFilesCollectionChanged;
    }

    private readonly HashSet<MediaFile> _subscribedFiles = new();

    private void OnFilesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is MediaFile mf && _subscribedFiles.Add(mf))
                {
                    mf.PropertyChanged += OnMediaFilePropertyChanged;
                }
            }
        }
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is MediaFile mf && _subscribedFiles.Remove(mf))
                {
                    mf.PropertyChanged -= OnMediaFilePropertyChanged;
                }
            }
        }
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            foreach (var mf in _subscribedFiles)
            {
                mf.PropertyChanged -= OnMediaFilePropertyChanged;
            }
            _subscribedFiles.Clear();
        }

        // v0.11: 任何集合变化都通知相关属性
        OnPropertyChanged(nameof(TrashCount));
        OnPropertyChanged(nameof(CapacityStats));
        OnPropertyChanged(nameof(HasCloudFiles));
        OnPropertyChanged(nameof(HasLocalFiles));
    }

    private void OnMediaFilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MediaFile.IsSelected)) return;
        if (sender is not MediaFile file) return;

        // 同步 IsSelected 变化到 SelectedCloudIds / SelectedLocalIds
        var set = file.IsUploaded ? SelectedCloudIds : SelectedLocalIds;
        if (file.IsSelected)
        {
            set.Add(file.Id);
        }
        else
        {
            set.Remove(file.Id);
        }

        // 批量选中时跳过单文件计数更新，由 SelectAll 最终统一通知
        if (_isBulkSelecting) return;

        if (file.IsUploaded) SelectedCloudCount = SelectedCloudIds.Count;
        else                 SelectedLocalCount = SelectedLocalIds.Count;
        OnPropertyChanged(nameof(TotalSelectedCount));
    }

    // === 加载 ===

    public async Task LoadAsync(string projectPath, CancellationToken ct = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        ProjectPath = projectPath;
        IsLoading = true;
        CloudFiles.Clear();
        LocalFiles.Clear();
        // 切项目/重载时退出多选 + 清撤销栈
        ExitMultiSelect();
        ClearUndoEntry();
        Trace.WriteLine($"[Trash] LoadAsync: projectPath={projectPath}");

        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                IsEmpty = true;
                OnPropertyChanged(nameof(CapacityStats));
                return;
            }

            var files = await _mediaRepo.GetDeletedAsync(projectPath, token);
            foreach (var f in files)
            {
                token.ThrowIfCancellationRequested();
                if (f.IsUploaded) CloudFiles.Add(f);
                else              LocalFiles.Add(f);
            }

            IsEmpty = files.Count == 0;
            Trace.WriteLine($"[Trash] LoadAsync: 完成 CloudFiles={CloudFiles.Count}, LocalFiles={LocalFiles.Count}");
        }
        catch (OperationCanceledException)
        {
            Trace.WriteLine($"[Trash] LoadAsync: 取消");
            // 不覆盖已有数据（spec §11「垃圾筒加载失败保持上一批数据」）
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Trash] LoadAsync: 失败 {ex.Message}");
            ShowToast($"加载垃圾筒失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasCloudFiles));
            OnPropertyChanged(nameof(HasLocalFiles));
            OnPropertyChanged(nameof(CapacityStats));
        }
    }

    // === 多选状态机（spec §5.1） ===

    [RelayCommand]
    private void EnterMultiSelect()
    {
        IsMultiSelectMode = true;
        ClearSelection();
        Trace.WriteLine("[Trash] EnterMultiSelect");
    }

    [RelayCommand]
    private void ExitMultiSelect()
    {
        if (IsMultiSelectMode)
        {
            Trace.WriteLine("[Trash] ExitMultiSelect");
        }
        IsMultiSelectMode = false;
        ClearSelection();
    }

    /// <summary>v0.11: 供 MainWindowViewModel 切页时调用（spec §10 边界：切页时退出多选）。</summary>
    public void ExitMultiSelectPublic() => ExitMultiSelectCommand.Execute(null);

    private void ClearSelection()
    {
        // 把 IsSelected 同步成 false
        foreach (var f in CloudFiles) f.IsSelected = false;
        foreach (var f in LocalFiles) f.IsSelected = false;
        SelectedCloudIds.Clear();
        SelectedLocalIds.Clear();
        SelectedCloudCount = 0;
        SelectedLocalCount = 0;
        OnPropertyChanged(nameof(TotalSelectedCount));
    }

    [RelayCommand]
    private void ToggleSelect(MediaFile? file)
    {
        if (file == null) return;
        // 只改 IsSelected，HashSet / Count 由 OnMediaFilePropertyChanged 同步（spec §5.1 + §5.3）
        file.IsSelected = !file.IsSelected;
    }

    [RelayCommand]
    private void SelectAll()
    {
        _isBulkSelecting = true;
        try
        {
            foreach (var f in CloudFiles) { SelectedCloudIds.Add(f.Id); f.IsSelected = true; }
            foreach (var f in LocalFiles) { SelectedLocalIds.Add(f.Id); f.IsSelected = true; }
        }
        finally
        {
            _isBulkSelecting = false;
            SelectedCloudCount = SelectedCloudIds.Count;
            SelectedLocalCount = SelectedLocalIds.Count;
            OnPropertyChanged(nameof(TotalSelectedCount));
        }
        Trace.WriteLine($"[Trash] SelectAll: cloud={SelectedCloudCount}, local={SelectedLocalCount}");
    }

    [RelayCommand]
    private void DeselectAll() => ClearSelection();

    // === 恢复 ===

    [RelayCommand]
    private async Task Restore(MediaFile? file)
    {
        if (file == null) return;

        Trace.WriteLine($"[Trash] Restore: id={file.Id}, fileName={file.FileName}");

        try
        {
            await _mediaRepo.RestoreAsync(file.Id);
            CloudFiles.Remove(file);
            LocalFiles.Remove(file);
            IsEmpty = CloudFiles.Count == 0 && LocalFiles.Count == 0;
            OnPropertyChanged(nameof(HasCloudFiles));
            OnPropertyChanged(nameof(HasLocalFiles));
            OnPropertyChanged(nameof(CapacityStats));

            // 跳转链接：仅当回调存在时显示（MainWindow 决定是否展示"项目不匹配"等边界）
            if (_onNavigateToFile != null)
            {
                var capturedId = file.Id;
                var capturedName = file.FileName;
                ShowActionToast($"已恢复 {capturedName}", "跳转", () => _onNavigateToFile(capturedId));
            }
            else
            {
                ShowToast($"已恢复 {file.FileName}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Trash] Restore: 失败 id={file.Id}: {ex.Message}");
            ShowToast($"恢复失败: {ex.Message}");
        }
    }

    // === 批量恢复（spec §5.6） ===

    [RelayCommand]
    private async Task BatchRestore()
    {
        var total = TotalSelectedCount;
        if (total == 0) return;

        var ids = SelectedCloudIds.Concat(SelectedLocalIds).ToList();
        Trace.WriteLine($"[Trash] BatchRestore: count={total}");

        ExitMultiSelect();

        int ok = 0;
        int failed = 0;
        foreach (var id in ids)
        {
            try
            {
                await _mediaRepo.RestoreAsync(id);
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Trace.WriteLine($"[Trash] BatchRestore: 失败 id={id}: {ex.Message}");
            }
        }

        // 重新加载以确保列表与 DB 一致（避免出现部分恢复/部分失败的鬼影）
        await ReloadAsync();

        var summary = failed == 0
            ? $"已恢复 {ok} 个文件"
            : $"已恢复 {ok} 个文件（{failed} 个失败）";
        ShowToast(summary);
        Trace.WriteLine($"[Trash] BatchRestore: 完成 ok={ok}, failed={failed}");
    }

    // === 下载云端文件到本地（spec doc/14-delete-and-trash.md §7.4） ===

    [RelayCommand]
    private async Task Download(MediaFile? file)
    {
        if (file == null || !file.IsUploaded || file.LocalExists) return;

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        Trace.WriteLine($"[Trash] Download: id={file.Id}, fileName={file.FileName}");

        var storage = _ossFactory.TryCreate();
        if (storage == null)
        {
            Trace.WriteLine("[Trash] Download: OSS 未配置");
            if (_onOssNotConfigured != null)
            {
                await _onOssNotConfigured();
            }
            else
            {
                ShowToast("OSS 未配置，无法下载");
            }
            return;
        }

        try
        {
            var ossCfg = await _configService.GetAsync<OssConfig>(ConfigKeys.Oss) ?? new OssConfig();
            var objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);
            var localPath = Path.Combine(file.ProjectPath, file.RelativePath);

            // 确保目录存在
            var dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await storage.DownloadAsync(objectKey, localPath);

            file.LocalExists = true;
            await _mediaRepo.UpdateLocalExistsAsync(file.Id, true);

            // v0.8.1: 下载后重新生成缩略图
            try
            {
                var newThumb = await _thumbnailService.GenerateThumbnailAsync(localPath, file.ProjectPath);
                if (!string.IsNullOrEmpty(newThumb))
                {
                    file.ThumbnailPath = newThumb;
                }
            }
            catch
            {
                // 缩略图生成失败不影响主流程
            }

            ShowToast($"已下载 {file.FileName}");
            Trace.WriteLine($"[Trash] Download: 完成 id={file.Id}, localPath={localPath}");
        }
        catch (OssException ex)
        {
            Trace.WriteLine($"[Trash] Download: OSS 错误 id={file.Id}: {ex.Message}");
            ShowToast($"下载失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Trash] Download: 失败 id={file.Id}: {ex.Message}");
            ShowToast($"下载失败: {ex.Message}");
        }
    }

    // === 单文件彻底删除 ===

    [RelayCommand]
    private async Task CleanSingle(MediaFile? file)
    {
        if (file == null) return;

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        Trace.WriteLine($"[Trash] CleanSingle: id={file.Id}, isUploaded={file.IsUploaded}, localExists={file.LocalExists}");

        if (file.IsUploaded)
        {
            // 三选项
            var choice = await DialogHelper.ShowChoiceAsync(
                window,
                title: "彻底删除",
                message: $"「{file.FileName}」已上传云端。\n是否一并从云端删除？",
                primaryButtonText: "从云端也删除",
                secondaryButtonText: "仅删除本地",
                tertiaryButtonText: "取消");

            if (choice == DialogHelper.DialogChoice.Tertiary || choice == DialogHelper.DialogChoice.Cancelled)
            {
                Trace.WriteLine("[Trash] CleanSingle: 用户取消");
                return;
            }

            bool deleteFromCloud = choice == DialogHelper.DialogChoice.Primary;

            if (deleteFromCloud)
            {
                var storage = _ossFactory.TryCreate();
                if (storage == null)
                {
                    Trace.WriteLine("[Trash] CleanSingle: OSS 未配置，无法从云端删");
                    ShowToast("OSS 未配置，无法从云端删除");
                    return;
                }

                try
                {
                    var ossCfg = await _configService.GetAsync<OssConfig>(ConfigKeys.Oss) ?? new OssConfig();
                    var objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);
                    await storage.DeleteObjectAsync(objectKey);
                    Trace.WriteLine($"[Trash] CleanSingle: OSS 删除成功 objectKey={objectKey}");
                }
                catch (OssException ex)
                {
                    Trace.WriteLine($"[Trash] CleanSingle: OSS 删除失败 {ex.Message}");
                    ShowToast($"云端删除失败: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Trash] CleanSingle: OSS 删除异常 {ex.Message}");
                    ShowToast($"云端删除失败: {ex.Message}");
                    return;
                }

                // 云端已删：删本地 + DB（永久删除）
                if (file.LocalExists)
                {
                    DeleteLocalFile(file);
                }
                await DeleteFileAndJobAsync(file);
                CloudFiles.Remove(file);
                IsEmpty = CloudFiles.Count == 0 && LocalFiles.Count == 0;
                OnPropertyChanged(nameof(HasCloudFiles));
                OnPropertyChanged(nameof(CapacityStats));
                ShowToast("已清除");
            }
            else
            {
                // 「仅删除本地」（v0.11 spec §6.1 最终方案）：
                //   删本地 → Restore（deleted_at=NULL）→ 文件回到 Gallery「云端有、本地无」
                //   Toast「已释放本地空间」+「撤销」(5s) → UndoDeleteAsync 重新软删除 → 回到垃圾筒
                if (file.LocalExists)
                {
                    DeleteLocalFile(file);
                }

                // 记录撤销信息（必须先记录再做 Restore，否则 deletedAt 已经为 NULL）
                var undoDeletedAt = file.DeletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _lastUndoEntry = new UndoEntry(file.Id, file.FileName, undoDeletedAt);

                try
                {
                    await _mediaRepo.UpdateLocalExistsAsync(file.Id, false);
                    await _mediaRepo.RestoreAsync(file.Id);
                    Trace.WriteLine($"[Trash] CleanSingle: Restore+Release 完成 id={file.Id}, undoDeletedAt={undoDeletedAt}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Trash] CleanSingle: Restore+Release 失败 id={file.Id}: {ex.Message}");
                    ShowToast($"恢复失败: {ex.Message}");
                    ClearUndoEntry();
                    return;
                }

                CloudFiles.Remove(file);
                IsEmpty = CloudFiles.Count == 0 && LocalFiles.Count == 0;
                OnPropertyChanged(nameof(HasCloudFiles));
                OnPropertyChanged(nameof(CapacityStats));

                // 撤销 Toast（5s 自动消失）
                var capturedEntry = _lastUndoEntry;
                ShowActionToast(
                    $"已释放本地空间：{file.FileName}",
                    "撤销",
                    () => _ = PerformUndoAsync(capturedEntry));
                StartUndoTimeout();
            }
        }
        else
        {
            // 未上传：直接确认删本地 + DB（无云端备份，文件内容确实没了 → 不做撤销）
            var confirmed = await DialogHelper.ShowConfirmAsync(
                window,
                title: "彻底删除",
                message: $"「{file.FileName}」未上传云端。\n将永久删除，不可恢复。",
                primaryButtonText: "彻底删除",
                secondaryButtonText: "取消");

            if (!confirmed) return;

            if (file.LocalExists)
            {
                DeleteLocalFile(file);
            }
            await DeleteFileAndJobAsync(file);
            LocalFiles.Remove(file);
            IsEmpty = CloudFiles.Count == 0 && LocalFiles.Count == 0;
            OnPropertyChanged(nameof(HasLocalFiles));
            OnPropertyChanged(nameof(CapacityStats));
            ShowToast("已清除");
        }
    }

    // === 撤销（spec §6.1） ===

    private async Task PerformUndoAsync(UndoEntry? entry)
    {
        if (entry == null) return;
        Trace.WriteLine($"[Trash] PerformUndo: id={entry.MediaId}, fileName={entry.FileName}, deletedAt={entry.DeletedAt}");

        try
        {
            await _mediaRepo.UndoDeleteAsync(entry.MediaId, entry.DeletedAt);

            // 重新从 DB 读出该 file（它的 deletedAt、localExists 已经是新值了）
            var restored = await _mediaRepo.GetByIdAsync(entry.MediaId);
            if (restored != null)
            {
                if (restored.IsUploaded) CloudFiles.Add(restored);
                else                      LocalFiles.Add(restored);
                IsEmpty = CloudFiles.Count == 0 && LocalFiles.Count == 0;
                OnPropertyChanged(nameof(HasCloudFiles));
                OnPropertyChanged(nameof(HasLocalFiles));
                OnPropertyChanged(nameof(CapacityStats));
            }
            ShowToast($"已撤销：{entry.FileName}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Trash] PerformUndo: 失败 id={entry.MediaId}: {ex.Message}");
            ShowToast($"撤销失败: {ex.Message}");
        }
        finally
        {
            ClearUndoEntry();
        }
    }

    private void StartUndoTimeout()
    {
        _undoCts?.Cancel();
        _undoCts = new CancellationTokenSource();
        var token = _undoCts.Token;
        _ = Task.Delay(5000, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_lastUndoEntry != null)
                {
                    _lastUndoEntry = null;
                    // 只清 ToastAction，保留普通消息
                    ToastActionText = null;
                    ToastActionCommand = null;
                    Trace.WriteLine("[Trash] 撤销入口超时清空");
                }
            });
        }, TaskScheduler.Default);
    }

    private void ClearUndoEntry()
    {
        _undoCts?.Cancel();
        _undoCts = null;
        _lastUndoEntry = null;
    }

    // === 批量清理（spec §5.7） ===

    [RelayCommand]
    private async Task BatchCleanSelected()
    {
        var total = TotalSelectedCount;
        if (total == 0) return;

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        // 统计：选中里有多少云端/本地
        var cloudCount = SelectedCloudCount;
        var localCount = SelectedLocalCount;

        Trace.WriteLine($"[Trash] BatchCleanSelected: cloud={cloudCount}, local={localCount}");

        bool deleteFromCloud = false;
        bool userCancelled = false;

        if (cloudCount > 0)
        {
            var choice = await DialogHelper.ShowChoiceAsync(
                window,
                title: "批量清理",
                message: $"共有 {cloudCount} 个云端文件、{localCount} 个本地文件。\n确认清空？",
                primaryButtonText: "从云端也删除",
                secondaryButtonText: "仅删除本地",
                tertiaryButtonText: "取消");

            switch (choice)
            {
                case DialogHelper.DialogChoice.Tertiary:
                case DialogHelper.DialogChoice.Cancelled:
                    userCancelled = true;
                    break;
                case DialogHelper.DialogChoice.Primary:
                    deleteFromCloud = true;
                    break;
                case DialogHelper.DialogChoice.Secondary:
                    deleteFromCloud = false;
                    break;
            }
        }
        else
        {
            var confirmed = await DialogHelper.ShowConfirmAsync(
                window,
                title: "批量清理",
                message: $"将永久删除 {localCount} 个本地文件，不可恢复。",
                primaryButtonText: "彻底删除",
                secondaryButtonText: "取消");

            if (!confirmed) userCancelled = true;
        }

        if (userCancelled)
        {
            Trace.WriteLine("[Trash] BatchCleanSelected: 用户取消");
            return;
        }

        ExitMultiSelect();
        IsCleaning = true;

        try
        {
            int cloudErrors = 0;
            int permanentlyDeleted = 0;
            int restored = 0;

            // 1) 云端删除（如果选了）
            if (deleteFromCloud && cloudCount > 0)
            {
                var storage = _ossFactory.TryCreate();
                if (storage == null)
                {
                    Trace.WriteLine("[Trash] BatchCleanSelected: OSS 未配置，跳过云端删除");
                    ShowToast("OSS 未配置，仅删除本地");
                    deleteFromCloud = false;
                }
                else
                {
                    var ossCfg = await _configService.GetAsync<OssConfig>(ConfigKeys.Oss) ?? new OssConfig();
                    foreach (var id in SelectedCloudIds)
                    {
                        var file = CloudFiles.FirstOrDefault(f => f.Id == id);
                        if (file == null) continue;
                        try
                        {
                            var objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);
                            await storage.DeleteObjectAsync(objectKey);
                        }
                        catch (Exception ex)
                        {
                            cloudErrors++;
                            Trace.WriteLine($"[Trash] BatchCleanSelected: OSS 删失败 id={file.Id}: {ex.Message}");
                        }
                    }
                }
            }

            // 2) 处理 CloudFiles 中被选中的
            foreach (var id in SelectedCloudIds)
            {
                var file = CloudFiles.FirstOrDefault(f => f.Id == id);
                if (file == null) continue;

                if (file.LocalExists) DeleteLocalFile(file);

                if (deleteFromCloud)
                {
                    try
                    {
                        await DeleteFileAndJobAsync(file);
                        permanentlyDeleted++;
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[Trash] BatchCleanSelected: DB 删失败 id={file.Id}: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        await _mediaRepo.UpdateLocalExistsAsync(file.Id, false);
                        await _mediaRepo.RestoreAsync(file.Id);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[Trash] BatchCleanSelected: Restore 失败 id={file.Id}: {ex.Message}");
                    }
                }
            }

            // 3) 处理 LocalFiles 中被选中的
            foreach (var id in SelectedLocalIds)
            {
                var file = LocalFiles.FirstOrDefault(f => f.Id == id);
                if (file == null) continue;

                if (file.LocalExists) DeleteLocalFile(file);
                try
                {
                    await DeleteFileAndJobAsync(file);
                    permanentlyDeleted++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Trash] BatchCleanSelected: DB 删失败 id={file.Id}: {ex.Message}");
                }
            }

            // 4) 重新加载对齐 DB
            await ReloadAsync();

            var summary = deleteFromCloud
                ? $"已清除 {permanentlyDeleted} 个文件"
                : $"已恢复 {restored} 个，永久删除 {permanentlyDeleted} 个";
            if (cloudErrors > 0) summary += $"，{cloudErrors} 个云端删除失败";
            ShowToast(summary);
            Trace.WriteLine($"[Trash] BatchCleanSelected: 完成 restored={restored}, deleted={permanentlyDeleted}, cloudErrors={cloudErrors}");
        }
        finally
        {
            IsCleaning = false;
        }
    }

    // === 批量清空（保留 v0.8 入口，行为不变） ===

    [RelayCommand]
    private async Task BatchCleanAll()
    {
        var cloudCount = CloudFiles.Count;
        var localCount = LocalFiles.Count;
        var total = cloudCount + localCount;

        if (total == 0) return;

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        Trace.WriteLine($"[Trash] BatchCleanAll: cloud={cloudCount}, local={localCount}");

        bool deleteFromCloud = false;
        bool userCancelled = false;

        if (cloudCount > 0)
        {
            var choice = await DialogHelper.ShowChoiceAsync(
                window,
                title: "清空垃圾筒",
                message: $"共有 {cloudCount} 个云端文件、{localCount} 个本地文件。\n确认清空？",
                primaryButtonText: "从云端也删除",
                secondaryButtonText: "仅删除本地",
                tertiaryButtonText: "取消");

            switch (choice)
            {
                case DialogHelper.DialogChoice.Tertiary:
                case DialogHelper.DialogChoice.Cancelled:
                    userCancelled = true;
                    break;
                case DialogHelper.DialogChoice.Primary:
                    deleteFromCloud = true;
                    break;
                case DialogHelper.DialogChoice.Secondary:
                    deleteFromCloud = false;
                    break;
            }
        }
        else
        {
            // v0.11 spec/08 §5: 「不再提示」支持（仅当 DontAskAgainService 注入时启用）
            const string opKey = "empty_trash";
            var needAsk = _dontAskAgain == null || await _dontAskAgain.ShouldAskAsync(opKey);

            bool confirmed;
            bool dontAskChecked = false;
            if (needAsk)
            {
                (confirmed, dontAskChecked) = await DialogHelper.ShowConfirmWithOptionAsync(
                    window,
                    title: "清空垃圾筒",
                    message: $"将永久删除 {total} 个文件，不可恢复。",
                    primaryButtonText: "彻底删除",
                    secondaryButtonText: "取消",
                    dontAskAgainText: "30 天内不再提示",
                    showDontAskAgain: _dontAskAgain != null);

                if (dontAskChecked && _dontAskAgain != null)
                {
                    await _dontAskAgain.SetDontAskAsync(opKey);
                }
            }
            else
            {
                confirmed = true; // 已设过「不再提示」，直接执行
            }

            if (!confirmed) userCancelled = true;
        }

        if (userCancelled)
        {
            Trace.WriteLine("[Trash] BatchCleanAll: 用户取消");
            return;
        }

        IsCleaning = true;

        try
        {
            int cloudErrors = 0;
            int permanentlyDeleted = 0;
            int restored = 0;

            if (deleteFromCloud && cloudCount > 0)
            {
                var storage = _ossFactory.TryCreate();
                if (storage == null)
                {
                    Trace.WriteLine("[Trash] BatchCleanAll: OSS 未配置，跳过云端删除");
                    ShowToast("OSS 未配置，仅删除本地");
                    deleteFromCloud = false;
                }
                else
                {
                    var ossCfg = await _configService.GetAsync<OssConfig>(ConfigKeys.Oss) ?? new OssConfig();
                    foreach (var file in CloudFiles.ToList())
                    {
                        try
                        {
                            var objectKey = AliyunOssStorage.BuildObjectKey(ossCfg.PathPrefix, file.RelativePath);
                            await storage.DeleteObjectAsync(objectKey);
                        }
                        catch (Exception ex)
                        {
                            cloudErrors++;
                            Trace.WriteLine($"[Trash] BatchCleanAll: OSS 删失败 id={file.Id}: {ex.Message}");
                        }
                    }
                }
            }

            var cloudFilesList = CloudFiles.ToList();
            foreach (var file in cloudFilesList)
            {
                if (file.LocalExists) DeleteLocalFile(file);

                if (deleteFromCloud)
                {
                    try
                    {
                        await DeleteFileAndJobAsync(file);
                        permanentlyDeleted++;
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[Trash] BatchCleanAll: DB 删失败 id={file.Id}: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        await _mediaRepo.UpdateLocalExistsAsync(file.Id, false);
                        await _mediaRepo.RestoreAsync(file.Id);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[Trash] BatchCleanAll: Restore 失败 id={file.Id}: {ex.Message}");
                    }
                }
            }

            foreach (var file in LocalFiles.ToList())
            {
                if (file.LocalExists) DeleteLocalFile(file);
                try
                {
                    await DeleteFileAndJobAsync(file);
                    permanentlyDeleted++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Trash] BatchCleanAll: DB 删失败 id={file.Id}: {ex.Message}");
                }
            }

            CloudFiles.Clear();
            LocalFiles.Clear();
            IsEmpty = true;
            OnPropertyChanged(nameof(HasCloudFiles));
            OnPropertyChanged(nameof(HasLocalFiles));
            OnPropertyChanged(nameof(CapacityStats));

            var summary = deleteFromCloud
                ? $"已清除 {permanentlyDeleted} 个文件"
                : $"已恢复 {restored} 个，永久删除 {permanentlyDeleted} 个";
            if (cloudErrors > 0) summary += $"，{cloudErrors} 个云端删除失败";
            ShowToast(summary);
            Trace.WriteLine($"[Trash] BatchCleanAll: 完成 restored={restored}, deleted={permanentlyDeleted}, cloudErrors={cloudErrors}");
        }
        finally
        {
            IsCleaning = false;
        }
    }

    // === helpers ===

    /// <summary>重新加载当前项目的垃圾筒列表（用于批量操作后对齐 DB 状态）。</summary>
    private async Task ReloadAsync()
    {
        if (string.IsNullOrEmpty(ProjectPath)) return;
        await LoadAsync(ProjectPath);
    }

    private static void DeleteLocalFile(MediaFile file)
    {
        var fullPath = Path.Combine(file.ProjectPath, file.RelativePath);
        if (!File.Exists(fullPath)) return;
        try
        {
            File.Delete(fullPath);
        }
        catch (FileNotFoundException)
        {
            // 幂等
        }
    }

    private async Task DeleteFileAndJobAsync(MediaFile file)
    {
        try
        {
            await _uploadJobRepo.DeleteByFileAsync(file.ProjectPath, file.RelativePath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Trash] DeleteFileAndJob: 清 upload_job 失败 id={file.Id}: {ex.Message}");
            // 不阻塞
        }

        await _mediaRepo.PermanentDeleteAsync(file.Id);
    }

    // === Toast（v0.11 扩展：带操作按钮） ===

    /// <summary>无操作按钮 toast，3s 自动消失。</summary>
    private void ShowToast(string message)
    {
        ToastMessage = message;
        ToastActionText = null;
        ToastActionCommand = null;
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ToastMessage == message) ToastMessage = null;
            });
        });
    }

    /// <summary>带操作按钮的 toast，5s 自动消失。spec §9。</summary>
    private void ShowActionToast(string message, string actionText, Action action)
    {
        ToastMessage = message;
        ToastActionText = actionText;
        ToastActionCommand = new RelayCommand(action);
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // 简单清空（如果有更新 toast 比较消息的话可以更稳，这里不严格）
                ToastMessage = null;
                ToastActionText = null;
                ToastActionCommand = null;
            });
        });
    }

    /// <summary>字节数 → "12.3 MB"（与 BytesToHumanReadableConverter 同源；CapacityStats 计算用）。</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}
