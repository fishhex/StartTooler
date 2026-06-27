using Avalonia.Controls;
using Avalonia.Input;
using StartTooler.Data;
using StartTooler.ViewModels;

namespace StartTooler.Views;

public partial class GalleryView : UserControl
{
    public GalleryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 双击卡片：用系统默认 app 打开文件；本地缺失则弹窗询问是否从云端下载。
    /// 单击仍走 Button.Command → ToggleSelectionCommand（保持多选行为不变）。
    /// </summary>
    private void OnPhotoTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not MediaFile file)
            return;

        if (DataContext is not GalleryViewModel vm)
            return;

        // 阻止双击事件继续冒泡触发其他处理（目前没看到需要，但留着以防万一）
        e.Handled = true;

        vm.OpenFileCommand.Execute(file);
    }
}
