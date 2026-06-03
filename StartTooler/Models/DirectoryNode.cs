using System.Collections.ObjectModel;

namespace StartTooler.Models;

/// <summary>
/// 目录节点模型，用于 TreeView 展示目录结构
/// </summary>
public class DirectoryNode
{
    /// <summary>
    /// 目录名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 目录的完整路径
    /// </summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// 子目录集合
    /// </summary>
    public ObservableCollection<DirectoryNode> Children { get; set; } = new();

    /// <summary>
    /// 是否为根目录
    /// </summary>
    public bool IsRoot { get; set; }

    /// <summary>
    /// 展开状态
    /// </summary>
    public bool IsExpanded { get; set; }

    public override string ToString()
    {
        return Name;
    }
}
