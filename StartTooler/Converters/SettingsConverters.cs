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
/// 表单验证错误 → 边框颜色 brush。
/// v0.11（spec §3.2）：错误信息非空 → State.Danger brush；空 → null（透传 XAML 模板默认）。
/// 直接解析 brush key，理由跟 AITestConverters.StateToBrush 一样 —— converter 里拿不到 resource scope。
/// </summary>
public class ErrorToBorderBrushConverter : IValueConverter
{
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF5252"));
    private static readonly IBrush TransparentBrush = new SolidColorBrush(Colors.Transparent);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // string? → 非空 = 错误，返回 Danger brush；空 / null = 无错误，返回 null
        // （XAML 端 BorderBrush 收到 null 时不会清空已 set 的 brush，VM 端要切到默认 brush）
        if (value is string s && !string.IsNullOrEmpty(s))
            return DangerBrush;
        return TransparentBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// string? → bool：非空 = true，空 = false。给 IsVisible / IsEnabled 绑定用。
/// </summary>
public class IsNotEmptyToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrEmpty(s);
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

/// <summary>
/// v0.11 spec/08 §3.2: 按钮文字加载态转换器。
/// value = bool IsLoading（true = 加载中），
/// parameter = 按钮 idle 态文字（如 "保存设置" / "导出配置"）。
/// 加载中返回 "处理中..."，否则返回 parameter。
/// </summary>
public class IsSavingToButtonTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSaving && isSaving)
            return "处理中...";
        return parameter?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// v0.11 spec/08 §3.2: 按钮启用状态加载态转换器。
/// 加载中返回 false（按钮禁用），否则返回 true。
/// </summary>
public class IsSavingToButtonEnabledConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSaving && isSaving)
            return false;
        return true;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
