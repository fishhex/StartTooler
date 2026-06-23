using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Data;
using Avalonia.Data.Converters;
using StartTooler.Data;
using StartTooler.ViewModels;

namespace StartTooler.Converters;

/// <summary>
/// 单值 converter：判定文件是否被选中
/// 用法: IsVisible="{Binding ..., Converter={x:Static converters:IsFileSelectedConverter.Instance}, ConverterParameter={Binding ...SelectedFiles}}"
///
/// 注意：ConverterParameter 必须是 ObservableCollection&lt;MediaFile&gt; 引用。
/// </summary>
public class IsFileSelectedConverter : IValueConverter
{
    public static readonly IsFileSelectedConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MediaFile file) return false;
        if (parameter is not IEnumerable files) return false;

        foreach (var item in files)
        {
            if (item is MediaFile f && ReferenceEquals(f, file)) return true;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 多值 converter：根据文件 + 选中列表 + 多选模式返回 3 种 bool?
/// - 隐藏 (false): 不在多选模式
/// - 未选 (true1): 多选模式但未选中
/// - 已选 (true2): 多选模式且已选中
///
/// 用 MultiBinding 返回 bool? 给两个 Border 的 IsVisible 用。
/// </summary>
public class FileSelectionStateConverter : IMultiValueConverter
{
    public static readonly FileSelectionStateConverter Instance = new();

    /// <summary>
    /// 返回值: bool? (null=隐藏, false=未选, true=已选)
    /// </summary>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3) return null;
        if (values[0] is not MediaFile file) return null;
        if (values[1] is not IEnumerable selectedFiles) return null;
        if (values[2] is not bool isMultiSelect) return null;

        if (!isMultiSelect) return null;

        foreach (var item in selectedFiles)
        {
            if (item is MediaFile f && ReferenceEquals(f, file)) return true;
        }
        return false;
    }
}

/// <summary>
/// 把 FileSelectionStateConverter 的 bool? 拆成「未选」用的 bool
/// </summary>
public class IsUncheckedConverter : IValueConverter
{
    public static readonly IsUncheckedConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && b == false;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 把 FileSelectionStateConverter 的 bool? 拆成「已选」用的 bool
/// </summary>
public class IsCheckedConverter : IValueConverter
{
    public static readonly IsCheckedConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && b == true;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
