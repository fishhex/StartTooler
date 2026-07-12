using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartTooler.Services;

public enum NotificationType
{
    Info,
    Success,
    Error,
}

/// <summary>
/// 公网接收通知项（MainWindow 右下角浮窗显示）。
///
/// 两种生命周期：
/// - 简单通知（Show）：5 秒后自动消失，无进度条
/// - 进度通知（ShowProgress + UpdateProgress + Dismiss）：由调用方控制生命周期
///   - 可带 Progress（double?，null 时不显示进度条）
///   - 完成后用 Dismiss 移除（或不调，让用户手动点 X）
/// </summary>
public partial class NotificationItem : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private NotificationType _type;
    [ObservableProperty] private double? _progress;
    [ObservableProperty] private DateTime _createdAt = DateTime.Now;

    public NotificationItem()
    {
    }

    public NotificationItem(string title, string body, NotificationType type)
    {
        _title = title;
        _body = body;
        _type = type;
    }
}

/// <summary>
/// 应用级单例：所有 ViewModel 都通过 NotificationService.Current 发通知。
/// MainWindow.axaml 绑 Items 渲染右下角浮窗。
/// </summary>
public class NotificationService
{
    public static NotificationService Current { get; } = new();

    public ObservableCollection<NotificationItem> Items { get; } = new();

    private const int DefaultDurationSeconds = 5;

    /// <summary>
    /// 简单通知：5 秒后自动消失。
    /// </summary>
    public void Show(string title, string body, NotificationType type = NotificationType.Info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = new NotificationItem(title, body, type);
            Items.Add(item);

            // N 秒后自动消失
            DispatcherTimer.RunOnce(() =>
            {
                if (Items.Contains(item))
                    Items.Remove(item);
            }, TimeSpan.FromSeconds(DefaultDurationSeconds));
        });
    }

    /// <summary>
    /// 进度通知：返回 NotificationItem 引用，由调用方 UpdateProgress / Dismiss 控制生命周期。
    /// 不自动消失 —— 进度任务可能跑很久。
    /// </summary>
    public NotificationItem ShowProgress(string title, string body)
    {
        var item = new NotificationItem(title, body, NotificationType.Info);
        Dispatcher.UIThread.Post(() => Items.Add(item));
        return item;
    }

    /// <summary>
    /// 更新现有进度通知。参数为 null 的字段保持不变。
    /// </summary>
    public void UpdateProgress(NotificationItem item, string? body = null, NotificationType? type = null, double? progress = null)
    {
        if (item == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (body != null) item.Body = body;
            if (type.HasValue) item.Type = type.Value;
            if (progress.HasValue) item.Progress = progress.Value;
        });
    }

    /// <summary>
    /// 立即移除通知。
    /// </summary>
    public void Dismiss(NotificationItem item)
    {
        if (item == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (Items.Contains(item))
                Items.Remove(item);
        });
    }
}
