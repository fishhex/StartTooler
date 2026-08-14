using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Helpers;
using StartTooler.Services;

namespace StartTooler.ViewModels;

/// <summary>
/// 高级页「数据库」Tab 的 VM。提供 DB Browser 风格的底层数据访问能力：
///   - 切换数据库 → 加载表列表 → 选表 → 分页浏览
///   - 每页大小 / 翻页 / WHERE 子句过滤
///   - 按主键删除单行（带二次确认）
///   - 自由 SQL 控制台（SELECT/PRAGMA/WITH 直接显示；DML/DDL 二次确认 + 影响行数）
///
/// 关键边界：
///   - 不缓存 DataTable（避免内存膨胀 + 旧数据误导），每次切表/翻页都重新拉
///   - 不允许修改 schema（DROP/CREATE 不会被 UI 直接调用；但 ExecuteSql 接受任意 SQL，
///     二次确认对话框提示用户，自负其责）
///   - 不动业务层 Repository；这里只动 DB 文件本身
/// </summary>
public partial class DbInspectorViewModel : ObservableObject
{
    private readonly DbInspectorService _service;
    private CancellationTokenSource? _queryCts;

    public DbInspectorViewModel(DbInspectorService service)
    {
        _service = service;
        Databases = new ObservableCollection<DatabaseInfo>(service.Databases);
        // 默认选 config.db（更小、列表更短、降低误操作风险）
        SelectedDatabase = Databases.FirstOrDefault(d => d.Key == "config") ?? Databases.FirstOrDefault();
    }

    // ====== 数据库选择 ======

    public ObservableCollection<DatabaseInfo> Databases { get; }

    [ObservableProperty] private DatabaseInfo? _selectedDatabase;

    partial void OnSelectedDatabaseChanged(DatabaseInfo? value)
    {
        // 切库 → 清表选择 + 清数据
        Tables.Clear();
        SelectedTable = null;
        Rows = null;
        Columns.Clear();
        StatusText = "已切换数据库，点击「刷新表」加载表列表";
        _ = LoadTablesAsync();
    }

    /// <summary>UI 绑定：当前是否选中了表。用于显示列定义摘要。</summary>
    public bool HasSelectedTable => SelectedTable != null;

    partial void OnSelectedTableChanged(TableInfo? value)
    {
        OnPropertyChanged(nameof(HasSelectedTable));
        Columns.Clear();
        Rows = null;
        WhereClause = "";
        OrderByClause = "";
        PageIndex = 0;
        if (value != null) _ = LoadColumnsAndRowsAsync();
    }

    // ====== 表选择 ======

    public ObservableCollection<TableInfo> Tables { get; } = new();

    [ObservableProperty] private TableInfo? _selectedTable;

    [RelayCommand]
    public async Task LoadTablesAsync()
    {
        if (SelectedDatabase == null) return;
        try
        {
            var tables = await _service.GetTablesAsync(SelectedDatabase.Key);
            Tables.Clear();
            foreach (var t in tables) Tables.Add(t);
            StatusText = $"{SelectedDatabase.DisplayName}：共 {Tables.Count} 张表";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DbInspector] LoadTables 失败: {ex}");
            NotificationService.Current.Show("加载表列表失败", ex.Message, NotificationType.Error);
        }
    }

    // ====== 列 ======

    public ObservableCollection<ColumnInfo> Columns { get; } = new();

    private async Task LoadColumnsAndRowsAsync()
    {
        if (SelectedDatabase == null || SelectedTable == null) return;
        try
        {
            var cols = await _service.GetColumnsAsync(SelectedDatabase.Key, SelectedTable.Name);
            Columns.Clear();
            foreach (var c in cols) Columns.Add(c);
            await LoadRowsAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DbInspector] LoadColumns 失败: {ex}");
            NotificationService.Current.Show("加载列定义失败", ex.Message, NotificationType.Error);
        }
    }

    // ====== 数据行 / 分页 ======

    [ObservableProperty] private ObservableCollection<DbRow>? _rows;

    [ObservableProperty] private int _pageIndex;

    [ObservableProperty] private int _pageSize = 100;

    [ObservableProperty] private long _totalCount;

    /// <summary>是否可以往前翻一页。</summary>
    public bool CanGoPrev => PageIndex > 0;

    /// <summary>是否可以往后翻一页。</summary>
    public bool CanGoNext => (PageIndex + 1L) * PageSize < TotalCount;

    partial void OnPageIndexChanged(int value) { OnPropertyChanged(nameof(CanGoPrev)); OnPropertyChanged(nameof(CanGoNext)); }
    partial void OnPageSizeChanged(int value)  { OnPropertyChanged(nameof(CanGoNext)); }
    partial void OnTotalCountChanged(long value){ OnPropertyChanged(nameof(CanGoNext)); }

    [ObservableProperty] private string _whereClause = "";

    [ObservableProperty] private string _orderByClause = "";

    [ObservableProperty] private string _statusText = "请选择一张表";

    [ObservableProperty] private bool _isLoading;

    [RelayCommand]
    public async Task LoadRowsAsync()
    {
        if (SelectedDatabase == null || SelectedTable == null) return;

        // 取消上一次未完成的查询（避免翻页时旧查询覆盖新结果）
        _queryCts?.Cancel();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;

        IsLoading = true;
        try
        {
            var totalTask = _service.CountAsync(SelectedDatabase.Key, SelectedTable.Name, WhereClause, ct);
            var offset = PageIndex * PageSize;
            var rowsTask = _service.SelectAsync(
                SelectedDatabase.Key, SelectedTable.Name,
                WhereClause, OrderByClause, PageSize, offset, ct);

            await Task.WhenAll(totalTask, rowsTask);
            if (ct.IsCancellationRequested) return;

            TotalCount = await totalTask;
            var query = await rowsTask;
            // 转成 ObservableCollection<DbRow> 让 code-behind 按真实列名生成 DataGridTextColumn
            Rows = new ObservableCollection<DbRow>(
                query.Rows.Select(r => new DbRow(query.ColumnNames, r)));

            var start = TotalCount == 0 ? 0 : offset + 1;
            var end = Math.Min(offset + PageSize, TotalCount);
            StatusText = $"{SelectedTable.Name}：第 {start}-{end} 条 / 共 {TotalCount} 条";
        }
        catch (OperationCanceledException)
        {
            // 翻页太快，旧查询被取消是预期行为，不报错
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DbInspector] LoadRows 失败: {ex}");
            NotificationService.Current.Show("查询失败", ex.Message, NotificationType.Error);
            StatusText = $"查询失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public Task NextPageAsync()
    {
        if ((PageIndex + 1) * PageSize < TotalCount)
        {
            PageIndex++;
            return LoadRowsAsync();
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    public Task PrevPageAsync()
    {
        if (PageIndex > 0)
        {
            PageIndex--;
            return LoadRowsAsync();
        }
        return Task.CompletedTask;
    }

    // ====== 按主键删除行 ======

    /// <summary>删除当前选中表的一行（按 PK）。带二次确认。</summary>
    [RelayCommand]
    public async Task DeleteRowAsync(DbRow? row)
    {
        if (row == null || SelectedTable == null || SelectedDatabase == null) return;

        // 找到 PK 列
        var pk = Columns.FirstOrDefault(c => c.IsPrimaryKey);
        if (pk == null)
        {
            NotificationService.Current.Show(
                "无法删除",
                $"{SelectedTable.Name} 没有主键，UI 不支持。请用 SQL 控制台手动 DELETE。",
                NotificationType.Warning);
            return;
        }

        var pkValue = row[pk.Name];

        // 主键理论上不可能 NULL（DB 设计上 PK NOT NULL），这里兜个底
        if (pkValue == null || pkValue == DBNull.Value)
        {
            NotificationService.Current.Show(
                "PK 为空",
                $"主键列「{pk.Name}」的值为 NULL，无法定位行。请用 SQL 控制台手动 DELETE。",
                NotificationType.Warning);
            return;
        }

        var window = DialogHelper.GetMainWindow();
        if (window == null) return;

        var confirm = await DialogHelper.ShowConfirmAsync(
            window,
            title: "删除数据库行",
            message: $"即将从「{SelectedDatabase.DisplayName} → {SelectedTable.Name}」删除一行：\n\n" +
                     $"PK 列：{pk.Name}\nPK 值：{FormatCell(pkValue)}\n\n" +
                     $"此操作不可撤销，请确认。",
            primaryButtonText: "删除",
            secondaryButtonText: "取消");

        if (!confirm) return;

        try
        {
            var n = await _service.DeleteByPkAsync(
                SelectedDatabase.Key, SelectedTable.Name, pk.Name, pkValue);
            NotificationService.Current.Show(
                n > 0 ? "已删除" : "未删除",
                n > 0 ? $"PK={FormatCell(pkValue)}" : "未匹配到行（可能已被删除）",
                n > 0 ? NotificationType.Success : NotificationType.Warning);
            await LoadRowsAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DbInspector] DeleteRow 失败: {ex}");
            NotificationService.Current.Show("删除失败", ex.Message, NotificationType.Error);
        }
    }

    /// <summary>在 UI 端把 DataRowView 的某列值格式化成可读字符串，用于 PK 显示等。</summary>
    public static string FormatCell(object? value)
    {
        if (value == null || value == DBNull.Value) return "NULL";
        return value switch
        {
            byte[] bytes => $"<BLOB {bytes.Length}B>",
            string s => s.Length > 80 ? s[..80] + "…" : s,
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            _ => value.ToString() ?? "",
        };
    }

    // ====== 自由 SQL 控制台 ======

    [ObservableProperty] private string _sqlInput = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";

    /// <summary>
    /// 执行 SQL 控制台输入。
    /// SELECT/PRAGMA/WITH → 返回 DataTable 渲染到下方；
    /// DML → 二次确认后执行，返回受影响行数；
    /// DDL → 二次确认（更严厉提示）后执行；
    /// 其他 / 空 → 提示用户。
    /// </summary>
    [RelayCommand]
    public async Task ExecuteSqlAsync()
    {
        if (SelectedDatabase == null) return;
        var sql = SqlInput?.Trim();
        if (string.IsNullOrWhiteSpace(sql))
        {
            NotificationService.Current.Show("SQL 为空", "请输入要执行的语句", NotificationType.Warning);
            return;
        }

        var kind = DbInspectorService.ClassifySql(sql);

        if (kind == SqlKind.Empty)
        {
            NotificationService.Current.Show("无法识别", "SQL 为空或仅含注释", NotificationType.Warning);
            return;
        }

        if (kind == SqlKind.Dml || kind == SqlKind.Ddl)
        {
            var window = DialogHelper.GetMainWindow();
            if (window == null) return;

            var kindText = kind == SqlKind.Ddl ? "DDL（结构变更）" : "DML（写操作）";
            var confirm = await DialogHelper.ShowConfirmAsync(
                window,
                title: $"确认执行 {kindText}",
                message: $"将在「{SelectedDatabase.DisplayName}」执行：\n\n{sql}\n\n" +
                         (kind == SqlKind.Ddl
                             ? "警告：DDL 会改变表结构（CREATE/DROP/ALTER），操作不可撤销。\n"
                             : "将修改/删除数据，操作不可撤销（除非有备份）。\n") +
                         "确认继续？",
                primaryButtonText: "执行",
                secondaryButtonText: "取消");

            if (!confirm) return;

            try
            {
                var n = await _service.ExecuteNonQueryAsync(SelectedDatabase.Key, sql);
                NotificationService.Current.Show(
                    "执行成功", $"受影响行数：{n}", NotificationType.Success);
                StatusText = $"执行成功：{n} 行受影响";
                // 写操作后：刷新当前表数据 + 重新加载表列表（防止新表/删表）
                await LoadTablesAsync();
                if (SelectedTable != null) await LoadColumnsAndRowsAsync();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DbInspector] ExecuteSql 失败: {ex}");
                NotificationService.Current.Show("执行失败", ex.Message, NotificationType.Error);
                StatusText = $"执行失败：{ex.Message}";
            }
            return;
        }

        // SELECT/PRAGMA/WITH → 用 ExecuteReader 路径，返回 DataTable
        try
        {
            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={SelectedDatabase.Path}");
            await conn.OpenAsync();
            await using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var columnNames = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columnNames.Add(reader.GetName(i));
            }

            var dictRows = new List<IReadOnlyDictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var dict = new Dictionary<string, object?>(columnNames.Count);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    dict[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                dictRows.Add(dict);
            }

            Rows = new ObservableCollection<DbRow>(dictRows.Select(d => new DbRow(columnNames, d)));
            TotalCount = dictRows.Count;
            StatusText = $"SQL 查询完成：返回 {dictRows.Count} 行 {columnNames.Count} 列";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DbInspector] ExecuteSql(Select) 失败: {ex}");
            NotificationService.Current.Show("查询失败", ex.Message, NotificationType.Error);
            StatusText = $"查询失败：{ex.Message}";
        }
    }

    /// <summary>把当前选中表的全列 SQL 模板塞到 SQL 控制台（节省手写时间）。</summary>
    [RelayCommand]
    public void InsertSelectTemplate()
    {
        if (SelectedTable == null) return;
        SqlInput = $"SELECT * FROM {SelectedTable.Name}\nWHERE 1=1\n-- LIMIT 100;";
    }
}

/// <summary>
/// 数据库浏览器的单行展示模型。
/// 内部存「列名 → 值」的字典，并通过索引器暴露，让 XAML 用 [{列名}] 绑定。
///
/// 同时持有当前查询的 ColumnNames 列表（共享给所有行），
/// 让 View 的 code-behind 能按真实列名生成 DataGridTextColumn。
/// </summary>
public sealed class DbRow
{
    private readonly Dictionary<string, object?> _values;

    public DbRow(IReadOnlyList<string> columnNames, IReadOnlyDictionary<string, object?> source)
    {
        ColumnNames = columnNames;
        _values = new Dictionary<string, object?>(source, StringComparer.Ordinal);
    }

    /// <summary>当前行的列名顺序（所有行共享同一份引用）。</summary>
    public IReadOnlyList<string> ColumnNames { get; }

    /// <summary>索引器：按列名取值。XAML Binding 用 [{列名}] 走这里。</summary>
    public object? this[string columnName]
    {
        get => _values.TryGetValue(columnName, out var v) ? v : null;
        set => _values[columnName] = value;
    }

    /// <summary>用于格式化显示 / 调试。</summary>
    public override string ToString() => $"DbRow({_values.Count} cols)";
}