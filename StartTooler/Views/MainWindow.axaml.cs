using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StartTooler.Models;
using StartTooler.Services;
using StartTooler.ViewModels;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace StartTooler.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private ScanProgressWindow? _scanProgressWindow;
    private DispatcherTimer? _toastTimer;
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        AttachViewModel(DataContext as MainWindowViewModel);
        DataContextChanged += OnDataContextChanged;
        UpdateThemeGlyph(ThemeManager.CurrentMode);
        ToastService.Instance.ShowRequested += OnToastShowRequested;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // 检查是否有正在进行的任务
        if (_viewModel?.IsUploading == true)
        {
            e.Cancel = true; // 取消关闭

            // 显示确认对话框
            var result = await ShowConfirmationDialogAsync(
                "正在上传",
                "有文件正在上传中，是否等待上传完成后退出？");

            if (result == true)
            {
                // 用户选择等待，设置为强制关闭
                _forceClose = true;

                // 等待上传完成
                await WaitForUploadToComplete();
            }
            // 用户选择取消，不做任何操作
        }
        else if (_viewModel?.IsScanning == true)
        {
            e.Cancel = true;
            ToastService.Instance.Info("正在扫描中，请稍候...");
        }
        else
        {
            base.OnClosing(e);
        }
    }

    private async Task WaitForUploadToComplete()
    {
        // 使用 TaskCompletionSource 等待上传完成
        var tcs = new TaskCompletionSource<bool>();

        if (_viewModel != null)
        {
            void onCompleted(object? sender, EventArgs args)
            {
                _viewModel.UploadCompleted -= onCompleted;
                tcs.TrySetResult(true);
            }

            _viewModel.UploadCompleted += onCompleted;

            try
            {
                // 等待最多 10 分钟
                await tcs.Task.WaitAsync(TimeSpan.FromMinutes(10));
            }
            catch (TimeoutException)
            {
                ToastService.Instance.Error("等待上传超时");
            }
        }

        // 上传完成后强制关闭
        if (_forceClose)
        {
            _forceClose = false;
            Close();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        AttachViewModel(DataContext as MainWindowViewModel);
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (_viewModel != null)
        {
            _viewModel.RefreshStarted -= OnRefreshStarted;
            _viewModel.RefreshProgressChanged -= OnRefreshProgressChanged;
            _viewModel.RefreshCompleted -= OnRefreshCompleted;
        }

        _viewModel = viewModel;

        if (_viewModel != null)
        {
            _viewModel.RefreshStarted += OnRefreshStarted;
            _viewModel.RefreshProgressChanged += OnRefreshProgressChanged;
            _viewModel.RefreshCompleted += OnRefreshCompleted;
        }
    }

    private async void OnSelectFolderButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is MainWindowViewModel viewModel)
        {
            var folderPath = folders[0].Path.LocalPath;
            viewModel.ScanFolder(folderPath);
        }
    }

    private void OnRefreshStarted(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _scanProgressWindow?.Complete();
            _scanProgressWindow = new ScanProgressWindow
            {
                Topmost = true
            };
            _scanProgressWindow.UpdateStatus("正在扫描文件...", 0, 0, true);
            _scanProgressWindow.Show(this);
        });
    }

    private void OnRefreshProgressChanged(object? sender, RefreshProgressChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _scanProgressWindow?.UpdateStatus(e.Message, e.Current, e.Total, e.IsIndeterminate);
        });
    }

    private void OnRefreshCompleted(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _scanProgressWindow?.Complete();
            _scanProgressWindow = null;
        });
    }

    private void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border border && border.DataContext is MediaFile mediaFile)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                if (viewModel.IsMultiSelectMode)
                {
                    mediaFile.IsSelected = !mediaFile.IsSelected;
                }
                else
                {
                    OpenWithDefaultPlayer(mediaFile.FilePath);
                }
            }
        }
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is MediaFile mediaFile)
        {
            if (DataContext is MainWindowViewModel viewModel && viewModel.IsMultiSelectMode)
            {
                mediaFile.IsSelected = !mediaFile.IsSelected;
            }
        }
    }

    private void OnGroupCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border border && border.DataContext is MediaBurstGroup group)
        {
            if (group.Files.Count == 1)
            {
                OpenWithDefaultPlayer(group.Files[0].FilePath);
            }
        }
        e.Handled = true;
    }

    private void OnThemeToggleClick(object? sender, RoutedEventArgs e)
    {
        var newMode = ThemeManager.CurrentMode == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light;
        ThemeManager.ApplyTheme(newMode);
        UpdateThemeGlyph(newMode);

        if (sender is Button button)
        {
            ToolTip.SetTip(button, newMode == ThemeMode.Dark ? "切换为浅色模式" : "切换为深色模式");
        }
    }

    private void OnBatchDeleteButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowBatchDeleteConfirmation();
    }

    private void UpdateThemeGlyph(ThemeMode mode)
    {
        if (this.FindControl<TextBlock>("ThemeToggleGlyph") is { } glyph)
        {
            glyph.Text = mode == ThemeMode.Dark ? "☀️" : "🌙";
        }
    }

    private void OpenWithDefaultPlayer(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error opening file: {ex.Message}");
        }
    }

    private void OnToastShowRequested(object? sender, ToastEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<Border>("ToastOverlay") is not Border toast ||
                this.FindControl<TextBlock>("ToastMessage") is not TextBlock msg)
                return;

            msg.Text = e.Message;
            toast.Background = e.Type switch
            {
                ToastType.Success => new SolidColorBrush(Color.Parse("#2ECC71")),
                ToastType.Error => new SolidColorBrush(Color.Parse("#E74C3C")),
                _ => new SolidColorBrush(Color.Parse("#DD181B25"))
            };
            toast.Opacity = 1;
            toast.IsHitTestVisible = false;

            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _toastTimer.Tick += (_, _) =>
            {
                toast.Opacity = 0;
                _toastTimer.Stop();
            };
            _toastTimer.Start();
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_viewModel == null)
        {
            base.OnKeyDown(e);
            return;
        }

        var ctrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrlPressed)
        {
            switch (e.Key)
            {
                case Key.A:
                    if (_viewModel.IsMultiSelectMode)
                    {
                        _viewModel.SelectAll();
                        e.Handled = true;
                    }
                    break;
                case Key.D:
                    if (_viewModel.IsMultiSelectMode && _viewModel.HasSelectedFiles)
                    {
                        ShowBatchDeleteConfirmation();
                        e.Handled = true;
                    }
                    break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (_viewModel.IsMultiSelectMode)
                    {
                        _viewModel.IsMultiSelectMode = false;
                        _viewModel.OnMultiSelectModeChanged(false);
                        e.Handled = true;
                    }
                    break;
            }
        }

        base.OnKeyDown(e);
    }

    public async void ShowBatchDeleteConfirmation()
    {
        if (_viewModel == null || _viewModel.SelectedCount == 0)
            return;

        var count = _viewModel.SelectedCount;
        var result = await ShowConfirmationDialogAsync(
            "确认删除",
            $"确定要删除选中的 {count} 个文件吗？此操作不可撤销。");

        if (result == true)
        {
            _viewModel.BatchDelete();
        }
    }

    public async Task<bool?> ShowConfirmationDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Avalonia.Media.Brushes.White
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Avalonia.Thickness(24)
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 14
        };
        Grid.SetRow(messageBlock, 0);
        grid.Children.Add(messageBlock);

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 12
        };
        Grid.SetRow(buttonPanel, 1);

        var cancelButton = new Button
        {
            Content = "取消",
            Padding = new Avalonia.Thickness(20, 8),
            MinWidth = 80
        };
        cancelButton.Click += (_, _) =>
        {
            dialog.Close(false);
        };

        var confirmButton = new Button
        {
            Content = "确定",
            Padding = new Avalonia.Thickness(20, 8),
            MinWidth = 80,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E74C3C")),
            Foreground = Avalonia.Media.Brushes.White
        };
        confirmButton.Click += (_, _) =>
        {
            dialog.Close(true);
        };

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(confirmButton);
        grid.Children.Add(buttonPanel);

        dialog.Content = grid;

        return await dialog.ShowDialog<bool?>(this);
    }
}
