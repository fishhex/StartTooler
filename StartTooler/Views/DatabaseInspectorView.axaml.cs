using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using StartTooler.ViewModels;

namespace StartTooler.Views;

/// <summary>
/// 数据库浏览器子 View。由 AdvancedView 的「数据库」Tab 通过 ContentControl + DataTemplate 渲染。
/// DataContext 由 ContentControl 自动设置成 DbInspectorViewModel。
///
/// 设计要点：DataGrid 的列在 Avalonia 11 下不能 AutoGenerateColumns（DataTable / DataView / 动态类型都不可靠）。
/// 所以这里用 code-behind 监听 VM.Rows 变化，按 QueryResult.ColumnNames 手动构造 DataGridTextColumn。
/// </summary>
public partial class DatabaseInspectorView : UserControl
{
    private DataGrid? _grid;

    public DatabaseInspectorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _grid = this.FindControl<DataGrid>("RowsGrid");
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // 解绑旧 VM
        if (DataContext is INotifyPropertyChanged oldInpc)
        {
            oldInpc.PropertyChanged -= OnVmPropertyChanged;
        }
        // 绑新 VM
        if (DataContext is INotifyPropertyChanged newInpc)
        {
            newInpc.PropertyChanged += OnVmPropertyChanged;
        }
        RebuildColumns();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Rows 一变（翻页 / 选表 / 跑 SQL）→ 重建列
        if (e.PropertyName == nameof(DbInspectorViewModel.Rows))
        {
            RebuildColumns();
        }
    }

    /// <summary>
    /// 根据当前 DataContext.Rows 中的 DbRow 列名动态生成 DataGridTextColumn。
    /// 用索引器 Binding（[$Name]）从 DbRow 字典里取值。
    /// </summary>
    private void RebuildColumns()
    {
        if (_grid == null) return;
        _grid.Columns.Clear();
        if (DataContext is not DbInspectorViewModel vm) return;
        if (vm.Rows == null || vm.Rows.Count == 0) return;

        // 取第一行的 keys 作为列定义（DbRow 用列名做 key）
        var first = vm.Rows[0];
        var columnNames = first.ColumnNames;

        foreach (var name in columnNames)
        {
            var col = new DataGridTextColumn
            {
                Header = name,
                Binding = new Binding($"[{name}]"),
                IsReadOnly = true,
            };
            _grid.Columns.Add(col);
        }
    }
}