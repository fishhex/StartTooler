using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StartTooler.ViewModels;

namespace StartTooler.Converters;

/// <summary>
/// EditTagScope ↔ bool 转换器（spec doc/15-manual-tag-edit.md §5.2 RadioButton 绑定）。
///   true  ↔ Replace / Append / Remove
///   false ↔ 其它
/// 用法：ConverterParameter 区分三种 scope（"Replace" / "Append" / "Remove"）。
/// </summary>
public class EditTagScopeConverter : IValueConverter
{
    public static readonly EditTagScopeConverter ToReplace = new() { _target = EditTagScope.Replace };
    public static readonly EditTagScopeConverter ToAppend = new() { _target = EditTagScope.Append };
    public static readonly EditTagScopeConverter ToRemove = new() { _target = EditTagScope.Remove };

    private EditTagScope _target;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is EditTagScope s && s == _target;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b) return _target;
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
