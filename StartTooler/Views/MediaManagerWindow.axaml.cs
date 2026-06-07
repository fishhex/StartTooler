using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StartTooler.Models;
using StartTooler.Services;
using StartTooler.ViewModels;
using System;
using System.Diagnostics;
using System.Linq;

namespace StartTooler.Views;

public partial class MediaManagerWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private ScanProgressWindow? _scanProgressWindow;

    public MediaManagerWindow()
    {
        InitializeComponent();
        AttachViewModel(DataContext as MainWindowViewModel);
        DataContextChanged += OnDataContextChanged;
        UpdateThemeGlyph(ThemeManager.CurrentMode);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
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
            if (mediaFile.FileType == "视频")
            {
                OpenWithDefaultPlayer(mediaFile.FilePath);
            }
            else
            {
                var previewWindow = new PreviewWindow();
                previewWindow.ShowFile(mediaFile);
                previewWindow.Show(this);
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
        if (e.Source is Control { DataContext: MediaFile })
        {
            return;
        }

        if (sender is Border { DataContext: MediaBurstGroup group })
        {
            ToggleGroupExpansion(group);
            e.Handled = true;
        }
    }

    private void OnGroupBadgeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MediaBurstGroup group })
        {
            ToggleGroupExpansion(group);
            e.Handled = true;
        }
    }

    private static void ToggleGroupExpansion(MediaBurstGroup group)
    {
        group.IsExpanded = !group.IsExpanded;
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
}
