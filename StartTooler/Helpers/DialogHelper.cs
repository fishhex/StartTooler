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
    /// 三选项对话框结果。v0.8 垃圾筒场景用（spec doc/14-delete-and-trash.md §7.3）：
    ///   - CleanSingle: Primary="从云端也删除" / Secondary="仅删除本地" / Tertiary="取消"
    ///   - BatchCleanAll: 同上
    /// 用户关闭弹窗（点 X 或 Esc）→ Cancelled。
    /// </summary>
    public enum DialogChoice
    {
        Cancelled,
        Primary,
        Secondary,
        Tertiary,
    }
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
    /// v0.11 spec/08 §5: 带「不再提示」选项的确认对话框。
    /// 返回 (confirmed, dontAskChecked)：confirmed = 是否点了 primary，dontAskChecked = CheckBox 是否勾选。
    /// </summary>
    /// <param name="showDontAskAgain">false 时 CheckBox 隐藏，但 dontAskAgainText/dontAskChecked 仍可读</param>
    public static async Task<(bool confirmed, bool dontAskChecked)> ShowConfirmWithOptionAsync(
        Window owner,
        string title,
        string message,
        string primaryButtonText,
        string? secondaryButtonText = "取消",
        string dontAskAgainText = "30 天内不再提示",
        bool showDontAskAgain = true)
    {
        var primaryClicked = false;
        var dontAskChecked = false;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Classes = { "dialog-window" }
        };

        var root = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 20),
            Spacing = 12
        };

        root.Children.Add(new TextBlock
        {
            Text = title,
            Classes = { "dialog-title" }
        });

        if (!string.IsNullOrEmpty(message))
        {
            root.Children.Add(new TextBlock
            {
                Text = message,
                Classes = { "dialog-message" }
            });
        }

        // 「不再提示」CheckBox
        if (showDontAskAgain)
        {
            var checkBox = new CheckBox
            {
                Content = dontAskAgainText,
                Margin = new Thickness(0, 4, 0, 0),
                IsChecked = false,
            };
            checkBox.IsCheckedChanged += (_, _) =>
            {
                dontAskChecked = checkBox.IsChecked == true;
            };
            root.Children.Add(checkBox);
        }

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
        return (primaryClicked, dontAskChecked);
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

    /// <summary>
    /// 通用通知/告警对话框。单按钮（"知道了"），阻塞直到用户关闭。
    /// 用途：上传失败汇总、不可恢复错误提示等需要明确告知的场景。
    /// </summary>
    /// <param name="message">
    /// 详细说明。多行 OK，会按 \n 自动换行。
    /// </param>
    public static async Task ShowAlertAsync(Window owner, string title, string message, string buttonText = "知道了")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Classes = { "dialog-window" }
        };

        var root = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 20),
            Spacing = 12
        };

        root.Children.Add(new TextBlock
        {
            Text = title,
            Classes = { "dialog-title" }
        });

        if (!string.IsNullOrEmpty(message))
        {
            root.Children.Add(new TextBlock
            {
                Text = message,
                Classes = { "dialog-message" },
                TextAlignment = TextAlignment.Left,  // 错误列表左对齐更易读
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxWidth = 360,
            });
        }

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var okButton = new Button
        {
            Content = buttonText,
            Classes = { "dialog-primary" }
        };
        okButton.Click += (_, _) => dialog.Close();
        buttonRow.Children.Add(okButton);

        root.Children.Add(buttonRow);
        dialog.Content = root;

        await dialog.ShowDialog(owner);
    }

    /// <summary>
    /// 三选项对话框（v0.8 垃圾筒彻底删除用）。
    ///
    /// 三个按钮横向排列（primary 在最右强调），返回用户点击的按钮枚举。
    /// 用户关闭弹窗（点 X / Esc）→ DialogChoice.Cancelled。
    ///
    /// 按钮排列：tertiary（左） / secondary（中） / primary（右，destructive 操作时配 State.Danger 风格）。
    /// </summary>
    public static async Task<DialogChoice> ShowChoiceAsync(
        Window owner,
        string title,
        string message,
        string primaryButtonText,
        string secondaryButtonText,
        string tertiaryButtonText)
    {
        var choice = DialogChoice.Cancelled;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Classes = { "dialog-window" }
        };

        var root = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 20),
            Spacing = 12
        };

        root.Children.Add(new TextBlock
        {
            Text = title,
            Classes = { "dialog-title" }
        });

        if (!string.IsNullOrEmpty(message))
        {
            root.Children.Add(new TextBlock
            {
                Text = message,
                Classes = { "dialog-message" }
            });
        }

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };

        // tertiary: 左，最弱动作（取消 / 暂不）
        var tertiary = new Button
        {
            Content = tertiaryButtonText,
            Classes = { "dialog-secondary" }
        };
        tertiary.Click += (_, _) =>
        {
            choice = DialogChoice.Tertiary;
            dialog.Close();
        };
        buttonRow.Children.Add(tertiary);

        // secondary: 中，备选动作
        var secondary = new Button
        {
            Content = secondaryButtonText,
            Classes = { "dialog-secondary" }
        };
        secondary.Click += (_, _) =>
        {
            choice = DialogChoice.Secondary;
            dialog.Close();
        };
        buttonRow.Children.Add(secondary);

        // primary: 右，主推动作（destructive 也走这里，由调用方按需覆盖样式）
        var primary = new Button
        {
            Content = primaryButtonText,
            Classes = { "dialog-primary" }
        };
        primary.Click += (_, _) =>
        {
            choice = DialogChoice.Primary;
            dialog.Close();
        };
        buttonRow.Children.Add(primary);

        root.Children.Add(buttonRow);
        dialog.Content = root;

        await dialog.ShowDialog(owner);
        return choice;
    }
}
