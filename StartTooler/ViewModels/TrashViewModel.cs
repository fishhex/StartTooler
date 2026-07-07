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
/// 垃圾筒页 ViewModel（spec doc/14-delete-and-trash.md §7）。
///
/// 数据分组：
///   CloudFiles — 已上传云端的文件（可从云端下载回来）
///   LocalFiles — 未上传的文件（仅本地存在）
///
/// 操作：
///   Restore        — 软删除 → 恢复（DB deleted_at = NULL）
///   Download       — 云端文件下载到本地（垃圾筒内不自动恢复）
///   CleanSingle    — 单文件彻底删除（可选从云端删）
///   BatchCleanAll  — 清空垃圾筒（可选从云端删）
/// </summary>
public partial class TrashViewModel : ObservableObject
{
    private readonly IMediaRepository _mediaRepo;
    private readonly IUploadJobRepository _uploadJobRepo;
    private readonly IOssStorageFactory _ossFactory;
    private readonly IConfigService _configService;
    private readonly IThumbnailService _thumbnailService;  // v0.8.1 新增：下载后重生成缩略图
    private readonly Func<Task<bool>>? _onOssNotConfigured;
    private CancellationTokenSource? _cts;

    // === 状态 ===
    [ObservableProperty] private string? _projectPath;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isCleaning;       // 批量清理中（防重复点击）
    [ObservableProperty] private string? _toastMessage;

    // === 数据 ===
    public ObservableCollection<MediaFile> CloudFiles { get; } = new();
    public ObservableCollection<MediaFile> LocalFiles { get; } = new();

    public bool HasCloudFiles => CloudFiles.Count > 0;
    public bool HasLocalFiles => LocalFiles.Count > 0;

    public TrashViewModel(
        IMediaRepository mediaRepo,
        IUploadJobRepository uploadJobRepo,
        IOssStorageFactory ossFactory,
        IConfigService configService,
        IThumbnailService thumbnailService,
        Func<Task<bool>>? onOssNotConfigured = null)
    {
        _mediaRepo = mediaRepo;
        _uploadJobRepo = uploadJobRepo;
        _ossFactory = ossFactory;
        _configService = configService;
        _thumbnailService = thumbnailService;
        _onOssNotConfigured = onOssNotConfigured;
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
        Trace.WriteLine($"[Trash] LoadAsync: projectPath={projectPath}");

        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                IsEmpty = true;
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
        }
    }

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
            ShowToast($"已恢复 {file.FileName}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Trash] Restore: 失败 id={file.Id}: {ex.Message}");
            ShowToast($"恢复失败: {ex.Message}");
        }
    }

    // === 下载云端文件到本地（垃圾筒内不自动恢复，spec doc/14-delete-and-trash.md §7.4） ===

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

            // v0.8.1 新增：下载后重新生成缩略图，修复「表里有 ThumbnailPath 但文件不存在」的死路径
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
                // 缩略图生成失败不影响主流程，UI 用占位符兜底
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
            // 三选项：从云端也删除 / 仅删除本地 / 取消
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
                    return;  // 云端失败 → 不删本地和 DB（spec §7.3）
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
                ShowToast("已清除");
            }
            else
            {
                // 「仅删除本地」→ Restore + 释放本地空间（v0.8 review 调整）
                // 云端保留，文件回到 Gallery 显示为「云端有、本地无」。
                // 不调 PermanentDelete，DB 行保留，deleted_at = NULL。
                if (file.LocalExists)
                {
                    DeleteLocalFile(file);
                }
                try
                {
                    await _mediaRepo.UpdateLocalExistsAsync(file.Id, false);
                    await _mediaRepo.RestoreAsync(file.Id);
                    Trace.WriteLine($"[Trash] CleanSingle: Restore+Release 完成 id={file.Id}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Trash] CleanSingle: Restore+Release 失败 id={file.Id}: {ex.Message}");
                    ShowToast($"恢复失败: {ex.Message}");
                    return;
                }
                CloudFiles.Remove(file);
                IsEmpty = CloudFiles.Count == 0 && LocalFiles.Count == 0;
                OnPropertyChanged(nameof(HasCloudFiles));
                ShowToast("已恢复并释放本地空间");
            }
        }
        else
        {
            // 未上传：直接确认删本地 + DB
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
            ShowToast("已清除");
        }
    }

    // === 批量清空 ===

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
                message: $"将永久删除 {total} 个文件。\n其中 {cloudCount} 个文件已上传云端。\n是否一并从云端删除？",
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
                title: "清空垃圾筒",
                message: $"将永久删除 {total} 个文件，不可恢复。",
                primaryButtonText: "彻底删除",
                secondaryButtonText: "取消");

            if (!confirmed)
            {
                userCancelled = true;
            }
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

            // 1) 先处理云端（如果选了）
            if (deleteFromCloud && cloudCount > 0)
            {
                var storage = _ossFactory.TryCreate();
                if (storage == null)
                {
                    Trace.WriteLine("[Trash] BatchCleanAll: OSS 未配置，跳过云端删除");
                    ShowToast("OSS 未配置，仅删除本地");
                    deleteFromCloud = false;  // 退化：仅删本地
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

            // 2) 处理 CloudFiles
            //    - deleteFromCloud=true：删本地 + DB（永久删除）
            //    - deleteFromCloud=false：仅删本地 + Restore（回到 Gallery）
            var cloudFilesList = CloudFiles.ToList();
            foreach (var file in cloudFilesList)
            {
                if (file.LocalExists)
                {
                    DeleteLocalFile(file);
                }

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

            // 3) 处理 LocalFiles（未上传，无云端可恢复）→ 永久删除
            foreach (var file in LocalFiles.ToList())
            {
                if (file.LocalExists)
                {
                    DeleteLocalFile(file);
                }
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

    /// <summary>删本地文件，FileNotFound 视为已删（幂等）。</summary>
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

    /// <summary>
    /// 删 DB 行 + 关联 upload_jobs（无 FK，靠应用层兜底）。
    /// Repository 之间不依赖，由 TrashViewModel 组合调用。
    /// </summary>
    private async Task DeleteFileAndJobAsync(MediaFile file)
    {
        try
        {
            await _uploadJobRepo.DeleteByFileAsync(file.ProjectPath, file.RelativePath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Trash] DeleteFileAndJob: 清 upload_job 失败 id={file.Id}: {ex.Message}");
            // 不阻塞 — media_files 行还是要删
        }

        await _mediaRepo.PermanentDeleteAsync(file.Id);
    }

    private void ShowToast(string message)
    {
        ToastMessage = message;
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ToastMessage = null);
        });
    }
}