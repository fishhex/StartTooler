using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Data;
using StartTooler.Helpers;
using StartTooler.Services;

namespace StartTooler.ViewModels;

/// <summary>
/// 高级页子 Tab。当前只实现「上传任务管理」，后续可扩展。
/// </summary>
public enum AdvancedTab
{
    UploadTasks,
}

public partial class AdvancedViewModel : ObservableObject
{
    private readonly UploadJobRepository _uploadJobRepo;
    private readonly GalleryViewModel _gallery;

    [ObservableProperty] private AdvancedTab selectedTab = AdvancedTab.UploadTasks;

    /// <summary>同步 SelectedTabIndex 给子 TabControl 用。保留 selectedTab 枚举做语义层兼容。</summary>
    public int SelectedTabIndex
    {
        get => (int)SelectedTab;
        set
        {
            if (value < 0) value = 0;
            if (value > (int)AdvancedTab.UploadTasks) value = 0;
            SelectedTab = (AdvancedTab)value;
        }
    }

    /// <summary>当前项目的所有未完成上传任务。点击「刷新」或初始化时重载。</summary>
    public ObservableCollection<UploadJob> UploadJobs { get; } = new();

    /// <summary>任务为空时显示提示区块。</summary>
    public bool HasUploadJobs => UploadJobs.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUploadJobs))]
    private int _uploadJobCount;

    [ObservableProperty] private bool _isLoading;

    public AdvancedViewModel(UploadJobRepository uploadJobRepo, GalleryViewModel gallery)
    {
        _uploadJobRepo = uploadJobRepo;
        _gallery = gallery;
        UploadJobs.CollectionChanged += (_, _) =>
        {
            UploadJobCount = UploadJobs.Count;
        };
    }

    /// <summary>
    /// 切到本页或上传完成时调用：刷新当前项目下的 upload_jobs。
    /// 没有项目目录时直接清空（避免上一个项目的残留数据误导用户）。
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var projectPath = _gallery.ProjectPath;
        if (string.IsNullOrEmpty(projectPath))
        {
            UploadJobs.Clear();
            NotificationService.Current.Show(
                "请先选择项目目录", "在「媒体」页选择项目后再查看上传任务", NotificationType.Warning);
            return;
        }

        IsLoading = true;
        try
        {
            var jobs = await _uploadJobRepo.GetInProgressAsync(projectPath);
            UploadJobs.Clear();
            foreach (var job in jobs) UploadJobs.Add(job);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AdvancedVM] RefreshAsync 异常: {ex}");
            NotificationService.Current.Show(
                "加载上传任务失败", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>删除单个任务的续传记录（不删除本地文件、不调用 Abort —— 用户可自行重新上传）。</summary>
    [RelayCommand]
    public async Task DeleteJobAsync(UploadJob? job)
    {
        if (job == null) return;
        try
        {
            await _uploadJobRepo.DeleteAsync(job.Id);
            UploadJobs.Remove(job);
            NotificationService.Current.Show(
                "已删除上传任务", job.RelativePath, NotificationType.Success);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AdvancedVM] DeleteJobAsync 异常: {ex}");
            NotificationService.Current.Show(
                "删除失败", ex.Message, NotificationType.Error);
        }
    }

    /// <summary>清空当前项目的所有未完成任务（一次确认，避免误操作）。</summary>
    [RelayCommand]
    public async Task ClearAllAsync()
    {
        if (UploadJobs.Count == 0) return;

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        var confirm = await DialogHelper.ShowConfirmAsync(
            window,
            title: "清空上传任务",
            message: $"将移除当前项目下全部 {UploadJobs.Count} 个未完成任务。\n本地文件不受影响，但再次上传会从头开始。是否继续？",
            primaryButtonText: "清空",
            secondaryButtonText: "取消");

        if (!confirm) return;

        try
        {
            foreach (var job in UploadJobs.ToList())
            {
                await _uploadJobRepo.DeleteAsync(job.Id);
            }
            var removed = UploadJobs.Count;
            UploadJobs.Clear();
            NotificationService.Current.Show(
                "已清空所有上传任务", $"共移除 {removed} 个", NotificationType.Success);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AdvancedVM] ClearAllAsync 异常: {ex}");
            NotificationService.Current.Show(
                "清空失败", ex.Message, NotificationType.Error);
        }
    }

    /// <summary>恢复单个任务（走 GalleryViewModel.ResumeInterruptedAsync 续传流程）。</summary>
    [RelayCommand]
    public async Task ResumeAsync(UploadJob? job)
    {
        if (job == null) return;
        await _gallery.ResumeInterruptedAsync(new[] { job });
        // 续传启动后稍等一会再刷新（让 Upsert/Complete 落到 DB 后再读）
        await Task.Delay(500);
        await RefreshAsync();
    }

    /// <summary>恢复所有任务。</summary>
    [RelayCommand]
    public async Task ResumeAllAsync()
    {
        if (UploadJobs.Count == 0) return;
        var snapshot = UploadJobs.ToList();
        await _gallery.ResumeInterruptedAsync(snapshot);
        await Task.Delay(500);
        await RefreshAsync();
    }
}