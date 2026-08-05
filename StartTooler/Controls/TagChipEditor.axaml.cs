using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StartTooler.Data;

namespace StartTooler.Controls;

/// <summary>
/// 通用 tag chip 编辑器 + v0.12 autocomplete 下拉。
/// 通过 DataContext 强转 ITagEditorHost 拿到 host VM 的命令和数据。
/// </summary>
public partial class TagChipEditor : UserControl
{
    private readonly ObservableCollection<SuggestionItem> _filteredSuggestions = new();
    private int _highlightedIndex = -1;
    private bool _suppressUpdate;

    public TagChipEditor()
    {
        InitializeComponent();
        SuggestionsList.ItemsSource = _filteredSuggestions;
        SuggestionsPopup.PlacementTarget = InputBox;
        InputBox.PropertyChanged += OnInputBoxPropertyChanged;
    }

    // ── 按键导航 ──────────────────────────────────────────

    private void OnInputGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (DataContext is ITagEditorHost host && host.AllProjectTags.Count > 0)
            UpdateSuggestions();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ITagEditorHost host) return;

        if (SuggestionsPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = true;
                    MoveHighlight(1);
                    return;
                case Key.Up:
                    e.Handled = true;
                    MoveHighlight(-1);
                    return;
                case Key.Enter:
                    e.Handled = true;
                    if (!SelectHighlighted(host))
                        ExecuteAddTag(host);
                    return;
                case Key.Escape:
                    e.Handled = true;
                    ClosePopup();
                    return;
            }
        }
        else
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ExecuteAddTag(host);
            }
        }
    }

    // ── 过滤 / 候选更新 ────────────────────────────────────

    private void OnInputBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty)
            UpdateSuggestions();
    }

    private void UpdateSuggestions()
    {
        if (_suppressUpdate) return;
        if (DataContext is not ITagEditorHost host) return;

        var query = (host.NewTagInput ?? "").Trim();
        _highlightedIndex = -1;

        _filteredSuggestions.Clear();

        if (host.AllProjectTags.Count == 0)
        {
            // 数据未加载或项目无 tag：下拉空，仅显示"新建"行
            if (query.Length > 0)
                ShowNewTagOnly(query);
            else
            {
                ClosePopup();
                return;
            }
            return;
        }

        var alreadyAdded = new HashSet<string>(host.Tags, StringComparer.OrdinalIgnoreCase);

        // StartsWith 优先，Contains 其次，各按 UsageCount 降序；ScrollViewer 支持滚动，不限条数
        var startsWith = host.AllProjectTags
            .Where(t => !alreadyAdded.Contains(t.Name) && t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.UsageCount)
            .ThenBy(t => t.Name)
            .ToList();

        var contains = host.AllProjectTags
            .Where(t => !alreadyAdded.Contains(t.Name)
                        && !t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                        && t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.UsageCount)
            .ThenBy(t => t.Name)
            .ToList();

        var hasExactMatch = startsWith.Any(s => string.Equals(s.Name, query, StringComparison.OrdinalIgnoreCase))
                         || contains.Any(c => string.Equals(c.Name, query, StringComparison.OrdinalIgnoreCase));

        foreach (var t in startsWith)
            _filteredSuggestions.Add(new SuggestionItem(t.Name, t.UsageCount));
        foreach (var t in contains)
            _filteredSuggestions.Add(new SuggestionItem(t.Name, t.UsageCount));

        // 新建行：仅查询非空 && 无精确匹配
        var showNew = query.Length > 0 && !hasExactMatch;

        if (_filteredSuggestions.Count > 0 || showNew)
        {
            SectionSeparator.IsVisible = _filteredSuggestions.Count > 0 && showNew;
            NewTagRow.IsVisible = showNew;
            if (showNew) NewTagNameRun.Text = query;
            SuggestionsPopup.IsOpen = true;
        }
        else
        {
            SuggestionsPopup.IsOpen = false;
        }
    }

    private void ShowNewTagOnly(string query)
    {
        _filteredSuggestions.Clear();
        SectionSeparator.IsVisible = false;
        NewTagRow.IsVisible = true;
        NewTagNameRun.Text = query;
        SuggestionsPopup.IsOpen = true;
    }

    // ── 高亮导航 ──────────────────────────────────────────

    private void MoveHighlight(int delta)
    {
        var totalItems = _filteredSuggestions.Count;
        var hasNewRow = NewTagRow.IsVisible;
        var maxIndex = totalItems - 1 + (hasNewRow ? 1 : 0); // -1 = new tag row

        if (maxIndex < 0) return;

        var newIndex = _highlightedIndex + delta;
        if (newIndex > maxIndex) newIndex = 0;
        if (newIndex < -1) newIndex = maxIndex;

        _highlightedIndex = newIndex;
        ApplyHighlight();
    }

    private void ApplyHighlight()
    {
        // 清除所有行的高亮
        for (int i = 0; i < _filteredSuggestions.Count; i++)
        {
            if (SuggestionsList.ContainerFromIndex(i) is Control container)
            {
                var isHighlighted = i == _highlightedIndex;
                if (container.Classes.Contains("suggestion-row-highlighted") != isHighlighted)
                {
                    if (isHighlighted)
                        container.Classes.Add("suggestion-row-highlighted");
                    else
                        container.Classes.Remove("suggestion-row-highlighted");
                }
            }
        }

        // 新标签行高亮
        if (NewTagRow.IsVisible)
        {
            var isNewHighlighted = _highlightedIndex == _filteredSuggestions.Count;
            if (NewTagRow.Classes.Contains("suggestion-row-highlighted") != isNewHighlighted)
            {
                if (isNewHighlighted)
                    NewTagRow.Classes.Add("suggestion-row-highlighted");
                else
                    NewTagRow.Classes.Remove("suggestion-row-highlighted");
            }
        }
    }

    // ── 选中操作 ──────────────────────────────────────────

    /// <returns>true 表示选中了一个候选（包括新建行）</returns>
    private bool SelectHighlighted(ITagEditorHost host)
    {
        if (_highlightedIndex >= 0 && _highlightedIndex < _filteredSuggestions.Count)
        {
            // 选中已有 tag 候选
            var item = _filteredSuggestions[_highlightedIndex];
            SelectTag(host, item.DisplayName);
            return true;
        }

        if (_highlightedIndex == _filteredSuggestions.Count && NewTagRow.IsVisible)
        {
            // 选中"新建"行
            ExecuteAddTag(host);
            return true;
        }

        return false;
    }

    private void SelectTag(ITagEditorHost host, string name)
    {
        if (host.AddTagFromSuggestionCommand.CanExecute(name))
        {
            host.AddTagFromSuggestionCommand.Execute(name);
        }
        ClosePopup();
    }

    private void ExecuteAddTag(ITagEditorHost host)
    {
        if (host.AddTagCommand.CanExecute(null))
        {
            host.AddTagCommand.Execute(null);
        }
        ClosePopup();
    }

    private void ClosePopup()
    {
        _suppressUpdate = true;
        SuggestionsPopup.IsOpen = false;
        _highlightedIndex = -1;
        InputBox.Focus();
        _suppressUpdate = false;
    }

    // ── 鼠标事件（Button.Click，比 PointerPressed 更可靠） ──

    private void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ITagEditorHost host) return;
        if (sender is not Button btn) return;
        if (btn.DataContext is not SuggestionItem item) return;

        SelectTag(host, item.DisplayName);
        e.Handled = true;
    }

    private void OnNewTagClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ITagEditorHost host) return;
        ExecuteAddTag(host);
        e.Handled = true;
    }

    // ── chevron 按钮 ──────────────────────────────────────

    private void OnChevronClick(object? sender, RoutedEventArgs e)
    {
        if (SuggestionsPopup.IsOpen)
        {
            ClosePopup();
        }
        else
        {
            UpdateSuggestions();
        }
    }

    // ── chip 点击（保持不变） ──────────────────────────────

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
}

/// <summary>
/// 下拉候选行视图模型（XAML DataTemplate 绑定，需为顶级 public 类）。
/// </summary>
public sealed class SuggestionItem
{
    public SuggestionItem(string name, int usageCount)
    {
        DisplayName = name;
        UsageText = usageCount > 0 ? $"({usageCount})" : "";
        ShowUsage = usageCount > 0;
    }

    public string DisplayName { get; }
    public string UsageText { get; }
    public bool ShowUsage { get; }
}
