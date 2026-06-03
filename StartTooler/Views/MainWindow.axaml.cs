using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StartTooler.ViewModels;

namespace StartTooler.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        // 订阅 ViewModel 的选择目录命令，实际执行文件夹选择对话框
        if (DataContext is MainWindowViewModel viewModel)
        {
            // 这里可以通过事件或消息传递机制来处理
            // 简单起见，我们在按钮点击时直接处理
        }
    }

    /// <summary>
    /// 打开文件夹选择对话框
    /// </summary>
    public async Task<string?> SelectFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择媒体文件根目录",
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>
    /// 处理选择目录按钮点击
    /// </summary>
    private async void OnSelectDirectoryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folderPath = await SelectFolderAsync();
        if (!string.IsNullOrEmpty(folderPath) && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SetRootDirectoryAndScanAsync(folderPath);
        }
    }
}