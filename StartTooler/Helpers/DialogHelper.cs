using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace StartTooler.Helpers;

/// <summary>
/// 集中式对话框构造器。所有 dialog 走这里 → 统一走 Themes/Styles.axaml 里的
/// Window.dialog-window / TextBlock.dialog-* / Button.dialog-* 样式类。
///
/// 不要再在 ViewModel 里直接 new Window 拼 dialog 了，新增对话框都走这里。
/// </summary>
public static class DialogHelper
{
    /// <summary>
    /// 通用确认对话框。返回 true = 用户点了 primary 按钮，false = 点了 secondary 或关闭。
    ///
    /// primaryButtonText 必填（"去设置" / "丢弃" / "删除" 等）；
    /// secondaryButtonText 留空则只显示 primary（变成纯提示对话框）。
    /// </summary>
    public static async Task<bool> ShowConfirmAsync(
        Window owner,
        string title,
        string message,
        string primaryButtonText,
        string? secondaryButtonText = "取消")
    {
        var primaryClicked = false;

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Classes = { "dialog-window" }
        };

        var root = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 20),
            Spacing = 12
        };

        // 标题
        root.Children.Add(new TextBlock
        {
            Text = title,
            Classes = { "dialog-title" }
        });

        // 正文
        if (!string.IsNullOrEmpty(message))
        {
            root.Children.Add(new TextBlock
            {
                Text = message,
                Classes = { "dialog-message" }
            });
        }

        // 按钮行
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };

        if (!string.IsNullOrEmpty(secondaryButtonText))
        {
            var secondary = new Button
            {
                Content = secondaryButtonText,
                Classes = { "dialog-secondary" }
            };
            secondary.Click += (_, _) => dialog.Close();
            buttonRow.Children.Add(secondary);
        }

        var primary = new Button
        {
            Content = primaryButtonText,
            Classes = { "dialog-primary" }
        };
        primary.Click += (_, _) =>
        {
            primaryClicked = true;
            dialog.Close();
        };
        buttonRow.Children.Add(primary);

        root.Children.Add(buttonRow);
        dialog.Content = root;

        await dialog.ShowDialog(owner);
        return primaryClicked;
    }

    /// <summary>
    /// 拿到当前主窗口（MainWindow）。在 dialog 入口处统一调一次，
    /// 避免每个调用点都重复 IClassicDesktopStyleApplicationLifetime 那一坨。
    /// </summary>
    public static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }
}
