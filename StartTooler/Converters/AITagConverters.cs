using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StartTooler.Converters;

// === v0.6 AI 打标 Converter（spec doc/12-ai-toolbar-buttons.md §3.5.5） ===

/// <summary>
/// int? Score → IBrush（按分数区间返回颜色）
///   ≥80 → 绿 #4CAF50
///   60-79 → 黄 #FFA726
///   &lt;60 → 灰 #90A4AE
///   null → 透明 Brushes.Transparent（让 photo tile 角标隐藏而非显示灰底）
/// </summary>
public class ScoreToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int score)
        {
            if (score >= 80) return new SolidColorBrush(Color.Parse("#4CAF50"));
            if (score >= 60) return new SolidColorBrush(Color.Parse("#FFA726"));
            return new SolidColorBrush(Color.Parse("#90A4AE"));
        }
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// int? Score → string（用于角标显示）
///   null → ""
///   else → (score / 10.0).ToString("F1")  （例 82 → "8.2"）
/// </summary>
public class ScoreToDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int score)
        {
            return (score / 10.0).ToString("F1", CultureInfo.InvariantCulture);
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// List&lt;string&gt; Tags → string（用于标签小角标条截断显示）
///   null/empty → ""
///   ≤3 个 → " · " join
///   &gt;3 个 → 前 2 个 + " +N"  （N = 剩余数量）
///   ConverterParameter="Full" → 全部 join 不截断（tooltip 用）
/// </summary>
public class TagsToShortTextConverter : IValueConverter
{
    private const string Separator = " · ";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is List<string> tags && tags.Count > 0)
        {
            // 完整列表（tooltip 用），通过 ConverterParameter="Full" 触发
            if (parameter is string s && string.Equals(s, "Full", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(Separator, tags);
            }
            // 角标截断显示（默认）
            if (tags.Count <= 3)
            {
                return string.Join(Separator, tags);
            }
            var firstTwo = string.Join(Separator, tags.Take(2));
            var rest = tags.Count - 2;
            return $"{firstTwo} +{rest}";
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}