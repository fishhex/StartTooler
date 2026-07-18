using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using StartTooler.Models;

namespace StartTooler.Converters;

/// <summary>
/// v0.11 spec/15 §6.1：TimelineBoolConverters 必须支持主题切换（监听 ActualThemeVariant 或动态读取资源）。
/// 旧版硬编码 #4FC3F7/#FF6B6B/#2A3050 改为从 Application.Resources 取对应 token，主题切换即时刷新。
/// </summary>
public static class TimelineThemeLookup
{
    public static IBrush Brush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) == true
            && v is IBrush b)
            return b;
        return fallback;
    }
}

/// <summary>
/// 圆点/文字 Foreground（未选中 = Bg.Divider / Text.Secondary；选中 = Timeline.Dot.Selected / Timeline.Selected）。
/// </summary>
public class BoolToAccentOrSecondaryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var selected = value is bool b && b;
        // 默认未选中用 Text.Secondary，选中用 Timeline.Selected。
        // 注意：圆点本身想用 Timeline.Dot.Selected。这里 History 行为是「圆点选中 = 蓝色」，
        // 通过 parameter="Dot" 区分使用 token。
        if (parameter is string s && s == "Dot")
        {
            return TimelineThemeLookup.Brush(
                selected ? "Timeline.Dot.Selected" : "Bg.Divider",
                new SolidColorBrush(selected ? Color.Parse("#4FC3F7") : Color.Parse("#2A3050")));
        }
        return TimelineThemeLookup.Brush(
            selected ? "Timeline.Selected" : "Text.Secondary",
            new SolidColorBrush(selected ? Color.Parse("#FF6B6B") : Color.Parse("#8892B0")));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>字体粗细：选中 Bold，否则 Normal。</summary>
public class BoolToFontWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool s && s) ? FontWeight.Bold : FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>兼容旧名（v0.6.1 起）：改名 BoolToAccentOrDividerConverter → BoolToDotConverter（语义不变）。</summary>
public class BoolToAccentOrDividerConverter : BoolToAccentOrSecondaryConverter
{
    public new object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => base.Convert(value, targetType, "Dot", culture);
}

/// <summary>
/// 选中时反色：用于 count pill 内的数字（spec §2.2 「2026 年 92」胶囊在选中时反白）。
/// 选中 = Text.Inverse；未选中 = Text.Secondary。
/// </summary>
public class BoolToInverseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var selected = value is bool b && b;
        return TimelineThemeLookup.Brush(
            selected ? "Text.Inverse" : "Text.Secondary",
            new SolidColorBrush(selected ? Color.Parse("#0A0E1A") : Color.Parse("#8892B0")));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 折叠箭头：IsExpanded=true → "▾"，false → "▸"（spec §3）。
/// </summary>
public class BoolToCaretConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? "▾" : "▸";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// TimelineNodeKind → 缩进左边距：Level=0 → 0, Level=1 → 16, Level=2 → 32。
/// spec §2.3 节提到 Year 不缩进、Month 缩进 1 级、Day 缩进 2 级。
/// </summary>
public class LevelToMarginConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        var indent = level * 16;
        return new Thickness(indent, 0, 0, 0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// TimelineNodeKind → 圆点尺寸：Year=10, Month=8, Day=8。
/// </summary>
public class KindToDotSizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimelineNodeKind k)
        {
            return k switch
            {
                TimelineNodeKind.Year => 10.0,
                _ => 8.0,
            };
        }
        return 8.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// TimelineNodeKind → 标题字号：Year=15 Bold, Month=12, Day=12。
/// </summary>
public class KindToFontSizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimelineNodeKind k)
        {
            return k switch
            {
                TimelineNodeKind.Year => 15.0,
                _ => 12.0,
            };
        }
        return 12.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>TimelineNodeKind != Day → true（Year/Month 显示折叠箭头）。</summary>
public class KindToNotDayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimelineNodeKind k && k != TimelineNodeKind.Day;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
