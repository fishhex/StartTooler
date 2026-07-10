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
}