using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace StartTooler.Services;

public enum NotificationType
{
    Info,
    Success,
    Error,
}

/// <summary>
/// 公网接收通知项（MainWindow 右下角浮窗显示）。
/// 5 秒后自动消失。
/// </summary>
public class NotificationItem
{
    public string Title { get; }
    public string Body { get; }
    public NotificationType Type { get; }
    public DateTime CreatedAt { get; } = DateTime.Now;

    public NotificationItem(string title, string body, NotificationType type)
    {
        Title = title;
        Body = body;
        Type = type;
    }
}

/// <summary>
/// 应用级单例：所有 ViewModel 都通过 NotificationService.Current.Show() 发通知。
/// MainWindow.axaml 绑 Items 渲染右下角浮窗。
/// </summary>
public class NotificationService
{
    public static NotificationService Current { get; } = new();

    public ObservableCollection<NotificationItem> Items { get; } = new();

    private const int DefaultDurationSeconds = 5;

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
}
