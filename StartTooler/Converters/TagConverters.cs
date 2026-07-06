using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace StartTooler.Converters;

// === v0.6.1 标签分类 Converter（spec doc/11-ai-tagging.md §5.5） ===

/// <summary>
/// string? Tag → string（左栏标签行显示用）
///   null / 空字符串 → "未分类"
///   其他 → 原值返回
///
/// 用例：左栏「标签」tab 单个 TagGroupItem 行右侧文字 —— 兜底空 tag 名显示"未分类"。
/// </summary>
public class EmptyTagToUntitledConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrEmpty(s))
        {
            return s;
        }
        return "未分类";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}