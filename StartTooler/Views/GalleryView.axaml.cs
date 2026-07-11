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
    /// 双击卡片：v0.11 起统一打开灯箱（图片 + 视频缩略图）。
    /// 单击仍走 Button.Command → ToggleSelectionCommand（保持多选行为不变）。
/// </summary>
    private void OnPhotoTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not MediaFile file)
            return;

        if (DataContext is not GalleryViewModel vm)
            return;

        // 阻止双击事件继续冒泡触发其他处理
        e.Handled = true;

        // 统一进灯箱；视频模式在灯箱里只显示缩略图 + ▶ overlay
        vm.PreviewCommand.Execute(file);
    }

    /// <summary>
    /// 右键点击 photo tile：构建 MenuFlyout 并显示（spec §5.3）。
    ///
    /// v0.8.1: 改为 code-behind 构建，避开 XAML `$parent[ItemsControl]...XxxCommand`
    /// 在 DataTemplate + 编译 binding 模式下解析失败导致 Command=null → 灰显的问题。
    /// MultiBinding 走运行时实例化能解析（IsVisible 看着对），但编译期 binding 的
    /// 链式 cast 路径整个返回 null。
    ///
    /// 多选模式不弹菜单（spec §5.3：避免与多选冲突）。
    /// IsVisible 在代码里一次性快照 MediaFile 状态；菜单生命周期短，不用订阅 PropertyChanged。
    /// </summary>
    private void OnPhotoTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsRightButtonPressed) return;
        if (sender is not Control c || c.DataContext is not MediaFile file) return;
        if (DataContext is not GalleryViewModel vm) return;

        // 多选模式下不弹菜单（spec §5.3）
        if (vm.IsMultiSelectMode) return;

        var menu = BuildPhotoContextMenu(file, vm);
        menu.ShowAt(c, true);
        e.Handled = true;
    }

    /// <summary>
    /// 构建 photo tile 右键菜单（5 项：AI 打标 / 编辑标签 / 删除 / 释放本地空间 / 下载到本地）。
    /// 直接挂 VM 实例的 ICommand 属性，跳过 binding 解析。
    /// v0.12: 加「编辑标签」菜单项在 AI 打标下方（spec doc/15-manual-tag-edit.md §4）。
    /// </summary>
    private static MenuFlyout BuildPhotoContextMenu(MediaFile file, GalleryViewModel vm)
    {
        var menu = new MenuFlyout();

        menu.Items.Add(new MenuItem
        {
            Header = "AI 打标",
            Command = vm.TagSingleCommand,
            CommandParameter = file,
        });

        // v0.12: 手动编辑标签（spec §4）— AI 打标下方紧邻。
        // IsEnabled 同步 CanEditTagsSingle 状态（AI 打标中 / 软删除时灰显）。
        menu.Items.Add(new MenuItem
        {
            Header = "编辑标签",
            Command = vm.EditTagsSingleCommand,
            CommandParameter = file,
            IsEnabled = vm.CanEditTagsSingleForMenu(file),
        });

        menu.Items.Add(new MenuItem
        {
            Header = "删除",
            Command = vm.DeleteSingleCommand,
            CommandParameter = file,
        });

        menu.Items.Add(new MenuItem
        {
            Header = "释放本地空间",
            Command = vm.FreeUpSpaceCommand,
            CommandParameter = file,
            // 仅云端已备份 + 本地存在的文件可见（spec §5.3）
            IsVisible = file.IsUploaded && file.LocalExists,
        });

        menu.Items.Add(new MenuItem
        {
            Header = "下载到本地",
            Command = vm.DownloadSingleCommand,
            CommandParameter = file,
            // 仅云端已备份的文件可见；LocalExists 在命令内二次判断（spec §5.3）
            IsVisible = file.IsUploaded,
        });

        return menu;
    }
}
