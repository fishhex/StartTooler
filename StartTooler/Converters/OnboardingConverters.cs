using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace StartTooler.Converters;

/// <summary>
/// v0.11 spec/07 §3.2: 引导步骤号(①②③)的颜色。
/// true(完成)→ 绿，false(未完成)→ 次要文字色。
/// </summary>
public class BoolToStepColorConverter : IValueConverter
{
    public static readonly BoolToStepColorConverter Completed = new();

    private static readonly IBrush CompletedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly IBrush PendingBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x80));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool done) return done ? CompletedBrush : PendingBrush;
        return PendingBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
