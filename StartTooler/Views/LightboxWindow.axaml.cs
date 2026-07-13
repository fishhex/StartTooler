using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StartTooler.ViewModels;

namespace StartTooler.Views;

/// <summary>
/// v0.11 灯箱预览窗口 code-behind（图片 only）。
///
/// 职责：
///   1. 键盘快捷键：方向键翻页 / +/-/= 缩放 / 0 重置缩放 / Esc 关闭 / F 全屏切换（spec §5）
///   2. 鼠标滚轮缩放
///   3. 双击图片切换缩放（spec §3.3）
///   4. 窗口 Closed 事件兜底 Dispose（VM 的 Close 命令不触发 Closed，需要二次保险）
///   5. v0.12 标签输入框 KeyDown：回车提交新 tag（spec §3.3 OnTagInputKeyDown）
///
/// 视频文件不进灯箱 —— 双击走系统默认播放器（spec §8）。
/// </summary>
public partial class LightboxWindow : Window
{
    public LightboxWindow()
    {
        InitializeComponent();
        // 窗口关闭时兜底清理 VM 状态（VM 的 CloseCommand 也会 Dispose，但用户直接点 X 时要走这里）
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is LightboxViewModel vm)
        {
            vm.Dispose();
        }
    }

    /// <summary>
    /// 键盘快捷键（spec §5）：
    ///   ←/→: 翻页（图片/视频通用）
    ///   +/-/=: 缩放（仅图片模式）
    ///   0: 缩放重置（仅图片模式）
    ///   Esc: 关闭窗口
    ///   F: 切换 Maximized / Normal（spec §5 F 键）
    ///   Space: 视频模式下触发「打开外部」（用户对视频最自然期望 = Space 播放），图片模式无操作
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not LightboxViewModel vm) return;

        switch (e.Key)
        {
            case Key.Left:
                vm.GoPrevCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                vm.GoNextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space:
                // 视频模式下 Space = 打开外部（用户期望「Space 播放」）；图片模式不响应
                if (vm.IsVideo)
                {
                    vm.OpenExternallyCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemPlus:
            case Key.Add:
                if (vm.IsImage) { vm.ZoomInCommand.Execute(null); e.Handled = true; }
                break;
            case Key.OemMinus:
            case Key.Subtract:
                if (vm.IsImage) { vm.ZoomOutCommand.Execute(null); e.Handled = true; }
                break;
            case Key.D0:
                if (vm.IsImage) { vm.ZoomResetCommand.Execute(null); e.Handled = true; }
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.F:
                // 全屏切换：Maximize ↔ Normal
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 滚轮缩放（spec §3.3）。
    /// 滚轮 delta > 0 放大，&lt; 0 缩小。
    /// </summary>
    private void OnImagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not LightboxViewModel vm) return;

        // Delta.Y > 0 = 向上滚 = 放大
        var delta = e.Delta.Y > 0 ? 1 : -1;
        vm.ZoomByWheel(delta);
        e.Handled = true;
    }

    /// <summary>
    /// 双击图片切换缩放（1.0 ↔ 2.0）。
    /// </summary>
    private void OnImageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not LightboxViewModel vm) return;
        vm.ZoomToggle();
        e.Handled = true;
    }

    // ============================================================
    //  v0.12 标签编辑（spec doc/15-manual-tag-edit.md §3.3 OnTagInputKeyDown）
    // ============================================================

    /// <summary>
    /// 标签输入框 KeyDown：
    ///   - Enter / Return：调 AddTagCommand 添加新 tag
    ///   - Esc：调 CancelEditTagsCommand 退出编辑态
    /// XAML 绑 TagInputBox.KeyDown = OnTagInputKeyDown。
    /// 实际上 TagChipEditor UserControl 自己处理回车提交（spec §4），这里保留作为防御性 hook。
    /// </summary>
    private void OnTagInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not LightboxViewModel vm) return;
        if (!vm.IsEditingTags) return;

        switch (e.Key)
        {
            case Key.Enter:    // Key.Enter = Key.Return = 6 in Avalonia 11 枚举
                if (vm.AddTagCommand.CanExecute(null))
                {
                    vm.AddTagCommand.Execute(null);
                }
                e.Handled = true;
                break;

            case Key.Escape:
                if (vm.CancelEditTagsCommand.CanExecute(null))
                {
                    vm.CancelEditTagsCommand.Execute(null);
                }
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 编辑态 chip 点击删除（spec §3.3）：
    ///   整个 chip 是 Button，Click 事件 → 从 Button.Tag 拿 tag 字符串 → 调 RemoveTagCommand。
    /// 避开 DataTemplate 编译 binding 模式下的 $parent[Window] 链式 cast 整个返回 null 坑
    /// （v0.8.1 spec 警告，GalleryView 右键菜单踩过）。
    /// 注意：现在 TagChipEditor UserControl 自己处理这个事件（TagChipEditor.axaml.cs 的 OnChipClick），
    /// 这里保留作为防御性 hook（如果 XAML 误用 LightboxWindow 直接放 chip 仍可走这里）。
    /// </summary>
    private void OnEditingChipClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not LightboxViewModel vm) return;
        if (sender is not Control c) return;
        if (c.Tag is not string tag || string.IsNullOrEmpty(tag)) return;

        if (vm.RemoveTagCommand.CanExecute(tag))
        {
            vm.RemoveTagCommand.Execute(tag);
        }
    }

    /// <summary>
    /// v0.11: 视频 overlay 点击 → 直接调 OpenExternally（spec §11.1）。
    /// 用户对视频最自然期望 = 点中心 ▶ 播放；之前 IsHitTestVisible=False 强制走底栏
    /// 「打开外部」按钮 / Space 键，体验割裂。
    /// </summary>
    private void OnVideoPlayTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is LightboxViewModel vm)
            vm.OpenExternallyCommand.Execute(null);
    }
}