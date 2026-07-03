using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StartTooler.Converters;

public class BoolToSaveTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSaving && isSaving)
            return "保存中…";
        return "保存";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class StringNullOrEmptyToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class StringNullOrEmptyToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(value as string))
            return parameter ?? "选择项目目录";
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class StringNullOrEmptyToPlaceholderClassConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 返回 true 表示应该显示占位符（值为空）
        return string.IsNullOrEmpty(value as string);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return 1.0;
        return 0.4;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// API Key 显示/隐藏切换：true（已显示）→ 不打码 '\0'；false（默认隐藏）→ '•'。
/// Avalonia TextBox.PasswordChar 是 char，不是 string，所以这里直接返回 char。
/// </summary>
public class BoolToPasswordCharConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool visible && visible)
            return '\0';  // 不打码，明文
        return '•';        // 默认隐藏
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// API Key 显示/隐藏切换：true（已显示，当前可点 → 隐藏）→ 显示 EyeOff 图标；
/// false（默认隐藏，当前可点 → 显示）→ 显示 Eye 图标。
/// 让按钮图标跟当前状态语义对齐。
/// </summary>
public class BoolToApiKeyToggleTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVisible && isVisible)
            return "隐藏";   // 当前可见 → 按钮文案提示点这个会隐藏
        return "显示";       // 当前隐藏 → 按钮文案提示点这个会显示
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
