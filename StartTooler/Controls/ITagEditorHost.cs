using System.Collections.ObjectModel;
using System.Windows.Input;

namespace StartTooler.Controls;

/// <summary>
/// 标签编辑器宿主页契约（spec doc/15-manual-tag-edit.md §4）。
/// 任何承载 TagChipEditor UserControl 的 ViewModel 都实现此接口，
/// 控件通过 DataContext 强转 ITagEditorHost 拿到 chip 列表 / 输入框 / Add / Remove 命令。
/// 当前实现：
///   - LightboxViewModel（灯箱内嵌编辑器，主路径）
///   - EditTagsDialogViewModel（单张模态弹窗）
///   - EditTagsBatchDialogViewModel（批量编辑）
/// </summary>
public interface ITagEditorHost
{
    /// <summary>编辑态下的 tag chip 集合（绑定 ItemsControl.ItemsSource）。</summary>
    ObservableCollection<string> Tags { get; }

    /// <summary>输入框文本（TwoWay 绑 TextBox.Text）。</summary>
    string NewTagInput { get; set; }

    /// <summary>单 tag 最大长度（XAML 软限，code-behind 二次校验）。</summary>
    int MaxTagLength { get; }

    /// <summary>输入框 placeholder（不同上下文不同文案：灯箱/单张/批量不同）。</summary>
    string Watermark { get; }

    /// <summary>是否显示输入框（批量编辑未来可能隐藏，保留扩展点）。</summary>
    bool ShowInputBox { get; }

    /// <summary>从 NewTagInput 添加一个新 tag（回车键触发）。</summary>
    ICommand AddTagCommand { get; }

    /// <summary>从 Tags 列表移除指定 tag（chip 点击触发）。</summary>
    ICommand RemoveTagCommand { get; }
}
