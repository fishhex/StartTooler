using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StartTooler.ViewModels;

namespace StartTooler.Views;

public partial class SettingsView : UserControl
{
    private MenuFlyout? _selectFlyout;
    private Panel? _recentItemsContainer;

    public SettingsView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _selectFlyout = SelectButton?.Flyout as MenuFlyout;
        if (_selectFlyout != null)
        {
            _selectFlyout.Opened += OnSelectFlyoutOpened;
        }

        // 找最近使用容器 —— Flyout 内部 StackPanel AXN 不支持 x:Name，运行时按位置遍历
        _recentItemsContainer = FindRecentItemsContainer(_selectFlyout);
    }

    private static Panel? FindRecentItemsContainer(MenuFlyout? flyout)
    {
        if (flyout == null) return null;
        // MenuFlyout 的 Items 是 MenuItem / Separator / 自定义 Panel 等；
        // 我们知道结构是：MenuItem "浏览..." → Separator → StackPanel (header+items+separator) → MenuItem "清空最近"
        // 直接找第一个带 IsVisible 绑定的 StackPanel —— header 一定是 StackPanel
        foreach (var item in flyout.Items)
        {
            if (item is Panel p) return p;
        }
        return null;
    }

    private void OnSelectFlyoutOpened(object? sender, EventArgs e)
    {
        // 1) 设置 popup 宽度（老逻辑保留）
        if (SelectButton != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var popup = this.FindDescendantOfType<Popup>(true);
                if (popup != null)
                {
                    popup.Width = SelectButton.Bounds.Width;
                }
            }, DispatcherPriority.Loaded);
        }

        // 2) v0.11 §3.1: 动态生成最近目录 MenuItem
        RebuildRecentItems();
    }

    private void RebuildRecentItems()
    {
        if (_recentItemsContainer == null) return;
        if (DataContext is not SettingsViewModel vm) return;

        // 容器结构：TextBlock (header) + 0..N MenuItem + Separator
        // 索引 0 = header (TextBlock)
        // 索引 1..N-1 = MenuItem
        // 末尾 = Separator
        // 我们只清掉 1..(Count-1) 之间的 MenuItem，保留 header 和末尾 Separator
        _recentItemsContainer.Children.Clear();

        var header = new TextBlock
        {
            Text = "最近使用",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = (Avalonia.Media.IBrush?)Resources["Text.Secondary"] ?? Avalonia.Media.Brushes.Gray,
            Padding = new Avalonia.Thickness(8, 4),
        };
        _recentItemsContainer.Children.Add(header);

        if (vm.RecentDirectories.Count == 0)
        {
            // 显式占位 —— 让用户看到"这里有分组"
            var placeholder = new MenuItem { Header = "(暂无)", IsEnabled = false };
            _recentItemsContainer.Children.Add(placeholder);
        }
        else
        {
            foreach (var dir in vm.RecentDirectories)
            {
                var item = new MenuItem { Header = dir };
                item.Click += (_, _) =>
                {
                    // VM 命令签名接受 string?，直接传 dir
                    vm.SelectRecentDirectoryCommand.Execute(dir);
                };
                _recentItemsContainer.Children.Add(item);
            }
        }

        var separator = new Separator();
        _recentItemsContainer.Children.Add(separator);
    }

    // ============================================================
    // v0.11 表单验证：LostFocus → VM 校验方法
    // ============================================================

    private void OnFfmpegPathLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ValidateFfmpegPath();
    }

    private void OnFfprobePathLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ValidateFfprobePath();
    }

    /// <summary>
    /// v0.11 §4.4: 路径前缀失焦时若非空且末字符非 / 自动补。
    /// </summary>
    private void OnOssPathPrefixLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.NormalizeOssPathPrefix();
    }

    // ============================================================
    // v0.11: FFmpeg / FFprobe 路径 TextChanged debounce（spec §12.3）
    // 500ms 内无新输入才触发 ValidateFfmpegPath —— 输入体验比 LostFocus 更快。
    // 保留 LostFocus 作为补充（用户点出去 / 切 Tab 时立即校验）。
    // ============================================================

    private CancellationTokenSource? _ffmpegValidateCts;
    private CancellationTokenSource? _ffprobeValidateCts;
    private static readonly TimeSpan ValidateDebounce = TimeSpan.FromMilliseconds(500);

    private void OnFfmpegPathTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        _ffmpegValidateCts?.Cancel();
        _ffmpegValidateCts = new CancellationTokenSource();
        var ct = _ffmpegValidateCts.Token;

        _ = DebounceValidateAsync(ct, () => Dispatcher.UIThread.Post(() => vm.ValidateFfmpegPath()));
    }

    private void OnFfprobePathTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        _ffprobeValidateCts?.Cancel();
        _ffprobeValidateCts = new CancellationTokenSource();
        var ct = _ffprobeValidateCts.Token;

        _ = DebounceValidateAsync(ct, () => Dispatcher.UIThread.Post(() => vm.ValidateFfprobePath()));
    }

    private static async Task DebounceValidateAsync(CancellationToken ct, Action action)
    {
        try
        {
            await Task.Delay(ValidateDebounce, ct);
            action();
        }
        catch (OperationCanceledException) { /* 后续输入覆盖，跳过本次验证 */ }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Settings] debounce validate failed: {ex.Message}");
        }
    }
}
