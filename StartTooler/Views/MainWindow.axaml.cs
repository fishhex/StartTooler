using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using StartTooler.Models;
using StartTooler.ViewModels;
using System;
using System.Diagnostics;
using System.Linq;

namespace StartTooler.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
