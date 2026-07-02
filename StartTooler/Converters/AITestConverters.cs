using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StartTooler.ViewModels;

namespace StartTooler.Converters;

/// <summary>
/// AI 连接测试相关 converter。集中放在这里方便 ViewModel ↔ View 解耦。
/// </summary>
public static class AITestConverters
{
    /// <summary>TestState → 按钮文字：Running 时显示"测试中…"，其它显示"测试连接"。</summary>
    public static readonly IValueConverter StateToButtonText = new StateToButtonTextConverter();

    /// <summary>TestState → 状态图标字符（✓ / ✗ / • / …）。</summary>
    public static readonly IValueConverter StateToIcon = new StateToIconConverter();

    /// <summary>TestState → 颜色 brush（State.Success / Danger / Warning / Quiet）。</summary>
    public static readonly IValueConverter StateToBrush = new StateToBrushConverter();

    /// <summary>TestState != Idle → true。结果行只在非 Idle 时显示。</summary>
    public static readonly IValueConverter IsResultVisible = new IsResultVisibleConverter();

    private class StateToButtonTextConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is AITestState state)
            {
                return state == AITestState.Running ? "测试中…" : "测试连接";
            }
            return "测试连接";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    private class StateToIconConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is AITestState state)
            {
                return state switch
                {
                    AITestState.Ok => "✓",
                    AITestState.Failed => "✗",
                    AITestState.Running => "…",
                    _ => "•",
                };
            }
            return "•";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    private class StateToBrushConverter : IValueConverter
    {
        // 直接解析 brush key（不走 DynamicResource，converter 里拿不到 resource scope）
        private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#4DD0E1"));
        private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF5252"));
        private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#FFA726"));
        private static readonly IBrush QuietBrush = new SolidColorBrush(Color.Parse("#4A5273"));

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is AITestState state)
            {
                return state switch
                {
                    AITestState.Ok => SuccessBrush,
                    AITestState.Failed => DangerBrush,
                    AITestState.Running => WarningBrush,
                    _ => QuietBrush,
                };
            }
            return QuietBrush;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    private class IsResultVisibleConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is AITestState state)
            {
                return state != AITestState.Idle;
            }
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}