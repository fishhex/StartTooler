using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace StartTooler.Controls;

/// <summary>
/// 通用 tag chip 编辑器 code-behind（spec doc/15-manual-tag-edit.md §4）。
/// 通过 DataContext 强转 ITagEditorHost 拿到 host VM 的 AddTagCommand / RemoveTagCommand。
///
/// 为什么不直接用 {Binding} 写链式 cast：
///   编译 binding 模式下，DataTemplate 内 $parent[UserControl].DataContext.XxxCommand
///   链式 cast 整个返回 null（v0.8.1 spec 警告，GalleryView 右键菜单 + 灯箱 chip 都踩过）。
///   改用 code-behind 强转 ITagEditorHost 拿 ICommand 绕开这个坑。
/// </summary>
public partial class TagChipEditor : UserControl
{
    public TagChipEditor()
    {
        InitializeComponent();
    }

    private void OnChipClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ITagEditorHost host) return;
        if (sender is not Control c) return;
        if (c.Tag is not string tag || string.IsNullOrEmpty(tag)) return;

        if (host.RemoveTagCommand.CanExecute(tag))
        {
            host.RemoveTagCommand.Execute(tag);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ITagEditorHost host) return;
        if (e.Key != Key.Enter) return;   // Key.Enter = Key.Return = 6 in Avalonia 11

        if (host.AddTagCommand.CanExecute(null))
        {
            host.AddTagCommand.Execute(null);
        }
        e.Handled = true;
    }

    /// <summary>
    /// 输入框失焦时自动提交（spec 用户体验：避免输入了文字但没回车就失焦导致的"丢字"）。
    /// 复用 host.AddTagCommand 的 trim/length/duplicate 校验；空 input 静默跳过。
    /// 副作用链路：AddTag → Tags.Add → CollectionChanged → host.IsDirty 更新 + SaveCommand.NotifyCanExecuteChanged
    ///   → 保存按钮在 chip 进入集合后立刻变可点。
    /// </summary>
    private void OnInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ITagEditorHost host) return;
        if (string.IsNullOrWhiteSpace(host.NewTagInput)) return;

        if (host.AddTagCommand.CanExecute(null))
        {
            host.AddTagCommand.Execute(null);
        }
    }
}
